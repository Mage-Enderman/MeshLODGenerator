using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace MeshLODGenerator
{
    /// <summary>
    /// Exterior-visibility culling for mesh LOD: drops triangles that cannot be seen from any
    /// outside direction (inner surfaces, hidden linings, cavities) BEFORE decimation, so the
    /// triangle budget is focused on visible silhouette and exterior surfaces.
    /// Compatible with Unity 2022.3 LTS and Unity 6+.
    /// </summary>
    public static class MeshLodVisibilityCuller
    {
        private const int OcclusionLayer = 2; // Ignore Raycast: isolated physics layer
        private const float SkipBudgetHeadroom = 1.25f;

        public static int RemoveHiddenTriangles(
            List<Vector3> positions,
            List<int> indices,
            Transform root,
            int targetTriangles,
            out byte[] vertexHidden,
            List<int> triangleMaterials = null)
        {
            vertexHidden = null;
            if (positions == null || indices == null || indices.Count < 3)
            {
                return 0;
            }

            int triangleCount = indices.Count / 3;
            if (triangleCount <= Mathf.RoundToInt(targetTriangles * SkipBudgetHeadroom))
            {
                return 0;
            }

            Bounds bounds = new Bounds(positions[0], Vector3.zero);
            for (int i = 1; i < positions.Count; i++)
            {
                bounds.Encapsulate(positions[i]);
            }
            float radius = Mathf.Max(bounds.extents.magnitude, 0.05f);
            float rayLength = radius * 3f;
            float bias = Mathf.Max(0.002f, radius * 0.0015f);
            int layerMask = 1 << OcclusionLayer;

            Vector3[] fan = BuildFan();

            GameObject colliderObject = null;
            Mesh colliderMesh = null;
            try
            {
                colliderMesh = new Mesh
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = positions.Count > 65534 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16,
                };
                colliderMesh.SetVertices(positions);
                colliderMesh.SetTriangles(indices, 0);

                colliderObject = new GameObject("MeshLodVisibilityCollider")
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    layer = OcclusionLayer
                };
                colliderObject.transform.SetPositionAndRotation(root.position, root.rotation);
                colliderObject.transform.localScale = root.lossyScale;
                colliderObject.AddComponent<MeshCollider>().sharedMesh = colliderMesh;
                Physics.SyncTransforms();

                Matrix4x4 rootToWorld = root.localToWorldMatrix;
                Quaternion rootRotation = root.rotation;

                bool[] triangleVisible = new bool[triangleCount];
                bool[] vertexNearVisible = new bool[positions.Count];

                Vector3[] triOrigin = new Vector3[triangleCount];
                Vector3[] triTangent = new Vector3[triangleCount];
                Vector3[] triBitangent = new Vector3[triangleCount];
                Vector3[] triNormal = new Vector3[triangleCount];
                bool[] triDegenerate = new bool[triangleCount];

                for (int t = 0; t < triangleCount; t++)
                {
                    Vector3 p0 = positions[indices[t * 3]];
                    Vector3 p1 = positions[indices[t * 3 + 1]];
                    Vector3 p2 = positions[indices[t * 3 + 2]];
                    Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0);
                    float length = normal.magnitude;
                    if (length < 1e-12f)
                    {
                        triDegenerate[t] = true;
                        continue;
                    }
                    normal /= length;
                    Vector3 normalWorld = SafeNormalize(rootRotation * normal, Vector3.up);
                    Vector3 centroidLocal = (p0 + p1 + p2) * (1f / 3f);
                    triOrigin[t] = rootToWorld.MultiplyPoint3x4(centroidLocal) + normalWorld * bias;
                    triNormal[t] = normalWorld;

                    Vector3 tangent = Vector3.Cross(normalWorld, Mathf.Abs(normalWorld.y) < 0.9f ? Vector3.up : Vector3.right);
                    tangent = SafeNormalize(tangent, Vector3.right);
                    triTangent[t] = tangent;
                    triBitangent[t] = SafeNormalize(Vector3.Cross(normalWorld, tangent), Vector3.forward);
                }

                void MarkVisible(int t)
                {
                    triangleVisible[t] = true;
                    vertexNearVisible[indices[t * 3]] = true;
                    vertexNearVisible[indices[t * 3 + 1]] = true;
                    vertexNearVisible[indices[t * 3 + 2]] = true;
                }

                QueryParameters queryParams = new QueryParameters(layerMask, false, QueryTriggerInteraction.UseGlobal, false);

                // Phase 1: Straight-out ray (+normal)
                const int chunkSize = 65536;
                int chunkAlloc = Mathf.Min(chunkSize, triangleCount);
                var straightCommands = new NativeArray<RaycastCommand>(chunkAlloc, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                var straightHits = new NativeArray<RaycastHit>(chunkAlloc, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                try
                {
                    for (int chunkStart = 0; chunkStart < triangleCount; chunkStart += chunkSize)
                    {
                        int count = Mathf.Min(chunkSize, triangleCount - chunkStart);
                        for (int c = 0; c < count; c++)
                        {
                            int t = chunkStart + c;
                            straightCommands[c] = triDegenerate[t]
                                ? new RaycastCommand(Vector3.zero, Vector3.up, queryParams, 0f)
                                : new RaycastCommand(triOrigin[t], triNormal[t], queryParams, rayLength);
                        }

                        RaycastCommand.ScheduleBatch(straightCommands.GetSubArray(0, count), straightHits.GetSubArray(0, count), 128).Complete();

                        for (int c = 0; c < count; c++)
                        {
                            int t = chunkStart + c;
                            if (!triDegenerate[t] && !IsRayHit(straightHits[c]))
                            {
                                MarkVisible(t);
                            }
                        }
                    }
                }
                finally
                {
                    straightCommands.Dispose();
                    straightHits.Dispose();
                }

                // Phase 2: Fan rays for remainder
                List<int> survivors = new List<int>();
                for (int t = 0; t < triangleCount; t++)
                {
                    if (!triDegenerate[t] && !triangleVisible[t])
                    {
                        survivors.Add(t);
                    }
                }

                int raysPerTriangle = fan.Length - 1; // fan[0] is straight-out (+normal), tested in Phase 1
                if (raysPerTriangle > 0 && survivors.Count > 0)
                {
                    int trianglesPerChunk = Mathf.Max(1, chunkSize / raysPerTriangle);
                    var fanCommands = new NativeArray<RaycastCommand>(chunkSize, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    var fanHits = new NativeArray<RaycastHit>(chunkSize, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                    try
                    {
                        for (int chunkStart = 0; chunkStart < survivors.Count; chunkStart += trianglesPerChunk)
                        {
                            int chunkTriangles = Mathf.Min(trianglesPerChunk, survivors.Count - chunkStart);
                            int rayCount = chunkTriangles * raysPerTriangle;

                            for (int c = 0; c < chunkTriangles; c++)
                            {
                                int t = survivors[chunkStart + c];
                                Vector3 origin = triOrigin[t];
                                Vector3 tangent = triTangent[t];
                                Vector3 bitangent = triBitangent[t];
                                Vector3 normal = triNormal[t];
                                int baseIndex = c * raysPerTriangle;

                                for (int r = 1; r < fan.Length; r++)
                                {
                                    Vector3 dir = tangent * fan[r].x + bitangent * fan[r].y + normal * fan[r].z;
                                    dir = SafeNormalize(dir, normal);
                                    fanCommands[baseIndex + r - 1] = new RaycastCommand(origin, dir, queryParams, rayLength);
                                }
                            }

                            RaycastCommand.ScheduleBatch(fanCommands.GetSubArray(0, rayCount), fanHits.GetSubArray(0, rayCount), 128).Complete();

                            for (int c = 0; c < chunkTriangles; c++)
                            {
                                int baseIndex = c * raysPerTriangle;
                                for (int r = 0; r < raysPerTriangle; r++)
                                {
                                    if (!IsRayHit(fanHits[baseIndex + r]))
                                    {
                                        MarkVisible(survivors[chunkStart + c]);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        fanCommands.Dispose();
                        fanHits.Dispose();
                    }
                }

                // Keep pass: visible triangles plus anything sharing a vertex with one — seals the
                // boundary so a hem edge or seam never becomes a crack.
                List<int> kept = new List<int>(indices.Count);
                List<int> keptMaterials = triangleMaterials != null ? new List<int>(triangleMaterials.Count) : null;
                int removed = 0;
                for (int t = 0; t < triangleCount; t++)
                {
                    int i0 = indices[t * 3];
                    int i1 = indices[t * 3 + 1];
                    int i2 = indices[t * 3 + 2];
                    if (triangleVisible[t] || vertexNearVisible[i0] || vertexNearVisible[i1] || vertexNearVisible[i2])
                    {
                        kept.Add(i0);
                        kept.Add(i1);
                        kept.Add(i2);
                        if (keptMaterials != null && t < triangleMaterials.Count)
                        {
                            keptMaterials.Add(triangleMaterials[t]);
                        }
                    }
                    else
                    {
                        removed++;
                    }
                }

                // Fail-open safety: if visibility cull found no exterior surface or stripped almost everything,
                // keep the whole mesh rather than destroying the model.
                if (kept.Count == 0 || removed >= triangleCount * 0.9f)
                {
                    Debug.LogWarning($"[MeshLodVisibilityCuller] Visibility culling could not reliably resolve exterior surfaces ({removed}/{triangleCount} occluded) — preserving all geometry.");
                    return 0;
                }

                if (removed > 0)
                {
                    indices.Clear();
                    indices.AddRange(kept);
                    if (triangleMaterials != null && keptMaterials != null)
                    {
                        triangleMaterials.Clear();
                        triangleMaterials.AddRange(keptMaterials);
                    }
                }

                vertexHidden = new byte[positions.Count];
                for (int i = 0; i < vertexHidden.Length; i++)
                {
                    vertexHidden[i] = vertexNearVisible[i] ? (byte)0 : (byte)1;
                }

                return removed;
            }
            finally
            {
                if (colliderObject != null)
                {
                    Object.DestroyImmediate(colliderObject);
                }
                if (colliderMesh != null)
                {
                    Object.DestroyImmediate(colliderMesh);
                }
            }
        }

        public static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            float sqr = v.x * v.x + v.y * v.y + v.z * v.z;
            if (sqr > 1e-8f)
            {
                float inv = 1f / Mathf.Sqrt(sqr);
                return new Vector3(v.x * inv, v.y * inv, v.z * inv);
            }
            return fallback;
        }

        private static Vector3[] BuildFan()
        {
            List<Vector3> fan = new List<Vector3>(13)
            {
                new Vector3(0f, 0f, 1f), // straight-out (+normal)
            };
            for (int i = 0; i < 8; i++)
            {
                float yaw = i * Mathf.PI * 2f / 8f;
                const float mid = 40f * Mathf.Deg2Rad;
                Vector3 v = new Vector3(Mathf.Cos(yaw) * Mathf.Sin(mid), Mathf.Sin(yaw) * Mathf.Sin(mid), Mathf.Cos(mid));
                fan.Add(SafeNormalize(v, Vector3.up));
            }
            for (int i = 0; i < 4; i++)
            {
                float yaw = (i + 0.5f) * Mathf.PI * 2f / 4f;
                const float grazing = 78f * Mathf.Deg2Rad;
                Vector3 v = new Vector3(Mathf.Cos(yaw) * Mathf.Sin(grazing), Mathf.Sin(yaw) * Mathf.Sin(grazing), Mathf.Cos(grazing));
                fan.Add(SafeNormalize(v, Vector3.up));
            }
            return fan.ToArray();
        }

        private static bool IsRayHit(in RaycastHit hit)
        {
#if UNITY_6000_0_OR_NEWER
            return hit.colliderEntityId != EntityId.None;
#else
            return hit.colliderInstanceID != 0;
#endif
        }
    }
}
