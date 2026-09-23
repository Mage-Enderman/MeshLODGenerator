using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MeshLODGenerator
{
    /// <summary>
    /// Core engine for generating lower-detail LOD versions of any given GameObject (rigged or non-rigged).
    ///
    /// Pipeline:
    /// 1. If Humanoid Avatar: Snapshot pose, park at isolated coords, force T-pose, collapse bones onto CoreBones.
    ///    If Generic Rig / Static Mesh: Snapshot active hierarchy in rest pose.
    /// 2. Apply active blend shapes and collect geometry soup in root space.
    /// 3. Cull exterior-invisible triangles via hemisphere fan (MeshLodVisibilityCuller).
    /// 4. Decimate triangle soup via Quadric Error Metric (QEM) edge collapse with optimal vertex solver (TrySolveOptimal) and normal flip checks.
    /// 5. Generate clean unwrap UVs with Unwrapping.GenerateSecondaryUVSet.
    /// 6. SmoothNormalsAcrossSeams: Average normals across UV-split vertices so low-poly surfaces shade smooth without faceted seams.
    /// 7. RepackChartsByImportance: Shelf-pack UV charts by bone importance (faces, hands, feet receive higher texel resolution).
    /// 8. Bake appearance into unified atlas (Albedo, Emission, Custom channels) via multi-view capture with 16-bit depth testing and parallel projection.
    /// 9. Assemble final Mesh, Rigging (bindposes & bone weights if rigged), and Material.
    /// 10. Restore source hierarchy pose and position.
    /// </summary>
    public static class MeshLodGenerator
    {
        [System.Serializable]
        public class GenerationSettings
        {
            public int TargetTriangleCount = 4000;
            public float TargetTrianglePercentage = 0.5f;
            public bool UsePercentage = false;
            public int AtlasSize = 1024;
            public bool CullHiddenGeometry = true;
            public bool BakeEmission = false;
            public bool BakeCustomChannel = false;
            public string CustomChannelProperty = "_MetallicGlossMap";
            public bool ComputeAmbientOcclusion = true;
            public bool EnforceTPose = false;
            public bool EnableSymmetricDecimation = true;
            public bool SeparateByMaterial = false;
            public float SymmetryTolerance = 0.001f;
            public bool PreserveSubmeshMaterials = false;
            public List<Renderer> IgnoredRenderers = new List<Renderer>();
        }

        public class GenerationReport
        {
            public struct Entry
            {
                public string Label;
                public double Seconds;
                public string Detail;
            }

            public readonly List<Entry> Entries = new List<Entry>();
            public double TotalSeconds;
            public int SourceTriangles;
            public int SourceVertices;
            public int ResultTriangles;
            public int ResultVertices;
            public int RendererCount;
            public bool IsRigged;
            public bool IsHumanoid;
        }

        public class MeshLodResult
        {
            public Mesh LodMesh;
            public Texture2D AlbedoAtlas;
            public Texture2D EmissionAtlas;
            public Texture2D CustomChannelAtlas;
            public Material LodMaterial;
            public Material[] LodMaterials;
            public bool IsRigged;
            public Transform[] Bones;
            public Transform RootBone;
            public GenerationReport Report;
        }

        public sealed class GeometrySoup
        {
            public readonly List<Vector3> Positions = new List<Vector3>(65536);
            public readonly List<byte> BoneA = new List<byte>(65536);
            public readonly List<byte> BoneB = new List<byte>(65536);
            public readonly List<byte> WeightA = new List<byte>(65536);
            public readonly List<Vector2> UVs = new List<Vector2>(65536);
            public readonly List<Vector3> Normals = new List<Vector3>(65536);
            public readonly List<int> Indices = new List<int>(196608);
            public readonly List<int> TriangleMaterials = new List<int>(65536);
            public readonly List<Material> Materials = new List<Material>();
            public readonly List<Transform> Bones = new List<Transform>();
            public Transform RootBone;
            public bool IsRigged;
            public bool IsHumanoid;
        }

        private static readonly HumanBodyBones[] CoreBones =
        {
            HumanBodyBones.Hips,
            HumanBodyBones.Spine,
            HumanBodyBones.Chest,
            HumanBodyBones.UpperChest,
            HumanBodyBones.Neck,
            HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder,
            HumanBodyBones.RightShoulder,
            HumanBodyBones.LeftUpperArm,
            HumanBodyBones.RightUpperArm,
            HumanBodyBones.LeftLowerArm,
            HumanBodyBones.RightLowerArm,
            HumanBodyBones.LeftHand,
            HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg,
            HumanBodyBones.RightUpperLeg,
            HumanBodyBones.LeftLowerLeg,
            HumanBodyBones.RightLowerLeg,
            HumanBodyBones.LeftFoot,
            HumanBodyBones.RightFoot,
        };

        private static HumanBodyBones[] ParentChain(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.Spine: return new[] { HumanBodyBones.Hips };
                case HumanBodyBones.Chest: return new[] { HumanBodyBones.Spine, HumanBodyBones.Hips };
                case HumanBodyBones.UpperChest: return new[] { HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips };
                case HumanBodyBones.Neck:
                case HumanBodyBones.LeftShoulder:
                case HumanBodyBones.RightShoulder:
                    return new[] { HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips };
                case HumanBodyBones.Head: return new[] { HumanBodyBones.Neck, HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips };
                case HumanBodyBones.LeftUpperArm: return new[] { HumanBodyBones.LeftShoulder, HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips };
                case HumanBodyBones.RightUpperArm: return new[] { HumanBodyBones.RightShoulder, HumanBodyBones.UpperChest, HumanBodyBones.Chest, HumanBodyBones.Spine, HumanBodyBones.Hips };
                case HumanBodyBones.LeftLowerArm: return new[] { HumanBodyBones.LeftUpperArm };
                case HumanBodyBones.RightLowerArm: return new[] { HumanBodyBones.RightUpperArm };
                case HumanBodyBones.LeftHand: return new[] { HumanBodyBones.LeftLowerArm };
                case HumanBodyBones.RightHand: return new[] { HumanBodyBones.RightLowerArm };
                case HumanBodyBones.LeftUpperLeg:
                case HumanBodyBones.RightUpperLeg:
                    return new[] { HumanBodyBones.Hips };
                case HumanBodyBones.LeftLowerLeg: return new[] { HumanBodyBones.LeftUpperLeg };
                case HumanBodyBones.RightLowerLeg: return new[] { HumanBodyBones.RightUpperLeg };
                case HumanBodyBones.LeftFoot: return new[] { HumanBodyBones.LeftLowerLeg };
                case HumanBodyBones.RightFoot: return new[] { HumanBodyBones.RightLowerLeg };
                default: return Array.Empty<HumanBodyBones>();
            }
        }

        public sealed class HumanoidSkeleton
        {
            public readonly List<HumanBodyBones> Bones = new List<HumanBodyBones>();
            public readonly List<int> ParentIndex = new List<int>();
            public readonly List<Transform> Transforms = new List<Transform>();
            public readonly Dictionary<Transform, int> TransformToBone = new Dictionary<Transform, int>();
            public int Count => Bones.Count;
        }

        public static MeshLodResult Generate(GameObject target, GenerationSettings settings)
        {
            if (target == null)
            {
                Debug.LogError("[MeshLodGenerator] Target GameObject is null.");
                return null;
            }

            double startTime = EditorApplication.timeSinceStartup;
            GenerationReport report = new GenerationReport();
            Transform root = target.transform;

            Animator animator = target.GetComponentInChildren<Animator>();
            bool isHumanoid = animator != null && animator.avatar != null && animator.avatar.isHuman;
            report.IsHumanoid = isHumanoid;

            TransformPoseSnapshot poseSnapshot = TransformPoseSnapshot.Capture(root);
            RuntimeAnimatorController savedController = animator != null ? animator.runtimeAnimatorController : null;

            try
            {
                // Park model in isolated space during capture to prevent scene clutter interference
                root.position = new Vector3(4096f, 4096f, 4096f);

                HumanoidSkeleton humanoidSkeleton = null;
                if (isHumanoid)
                {
                    if (settings.EnforceTPose)
                    {
                        EditorUtility.DisplayProgressBar("Mesh LOD Generator", "Applying T-Pose...", 0.05f);
                        ApplyTPose(animator);
                    }
                    humanoidSkeleton = CaptureHumanoidSkeleton(animator, root);
                }

                EditorUtility.DisplayProgressBar("Mesh LOD Generator", "Scanning geometry...", 0.15f);
                GeometrySoup soup;
                if (isHumanoid && humanoidSkeleton != null && humanoidSkeleton.Count > 0)
                {
                    soup = SnapshotHumanoidGeometry(animator, root, humanoidSkeleton, settings);
                }
                else
                {
                    soup = SnapshotGenericGeometry(root, settings);
                }

                report.RendererCount = soup.Bones.Count > 0 ? soup.Bones.Count : 1;
                report.IsRigged = soup.IsRigged;
                report.SourceTriangles = soup.Indices.Count / 3;
                report.SourceVertices = soup.Positions.Count;

                if (soup.Indices.Count < 3)
                {
                    Debug.LogWarning("[MeshLodGenerator] No valid triangle geometry found on target.");
                    return null;
                }

                int targetTris = settings.UsePercentage
                    ? Mathf.Max(Mathf.RoundToInt(report.SourceTriangles * settings.TargetTrianglePercentage), 100)
                    : settings.TargetTriangleCount;

                // 2. Visibility Cull (Optional)
                List<byte> hiddenFlags = new List<byte>(soup.Positions.Count);
                if (settings.CullHiddenGeometry)
                {
                    EditorUtility.DisplayProgressBar("Mesh LOD Generator", "Culling occluded geometry...", 0.25f);
                    MeshLodVisibilityCuller.RemoveHiddenTriangles(soup.Positions, soup.Indices, root, targetTris, out byte[] vertexHiddenFlags, soup.TriangleMaterials);
                    if (vertexHiddenFlags != null)
                    {
                        hiddenFlags.AddRange(vertexHiddenFlags);
                    }
                    else
                    {
                        for (int i = 0; i < soup.Positions.Count; i++) hiddenFlags.Add(0);
                    }
                }
                else
                {
                    for (int i = 0; i < soup.Positions.Count; i++) hiddenFlags.Add(0);
                }

                // 3. Build Bake Mask from culled soup before simplification
                MeshLodAtlasBaker.BakeMask bakeMask = BuildBakeMask(soup, humanoidSkeleton);

                // 4. Mesh Simplification via QEM Edge-Collapse with optional bilateral symmetry and per-material boundary locking
                int[][] submeshTris = null;
                if (settings.SeparateByMaterial && soup.Materials.Count > 1)
                {
                    EditorUtility.DisplayProgressBar("Mesh LOD Generator", $"Simplifying mesh by material to {targetTris} triangles...", 0.40f);
                    submeshTris = SimplifyByMaterial(soup, hiddenFlags, targetTris, settings);
                }
                else
                {
                    EditorUtility.DisplayProgressBar("Mesh LOD Generator", $"Simplifying mesh to {targetTris} triangles...", 0.40f);
                    MeshLodMeshSimplifier.Simplify(
                        soup.Positions, soup.BoneA, soup.BoneB, soup.WeightA, hiddenFlags, soup.Indices, targetTris,
                        symmetric: settings.EnableSymmetricDecimation,
                        symmetryTolerance: settings.SymmetryTolerance,
                        lockedVertices: null,
                        uvs: soup.UVs,
                        normals: soup.Normals
                    );
                }

                if (soup.Positions.Count == 0 || soup.Indices.Count < 3)
                {
                    Debug.LogWarning("[MeshLodGenerator] Simplification left no geometry.");
                    return null;
                }

                // If user requested to preserve submesh materials, skip atlas baking and assemble final multi-material mesh immediately
                if (settings.SeparateByMaterial && settings.PreserveSubmeshMaterials && submeshTris != null && submeshTris.Length > 0)
                {
                    EditorUtility.DisplayProgressBar("Mesh LOD Generator", "Finalizing multi-material mesh...", 0.90f);

                    Vector3[] multiPositions = soup.Positions.ToArray();
                    Vector3[] multiNormals = soup.Normals.Count == soup.Positions.Count ? soup.Normals.ToArray() : null;
                    Vector2[] multiUvs = soup.UVs.Count == soup.Positions.Count ? soup.UVs.ToArray() : null;

                    Mesh multiMesh = new Mesh
                    {
                        name = $"{target.name}_LOD",
                        indexFormat = multiPositions.Length > 65534 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16
                    };
                    multiMesh.SetVertices(multiPositions);
                    if (multiNormals != null) multiMesh.SetNormals(multiNormals);
                    else multiMesh.RecalculateNormals();

                    if (multiUvs != null) multiMesh.SetUVs(0, multiUvs);

                    multiMesh.subMeshCount = submeshTris.Length;
                    for (int sm = 0; sm < submeshTris.Length; sm++)
                    {
                        multiMesh.SetTriangles(submeshTris[sm], sm);
                    }

                    if (soup.IsRigged && soup.Bones.Count > 0)
                    {
                        BoneWeight[] boneWeights = new BoneWeight[multiPositions.Length];
                        for (int i = 0; i < multiPositions.Length; i++)
                        {
                            float wA = soup.WeightA[i] / 255f;
                            boneWeights[i] = new BoneWeight
                            {
                                boneIndex0 = soup.BoneA[i],
                                weight0 = wA,
                                boneIndex1 = soup.BoneB[i],
                                weight1 = 1f - wA
                            };
                        }
                        multiMesh.boneWeights = boneWeights;

                        Matrix4x4[] bindposes = new Matrix4x4[soup.Bones.Count];
                        for (int b = 0; b < soup.Bones.Count; b++)
                        {
                            Transform bone = soup.Bones[b];
                            bindposes[b] = bone != null
                                ? bone.worldToLocalMatrix * root.localToWorldMatrix
                                : Matrix4x4.identity;
                        }
                        multiMesh.bindposes = bindposes;
                    }

                    multiMesh.RecalculateBounds();
                    multiMesh.RecalculateTangents();

                    Material[] multiMaterials = soup.Materials.ToArray();
                    Material primaryMat = multiMaterials.Length > 0 ? multiMaterials[0] : null;

                    report.TotalSeconds = EditorApplication.timeSinceStartup - startTime;
                    report.ResultTriangles = soup.Indices.Count / 3;
                    report.ResultVertices = multiPositions.Length;

                    return new MeshLodResult
                    {
                        LodMesh = multiMesh,
                        AlbedoAtlas = null,
                        EmissionAtlas = null,
                        CustomChannelAtlas = null,
                        LodMaterial = primaryMat,
                        LodMaterials = multiMaterials,
                        IsRigged = soup.IsRigged,
                        Bones = soup.Bones.ToArray(),
                        RootBone = soup.RootBone,
                        Report = report
                    };
                }

                // 5. Unwrap Secondary UV Set & Smooth Normals Across Seams
                EditorUtility.DisplayProgressBar("Mesh LOD Generator", "Unwrapping UVs...", 0.55f);
                Mesh unwrappedMesh = BuildUnwrappedMesh(soup, hiddenFlags, settings.AtlasSize,
                    out byte[] finalBoneA, out byte[] finalBoneB, out byte[] finalWeightA, out byte[] texelHidden);

                if (isHumanoid && humanoidSkeleton != null)
                {
                    RepackChartsByImportance(unwrappedMesh, finalBoneA, texelHidden, humanoidSkeleton, settings.AtlasSize);
                }

                // 6. Build Regions of Interest for close-up passes
                MeshLodAtlasBaker.RegionOfInterest[] regions = null;
                if (isHumanoid && humanoidSkeleton != null)
                {
                    regions = BuildCaptureRegions(humanoidSkeleton, unwrappedMesh.vertices, finalBoneA, finalBoneB, finalWeightA);
                    bakeMask.TexelVertexGroup = new byte[finalBoneA.Length];
                    for (int i = 0; i < finalBoneA.Length; i++)
                    {
                        bakeMask.TexelVertexGroup[i] = GroupOfBone(humanoidSkeleton.Bones[finalBoneA[i]]);
                    }
                    bakeMask.TexelHidden = texelHidden;
                }
                else
                {
                    bakeMask.TexelVertexGroup = new byte[finalBoneA.Length];
                    bakeMask.TexelHidden = texelHidden;
                }

                // 7. Bake Appearance Atlas
                EditorUtility.DisplayProgressBar("Mesh LOD Generator", "Baking texture atlas...", 0.70f);
                MeshLodAtlasBaker.BakeOptions bakeOptions = new MeshLodAtlasBaker.BakeOptions
                {
                    AtlasSize = settings.AtlasSize,
                    CaptureSize = settings.AtlasSize,
                    BakeEmission = settings.BakeEmission,
                    BakeCustomChannel = settings.BakeCustomChannel,
                    CustomChannelProperty = settings.CustomChannelProperty,
                    ComputeAmbientOcclusion = settings.ComputeAmbientOcclusion,
                    Regions = regions,
                    Mask = bakeMask
                };

                Vector3[] finalPositions = unwrappedMesh.vertices;
                Vector3[] finalNormals = unwrappedMesh.normals;
                Vector2[] finalUvs = unwrappedMesh.uv;
                int[] finalTriangles = unwrappedMesh.triangles;

                List<Renderer> disabledForBake = new List<Renderer>();
                if (settings.IgnoredRenderers != null)
                {
                    for (int i = 0; i < settings.IgnoredRenderers.Count; i++)
                    {
                        Renderer r = settings.IgnoredRenderers[i];
                        if (r != null && r.enabled)
                        {
                            r.enabled = false;
                            disabledForBake.Add(r);
                        }
                    }
                }

                MeshLodAtlasBaker.BakeResult bakeResult;
                try
                {
                    bakeResult = MeshLodAtlasBaker.Bake(
                        root, unwrappedMesh, finalPositions, finalNormals, finalUvs, finalTriangles, bakeOptions);
                }
                finally
                {
                    for (int i = 0; i < disabledForBake.Count; i++)
                    {
                        if (disabledForBake[i] != null) disabledForBake[i].enabled = true;
                    }
                }

                // 8. Assemble Final Mesh & Material
                EditorUtility.DisplayProgressBar("Mesh LOD Generator", "Finalizing assets...", 0.95f);

                Mesh finalMesh = new Mesh
                {
                    name = $"{target.name}_LOD",
                    indexFormat = finalPositions.Length > 65534 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16
                };
                finalMesh.SetVertices(finalPositions);
                finalMesh.SetNormals(finalNormals); // Preserve seam-smoothed normals!
                finalMesh.SetUVs(0, finalUvs);
                finalMesh.SetTriangles(finalTriangles, 0);

                if (soup.IsRigged && soup.Bones.Count > 0)
                {
                    BoneWeight[] boneWeights = new BoneWeight[finalPositions.Length];
                    for (int i = 0; i < finalPositions.Length; i++)
                    {
                        float wA = finalWeightA[i] / 255f;
                        boneWeights[i] = new BoneWeight
                        {
                            boneIndex0 = finalBoneA[i],
                            weight0 = wA,
                            boneIndex1 = finalBoneB[i],
                            weight1 = 1f - wA
                        };
                    }
                    finalMesh.boneWeights = boneWeights;

                    Matrix4x4[] bindposes = new Matrix4x4[soup.Bones.Count];
                    for (int b = 0; b < soup.Bones.Count; b++)
                    {
                        Transform bone = soup.Bones[b];
                        bindposes[b] = bone != null
                            ? bone.worldToLocalMatrix * root.localToWorldMatrix
                            : Matrix4x4.identity;
                    }
                    finalMesh.bindposes = bindposes;
                }

                finalMesh.RecalculateBounds();
                finalMesh.RecalculateTangents();

                // Material Setup
                Shader previewShader = Shader.Find("MeshLOD/Preview");
                if (previewShader == null) previewShader = Shader.Find("Universal Render Pipeline/Lit");
                if (previewShader == null) previewShader = Shader.Find("Standard");

                Material lodMaterial = new Material(previewShader) { name = $"{target.name}_LOD_Material" };
                if (bakeResult.AlbedoAtlas != null)
                {
                    if (lodMaterial.HasProperty("_BaseMap")) lodMaterial.SetTexture("_BaseMap", bakeResult.AlbedoAtlas);
                    else if (lodMaterial.HasProperty("_MainTex")) lodMaterial.SetTexture("_MainTex", bakeResult.AlbedoAtlas);
                }
                if (bakeResult.EmissionAtlas != null)
                {
                    if (lodMaterial.HasProperty("_EmissionMap"))
                    {
                        lodMaterial.SetTexture("_EmissionMap", bakeResult.EmissionAtlas);
                        lodMaterial.EnableKeyword("_EMISSION");
                    }
                }

                report.TotalSeconds = EditorApplication.timeSinceStartup - startTime;
                report.ResultTriangles = finalTriangles.Length / 3;
                report.ResultVertices = finalPositions.Length;

                Object.DestroyImmediate(unwrappedMesh);

                return new MeshLodResult
                {
                    LodMesh = finalMesh,
                    AlbedoAtlas = bakeResult.AlbedoAtlas,
                    EmissionAtlas = bakeResult.EmissionAtlas,
                    CustomChannelAtlas = bakeResult.CustomChannelAtlas,
                    LodMaterial = lodMaterial,
                    LodMaterials = new Material[] { lodMaterial },
                    IsRigged = soup.IsRigged,
                    Bones = soup.Bones.ToArray(),
                    RootBone = soup.RootBone,
                    Report = report
                };
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (animator != null && savedController != null)
                {
                    animator.runtimeAnimatorController = savedController;
                }
                poseSnapshot.Restore();
            }
        }

        private static HumanoidSkeleton CaptureHumanoidSkeleton(Animator animator, Transform root)
        {
            HumanoidSkeleton skeleton = new HumanoidSkeleton();
            Dictionary<HumanBodyBones, int> boneToIndex = new Dictionary<HumanBodyBones, int>();

            for (int i = 0; i < CoreBones.Length; i++)
            {
                HumanBodyBones bone = CoreBones[i];
                Transform boneTransform = animator.GetBoneTransform(bone);
                if (boneTransform == null) continue;

                int parentIndex = -1;
                Transform ancestor = boneTransform.parent;
                while (ancestor != null)
                {
                    if (skeleton.TransformToBone.TryGetValue(ancestor, out int found))
                    {
                        parentIndex = found;
                        break;
                    }
                    if (ancestor == root) break;
                    ancestor = ancestor.parent;
                }
                if (parentIndex < 0)
                {
                    HumanBodyBones[] chain = ParentChain(bone);
                    for (int c = 0; c < chain.Length; c++)
                    {
                        if (boneToIndex.TryGetValue(chain[c], out int found))
                        {
                            parentIndex = found;
                            break;
                        }
                    }
                }
                if (parentIndex < 0 && bone != HumanBodyBones.Hips)
                {
                    parentIndex = 0;
                }

                int index = skeleton.Count;
                skeleton.Bones.Add(bone);
                skeleton.ParentIndex.Add(parentIndex);
                skeleton.Transforms.Add(boneTransform);
                boneToIndex[bone] = index;
                skeleton.TransformToBone[boneTransform] = index;
            }

            return skeleton;
        }

        private static GeometrySoup SnapshotHumanoidGeometry(Animator animator, Transform root, HumanoidSkeleton skeleton, GenerationSettings settings)
        {
            GeometrySoup soup = new GeometrySoup
            {
                IsRigged = true,
                IsHumanoid = true,
                RootBone = animator.GetBoneTransform(HumanBodyBones.Hips)
            };
            for (int i = 0; i < skeleton.Transforms.Count; i++)
            {
                soup.Bones.Add(skeleton.Transforms[i]);
            }

            Matrix4x4 rootWorldToLocal = root.worldToLocalMatrix;
            Dictionary<Transform, int> ancestorCache = new Dictionary<Transform, int>();
            float[] weightScratch = new float[skeleton.Count];
            int[] touchedScratch = new int[8];

            Renderer[] renderers = animator.GetComponentsInChildren<Renderer>(false);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer ren = renderers[r];
                if (ren == null || !ren.enabled) continue;
                if (settings?.IgnoredRenderers != null && settings.IgnoredRenderers.Contains(ren)) continue;

                if (ren is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                {
                    AppendHumanoidSkinnedMesh(soup, skinned, root, rootWorldToLocal, skeleton, ancestorCache, weightScratch, touchedScratch);
                }
                else if (ren is MeshRenderer meshRenderer)
                {
                    MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null)
                    {
                        AppendHumanoidRigidMesh(soup, filter.sharedMesh, meshRenderer.transform, root, rootWorldToLocal, skeleton, ancestorCache, meshRenderer.sharedMaterials);
                    }
                }
            }

            return soup;
        }

        private static int ResolveAncestorBone(Transform transform, Transform root, HumanoidSkeleton skeleton, Dictionary<Transform, int> cache)
        {
            if (transform == null) return 0;
            if (cache.TryGetValue(transform, out int cached)) return cached;

            int result = 0;
            Transform current = transform;
            while (current != null)
            {
                if (skeleton.TransformToBone.TryGetValue(current, out int boneIndex))
                {
                    result = boneIndex;
                    break;
                }
                if (current == root) break;
                current = current.parent;
            }
            cache[transform] = result;
            return result;
        }

        private static void AppendHumanoidSkinnedMesh(
            GeometrySoup soup,
            SkinnedMeshRenderer skinned,
            Transform root,
            Matrix4x4 rootWorldToLocal,
            HumanoidSkeleton skeleton,
            Dictionary<Transform, int> ancestorCache,
            float[] weightScratch,
            int[] touchedScratch)
        {
            Mesh mesh = skinned.sharedMesh;
            Transform[] bones = skinned.bones;
            if (bones == null || bones.Length == 0)
            {
                AppendHumanoidRigidMesh(soup, mesh, skinned.rootBone != null ? skinned.rootBone : skinned.transform, root, rootWorldToLocal, skeleton, ancestorCache, skinned.sharedMaterials);
                return;
            }

            Vector3[] vertices = mesh.vertices;
            ApplyActiveBlendShapes(skinned, mesh, vertices);
            Vector2[] uvs = mesh.uv;
            Vector3[] normals = mesh.normals;

            Matrix4x4[] bindposes = mesh.bindposes;
            int boneCount = Mathf.Min(bones.Length, bindposes.Length);
            Matrix4x4[] skinMatrices = new Matrix4x4[boneCount];
            int[] boneToFarLodBone = new int[boneCount];
            for (int b = 0; b < boneCount; b++)
            {
                skinMatrices[b] = bones[b] != null ? bones[b].localToWorldMatrix * bindposes[b] : Matrix4x4.identity;
                boneToFarLodBone[b] = ResolveAncestorBone(bones[b] != null ? bones[b] : skinned.transform, root, skeleton, ancestorCache);
            }

            var bonesPerVertex = mesh.GetBonesPerVertex();
            var allWeights = mesh.GetAllBoneWeights();
            if (bonesPerVertex.Length != vertices.Length)
            {
                AppendHumanoidRigidMesh(soup, mesh, skinned.transform, root, rootWorldToLocal, skeleton, ancestorCache, skinned.sharedMaterials);
                return;
            }

            int vertexBase = soup.Positions.Count;
            int weightCursor = 0;
            for (int v = 0; v < vertices.Length; v++)
            {
                int influenceCount = bonesPerVertex[v];
                Vector3 world = Vector3.zero;
                int touchedCount = 0;
                float totalWeight = 0f;
                for (int i = 0; i < influenceCount; i++)
                {
                    BoneWeight1 weight = allWeights[weightCursor++];
                    if (weight.boneIndex < 0 || weight.boneIndex >= boneCount || weight.weight <= 0f) continue;

                    world += skinMatrices[weight.boneIndex].MultiplyPoint3x4(vertices[v]) * weight.weight;
                    totalWeight += weight.weight;

                    int farLodBone = boneToFarLodBone[weight.boneIndex];
                    if (weightScratch[farLodBone] == 0f && touchedCount < touchedScratch.Length)
                    {
                        touchedScratch[touchedCount++] = farLodBone;
                    }
                    weightScratch[farLodBone] += weight.weight;
                }

                if (totalWeight <= 1e-6f)
                {
                    world = skinned.transform.localToWorldMatrix.MultiplyPoint3x4(vertices[v]);
                    weightScratch[boneToFarLodBone.Length > 0 ? boneToFarLodBone[0] : 0] = 1f;
                    if (touchedCount == 0 && touchedScratch.Length > 0)
                    {
                        touchedScratch[touchedCount++] = boneToFarLodBone.Length > 0 ? boneToFarLodBone[0] : 0;
                    }
                }
                else if (totalWeight < 0.999f)
                {
                    world /= totalWeight;
                }

                int bestBone = 0, secondBone = 0;
                float bestWeight = -1f, secondWeight = -1f;
                for (int t = 0; t < touchedCount; t++)
                {
                    int bone = touchedScratch[t];
                    float w = weightScratch[bone];
                    if (w > bestWeight)
                    {
                        secondBone = bestBone; secondWeight = bestWeight;
                        bestBone = bone; bestWeight = w;
                    }
                    else if (w > secondWeight)
                    {
                        secondBone = bone; secondWeight = w;
                    }
                    weightScratch[bone] = 0f;
                }
                if (bestWeight <= 0f)
                {
                    bestBone = 0; bestWeight = 1f; secondBone = 0; secondWeight = 0f;
                }
                if (secondWeight < 0f)
                {
                    secondBone = bestBone; secondWeight = 0f;
                }

                float normalized = bestWeight / (bestWeight + secondWeight);
                soup.Positions.Add(rootWorldToLocal.MultiplyPoint3x4(world));
                soup.BoneA.Add((byte)bestBone);
                soup.BoneB.Add((byte)secondBone);
                soup.WeightA.Add((byte)Mathf.Clamp(Mathf.RoundToInt(normalized * 255f), 0, 255));
                soup.UVs.Add(uvs != null && v < uvs.Length ? uvs[v] : Vector2.zero);
                Vector3 n = (normals != null && v < normals.Length) ? rootWorldToLocal.MultiplyVector(skinned.transform.TransformDirection(normals[v])) : Vector3.up;
                soup.Normals.Add(n.normalized);
            }

            AppendTriangles(soup, mesh, vertexBase, skinned.sharedMaterials);
        }

        private static void AppendHumanoidRigidMesh(
            GeometrySoup soup,
            Mesh mesh,
            Transform meshTransform,
            Transform root,
            Matrix4x4 rootWorldToLocal,
            HumanoidSkeleton skeleton,
            Dictionary<Transform, int> ancestorCache,
            Material[] materials)
        {
            int bone = ResolveAncestorBone(meshTransform, root, skeleton, ancestorCache);
            Matrix4x4 toRoot = rootWorldToLocal * meshTransform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            Vector3[] normals = mesh.normals;
            int vertexBase = soup.Positions.Count;
            for (int v = 0; v < vertices.Length; v++)
            {
                soup.Positions.Add(toRoot.MultiplyPoint3x4(vertices[v]));
                soup.BoneA.Add((byte)bone);
                soup.BoneB.Add((byte)bone);
                soup.WeightA.Add(255);
                soup.UVs.Add(uvs != null && v < uvs.Length ? uvs[v] : Vector2.zero);
                Vector3 n = (normals != null && v < normals.Length) ? toRoot.MultiplyVector(normals[v]) : Vector3.up;
                soup.Normals.Add(n.normalized);
            }
            AppendTriangles(soup, mesh, vertexBase, materials);
        }

        private static GeometrySoup SnapshotGenericGeometry(Transform root, GenerationSettings settings)
        {
            GeometrySoup soup = new GeometrySoup();
            Matrix4x4 rootWorldToLocal = root.worldToLocalMatrix;
            Dictionary<Transform, int> boneToIndex = new Dictionary<Transform, int>();

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(false);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer ren = renderers[r];
                if (ren == null || !ren.enabled) continue;
                if (settings?.IgnoredRenderers != null && settings.IgnoredRenderers.Contains(ren)) continue;

                if (ren is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                {
                    soup.IsRigged = true;
                    if (soup.RootBone == null) soup.RootBone = skinned.rootBone != null ? skinned.rootBone : skinned.transform;
                    AppendGenericSkinnedMesh(soup, skinned, root, rootWorldToLocal, boneToIndex);
                }
                else if (ren is MeshRenderer meshRenderer)
                {
                    MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null)
                    {
                        AppendGenericStaticMesh(soup, filter.sharedMesh, meshRenderer.transform, rootWorldToLocal, meshRenderer.sharedMaterials);
                    }
                }
            }

            return soup;
        }

        private static void AppendGenericSkinnedMesh(
            GeometrySoup soup,
            SkinnedMeshRenderer skinned,
            Transform root,
            Matrix4x4 rootWorldToLocal,
            Dictionary<Transform, int> boneToIndex)
        {
            Mesh mesh = skinned.sharedMesh;
            Transform[] bones = skinned.bones;

            if (bones == null || bones.Length == 0)
            {
                AppendGenericStaticMesh(soup, mesh, skinned.transform, rootWorldToLocal, skinned.sharedMaterials);
                return;
            }

            Vector3[] vertices = mesh.vertices;
            ApplyActiveBlendShapes(skinned, mesh, vertices);
            Vector2[] uvs = mesh.uv;
            Vector3[] normals = mesh.normals;

            Matrix4x4[] bindposes = mesh.bindposes;
            int boneCount = Mathf.Min(bones.Length, bindposes.Length);

            Matrix4x4[] skinMatrices = new Matrix4x4[boneCount];
            int[] boneMap = new int[boneCount];

            for (int b = 0; b < boneCount; b++)
            {
                Transform bone = bones[b] != null ? bones[b] : skinned.transform;
                skinMatrices[b] = bone.localToWorldMatrix * bindposes[b];

                if (!boneToIndex.TryGetValue(bone, out int mappedIdx))
                {
                    mappedIdx = soup.Bones.Count;
                    soup.Bones.Add(bone);
                    boneToIndex[bone] = mappedIdx;
                }
                boneMap[b] = mappedIdx;
            }

            var bonesPerVertex = mesh.GetBonesPerVertex();
            var allWeights = mesh.GetAllBoneWeights();

            int vertexBase = soup.Positions.Count;
            int weightCursor = 0;

            for (int v = 0; v < vertices.Length; v++)
            {
                int influenceCount = bonesPerVertex.Length == vertices.Length ? bonesPerVertex[v] : 0;
                Vector3 world = Vector3.zero;
                float totalWeight = 0f;

                int bestBone = 0, secondBone = 0;
                float bestWeight = 0f, secondWeight = 0f;

                for (int i = 0; i < influenceCount; i++)
                {
                    BoneWeight1 w = allWeights[weightCursor++];
                    if (w.boneIndex < 0 || w.boneIndex >= boneCount || w.weight <= 0f) continue;

                    world += skinMatrices[w.boneIndex].MultiplyPoint3x4(vertices[v]) * w.weight;
                    totalWeight += w.weight;

                    int mappedBone = boneMap[w.boneIndex];
                    if (w.weight > bestWeight)
                    {
                        secondBone = bestBone; secondWeight = bestWeight;
                        bestBone = mappedBone; bestWeight = w.weight;
                    }
                    else if (w.weight > secondWeight)
                    {
                        secondBone = mappedBone; secondWeight = w.weight;
                    }
                }

                if (totalWeight <= 1e-5f)
                {
                    world = skinned.transform.localToWorldMatrix.MultiplyPoint3x4(vertices[v]);
                    bestBone = boneMap.Length > 0 ? boneMap[0] : 0;
                    bestWeight = 1f;
                }
                else
                {
                    world /= totalWeight;
                }

                float normWeight = bestWeight / (bestWeight + secondWeight + 1e-7f);
                soup.Positions.Add(rootWorldToLocal.MultiplyPoint3x4(world));
                soup.BoneA.Add((byte)Mathf.Clamp(bestBone, 0, 255));
                soup.BoneB.Add((byte)Mathf.Clamp(secondBone, 0, 255));
                soup.WeightA.Add((byte)Mathf.Clamp(Mathf.RoundToInt(normWeight * 255f), 0, 255));
                soup.UVs.Add(uvs != null && v < uvs.Length ? uvs[v] : Vector2.zero);
                Vector3 n = (normals != null && v < normals.Length) ? rootWorldToLocal.MultiplyVector(skinned.transform.TransformDirection(normals[v])) : Vector3.up;
                soup.Normals.Add(n.normalized);
            }

            AppendTriangles(soup, mesh, vertexBase, skinned.sharedMaterials);
        }

        private static void AppendGenericStaticMesh(
            GeometrySoup soup,
            Mesh mesh,
            Transform meshTransform,
            Matrix4x4 rootWorldToLocal,
            Material[] materials)
        {
            Matrix4x4 toRoot = rootWorldToLocal * meshTransform.localToWorldMatrix;
            Vector3[] vertices = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            Vector3[] normals = mesh.normals;
            int vertexBase = soup.Positions.Count;

            for (int v = 0; v < vertices.Length; v++)
            {
                soup.Positions.Add(toRoot.MultiplyPoint3x4(vertices[v]));
                soup.BoneA.Add(0);
                soup.BoneB.Add(0);
                soup.WeightA.Add(255);
                soup.UVs.Add(uvs != null && v < uvs.Length ? uvs[v] : Vector2.zero);
                Vector3 n = (normals != null && v < normals.Length) ? toRoot.MultiplyVector(normals[v]) : Vector3.up;
                soup.Normals.Add(n.normalized);
            }

            AppendTriangles(soup, mesh, vertexBase, materials);
        }

        private static void AppendTriangles(GeometrySoup soup, Mesh mesh, int vertexBase, Material[] materials)
        {
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                Material mat = (materials != null && sub < materials.Length) ? materials[sub] : null;
                int matIndex = RegisterMaterial(soup, mat);

                int[] tris = mesh.GetTriangles(sub);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    soup.Indices.Add(vertexBase + tris[i]);
                    soup.Indices.Add(vertexBase + tris[i + 1]);
                    soup.Indices.Add(vertexBase + tris[i + 2]);
                    soup.TriangleMaterials.Add(matIndex);
                }
            }
        }

        private static int RegisterMaterial(GeometrySoup soup, Material mat)
        {
            if (mat == null)
            {
                if (soup.Materials.Count == 0) soup.Materials.Add(null);
                return 0;
            }
            int idx = soup.Materials.IndexOf(mat);
            if (idx < 0)
            {
                idx = soup.Materials.Count;
                soup.Materials.Add(mat);
            }
            return idx;
        }

        private static int[][] SimplifyByMaterial(
            GeometrySoup soup,
            List<byte> hiddenFlags,
            int targetTris,
            GenerationSettings settings)
        {
            int matCount = soup.Materials.Count;
            List<int>[] groupTris = new List<int>[matCount];
            for (int m = 0; m < matCount; m++) groupTris[m] = new List<int>();

            int totalTrisInIndices = soup.Indices.Count / 3;
            for (int t = 0; t < totalTrisInIndices; t++)
            {
                int m = t < soup.TriangleMaterials.Count ? soup.TriangleMaterials[t] : 0;
                if (m >= 0 && m < matCount)
                {
                    groupTris[m].Add(soup.Indices[t * 3]);
                    groupTris[m].Add(soup.Indices[t * 3 + 1]);
                    groupTris[m].Add(soup.Indices[t * 3 + 2]);
                }
            }

            int vCount = soup.Positions.Count;
            int[] vertexMat = new int[vCount];
            for (int i = 0; i < vCount; i++) vertexMat[i] = -1;
            bool[] isBoundary = new bool[vCount];

            for (int m = 0; m < matCount; m++)
            {
                var tris = groupTris[m];
                for (int i = 0; i < tris.Count; i++)
                {
                    int v = tris[i];
                    if (vertexMat[v] == -1)
                    {
                        vertexMat[v] = m;
                    }
                    else if (vertexMat[v] != m)
                    {
                        isBoundary[v] = true;
                    }
                }
            }

            int totalSourceTris = Mathf.Max(soup.Indices.Count / 3, 1);

            List<Vector3>[] simpPos = new List<Vector3>[matCount];
            List<byte>[] simpBoneA = new List<byte>[matCount];
            List<byte>[] simpBoneB = new List<byte>[matCount];
            List<byte>[] simpWeightA = new List<byte>[matCount];
            List<byte>[] simpHidden = new List<byte>[matCount];
            List<Vector2>[] simpUVs = new List<Vector2>[matCount];
            List<Vector3>[] simpNormals = new List<Vector3>[matCount];
            List<int>[] simpIndices = new List<int>[matCount];
            List<bool>[] simpLocked = new List<bool>[matCount];

            for (int m = 0; m < matCount; m++)
            {
                if (groupTris[m].Count < 3) continue;

                float fraction = (float)(groupTris[m].Count / 3) / totalSourceTris;
                int groupTargetTris = Mathf.Max(Mathf.RoundToInt(targetTris * fraction), 12);

                Dictionary<int, int> soupToSub = new Dictionary<int, int>();
                List<Vector3> subPos = new List<Vector3>();
                List<byte> subBoneA = new List<byte>();
                List<byte> subBoneB = new List<byte>();
                List<byte> subWeightA = new List<byte>();
                List<byte> subHidden = new List<byte>();
                List<Vector2> subUVs = new List<Vector2>();
                List<Vector3> subNormals = new List<Vector3>();
                List<bool> subLocked = new List<bool>();
                List<int> subIndices = new List<int>(groupTris[m].Count);

                for (int i = 0; i < groupTris[m].Count; i++)
                {
                    int sIdx = groupTris[m][i];
                    if (!soupToSub.TryGetValue(sIdx, out int subIdx))
                    {
                        subIdx = subPos.Count;
                        soupToSub[sIdx] = subIdx;
                        subPos.Add(soup.Positions[sIdx]);
                        subBoneA.Add(soup.BoneA[sIdx]);
                        subBoneB.Add(soup.BoneB[sIdx]);
                        subWeightA.Add(soup.WeightA[sIdx]);
                        subHidden.Add(hiddenFlags.Count > sIdx ? hiddenFlags[sIdx] : (byte)0);
                        if (soup.UVs.Count > sIdx) subUVs.Add(soup.UVs[sIdx]);
                        if (soup.Normals.Count > sIdx) subNormals.Add(soup.Normals[sIdx]);
                        subLocked.Add(isBoundary[sIdx]);
                    }
                    subIndices.Add(subIdx);
                }

                bool groupSym = false;
                if (settings.EnableSymmetricDecimation)
                {
                    MeshLodMeshSimplifier.FindSymmetryPairs(subPos, settings.SymmetryTolerance, out float symRatio);
                    groupSym = symRatio >= 0.70f;
                }

                MeshLodMeshSimplifier.Simplify(
                    subPos, subBoneA, subBoneB, subWeightA, subHidden, subIndices, groupTargetTris,
                    symmetric: groupSym,
                    symmetryTolerance: settings.SymmetryTolerance,
                    lockedVertices: subLocked.ToArray(),
                    uvs: subUVs,
                    normals: subNormals
                );

                simpPos[m] = subPos;
                simpBoneA[m] = subBoneA;
                simpBoneB[m] = subBoneB;
                simpWeightA[m] = subWeightA;
                simpHidden[m] = subHidden;
                simpUVs[m] = subUVs;
                simpNormals[m] = subNormals;
                simpIndices[m] = subIndices;
                simpLocked[m] = subLocked;
            }

            // Re-merging all groups
            soup.Positions.Clear();
            soup.BoneA.Clear();
            soup.BoneB.Clear();
            soup.WeightA.Clear();
            soup.Indices.Clear();
            soup.TriangleMaterials.Clear();
            soup.UVs.Clear();
            soup.Normals.Clear();
            hiddenFlags.Clear();

            List<int[]> submeshTrisList = new List<int[]>();
            float weldDist = Mathf.Max(settings.SymmetryTolerance * 0.5f, 1e-5f);
            float invWeldDist = 1f / weldDist;
            Dictionary<Vector3Int, int> boundaryWeld = new Dictionary<Vector3Int, int>();

            for (int m = 0; m < matCount; m++)
            {
                if (simpIndices[m] == null || simpIndices[m].Count == 0)
                {
                    submeshTrisList.Add(System.Array.Empty<int>());
                    continue;
                }

                var posList = simpPos[m];
                var indList = simpIndices[m];
                var bAList = simpBoneA[m];
                var bBList = simpBoneB[m];
                var wAList = simpWeightA[m];
                var hidList = simpHidden[m];
                var uvList = simpUVs[m];
                var normList = simpNormals[m];
                var lockList = simpLocked[m];

                int[] groupToMerged = new int[posList.Count];
                for (int i = 0; i < posList.Count; i++)
                {
                    Vector3 p = posList[i];
                    bool wasLocked = lockList != null && i < lockList.Count && lockList[i];
                    int mergedIdx = -1;

                    if (wasLocked)
                    {
                        Vector3Int key = new Vector3Int(
                            Mathf.RoundToInt(p.x * invWeldDist),
                            Mathf.RoundToInt(p.y * invWeldDist),
                            Mathf.RoundToInt(p.z * invWeldDist)
                        );
                        if (!boundaryWeld.TryGetValue(key, out mergedIdx))
                        {
                            mergedIdx = soup.Positions.Count;
                            boundaryWeld[key] = mergedIdx;
                            soup.Positions.Add(p);
                            soup.BoneA.Add(bAList[i]);
                            soup.BoneB.Add(bBList[i]);
                            soup.WeightA.Add(wAList[i]);
                            hiddenFlags.Add(hidList[i]);
                            if (uvList != null && i < uvList.Count) soup.UVs.Add(uvList[i]);
                            if (normList != null && i < normList.Count) soup.Normals.Add(normList[i]);
                        }
                    }
                    else
                    {
                        mergedIdx = soup.Positions.Count;
                        soup.Positions.Add(p);
                        soup.BoneA.Add(bAList[i]);
                        soup.BoneB.Add(bBList[i]);
                        soup.WeightA.Add(wAList[i]);
                        hiddenFlags.Add(hidList[i]);
                        if (uvList != null && i < uvList.Count) soup.UVs.Add(uvList[i]);
                        if (normList != null && i < normList.Count) soup.Normals.Add(normList[i]);
                    }
                    groupToMerged[i] = mergedIdx;
                }

                int[] thisSubmeshTris = new int[indList.Count];
                for (int i = 0; i < indList.Count; i += 3)
                {
                    int i0 = groupToMerged[indList[i]];
                    int i1 = groupToMerged[indList[i + 1]];
                    int i2 = groupToMerged[indList[i + 2]];

                    thisSubmeshTris[i] = i0;
                    thisSubmeshTris[i + 1] = i1;
                    thisSubmeshTris[i + 2] = i2;

                    soup.Indices.Add(i0);
                    soup.Indices.Add(i1);
                    soup.Indices.Add(i2);
                    soup.TriangleMaterials.Add(m);
                }
                submeshTrisList.Add(thisSubmeshTris);
            }

            return submeshTrisList.ToArray();
        }

        private static void ApplyActiveBlendShapes(SkinnedMeshRenderer skinned, Mesh mesh, Vector3[] vertices)
        {
            int shapeCount = mesh.blendShapeCount;
            if (shapeCount == 0) return;

            Vector3[] deltaScratch = null;
            for (int s = 0; s < shapeCount; s++)
            {
                float weight = skinned.GetBlendShapeWeight(s);
                if (Mathf.Abs(weight) < 0.001f) continue;

                deltaScratch ??= new Vector3[vertices.Length];
                int frame = mesh.GetBlendShapeFrameCount(s) - 1;
                mesh.GetBlendShapeFrameVertices(s, frame, deltaScratch, null, null);
                float frameWeight = mesh.GetBlendShapeFrameWeight(s, frame);
                float amount = frameWeight > 0f ? weight / frameWeight : weight * 0.01f;
                for (int v = 0; v < vertices.Length; v++)
                {
                    vertices[v] += deltaScratch[v] * amount;
                }
            }
        }

        public static byte GroupOfBone(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.Head: return 1;
                case HumanBodyBones.LeftShoulder:
                case HumanBodyBones.LeftUpperArm:
                case HumanBodyBones.LeftLowerArm:
                case HumanBodyBones.LeftHand:
                    return 2;
                case HumanBodyBones.RightShoulder:
                case HumanBodyBones.RightUpperArm:
                case HumanBodyBones.RightLowerArm:
                case HumanBodyBones.RightHand:
                    return 3;
                case HumanBodyBones.LeftUpperLeg:
                case HumanBodyBones.LeftLowerLeg:
                case HumanBodyBones.LeftFoot:
                    return 4;
                case HumanBodyBones.RightUpperLeg:
                case HumanBodyBones.RightLowerLeg:
                case HumanBodyBones.RightFoot:
                    return 5;
                default: return 0; // Torso
            }
        }

        private static MeshLodAtlasBaker.BakeMask BuildBakeMask(GeometrySoup soup, HumanoidSkeleton skeleton)
        {
            int vertexCount = soup.Positions.Count;
            MeshLodAtlasBaker.BakeMask mask = new MeshLodAtlasBaker.BakeMask
            {
                Positions = soup.Positions.ToArray(),
                Indices = soup.Indices.ToArray(),
                Colors = new Color32[vertexCount],
            };

            for (int i = 0; i < vertexCount; i++)
            {
                byte group = 0;
                if (skeleton != null && soup.BoneA[i] < skeleton.Bones.Count)
                {
                    group = GroupOfBone(skeleton.Bones[soup.BoneA[i]]);
                }
                byte encoded = MeshLodAtlasBaker.EncodeGroup(group);
                mask.Colors[i] = new Color32(encoded, encoded, encoded, 255);
            }
            return mask;
        }

        private static MeshLodAtlasBaker.RegionOfInterest[] BuildCaptureRegions(
            HumanoidSkeleton skeleton,
            Vector3[] positions,
            byte[] boneA,
            byte[] boneB,
            byte[] weightA)
        {
            HumanBodyBones[] targets =
            {
                HumanBodyBones.LeftHand, HumanBodyBones.RightHand,
                HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot,
                HumanBodyBones.Head,
            };
            List<MeshLodAtlasBaker.RegionOfInterest> regions = new List<MeshLodAtlasBaker.RegionOfInterest>(targets.Length);
            for (int t = 0; t < targets.Length; t++)
            {
                int boneIndex = skeleton.Bones.IndexOf(targets[t]);
                if (boneIndex < 0) continue;

                bool hasBounds = false;
                Bounds bounds = default;
                for (int i = 0; i < positions.Length; i++)
                {
                    bool influencedByA = boneA[i] == boneIndex && weightA[i] > 32;
                    bool influencedByB = boneB[i] == boneIndex && weightA[i] < 223;
                    if (!influencedByA && !influencedByB) continue;

                    if (!hasBounds)
                    {
                        bounds = new Bounds(positions[i], Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(positions[i]);
                    }
                }
                if (hasBounds && bounds.size.magnitude > 0.01f)
                {
                    regions.Add(new MeshLodAtlasBaker.RegionOfInterest
                    {
                        Name = targets[t].ToString(),
                        RootBounds = bounds,
                    });
                }
            }
            return regions.ToArray();
        }

        private static Mesh BuildUnwrappedMesh(
            GeometrySoup soup,
            List<byte> hiddenFlags,
            int atlasSize,
            out byte[] boneA,
            out byte[] boneB,
            out byte[] weightA,
            out byte[] hidden)
        {
            Mesh mesh = new Mesh
            {
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = soup.Positions.Count > 65534 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16
            };
            mesh.SetVertices(soup.Positions);
            mesh.SetTriangles(soup.Indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            Dictionary<Vector3, int> posLookup = new Dictionary<Vector3, int>(soup.Positions.Count);
            for (int i = 0; i < soup.Positions.Count; i++)
            {
                posLookup[soup.Positions[i]] = i;
            }

            UnwrapParam.SetDefaults(out UnwrapParam param);
            param.hardAngle = 180f;
            param.angleError = 0.25f;
            param.areaError = 0.35f;
            param.packMargin = 4f / atlasSize;
            Unwrapping.GenerateSecondaryUVSet(mesh, param);

            Vector3[] vertices = mesh.vertices;
            Vector2[] uv2 = mesh.uv2;

            boneA = new byte[vertices.Length];
            boneB = new byte[vertices.Length];
            weightA = new byte[vertices.Length];
            hidden = new byte[vertices.Length];

            for (int i = 0; i < vertices.Length; i++)
            {
                int src = posLookup.TryGetValue(vertices[i], out int idx) ? idx : Mathf.Min(i, soup.Positions.Count - 1);
                boneA[i] = soup.BoneA[src];
                boneB[i] = soup.BoneB[src];
                weightA[i] = soup.WeightA[src];
                hidden[i] = hiddenFlags != null && src < hiddenFlags.Count ? hiddenFlags[src] : (byte)0;
            }

            mesh.uv = uv2;
            mesh.RecalculateNormals();
            SmoothNormalsAcrossSeams(mesh);
            return mesh;
        }

        private static void SmoothNormalsAcrossSeams(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Dictionary<Vector3, Vector3> accumulated = new Dictionary<Vector3, Vector3>(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                accumulated.TryGetValue(vertices[i], out Vector3 sum);
                accumulated[vertices[i]] = sum + normals[i];
            }
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 smoothed = accumulated[vertices[i]];
                float magnitude = smoothed.magnitude;
                if (magnitude > 1e-6f)
                {
                    normals[i] = smoothed / magnitude;
                }
            }
            mesh.normals = normals;
        }

        private static float ImportanceOfBone(HumanBodyBones bone)
        {
            switch (bone)
            {
                case HumanBodyBones.Head: return 2f;
                case HumanBodyBones.LeftHand:
                case HumanBodyBones.RightHand:
                    return 1.7f;
                case HumanBodyBones.LeftFoot:
                case HumanBodyBones.RightFoot:
                    return 1.4f;
                default: return 1f;
            }
        }

        private static void RepackChartsByImportance(
            Mesh mesh,
            byte[] boneA,
            byte[] hidden,
            HumanoidSkeleton skeleton,
            int atlasSize)
        {
            Vector2[] uv = mesh.uv;
            int[] triangles = mesh.triangles;
            int vertexCount = uv.Length;
            if (vertexCount == 0 || triangles.Length == 0) return;

            int[] parent = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) parent[i] = i;

            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int a = Find(triangles[t]);
                int b = Find(triangles[t + 1]);
                int c = Find(triangles[t + 2]);
                if (b != a) parent[b] = a;
                if (c != a) parent[c] = a;
            }

            Dictionary<int, ChartIsland> islands = new Dictionary<int, ChartIsland>(64);
            for (int i = 0; i < vertexCount; i++)
            {
                int root = Find(i);
                if (!islands.TryGetValue(root, out ChartIsland island))
                {
                    island = new ChartIsland { Min = uv[i], Max = uv[i], Importance = 1f, AllHidden = true };
                    islands[root] = island;
                }
                island.Min = Vector2.Min(island.Min, uv[i]);
                island.Max = Vector2.Max(island.Max, uv[i]);
                if (skeleton != null && boneA[i] < skeleton.Bones.Count)
                {
                    island.Importance = Mathf.Max(island.Importance, ImportanceOfBone(skeleton.Bones[boneA[i]]));
                }
                island.AllHidden &= hidden != null && hidden[i] != 0;
                island.Vertices.Add(i);
            }

            List<ChartIsland> sorted = new List<ChartIsland>(islands.Values);
            for (int s = 0; s < sorted.Count; s++)
            {
                if (sorted[s].AllHidden)
                {
                    sorted[s].Importance *= 0.5f;
                }
            }
            float margin = 4f / atlasSize;

            float scaledArea = 0f;
            foreach (ChartIsland island in sorted)
            {
                Vector2 size = island.Max - island.Min;
                scaledArea += (size.x * island.Importance + margin * 2f) * (size.y * island.Importance + margin * 2f);
            }
            float globalScale = Mathf.Min(1.5f, Mathf.Sqrt(0.82f / Mathf.Max(scaledArea, 1e-6f)));
            for (int attempt = 0; attempt < 48; attempt++)
            {
                if (TryShelfPack(sorted, globalScale, margin))
                {
                    for (int s = 0; s < sorted.Count; s++)
                    {
                        ChartIsland island = sorted[s];
                        float scale = island.Importance * globalScale;
                        for (int v = 0; v < island.Vertices.Count; v++)
                        {
                            int index = island.Vertices[v];
                            uv[index] = island.PackedOrigin + (uv[index] - island.Min) * scale;
                        }
                    }
                    mesh.uv = uv;
                    return;
                }
                globalScale *= 0.93f;
            }
        }

        private sealed class ChartIsland
        {
            public Vector2 Min;
            public Vector2 Max;
            public float Importance;
            public bool AllHidden;
            public Vector2 PackedOrigin;
            public readonly List<int> Vertices = new List<int>(64);
        }

        private static bool TryShelfPack(List<ChartIsland> islands, float globalScale, float margin)
        {
            islands.Sort((a, b) =>
            {
                float heightA = (a.Max.y - a.Min.y) * a.Importance;
                float heightB = (b.Max.y - b.Min.y) * b.Importance;
                return heightB.CompareTo(heightA);
            });

            float cursorX = 0f;
            float cursorY = 0f;
            float shelfHeight = 0f;
            for (int i = 0; i < islands.Count; i++)
            {
                ChartIsland island = islands[i];
                float scale = island.Importance * globalScale;
                float width = (island.Max.x - island.Min.x) * scale + margin * 2f;
                float height = (island.Max.y - island.Min.y) * scale + margin * 2f;
                if (width > 1f || height > 1f) return false;

                if (cursorX + width > 1f)
                {
                    cursorY += shelfHeight;
                    cursorX = 0f;
                    shelfHeight = 0f;
                }
                if (cursorY + height > 1f) return false;

                island.PackedOrigin = new Vector2(cursorX + margin, cursorY + margin);
                cursorX += width;
                shelfHeight = Mathf.Max(shelfHeight, height);
            }
            return true;
        }

        public static void ApplyTPose(Animator animator)
        {
            if (animator == null) return;

            // 1. Prioritize BasisTPose.anim via clean in-memory controller (cross-version compatible)
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/MeshLODGenerator/Animations/BasisTPose.anim");
            if (clip == null)
            {
                string[] clipGuids = AssetDatabase.FindAssets("BasisTPose t:AnimationClip");
                for (int i = 0; i < clipGuids.Length; i++)
                {
                    clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(AssetDatabase.GUIDToAssetPath(clipGuids[i]));
                    if (clip != null) break;
                }
            }

            if (clip != null)
            {
                var dynamicController = new UnityEditor.Animations.AnimatorController { name = "TempMeshLodTPose" };
                var layer = new UnityEditor.Animations.AnimatorControllerLayer
                {
                    name = "Base Layer",
                    defaultWeight = 1f,
                    stateMachine = new UnityEditor.Animations.AnimatorStateMachine { name = "Base Layer", hideFlags = HideFlags.HideAndDontSave }
                };
                var state = layer.stateMachine.AddState("TPOSE");
                state.motion = clip;
                layer.stateMachine.defaultState = state;
                dynamicController.AddLayer(layer);

                EvaluateTPoseController(animator, dynamicController);
                return;
            }

            // 2. Search for any controller named "Animated TPose" in project
            string[] guids = AssetDatabase.FindAssets("\"Animated TPose\" t:RuntimeAnimatorController");
            for (int i = 0; i < guids.Length; i++)
            {
                RuntimeAnimatorController candidate = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (candidate != null)
                {
                    EvaluateTPoseController(animator, candidate);
                    return;
                }
            }

            // 3. Fallback: preserve authored scene pose
            Debug.LogWarning("[MeshLodGenerator] No T-Pose controller or clip found. Preserving avatar in its current authored pose.");
        }

        private static void EvaluateTPoseController(Animator animator, RuntimeAnimatorController controller)
        {
            animator.runtimeAnimatorController = controller;
            animator.Rebind();
            if (animator.layerCount > 0)
            {
                animator.SetLayerWeight(0, 1f);
            }
            animator.Play("TPOSE", 0, 0f);
            animator.Update(0f);
            animator.Update(0.02f);
        }

        public static GameObject SpawnLodInstance(GameObject source, MeshLodResult result, Vector3 offset)
        {
            if (result == null || result.LodMesh == null) return null;

            GameObject lodObj = new GameObject($"{source.name}_LOD1");
            lodObj.transform.SetPositionAndRotation(source.transform.position + offset, source.transform.rotation);
            lodObj.transform.localScale = source.transform.localScale;

            Material[] mats = (result.LodMaterials != null && result.LodMaterials.Length > 0)
                ? result.LodMaterials
                : (result.LodMaterial != null ? new Material[] { result.LodMaterial } : System.Array.Empty<Material>());

            if (result.IsRigged && result.Bones != null && result.Bones.Length > 0)
            {
                SkinnedMeshRenderer smr = lodObj.AddComponent<SkinnedMeshRenderer>();
                smr.sharedMesh = result.LodMesh;
                smr.sharedMaterials = mats;
                smr.bones = result.Bones;
                smr.rootBone = result.RootBone != null ? result.RootBone : lodObj.transform;
            }
            else
            {
                MeshFilter filter = lodObj.AddComponent<MeshFilter>();
                filter.sharedMesh = result.LodMesh;
                MeshRenderer mr = lodObj.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mats;
            }

            Undo.RegisterCreatedObjectUndo(lodObj, "Spawn Mesh LOD");
            Selection.activeGameObject = lodObj;
            return lodObj;
        }

        private sealed class TransformPoseSnapshot
        {
            private Transform[] _transforms;
            private Vector3[] _localPositions;
            private Quaternion[] _localRotations;
            private Vector3[] _localScales;

            public static TransformPoseSnapshot Capture(Transform root)
            {
                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                TransformPoseSnapshot snapshot = new TransformPoseSnapshot
                {
                    _transforms = transforms,
                    _localPositions = new Vector3[transforms.Length],
                    _localRotations = new Quaternion[transforms.Length],
                    _localScales = new Vector3[transforms.Length],
                };
                for (int i = 0; i < transforms.Length; i++)
                {
                    transforms[i].GetLocalPositionAndRotation(out snapshot._localPositions[i], out snapshot._localRotations[i]);
                    snapshot._localScales[i] = transforms[i].localScale;
                }
                return snapshot;
            }

            public void Restore()
            {
                for (int i = 0; i < _transforms.Length; i++)
                {
                    Transform transform = _transforms[i];
                    if (transform == null) continue;
                    transform.SetLocalPositionAndRotation(_localPositions[i], _localRotations[i]);
                    transform.localScale = _localScales[i];
                }
            }
        }
    }
}
