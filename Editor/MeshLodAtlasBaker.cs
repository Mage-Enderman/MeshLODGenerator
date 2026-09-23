using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MeshLODGenerator
{
    /// <summary>
    /// Bakes the appearance of any GameObject (materials, textures, emission) into a unified
    /// UV atlas for the generated LOD mesh via multi-view orthographic capture.
    ///
    /// Engine Features:
    /// - Difference matting on black and white backgrounds for robust coverage and un-premultiplied edges.
    /// - 16-bit depth buffer capture & part-ID masking (renders Hidden/MeshLodPartId).
    /// - Multi-threaded parallel projection over row bands via in-memory 16-bit depth testing (no per-texel raycasts).
    /// - Region-of-interest close-up passes for small detail-dense areas (hands, feet, head).
    /// - Batched ambient occlusion raycasts via RaycastCommand.ScheduleBatch.
    /// - Pipeline compatibility probe (MSAA / 1x, RenderRequest / Camera.Render).
    /// - Multi-channel baking: Albedo, Emission, and custom shader properties (e.g. metallic, smoothness, normal).
    /// </summary>
    public static class MeshLodAtlasBaker
    {
        private const int DilatePasses = 16;
        private const float MinCoverage = 0.4f;
        private const float FallbackCoverage = 0.2f;
        private const int OcclusionLayer = 2; // Ignore Raycast: invisible to default queries, targetable by mask
        private const float RegionScoreBias = 0.5f;
        private const float MinViewFacing = 0.25f;
        private const float RegionMinFacing = 0.35f;

        private static bool sFlipSampleY;
        private static bool sUseRenderRequest;
        private static bool sUseMsaaTargets;

        public struct BakeOptions
        {
            public int AtlasSize;
            public int CaptureSize;
            public bool BakeEmission;
            public bool BakeCustomChannel;
            public string CustomChannelProperty;
            public bool ComputeAmbientOcclusion;
            public RegionOfInterest[] Regions;
            public BakeMask Mask;
        }

        public struct BakeResult
        {
            public Texture2D AlbedoAtlas;
            public Texture2D EmissionAtlas;
            public Texture2D CustomChannelAtlas;
        }

        public struct RegionOfInterest
        {
            public string Name;
            public Bounds RootBounds;
        }

        public struct BakeMask
        {
            public Vector3[] Positions;
            public Color32[] Colors;
            public int[] Indices;
            public byte[] TexelVertexGroup;
            public byte[] TexelHidden;

            public bool IsValid => Positions != null && Colors != null && Indices != null &&
                                   Positions.Length > 0 && Colors.Length == Positions.Length && Indices.Length >= 3;
        }

        public static byte EncodeGroup(byte group)
        {
            return (byte)(40 + group * 40);
        }

        private static byte DecodeGroup(byte encoded)
        {
            return encoded < 20 ? (byte)255 : (byte)Mathf.Clamp(Mathf.RoundToInt((encoded - 40f) / 40f), 0, 5);
        }

        private struct CaptureView
        {
            public Vector3 DirectionWorld;
            public Matrix4x4 WorldToPixel;
            public Color32[] Pixels; // rgb = un-premultiplied color, a = coverage
            public byte[] GroupIds;  // per pixel body group (255 = background); null when no mask
            public ushort[] Depth16; // per pixel normalized [near,far] depth; null when no mask
            public Vector3 CameraPositionWorld;
            public float DepthNear;
            public float DepthFar;
            public float DepthToleranceMeters;
            public int Size;
            public bool IsRegion;
            public Bounds ValidBoundsRoot;
        }

        public static BakeResult Bake(
            Transform root,
            Mesh decimatedMesh,
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uv,
            int[] indices,
            BakeOptions options)
        {
            int atlasSize = Mathf.Clamp(options.AtlasSize, 128, 4096);
            int captureSize = Mathf.Max(options.CaptureSize, atlasSize);
            int regionCaptureSize = Mathf.Clamp(atlasSize / 2, 512, 1024);

            Bounds rootBounds = new Bounds(positions[0], Vector3.zero);
            for (int i = 1; i < positions.Length; i++)
            {
                rootBounds.Encapsulate(positions[i]);
            }
            Matrix4x4 rootToWorld = root.localToWorldMatrix;
            Vector3 centerWorld = rootToWorld.MultiplyPoint3x4(rootBounds.center);
            float radius = 0.001f;
            Vector3 extents = rootBounds.extents;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 cornerLocal = rootBounds.center + Vector3.Scale(extents, new Vector3(
                    (corner & 1) == 0 ? -1f : 1f,
                    (corner & 2) == 0 ? -1f : 1f,
                    (corner & 4) == 0 ? -1f : 1f));
                radius = Mathf.Max(radius, (rootToWorld.MultiplyPoint3x4(cornerLocal) - centerWorld).magnitude);
            }

            Quaternion rootRotation = root.rotation;
            LightingScope lighting = LightingScope.Push();
            DepthPrimingScope depthPriming = DepthPrimingScope.Push();

            GameObject cameraObject = null;
            GameObject colliderObject = null;
            GameObject maskObject = null;
            Mesh maskMesh = null;
            Material maskMaterial = null;
            RenderTexture bodyTexture = null;
            RenderTexture regionTexture = null;
            RenderTexture maskBodyTexture = null;
            RenderTexture maskRegionTexture = null;
            Texture2D bodyReadback = null;
            Texture2D regionReadback = null;
            Renderer[] targetRenderers = null;
            bool[] targetRendererStates = null;

            BakeResult result = default;

            try
            {
                cameraObject = new GameObject("MeshLodBakeCamera") { hideFlags = HideFlags.HideAndDontSave };
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.orthographic = true;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.cullingMask = ~0;
                camera.allowHDR = false;
                camera.allowMSAA = true;
                camera.useOcclusionCulling = false;
                camera.aspect = 1f;

                bodyReadback = NewReadback(captureSize);
                regionReadback = NewReadback(regionCaptureSize);

                if (!DetectCaptureMode(camera, bodyReadback, captureSize))
                {
                    Debug.LogWarning("[MeshLodAtlasBaker] Primary capture probe failed. Falling back to Camera.Render with standard 1x targets.");
                    sUseRenderRequest = false;
                    sUseMsaaTargets = false;
                }

                bodyTexture = GetCaptureTarget(captureSize, sUseMsaaTargets);
                regionTexture = GetCaptureTarget(regionCaptureSize, sUseMsaaTargets);

                BakeMask mask = options.Mask;
                if (mask.IsValid)
                {
                    Shader maskShader = Shader.Find("Hidden/MeshLodPartId");
                    if (maskShader != null)
                    {
                        maskMesh = new Mesh
                        {
                            hideFlags = HideFlags.HideAndDontSave,
                            indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
                        };
                        maskMesh.SetVertices(mask.Positions);
                        maskMesh.SetColors(mask.Colors);
                        maskMesh.SetTriangles(mask.Indices, 0);
                        maskMesh.RecalculateBounds();
                        maskMaterial = new Material(maskShader) { hideFlags = HideFlags.HideAndDontSave };
                        maskObject = new GameObject("MeshLodBakeMask") { hideFlags = HideFlags.HideAndDontSave };
                        maskObject.transform.SetPositionAndRotation(root.position, root.rotation);
                        maskObject.transform.localScale = root.lossyScale;
                        maskObject.AddComponent<MeshFilter>().sharedMesh = maskMesh;
                        MeshRenderer maskRenderer = maskObject.AddComponent<MeshRenderer>();
                        maskRenderer.sharedMaterial = maskMaterial;
                        maskRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        maskObject.SetActive(false);

                        targetRenderers = root.GetComponentsInChildren<Renderer>(false);
                        targetRendererStates = new bool[targetRenderers.Length];

                        maskBodyTexture = RenderTexture.GetTemporary(captureSize, captureSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                        maskRegionTexture = RenderTexture.GetTemporary(regionCaptureSize, regionCaptureSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    }
                    else
                    {
                        Debug.LogWarning("[MeshLodAtlasBaker] Hidden/MeshLodPartId shader missing — baking without part isolation.");
                    }
                }

                // 1. Capture beauty views
                List<CaptureView> views = new List<CaptureView>(64);
                Vector3[] bodyDirections = BuildBodyViewDirections();
                int staleBodyViews = 0;

                for (int v = 0; v < bodyDirections.Length; v++)
                {
                    Vector3 directionWorld = (rootRotation * bodyDirections[v]).normalized;
                    CaptureView view = CaptureOne(camera, bodyTexture, bodyReadback, captureSize,
                        centerWorld, directionWorld, rootRotation, radius, isRegion: false, default,
                        maskObject, maskBodyTexture, targetRenderers, targetRendererStates, radius, out bool backgroundFresh);
                    if (backgroundFresh)
                    {
                        views.Add(view);
                    }
                    else
                    {
                        staleBodyViews++;
                    }
                }

                if (bodyDirections.Length - staleBodyViews < 6)
                {
                    Debug.LogWarning("[MeshLodAtlasBaker] Freshness check warned on body captures; proceeding with all captured views.");
                    views.Clear();
                    for (int v = 0; v < bodyDirections.Length; v++)
                    {
                        Vector3 directionWorld = (rootRotation * bodyDirections[v]).normalized;
                        CaptureView view = CaptureOne(camera, bodyTexture, bodyReadback, captureSize,
                            centerWorld, directionWorld, rootRotation, radius, isRegion: false, default,
                            maskObject, maskBodyTexture, targetRenderers, targetRendererStates, radius, out _);
                        views.Add(view);
                    }
                }

                // Close-up passes
                if (options.Regions != null)
                {
                    Vector3[] regionDirections =
                    {
                        Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back,
                    };
                    for (int r = 0; r < options.Regions.Length; r++)
                    {
                        Bounds region = options.Regions[r].RootBounds;
                        Vector3 regionCenterWorld = rootToWorld.MultiplyPoint3x4(region.center);
                        float regionRadius = Mathf.Max(rootToWorld.MultiplyVector(region.extents).magnitude, 0.01f);
                        Bounds valid = region;
                        valid.Expand(region.size.magnitude * 0.1f + 0.005f);
                        for (int d = 0; d < regionDirections.Length; d++)
                        {
                            Vector3 directionWorld = (rootRotation * regionDirections[d]).normalized;
                            views.Add(CaptureOne(camera, regionTexture, regionReadback, regionCaptureSize,
                                regionCenterWorld, directionWorld, rootRotation, regionRadius, isRegion: true, valid,
                                maskObject, maskRegionTexture, targetRenderers, targetRendererStates, radius, out _));
                        }
                    }
                }
                camera.targetTexture = null;

                // Setup collider for occlusion queries
                colliderObject = new GameObject("MeshLodBakeCollider") { hideFlags = HideFlags.HideAndDontSave, layer = OcclusionLayer };
                colliderObject.transform.SetPositionAndRotation(root.position, root.rotation);
                colliderObject.transform.localScale = root.lossyScale;
                MeshCollider collider = colliderObject.AddComponent<MeshCollider>();
                collider.sharedMesh = decimatedMesh;
                Physics.SyncTransforms();

                float[] vertexAo = options.ComputeAmbientOcclusion
                    ? ComputeVertexAo(positions, normals, indices, rootToWorld, rootRotation, radius)
                    : null;

                sFlipSampleY = DetectSampleFlip(views, rootToWorld, rootRotation, positions, normals);

                // Project beauty / albedo
                Color32[] albedoPixels = ProjectAtlas(views, rootToWorld, rootRotation, positions, normals, uv, indices, atlasSize, radius, mask.TexelVertexGroup, mask.TexelHidden, vertexAo);
                if (albedoPixels != null)
                {
                    result.AlbedoAtlas = CreateTextureFromPixels(albedoPixels, atlasSize, $"{root.name}_AlbedoAtlas");
                }

                // --- 2. Emission Pass (Optional) ---
                if (options.BakeEmission)
                {
                    MaterialSwapScope emissionSwap = MaterialSwapScope.PushEmission(root);
                    try
                    {
                        if (emissionSwap.HasAnyChannel)
                        {
                            List<CaptureView> emissionViews = new List<CaptureView>(views.Count);
                            for (int i = 0; i < views.Count; i++)
                            {
                                CaptureView src = views[i];
                                Color32[] ep = CaptureChannelPixels(camera, src.IsRegion ? regionTexture : bodyTexture,
                                    src.IsRegion ? regionReadback : bodyReadback, src.Size,
                                    centerWorld, src.DirectionWorld, rootRotation, src.IsRegion ? radius * 0.5f : radius, src.IsRegion);
                                emissionViews.Add(new CaptureView
                                {
                                    DirectionWorld = src.DirectionWorld,
                                    WorldToPixel = src.WorldToPixel,
                                    Pixels = ep,
                                    GroupIds = src.GroupIds,
                                    Depth16 = src.Depth16,
                                    CameraPositionWorld = src.CameraPositionWorld,
                                    DepthNear = src.DepthNear,
                                    DepthFar = src.DepthFar,
                                    DepthToleranceMeters = src.DepthToleranceMeters,
                                    Size = src.Size,
                                    IsRegion = src.IsRegion,
                                    ValidBoundsRoot = src.ValidBoundsRoot
                                });
                            }
                            Color32[] emissionPixels = ProjectAtlas(emissionViews, rootToWorld, rootRotation, positions, normals, uv, indices, atlasSize, radius, mask.TexelVertexGroup, mask.TexelHidden, null);
                            if (emissionPixels != null)
                            {
                                result.EmissionAtlas = CreateTextureFromPixels(emissionPixels, atlasSize, $"{root.name}_EmissionAtlas");
                            }
                        }
                    }
                    finally
                    {
                        emissionSwap.Dispose();
                    }
                }

                // --- 3. Custom Channel Pass (Optional) ---
                if (options.BakeCustomChannel && !string.IsNullOrEmpty(options.CustomChannelProperty))
                {
                    MaterialSwapScope customSwap = MaterialSwapScope.PushCustomProperty(root, options.CustomChannelProperty);
                    try
                    {
                        if (customSwap.HasAnyChannel)
                        {
                            List<CaptureView> customViews = new List<CaptureView>(views.Count);
                            for (int i = 0; i < views.Count; i++)
                            {
                                CaptureView src = views[i];
                                Color32[] cp = CaptureChannelPixels(camera, src.IsRegion ? regionTexture : bodyTexture,
                                    src.IsRegion ? regionReadback : bodyReadback, src.Size,
                                    centerWorld, src.DirectionWorld, rootRotation, src.IsRegion ? radius * 0.5f : radius, src.IsRegion);
                                customViews.Add(new CaptureView
                                {
                                    DirectionWorld = src.DirectionWorld,
                                    WorldToPixel = src.WorldToPixel,
                                    Pixels = cp,
                                    GroupIds = src.GroupIds,
                                    Depth16 = src.Depth16,
                                    CameraPositionWorld = src.CameraPositionWorld,
                                    DepthNear = src.DepthNear,
                                    DepthFar = src.DepthFar,
                                    DepthToleranceMeters = src.DepthToleranceMeters,
                                    Size = src.Size,
                                    IsRegion = src.IsRegion,
                                    ValidBoundsRoot = src.ValidBoundsRoot
                                });
                            }
                            Color32[] customPixels = ProjectAtlas(customViews, rootToWorld, rootRotation, positions, normals, uv, indices, atlasSize, radius, mask.TexelVertexGroup, mask.TexelHidden, null);
                            if (customPixels != null)
                            {
                                result.CustomChannelAtlas = CreateTextureFromPixels(customPixels, atlasSize, $"{root.name}_CustomAtlas");
                            }
                        }
                    }
                    finally
                    {
                        customSwap.Dispose();
                    }
                }

                return result;
            }
            finally
            {
                if (targetRenderers != null && targetRendererStates != null)
                {
                    for (int r = 0; r < targetRenderers.Length; r++)
                    {
                        if (targetRendererStates[r] && targetRenderers[r] != null && !targetRenderers[r].enabled)
                        {
                            targetRenderers[r].enabled = true;
                        }
                    }
                }
                if (maskObject != null) Object.DestroyImmediate(maskObject);
                if (maskMesh != null) Object.DestroyImmediate(maskMesh);
                if (maskMaterial != null) Object.DestroyImmediate(maskMaterial);
                if (maskBodyTexture != null) RenderTexture.ReleaseTemporary(maskBodyTexture);
                if (maskRegionTexture != null) RenderTexture.ReleaseTemporary(maskRegionTexture);
                if (bodyTexture != null) RenderTexture.ReleaseTemporary(bodyTexture);
                if (regionTexture != null) RenderTexture.ReleaseTemporary(regionTexture);
                if (bodyReadback != null) Object.DestroyImmediate(bodyReadback);
                if (regionReadback != null) Object.DestroyImmediate(regionReadback);
                if (cameraObject != null) Object.DestroyImmediate(cameraObject);
                if (colliderObject != null) Object.DestroyImmediate(colliderObject);
                depthPriming.Pop();
                lighting.Pop();
            }
        }

        private static Texture2D CreateTextureFromPixels(Color32[] pixels, int size, string name)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            tex.SetPixels32(pixels);
            tex.Apply(true, false);
            return tex;
        }

        private static CaptureView CaptureOne(
            Camera camera,
            RenderTexture target,
            Texture2D readback,
            int size,
            Vector3 centerWorld,
            Vector3 directionWorld,
            Quaternion rootRotation,
            float frameRadius,
            bool isRegion,
            Bounds validBoundsRoot,
            GameObject maskObject,
            RenderTexture maskTarget,
            Renderer[] avatarRenderers,
            bool[] avatarRendererStates,
            float clearanceRadius,
            out bool backgroundFresh)
        {
            Vector3 up = Mathf.Abs(Vector3.Dot(directionWorld, Vector3.up)) > 0.95f ? rootRotation * Vector3.forward : Vector3.up;
            camera.targetTexture = target;
            camera.orthographicSize = frameRadius * (isRegion ? 1.1f : 1f);

            if (isRegion)
            {
                float cameraDistance = clearanceRadius * 2f + frameRadius;
                camera.nearClipPlane = Mathf.Max(0.01f, cameraDistance - frameRadius * 2.5f);
                camera.farClipPlane = cameraDistance + frameRadius * 2.5f;
                camera.transform.SetPositionAndRotation(centerWorld - directionWorld * cameraDistance, Quaternion.LookRotation(directionWorld, up));
            }
            else
            {
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = frameRadius * 4f;
                camera.transform.SetPositionAndRotation(centerWorld - directionWorld * (frameRadius * 2f), Quaternion.LookRotation(directionWorld, up));
            }

            Color32[] onBlack = RenderAndRead(camera, readback, size, new Color(0f, 0f, 0f, 0f));
            Color32[] onWhite = RenderAndRead(camera, readback, size, new Color(1f, 1f, 1f, 0f));

            backgroundFresh = CornersMatch(onBlack, size, expectDark: true) && CornersMatch(onWhite, size, expectDark: false);
            for (int p = 0; p < onBlack.Length; p++)
            {
                int difference = (Mathf.Abs(onWhite[p].r - onBlack[p].r) + Mathf.Abs(onWhite[p].g - onBlack[p].g) + Mathf.Abs(onWhite[p].b - onBlack[p].b)) / 3;
                int coverage = 255 - difference;
                if (coverage > 6 && coverage < 255)
                {
                    float scale = 255f / coverage;
                    onBlack[p].r = (byte)Mathf.Min(255f, onBlack[p].r * scale);
                    onBlack[p].g = (byte)Mathf.Min(255f, onBlack[p].g * scale);
                    onBlack[p].b = (byte)Mathf.Min(255f, onBlack[p].b * scale);
                }
                onBlack[p].a = (byte)coverage;
            }

            // Part-id + depth pass
            byte[] groupIds = null;
            ushort[] depth16 = null;
            if (maskObject != null && maskTarget != null)
            {
                for (int r = 0; r < avatarRenderers.Length; r++)
                {
                    Renderer ren = avatarRenderers[r];
                    avatarRendererStates[r] = ren != null && ren.enabled;
                    if (avatarRendererStates[r])
                    {
                        ren.enabled = false;
                    }
                }
                maskObject.SetActive(true);
                camera.targetTexture = maskTarget;
                Color32[] maskPixels = RenderAndRead(camera, readback, size, new Color(0f, 0f, 0f, 0f));
                camera.targetTexture = target;
                maskObject.SetActive(false);
                for (int r = 0; r < avatarRenderers.Length; r++)
                {
                    if (avatarRendererStates[r] && avatarRenderers[r] != null)
                    {
                        avatarRenderers[r].enabled = true;
                    }
                }
                groupIds = new byte[maskPixels.Length];
                depth16 = new ushort[maskPixels.Length];
                for (int p = 0; p < maskPixels.Length; p++)
                {
                    Color32 maskPixel = maskPixels[p];
                    groupIds[p] = DecodeGroup(maskPixel.r);
                    float depth01 = maskPixel.g * (1f / 255f) + maskPixel.b * (1f / 65025f);
                    depth16[p] = (ushort)Mathf.Clamp(Mathf.RoundToInt(depth01 * 65535f), 0, 65535);
                }
            }

            Matrix4x4 clip = camera.projectionMatrix * camera.worldToCameraMatrix;
            Matrix4x4 ndcToPixel = Matrix4x4.TRS(new Vector3(size * 0.5f, size * 0.5f, 0f), Quaternion.identity, new Vector3(size * 0.5f, size * 0.5f, 1f));
            return new CaptureView
            {
                DirectionWorld = directionWorld,
                WorldToPixel = ndcToPixel * clip,
                Pixels = onBlack,
                GroupIds = groupIds,
                Depth16 = depth16,
                CameraPositionWorld = camera.transform.position,
                DepthNear = camera.nearClipPlane,
                DepthFar = camera.farClipPlane,
                DepthToleranceMeters = Mathf.Max(0.015f, frameRadius * 0.03f),
                Size = size,
                IsRegion = isRegion,
                ValidBoundsRoot = validBoundsRoot,
            };
        }

        private static Color32[] CaptureChannelPixels(
            Camera camera,
            RenderTexture target,
            Texture2D readback,
            int size,
            Vector3 centerWorld,
            Vector3 directionWorld,
            Quaternion rootRotation,
            float frameRadius,
            bool isRegion)
        {
            Vector3 up = Mathf.Abs(directionWorld.y) > 0.95f ? rootRotation * Vector3.forward : Vector3.up;
            camera.targetTexture = target;
            camera.orthographicSize = frameRadius * (isRegion ? 1.1f : 1f);

            if (isRegion)
            {
                float cameraDistance = frameRadius * 3f;
                camera.nearClipPlane = Mathf.Max(0.01f, cameraDistance - frameRadius * 2.5f);
                camera.farClipPlane = cameraDistance + frameRadius * 2.5f;
                camera.transform.SetPositionAndRotation(centerWorld - directionWorld * cameraDistance, Quaternion.LookRotation(directionWorld, up));
            }
            else
            {
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = frameRadius * 4f;
                camera.transform.SetPositionAndRotation(centerWorld - directionWorld * (frameRadius * 2f), Quaternion.LookRotation(directionWorld, up));
            }

            Color32[] pixels = RenderAndRead(camera, readback, size, Color.black);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i].a = 255;
            }
            return pixels;
        }

        private static Color32[] ProjectAtlas(
            List<CaptureView> views,
            Matrix4x4 rootToWorld,
            Quaternion rootRotation,
            Vector3[] positions,
            Vector3[] normals,
            Vector2[] uv,
            int[] indices,
            int atlasSize,
            float radius,
            byte[] texelGroups,
            byte[] texelHidden,
            float[] vertexAo)
        {
            int texelCount = atlasSize * atlasSize;
            Color32[] atlas = new Color32[texelCount];
            byte[] texelQuality = new byte[texelCount];
            float rayBias = Mathf.Max(0.004f, radius * 0.01f);
            int layerMask = 1 << OcclusionLayer;
            int viewCount = views.Count;
            CaptureView[] viewArray = views.ToArray();

            void ProjectRows(int bandStartY, int bandEndY)
            {
                int[] candidateOrder = new int[viewCount];
                float[] candidateScore = new float[viewCount];

                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    int i0 = indices[t], i1 = indices[t + 1], i2 = indices[t + 2];

                    if (texelHidden != null && texelHidden[i0] != 0 && texelHidden[i1] != 0 && texelHidden[i2] != 0)
                    {
                        continue;
                    }

                    Vector2 uv0 = uv[i0] * atlasSize, uv1 = uv[i1] * atlasSize, uv2 = uv[i2] * atlasSize;

                    float minX = Mathf.Min(uv0.x, Mathf.Min(uv1.x, uv2.x)) - 1f;
                    float maxX = Mathf.Max(uv0.x, Mathf.Max(uv1.x, uv2.x)) + 1f;
                    float minY = Mathf.Min(uv0.y, Mathf.Min(uv1.y, uv2.y)) - 1f;
                    float maxY = Mathf.Max(uv0.y, Mathf.Max(uv1.y, uv2.y)) + 1f;
                    if (Mathf.CeilToInt(maxY) < bandStartY || Mathf.FloorToInt(minY) > bandEndY)
                    {
                        continue;
                    }
                    int startX = Mathf.Clamp(Mathf.FloorToInt(minX), 0, atlasSize - 1);
                    int endX = Mathf.Clamp(Mathf.CeilToInt(maxX), 0, atlasSize - 1);
                    int startY = Mathf.Clamp(Mathf.FloorToInt(minY), bandStartY, bandEndY);
                    int endY = Mathf.Clamp(Mathf.CeilToInt(maxY), bandStartY, bandEndY);

                    Vector2 edge0 = uv1 - uv0;
                    Vector2 edge1 = uv2 - uv0;
                    float denominator = edge0.x * edge1.y - edge0.y * edge1.x;
                    if (Mathf.Abs(denominator) < 1e-8f)
                    {
                        continue;
                    }
                    float inverseDenominator = 1f / denominator;

                    byte allowedGroup0 = 255, allowedGroup1 = 255, allowedGroup2 = 255;
                    if (texelGroups != null)
                    {
                        allowedGroup0 = texelGroups[i0];
                        allowedGroup1 = texelGroups[i1];
                        allowedGroup2 = texelGroups[i2];
                    }

                    for (int y = startY; y <= endY; y++)
                    {
                        for (int x = startX; x <= endX; x++)
                        {
                            Vector2 point = new Vector2(x + 0.5f, y + 0.5f) - uv0;
                            float baryB = (point.x * edge1.y - point.y * edge1.x) * inverseDenominator;
                            float baryC = (edge0.x * point.y - edge0.y * point.x) * inverseDenominator;
                            float baryA = 1f - baryB - baryC;
                            const float slack = -0.08f;
                            if (baryA < slack || baryB < slack || baryC < slack)
                            {
                                continue;
                            }
                            bool interior = baryA >= 0f && baryB >= 0f && baryC >= 0f;
                            int texelIndex = y * atlasSize + x;
                            if (texelQuality[texelIndex] >= (interior ? (byte)2 : (byte)1))
                            {
                                continue;
                            }

                            Vector3 positionRoot = positions[i0] * baryA + positions[i1] * baryB + positions[i2] * baryC;
                            Vector3 normalRoot = normals[i0] * baryA + normals[i1] * baryB + normals[i2] * baryC;
                            Vector3 positionWorld = rootToWorld.MultiplyPoint3x4(positionRoot);
                            Vector3 normalWorld = (rootRotation * normalRoot).normalized;
                            float aoFactor = 1f;
                            if (vertexAo != null)
                            {
                                float ao = Mathf.Clamp01(vertexAo[i0] * baryA + vertexAo[i1] * baryB + vertexAo[i2] * baryC);
                                aoFactor = Mathf.Lerp(1f, ao, 0.45f);
                            }

                            int candidateCount = 0;
                            for (int v = 0; v < viewCount; v++)
                            {
                                ref CaptureView view = ref viewArray[v];
                                if (view.IsRegion && !view.ValidBoundsRoot.Contains(positionRoot))
                                {
                                    continue;
                                }
                                float facing = Vector3.Dot(normalWorld, -view.DirectionWorld);
                                if (facing > MinViewFacing)
                                {
                                    candidateOrder[candidateCount] = v;
                                    candidateScore[candidateCount] = facing + (view.IsRegion && facing > RegionMinFacing ? RegionScoreBias : 0f);
                                    candidateCount++;
                                }
                            }
                            if (candidateCount == 0)
                            {
                                for (int v = 0; v < viewCount; v++)
                                {
                                    ref CaptureView view = ref viewArray[v];
                                    if (view.IsRegion && !view.ValidBoundsRoot.Contains(positionRoot))
                                    {
                                        continue;
                                    }
                                    float facing = Vector3.Dot(normalWorld, -view.DirectionWorld);
                                    if (facing > 0.05f)
                                    {
                                        candidateOrder[candidateCount] = v;
                                        candidateScore[candidateCount] = facing;
                                        candidateCount++;
                                    }
                                }
                            }

                            // Sort candidates descending by score
                            for (int a = 1; a < candidateCount; a++)
                            {
                                int order = candidateOrder[a];
                                float score = candidateScore[a];
                                int b = a - 1;
                                while (b >= 0 && candidateScore[b] < score)
                                {
                                    candidateOrder[b + 1] = candidateOrder[b];
                                    candidateScore[b + 1] = candidateScore[b];
                                    b--;
                                }
                                candidateOrder[b + 1] = order;
                                candidateScore[b + 1] = score;
                            }

                            bool sampled = false;
                            Color32 fallbackColor = default;
                            bool hasFallback = false;

                            int consider = Mathf.Min(candidateCount, 6);
                            for (int c = 0; c < consider && !sampled; c++)
                            {
                                ref CaptureView view = ref viewArray[candidateOrder[c]];
                                if (!TrySampleView(in view, positionWorld, allowedGroup0, allowedGroup1, allowedGroup2, out Color32 color, out float coverage))
                                {
                                    continue;
                                }
                                if (!hasFallback && coverage >= FallbackCoverage)
                                {
                                    fallbackColor = color;
                                    hasFallback = true;
                                }
                                if (coverage < MinCoverage)
                                {
                                    continue;
                                }
                                if (view.Depth16 == null)
                                {
                                    Vector3 towardCamera = -view.DirectionWorld;
                                    Vector3 origin = positionWorld + normalWorld * rayBias + towardCamera * rayBias;
                                    if (Physics.Raycast(origin, towardCamera, radius * 3f, layerMask))
                                    {
                                        continue;
                                    }
                                }
                                atlas[texelIndex] = ApplyAo(color, aoFactor);
                                texelQuality[texelIndex] = interior ? (byte)2 : (byte)1;
                                sampled = true;
                            }

                            if (!sampled && hasFallback)
                            {
                                atlas[texelIndex] = ApplyAo(fallbackColor, aoFactor);
                                texelQuality[texelIndex] = interior ? (byte)2 : (byte)1;
                                sampled = true;
                            }

                            if (!sampled)
                            {
                                for (int c = 0; c < candidateCount && !sampled; c++)
                                {
                                    ref CaptureView rescueView = ref viewArray[candidateOrder[c]];
                                    if (TrySampleView(in rescueView, positionWorld, allowedGroup0, allowedGroup1, allowedGroup2, out Color32 rescueColor, out float rescueCoverage, 3f)
                                        && rescueCoverage >= 0.05f)
                                    {
                                        atlas[texelIndex] = ApplyAo(rescueColor, aoFactor);
                                        texelQuality[texelIndex] = 1;
                                        sampled = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }

            bool anyViewLacksDepth = false;
            for (int v = 0; v < viewCount; v++)
            {
                if (viewArray[v].Depth16 == null)
                {
                    anyViewLacksDepth = true;
                    break;
                }
            }

            if (anyViewLacksDepth)
            {
                ProjectRows(0, atlasSize - 1);
            }
            else
            {
                int bandHeight = Mathf.Max(16, atlasSize / (Mathf.Clamp(SystemInfo.processorCount, 1, 16) * 4));
                int bandCount = (atlasSize + bandHeight - 1) / bandHeight;
                System.Threading.Tasks.Parallel.For(0, bandCount, band =>
                {
                    int bandStart = band * bandHeight;
                    ProjectRows(bandStart, Mathf.Min(atlasSize - 1, bandStart + bandHeight - 1));
                });
            }

            Dilate(atlas, texelQuality, atlasSize);
            return atlas;
        }

        private static bool TrySampleView(
            in CaptureView view,
            Vector3 positionWorld,
            byte allowed0,
            byte allowed1,
            byte allowed2,
            out Color32 color,
            out float coverage,
            float depthToleranceScale = 1f)
        {
            Vector3 pixel = view.WorldToPixel.MultiplyPoint(positionWorld);
            if (sFlipSampleY)
            {
                pixel.y = view.Size - pixel.y;
            }

            bool hasDepth = view.Depth16 != null;
            float expectedDepth16 = 0f;
            float depthTolerance16 = 0f;
            if (hasDepth)
            {
                float depthRange = Mathf.Max(view.DepthFar - view.DepthNear, 1e-4f);
                float viewDepth = Vector3.Dot(positionWorld - view.CameraPositionWorld, view.DirectionWorld);
                expectedDepth16 = Mathf.Clamp01((viewDepth - view.DepthNear) / depthRange) * 65535f;
                depthTolerance16 = view.DepthToleranceMeters * depthToleranceScale / depthRange * 65535f;
            }

            float fx = pixel.x - 0.5f;
            float fy = pixel.y - 0.5f;
            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            if (x0 < -1 || y0 < -1 || x0 >= view.Size || y0 >= view.Size)
            {
                color = default;
                coverage = 0f;
                return false;
            }
            float tx = fx - x0;
            float ty = fy - y0;

            float r = 0f, g = 0f, b = 0f, weightedCoverage = 0f, totalWeight = 0f;
            for (int dy = 0; dy <= 1; dy++)
            {
                int sy = y0 + dy;
                if (sy < 0 || sy >= view.Size) continue;
                float wy = dy == 0 ? 1f - ty : ty;
                for (int dx = 0; dx <= 1; dx++)
                {
                    int sx = x0 + dx;
                    if (sx < 0 || sx >= view.Size) continue;
                    float weight = wy * (dx == 0 ? 1f - tx : tx);
                    if (weight <= 0f) continue;

                    int sampleIndex = sy * view.Size + sx;
                    if (view.GroupIds != null && allowed0 != 255)
                    {
                        byte pixelGroup = view.GroupIds[sampleIndex];
                        if (pixelGroup != allowed0 && pixelGroup != allowed1 && pixelGroup != allowed2)
                        {
                            totalWeight += weight;
                            continue;
                        }
                    }
                    if (hasDepth && Mathf.Abs(view.Depth16[sampleIndex] - expectedDepth16) > depthTolerance16)
                    {
                        totalWeight += weight;
                        continue;
                    }

                    Color32 sample = view.Pixels[sampleIndex];
                    float sampleCoverage = sample.a * (1f / 255f);
                    float colorWeight = weight * sampleCoverage;
                    r += sample.r * colorWeight;
                    g += sample.g * colorWeight;
                    b += sample.b * colorWeight;
                    weightedCoverage += sampleCoverage * weight;
                    totalWeight += weight;
                }
            }

            if (totalWeight <= 0f || weightedCoverage <= 1e-4f)
            {
                color = default;
                coverage = 0f;
                return false;
            }

            float inv = 1f / Mathf.Max(weightedCoverage, 1e-4f);
            color = new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(r * inv), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(g * inv), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(b * inv), 0, 255),
                255);
            coverage = weightedCoverage / totalWeight;
            return true;
        }

        private static void Dilate(Color32[] atlas, byte[] texelQuality, int atlasSize)
        {
            byte[] current = texelQuality;
            for (int pass = 0; pass < DilatePasses; pass++)
            {
                byte[] next = (byte[])current.Clone();
                bool any = false;
                for (int y = 0; y < atlasSize; y++)
                {
                    for (int x = 0; x < atlasSize; x++)
                    {
                        int index = y * atlasSize + x;
                        if (current[index] > 0) continue;

                        int r = 0, g = 0, b = 0, count = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = y + dy;
                            if (ny < 0 || ny >= atlasSize) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx;
                                if (nx < 0 || nx >= atlasSize) continue;
                                int neighbor = ny * atlasSize + nx;
                                if (current[neighbor] == 0) continue;

                                r += atlas[neighbor].r;
                                g += atlas[neighbor].g;
                                b += atlas[neighbor].b;
                                count++;
                            }
                        }
                        if (count > 0)
                        {
                            atlas[index] = new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), 255);
                            next[index] = 1;
                            any = true;
                        }
                    }
                }
                current = next;
                if (!any) break;
            }

            // Flood remaining unwritten texels from nearest neighbor
            Queue<int> frontier = new Queue<int>(4096);
            bool anyWritten = false;
            for (int i = 0; i < atlas.Length; i++)
            {
                if (current[i] > 0)
                {
                    frontier.Enqueue(i);
                    anyWritten = true;
                }
            }
            if (!anyWritten)
            {
                Color32 neutral = new Color32(128, 128, 128, 255);
                for (int i = 0; i < atlas.Length; i++) atlas[i] = neutral;
                return;
            }
            while (frontier.Count > 0)
            {
                int index = frontier.Dequeue();
                int x = index % atlasSize;
                int y = index / atlasSize;
                if (x > 0 && current[index - 1] == 0)
                {
                    atlas[index - 1] = atlas[index];
                    current[index - 1] = 1;
                    frontier.Enqueue(index - 1);
                }
                if (x < atlasSize - 1 && current[index + 1] == 0)
                {
                    atlas[index + 1] = atlas[index];
                    current[index + 1] = 1;
                    frontier.Enqueue(index + 1);
                }
                if (y > 0 && current[index - atlasSize] == 0)
                {
                    atlas[index - atlasSize] = atlas[index];
                    current[index - atlasSize] = 1;
                    frontier.Enqueue(index - atlasSize);
                }
                if (y < atlasSize - 1 && current[index + atlasSize] == 0)
                {
                    atlas[index + atlasSize] = atlas[index];
                    current[index + atlasSize] = 1;
                    frontier.Enqueue(index + atlasSize);
                }
            }
        }

        private static bool DetectSampleFlip(List<CaptureView> views, Matrix4x4 rootToWorld, Quaternion rootRotation, Vector3[] positions, Vector3[] normals)
        {
            int upright = 0;
            int flipped = 0;
            int stride = Mathf.Max(1, positions.Length / 512);
            for (int v = 0; v < views.Count; v++)
            {
                CaptureView view = views[v];
                if (view.IsRegion) continue;
                for (int i = 0; i < positions.Length; i += stride)
                {
                    Vector3 normalWorld = rootRotation * normals[i];
                    if (Vector3.Dot(normalWorld, -view.DirectionWorld) < 0.5f) continue;
                    Vector3 pixel = view.WorldToPixel.MultiplyPoint(rootToWorld.MultiplyPoint3x4(positions[i]));
                    int x = (int)pixel.x;
                    int y = (int)pixel.y;
                    if (x < 0 || y < 0 || x >= view.Size || y >= view.Size) continue;
                    if (view.Pixels[y * view.Size + x].a > 128) upright++;
                    if (view.Pixels[(view.Size - 1 - y) * view.Size + x].a > 128) flipped++;
                }
            }
            return flipped > upright + upright / 2 && flipped > 64;
        }

        private static bool DetectCaptureMode(Camera camera, Texture2D readback, int size)
        {
            int savedMask = camera.cullingMask;
            RenderTexture savedTarget = camera.targetTexture;
            GameObject probe = null;
            Material probeMaterial = null;
            RenderTexture probeTarget = null;
            try
            {
                probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
                probe.hideFlags = HideFlags.HideAndDontSave;
                if (probe.TryGetComponent(out Collider probeCollider))
                {
                    Object.DestroyImmediate(probeCollider);
                }
                Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
                if (unlit == null) unlit = Shader.Find("Unlit/Color");
                if (unlit != null)
                {
                    probeMaterial = new Material(unlit) { hideFlags = HideFlags.HideAndDontSave, color = Color.red };
                    probe.GetComponent<MeshRenderer>().sharedMaterial = probeMaterial;
                }
                probe.transform.position = new Vector3(0f, -8192f, 0f);

                camera.cullingMask = ~0;
                camera.orthographicSize = 1.2f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 10f;
                camera.transform.SetPositionAndRotation(probe.transform.position - Vector3.forward * 3f, Quaternion.identity);

                (bool useRequest, bool useMsaa)[] candidates =
                {
                    (false, true), (false, false), (true, true), (true, false),
                };
                foreach ((bool useRequest, bool useMsaa) in candidates)
                {
                    if (probeTarget != null) RenderTexture.ReleaseTemporary(probeTarget);
                    probeTarget = GetCaptureTarget(size, useMsaa);
                    camera.targetTexture = probeTarget;
                    sUseRenderRequest = useRequest;
                    sUseMsaaTargets = useMsaa;
                    if (CaptureProbeWorks(camera, readback, size))
                    {
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                camera.cullingMask = savedMask;
                camera.targetTexture = savedTarget;
                if (probeTarget != null) RenderTexture.ReleaseTemporary(probeTarget);
                if (probe != null) Object.DestroyImmediate(probe);
                if (probeMaterial != null) Object.DestroyImmediate(probeMaterial);
            }
        }

        private static bool CaptureProbeWorks(Camera camera, Texture2D readback, int size)
        {
            Color32[] onGreen = RenderAndRead(camera, readback, size, new Color(0f, 1f, 0f, 1f));
            Color32 center = onGreen[(size / 2) * size + size / 2];
            Color32 corner = onGreen[(size / 8) * size + size / 8];
            bool geometryRendered = center.r > 180 && center.g < 100 && center.b < 100;
            bool clearedGreen = corner.g > 180 && corner.r < 100 && corner.b < 100;
            if (!geometryRendered || !clearedGreen) return false;

            Color32[] onMagenta = RenderAndRead(camera, readback, size, new Color(1f, 0f, 1f, 1f));
            Color32 cornerMagenta = onMagenta[(size / 8) * size + size / 8];
            return cornerMagenta.r > 180 && cornerMagenta.b > 180 && cornerMagenta.g < 100;
        }

        private static bool CornersMatch(Color32[] pixels, int size, bool expectDark)
        {
            int margin = size / 16;
            int[] xs = { margin, size - 1 - margin };
            int[] ys = { margin, size - 1 - margin };
            int matching = 0;
            foreach (int y in ys)
            {
                foreach (int x in xs)
                {
                    Color32 pixel = pixels[y * size + x];
                    int brightness = (pixel.r + pixel.g + pixel.b) / 3;
                    if (expectDark ? brightness <= 64 : brightness >= 191)
                    {
                        matching++;
                    }
                }
            }
            return matching >= 2;
        }

        private static void SubmitRender(Camera camera)
        {
            if (sUseRenderRequest)
            {
                var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest();
                if (UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request))
                {
                    RenderTexture destination = camera.targetTexture;
                    camera.targetTexture = null;
                    try
                    {
                        request.destination = destination;
                        UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
                    }
                    finally
                    {
                        camera.targetTexture = destination;
                    }
                    return;
                }
            }
            camera.Render();
        }

        private static Color32[] RenderAndRead(Camera camera, Texture2D readback, int captureSize, Color background)
        {
            camera.backgroundColor = background;
            SubmitRender(camera);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = camera.targetTexture;
            readback.ReadPixels(new Rect(0, 0, captureSize, captureSize), 0, 0, false);
            RenderTexture.active = previous;
            return readback.GetPixels32();
        }

        private static float[] ComputeVertexAo(
            Vector3[] positions,
            Vector3[] normals,
            int[] indices,
            Matrix4x4 rootToWorld,
            Quaternion rootRotation,
            float radius)
        {
            const int aoRayCount = 12;
            int layerMask = 1 << OcclusionLayer;
            float rayLength = Mathf.Max(radius * 0.5f, 0.2f);
            float bias = Mathf.Max(0.004f, radius * 0.008f);
            float buriedDistance = bias * 6f;

            Vector3[] hemisphere = new Vector3[aoRayCount];
            for (int i = 0; i < aoRayCount; i++)
            {
                float u = (i + 0.5f) / aoRayCount;
                float phi = i * 2.3999632f;
                float sinTheta = Mathf.Sqrt(u);
                hemisphere[i] = new Vector3(Mathf.Cos(phi) * sinTheta, Mathf.Sin(phi) * sinTheta, Mathf.Sqrt(1f - u));
            }

            float[] ao = new float[positions.Length];
            int vertexCount = positions.Length;
            var commands = new NativeArray<RaycastCommand>(vertexCount * aoRayCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            var hits = new NativeArray<RaycastHit>(vertexCount * aoRayCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            try
            {
                QueryParameters query = new QueryParameters(layerMask, false, QueryTriggerInteraction.UseGlobal, false);
                for (int v = 0; v < vertexCount; v++)
                {
                    Vector3 normalWorld = MeshLodVisibilityCuller.SafeNormalize(rootRotation * normals[v], Vector3.up);
                    Vector3 origin = rootToWorld.MultiplyPoint3x4(positions[v]) + normalWorld * bias;
                    Vector3 tangent = Vector3.Cross(normalWorld, Mathf.Abs(normalWorld.y) < 0.9f ? Vector3.up : Vector3.right);
                    tangent = MeshLodVisibilityCuller.SafeNormalize(tangent, Vector3.right);
                    Vector3 bitangent = MeshLodVisibilityCuller.SafeNormalize(Vector3.Cross(normalWorld, tangent), Vector3.forward);
                    int baseIndex = v * aoRayCount;
                    for (int r = 0; r < aoRayCount; r++)
                    {
                        Vector3 direction = tangent * hemisphere[r].x + bitangent * hemisphere[r].y + normalWorld * hemisphere[r].z;
                        direction = MeshLodVisibilityCuller.SafeNormalize(direction, normalWorld);
                        commands[baseIndex + r] = new RaycastCommand(origin, direction, query, rayLength);
                    }
                }

                RaycastCommand.ScheduleBatch(commands, hits, 64).Complete();

                for (int v = 0; v < vertexCount; v++)
                {
                    int occluded = 0;
                    int nearHits = 0;
                    int baseIndex = v * aoRayCount;
                    for (int r = 0; r < aoRayCount; r++)
                    {
                        RaycastHit hit = hits[baseIndex + r];
                        if (IsRayHit(in hit))
                        {
                            occluded++;
                            if (hit.distance < buriedDistance)
                            {
                                nearHits++;
                            }
                        }
                    }
                    ao[v] = nearHits >= (aoRayCount * 3) / 4 ? 1f : 1f - (occluded / (float)aoRayCount) * 0.9f;
                }
            }
            finally
            {
                commands.Dispose();
                hits.Dispose();
            }

            // Neighbor smoothing passes
            float[] neighborSum = new float[positions.Length];
            int[] neighborCount = new int[positions.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                Array.Clear(neighborSum, 0, neighborSum.Length);
                Array.Clear(neighborCount, 0, neighborCount.Length);
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                    neighborSum[a] += ao[b] + ao[c]; neighborCount[a] += 2;
                    neighborSum[b] += ao[a] + ao[c]; neighborCount[b] += 2;
                    neighborSum[c] += ao[a] + ao[b]; neighborCount[c] += 2;
                }
                for (int v = 0; v < ao.Length; v++)
                {
                    if (neighborCount[v] > 0)
                    {
                        ao[v] = ao[v] * 0.5f + (neighborSum[v] / neighborCount[v]) * 0.5f;
                    }
                }
            }
            return ao;
        }

        private static bool IsRayHit(in RaycastHit hit)
        {
#if UNITY_6000_0_OR_NEWER
            return hit.colliderEntityId != EntityId.None;
#else
            return hit.colliderInstanceID != 0;
#endif
        }

        private static Color32 ApplyAo(Color32 color, float aoFactor)
        {
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.r * aoFactor), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.g * aoFactor), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(color.b * aoFactor), 0, 255),
                255);
        }

        private static Texture2D NewReadback(int size)
        {
            return new Texture2D(size, size, TextureFormat.RGBA32, false, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        private static RenderTexture GetCaptureTarget(int size, bool msaa)
        {
            RenderTextureDescriptor descriptor = new RenderTextureDescriptor(size, size, RenderTextureFormat.ARGB32, 24)
            {
                msaaSamples = msaa ? 4 : 1,
            };
            return RenderTexture.GetTemporary(descriptor);
        }

        private static Vector3[] BuildBodyViewDirections()
        {
            List<Vector3> directions = new List<Vector3>(18);
            for (int yaw = 0; yaw < 360; yaw += 45)
            {
                float radians = yaw * Mathf.Deg2Rad;
                directions.Add(new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians)));
            }
            for (int yaw = 0; yaw < 360; yaw += 90)
            {
                float radians = yaw * Mathf.Deg2Rad;
                directions.Add(new Vector3(Mathf.Sin(radians), 0.84f, Mathf.Cos(radians)).normalized);
            }
            for (int yaw = 45; yaw < 360; yaw += 90)
            {
                float radians = yaw * Mathf.Deg2Rad;
                directions.Add(new Vector3(Mathf.Sin(radians), -0.84f, Mathf.Cos(radians)).normalized);
            }
            directions.Add(Vector3.up);
            directions.Add(Vector3.down);
            return directions.ToArray();
        }

        private struct LightingScope
        {
            private UnityEngine.Rendering.AmbientMode _ambientMode;
            private Color _ambientLight;
            private float _reflectionIntensity;
            private bool _fog;
            private Light[] _disabledLights;

            public static LightingScope Push()
            {
                LightingScope scope = new LightingScope
                {
                    _ambientMode = RenderSettings.ambientMode,
                    _ambientLight = RenderSettings.ambientLight,
                    _reflectionIntensity = RenderSettings.reflectionIntensity,
                    _fog = RenderSettings.fog,
                };

                Light[] lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
                List<Light> disabled = new List<Light>(lights.Length);
                for (int i = 0; i < lights.Length; i++)
                {
                    if (lights[i].enabled)
                    {
                        lights[i].enabled = false;
                        disabled.Add(lights[i]);
                    }
                }
                scope._disabledLights = disabled.ToArray();

                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = Color.white;
                RenderSettings.reflectionIntensity = 0f;
                RenderSettings.fog = false;
                return scope;
            }

            public void Pop()
            {
                RenderSettings.ambientMode = _ambientMode;
                RenderSettings.ambientLight = _ambientLight;
                RenderSettings.reflectionIntensity = _reflectionIntensity;
                RenderSettings.fog = _fog;
                if (_disabledLights != null)
                {
                    for (int i = 0; i < _disabledLights.Length; i++)
                    {
                        if (_disabledLights[i] != null)
                        {
                            _disabledLights[i].enabled = true;
                        }
                    }
                }
            }
        }

        private struct DepthPrimingScope
        {
            private List<(UnityEngine.Object rendererData, System.Reflection.FieldInfo field, int originalMode)> _changed;

            public static DepthPrimingScope Push()
            {
                DepthPrimingScope scope = new DepthPrimingScope
                {
                    _changed = new List<(UnityEngine.Object, System.Reflection.FieldInfo, int)>(),
                };
                try
                {
                    UnityEngine.Rendering.RenderPipelineAsset pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                    if (pipeline == null) return scope;

                    System.Reflection.FieldInfo listField = pipeline.GetType().GetField("m_RendererDataList", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (listField == null || listField.GetValue(pipeline) is not System.Collections.IEnumerable rendererDatas) return scope;

                    foreach (object entry in rendererDatas)
                    {
                        if (entry is not UnityEngine.Object rendererData || rendererData == null) continue;
                        System.Reflection.FieldInfo modeField = rendererData.GetType().GetField("m_DepthPrimingMode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        if (modeField == null) continue;
                        int original = Convert.ToInt32(modeField.GetValue(rendererData));
                        if (original == 0) continue;
                        modeField.SetValue(rendererData, 0);
                        InvokeSetDirty(rendererData);
                        scope._changed.Add((rendererData, modeField, original));
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MeshLodAtlasBaker] Could not adjust depth priming: {ex.Message}");
                }
                return scope;
            }

            public void Pop()
            {
                if (_changed == null) return;
                for (int i = 0; i < _changed.Count; i++)
                {
                    (UnityEngine.Object rendererData, System.Reflection.FieldInfo field, int originalMode) = _changed[i];
                    if (rendererData != null)
                    {
                        field.SetValue(rendererData, originalMode);
                        InvokeSetDirty(rendererData);
                    }
                }
            }

            private static void InvokeSetDirty(UnityEngine.Object rendererData)
            {
                System.Reflection.MethodInfo setDirty = rendererData.GetType().GetMethod("SetDirty", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                setDirty?.Invoke(rendererData, null);
            }
        }

        private sealed class MaterialSwapScope : IDisposable
        {
            private struct SavedRenderer
            {
                public Renderer Renderer;
                public Material[] OriginalMaterials;
            }

            private readonly List<SavedRenderer> _saved = new List<SavedRenderer>();
            private readonly List<Material> _tempMaterials = new List<Material>();
            public bool HasAnyChannel { get; private set; }

            public static MaterialSwapScope PushEmission(Transform root)
            {
                MaterialSwapScope scope = new MaterialSwapScope();
                Shader unlitShader = Shader.Find("Hidden/MeshLodUnlitChannel");
                if (unlitShader == null) unlitShader = Shader.Find("Unlit/Texture");
                if (unlitShader == null) unlitShader = Shader.Find("Unlit/Color");

                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer ren = renderers[r];
                    if (ren == null || !ren.enabled) continue;

                    Material[] mats = ren.sharedMaterials;
                    Material[] replacement = new Material[mats.Length];
                    bool hasActiveMat = false;

                    for (int m = 0; m < mats.Length; m++)
                    {
                        Material orig = mats[m];
                        if (orig == null)
                        {
                            replacement[m] = GetBlackMaterial();
                            continue;
                        }

                        Texture emissionTex = null;
                        Color emissionColor = Color.black;
                        if (orig.HasProperty("_EmissionMap")) emissionTex = orig.GetTexture("_EmissionMap");
                        if (orig.HasProperty("_EmissionColor")) emissionColor = orig.GetColor("_EmissionColor");

                        if (emissionTex != null || emissionColor.maxColorComponent > 0.01f)
                        {
                            Material swapMat = new Material(unlitShader) { hideFlags = HideFlags.HideAndDontSave };
                            if (emissionTex != null)
                            {
                                if (swapMat.HasProperty("_ChannelTex")) swapMat.SetTexture("_ChannelTex", emissionTex);
                                else if (swapMat.HasProperty("_MainTex")) swapMat.SetTexture("_MainTex", emissionTex);
                            }
                            if (swapMat.HasProperty("_ChannelColor")) swapMat.SetColor("_ChannelColor", emissionColor);
                            else if (swapMat.HasProperty("_Color")) swapMat.SetColor("_Color", emissionColor);

                            replacement[m] = swapMat;
                            scope._tempMaterials.Add(swapMat);
                            hasActiveMat = true;
                            scope.HasAnyChannel = true;
                        }
                        else
                        {
                            replacement[m] = GetBlackMaterial();
                        }
                    }

                    if (hasActiveMat)
                    {
                        scope._saved.Add(new SavedRenderer { Renderer = ren, OriginalMaterials = mats });
                        ren.sharedMaterials = replacement;
                    }
                }

                return scope;
            }

            public static MaterialSwapScope PushCustomProperty(Transform root, string propertyName)
            {
                MaterialSwapScope scope = new MaterialSwapScope();
                Shader unlitShader = Shader.Find("Hidden/MeshLodUnlitChannel");
                if (unlitShader == null) unlitShader = Shader.Find("Unlit/Texture");
                if (unlitShader == null) unlitShader = Shader.Find("Unlit/Color");

                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
                for (int r = 0; r < renderers.Length; r++)
                {
                    Renderer ren = renderers[r];
                    if (ren == null || !ren.enabled) continue;

                    Material[] mats = ren.sharedMaterials;
                    Material[] replacement = new Material[mats.Length];
                    bool hasActiveMat = false;

                    for (int m = 0; m < mats.Length; m++)
                    {
                        Material orig = mats[m];
                        if (orig != null && orig.HasProperty(propertyName))
                        {
                            Texture tex = orig.GetTexture(propertyName);
                            if (tex != null)
                            {
                                Material swapMat = new Material(unlitShader) { hideFlags = HideFlags.HideAndDontSave };
                                if (swapMat.HasProperty("_ChannelTex")) swapMat.SetTexture("_ChannelTex", tex);
                                else if (swapMat.HasProperty("_MainTex")) swapMat.SetTexture("_MainTex", tex);
                                if (swapMat.HasProperty("_ChannelColor")) swapMat.SetColor("_ChannelColor", Color.white);

                                replacement[m] = swapMat;
                                scope._tempMaterials.Add(swapMat);
                                hasActiveMat = true;
                                scope.HasAnyChannel = true;
                                continue;
                            }
                        }
                        replacement[m] = GetBlackMaterial();
                    }

                    if (hasActiveMat)
                    {
                        scope._saved.Add(new SavedRenderer { Renderer = ren, OriginalMaterials = mats });
                        ren.sharedMaterials = replacement;
                    }
                }

                return scope;
            }

            private static Material sBlackMaterial;
            private static Material GetBlackMaterial()
            {
                if (sBlackMaterial == null)
                {
                    Shader unlit = Shader.Find("Unlit/Color");
                    if (unlit == null) unlit = Shader.Find("Universal Render Pipeline/Unlit");
                    sBlackMaterial = new Material(unlit) { hideFlags = HideFlags.HideAndDontSave, color = Color.black };
                }
                return sBlackMaterial;
            }

            public void Dispose()
            {
                for (int i = 0; i < _saved.Count; i++)
                {
                    SavedRenderer sr = _saved[i];
                    if (sr.Renderer != null)
                    {
                        sr.Renderer.sharedMaterials = sr.OriginalMaterials;
                    }
                }
                for (int i = 0; i < _tempMaterials.Count; i++)
                {
                    if (_tempMaterials[i] != null) Object.DestroyImmediate(_tempMaterials[i]);
                }
                _saved.Clear();
                _tempMaterials.Clear();
            }
        }
    }
}
