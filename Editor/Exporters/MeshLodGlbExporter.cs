using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace MeshLODGenerator
{
    /// <summary>
    /// Standalone glTF 2.0 Binary (.glb) exporter for mesh LODs.
    /// Exports geometry (positions, normals, UVs, indices), materials, embedded PNG textures,
    /// and skeleton/armature skinning (JOINTS_0, WEIGHTS_0, inverseBindMatrices, skins, bone hierarchy)
    /// without requiring any external packages.
    /// </summary>
    public static class MeshLodGlbExporter
    {
        private const uint GlbMagic = 0x46546C67; // "glTF"
        private const uint GlbVersion = 2;
        private const uint ChunkTypeJson = 0x4E4F534A; // "JSON"
        private const uint ChunkTypeBin = 0x004E4942; // "BIN\0"

        public struct ExportOptions
        {
            public string Name;
            public Mesh Mesh;
            public Texture2D AlbedoTexture;
            public Texture2D EmissionTexture;
            public Transform[] Bones;
            public Transform RootBone;
        }

        public static bool ExportGlb(string filePath, ExportOptions options)
        {
            if (string.IsNullOrEmpty(filePath) || options.Mesh == null)
            {
                Debug.LogError("[MeshLodGlbExporter] Export failed: invalid file path or mesh is null.");
                return false;
            }

            try
            {
                byte[] glbBytes = BuildGlb(options);
                File.WriteAllBytes(filePath, glbBytes);
                Debug.Log($"[MeshLodGlbExporter] Exported GLB to: {filePath} ({glbBytes.Length / 1024} KB)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MeshLodGlbExporter] Export failed with exception: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        public static byte[] BuildGlb(ExportOptions options)
        {
            Mesh mesh = options.Mesh;
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uvs = mesh.uv;
            int[] triangles = mesh.triangles;

            if (normals == null || normals.Length != vertices.Length)
            {
                mesh.RecalculateNormals();
                normals = mesh.normals;
            }

            bool isRigged = options.Bones != null && options.Bones.Length > 0 &&
                            mesh.boneWeights != null && mesh.boneWeights.Length == vertices.Length &&
                            mesh.bindposes != null && mesh.bindposes.Length == options.Bones.Length;

            int numBones = isRigged ? options.Bones.Length : 0;
            BoneWeight[] boneWeights = isRigged ? mesh.boneWeights : null;
            Matrix4x4[] bindposes = isRigged ? mesh.bindposes : null;

            using MemoryStream binStream = new MemoryStream();
            using BinaryWriter binWriter = new BinaryWriter(binStream);

            List<string> bufferViewsJson = new List<string>();
            List<string> accessorsJson = new List<string>();
            int bufferViewIndex = 0;
            int accessorIndex = 0;

            // 1. POSITION (Vec3 float)
            int posOffset = (int)binStream.Position;
            Vector3 minPos = vertices.Length > 0 ? new Vector3(vertices[0].x, vertices[0].y, -vertices[0].z) : Vector3.zero;
            Vector3 maxPos = minPos;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 v = vertices[i];
                Vector3 gltfV = new Vector3(v.x, v.y, -v.z);
                binWriter.Write(gltfV.x);
                binWriter.Write(gltfV.y);
                binWriter.Write(gltfV.z);

                minPos = Vector3.Min(minPos, gltfV);
                maxPos = Vector3.Max(maxPos, gltfV);
            }
            int posLength = (int)binStream.Position - posOffset;
            Pad4(binWriter);
            bufferViewsJson.Add($"{{\"buffer\":0,\"byteOffset\":{posOffset},\"byteLength\":{posLength},\"target\":34962}}");
            int posAccessor = accessorIndex++;
            accessorsJson.Add($"{{\"bufferView\":{bufferViewIndex++},\"byteOffset\":0,\"componentType\":5126,\"count\":{vertices.Length},\"type\":\"VEC3\",\"max\":[{maxPos.x.ToString("G9", CultureInfo.InvariantCulture)},{maxPos.y.ToString("G9", CultureInfo.InvariantCulture)},{maxPos.z.ToString("G9", CultureInfo.InvariantCulture)}],\"min\":[{minPos.x.ToString("G9", CultureInfo.InvariantCulture)},{minPos.y.ToString("G9", CultureInfo.InvariantCulture)},{minPos.z.ToString("G9", CultureInfo.InvariantCulture)}]}}");

            // 2. NORMAL (Vec3 float)
            int normOffset = (int)binStream.Position;
            for (int i = 0; i < normals.Length; i++)
            {
                Vector3 n = normals[i];
                binWriter.Write(n.x);
                binWriter.Write(n.y);
                binWriter.Write(-n.z);
            }
            int normLength = (int)binStream.Position - normOffset;
            Pad4(binWriter);
            bufferViewsJson.Add($"{{\"buffer\":0,\"byteOffset\":{normOffset},\"byteLength\":{normLength},\"target\":34962}}");
            int normAccessor = accessorIndex++;
            accessorsJson.Add($"{{\"bufferView\":{bufferViewIndex++},\"byteOffset\":0,\"componentType\":5126,\"count\":{normals.Length},\"type\":\"VEC3\"}}");

            // 3. TEXCOORD_0 (Vec2 float)
            int uvAccessor = -1;
            if (uvs != null && uvs.Length == vertices.Length)
            {
                int uvOffset = (int)binStream.Position;
                for (int i = 0; i < uvs.Length; i++)
                {
                    Vector2 uv = uvs[i];
                    binWriter.Write(uv.x);
                    binWriter.Write(1.0f - uv.y);
                }
                int uvLength = (int)binStream.Position - uvOffset;
                Pad4(binWriter);
                bufferViewsJson.Add($"{{\"buffer\":0,\"byteOffset\":{uvOffset},\"byteLength\":{uvLength},\"target\":34962}}");
                uvAccessor = accessorIndex++;
                accessorsJson.Add($"{{\"bufferView\":{bufferViewIndex++},\"byteOffset\":0,\"componentType\":5126,\"count\":{uvs.Length},\"type\":\"VEC2\"}}");
            }

            // 4. SKINNING: JOINTS_0 & WEIGHTS_0 (if rigged)
            int jointsAccessor = -1;
            int weightsAccessor = -1;
            if (isRigged)
            {
                // JOINTS_0 (VEC4 ushort)
                int jointsOffset = (int)binStream.Position;
                for (int i = 0; i < vertices.Length; i++)
                {
                    BoneWeight bw = boneWeights[i];
                    binWriter.Write((ushort)Mathf.Clamp(bw.boneIndex0, 0, numBones - 1));
                    binWriter.Write((ushort)Mathf.Clamp(bw.boneIndex1, 0, numBones - 1));
                    binWriter.Write((ushort)Mathf.Clamp(bw.boneIndex2, 0, numBones - 1));
                    binWriter.Write((ushort)Mathf.Clamp(bw.boneIndex3, 0, numBones - 1));
                }
                int jointsLength = (int)binStream.Position - jointsOffset;
                Pad4(binWriter);
                bufferViewsJson.Add($"{{\"buffer\":0,\"byteOffset\":{jointsOffset},\"byteLength\":{jointsLength},\"target\":34962}}");
                jointsAccessor = accessorIndex++;
                accessorsJson.Add($"{{\"bufferView\":{bufferViewIndex++},\"byteOffset\":0,\"componentType\":5123,\"count\":{vertices.Length},\"type\":\"VEC4\"}}");

                // WEIGHTS_0 (VEC4 float)
                int weightsOffset = (int)binStream.Position;
                for (int i = 0; i < vertices.Length; i++)
                {
                    BoneWeight bw = boneWeights[i];
                    float w0 = bw.weight0;
                    float w1 = bw.weight1;
                    float w2 = bw.weight2;
                    float w3 = bw.weight3;
                    float sum = w0 + w1 + w2 + w3;
                    if (sum > 1e-5f)
                    {
                        float inv = 1.0f / sum;
                        w0 *= inv;
                        w1 *= inv;
                        w2 *= inv;
                        w3 *= inv;
                    }
                    else
                    {
                        w0 = 1.0f;
                        w1 = 0f;
                        w2 = 0f;
                        w3 = 0f;
                    }
                    binWriter.Write(w0);
                    binWriter.Write(w1);
                    binWriter.Write(w2);
                    binWriter.Write(w3);
                }
                int weightsLength = (int)binStream.Position - weightsOffset;
                Pad4(binWriter);
                bufferViewsJson.Add($"{{\"buffer\":0,\"byteOffset\":{weightsOffset},\"byteLength\":{weightsLength},\"target\":34962}}");
                weightsAccessor = accessorIndex++;
                accessorsJson.Add($"{{\"bufferView\":{bufferViewIndex++},\"byteOffset\":0,\"componentType\":5126,\"count\":{vertices.Length},\"type\":\"VEC4\"}}");
            }

            // 5. INDICES (scalar uint16 or uint32)
            int indicesOffset = (int)binStream.Position;
            bool use16Bit = vertices.Length <= 65535;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 2];
                int c = triangles[i + 1];
                if (use16Bit)
                {
                    binWriter.Write((ushort)a);
                    binWriter.Write((ushort)b);
                    binWriter.Write((ushort)c);
                }
                else
                {
                    binWriter.Write((uint)a);
                    binWriter.Write((uint)b);
                    binWriter.Write((uint)c);
                }
            }
            int indicesLength = (int)binStream.Position - indicesOffset;
            Pad4(binWriter);
            bufferViewsJson.Add($"{{\"buffer\":0,\"byteOffset\":{indicesOffset},\"byteLength\":{indicesLength},\"target\":34963}}");
            int indicesAccessor = accessorIndex++;
            int indexComponentType = use16Bit ? 5123 : 5125;
            accessorsJson.Add($"{{\"bufferView\":{bufferViewIndex++},\"byteOffset\":0,\"componentType\":{indexComponentType},\"count\":{triangles.Length},\"type\":\"SCALAR\"}}");

            // 6. INVERSE BIND MATRICES (if rigged)
            int ibmAccessor = -1;
            if (isRigged)
            {
                int ibmOffset = (int)binStream.Position;
                Matrix4x4 S = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
                for (int b = 0; b < numBones; b++)
                {
                    Matrix4x4 unityBindpose = bindposes[b];
                    Matrix4x4 gltfIbm = S * unityBindpose * S;
                    for (int col = 0; col < 4; col++)
                    {
                        for (int row = 0; row < 4; row++)
                        {
                            binWriter.Write(gltfIbm[row, col]);
                        }
                    }
                }
                int ibmLength = (int)binStream.Position - ibmOffset;
                Pad4(binWriter);
                bufferViewsJson.Add($"{{\"buffer\":0,\"byteOffset\":{ibmOffset},\"byteLength\":{ibmLength}}}");
                ibmAccessor = accessorIndex++;
                accessorsJson.Add($"{{\"bufferView\":{bufferViewIndex++},\"byteOffset\":0,\"componentType\":5126,\"count\":{numBones},\"type\":\"MAT4\"}}");
            }

            // 7. IMAGES / TEXTURES
            List<string> imagesJson = new List<string>();
            List<string> texturesJson = new List<string>();
            int albedoTexIndex = -1;
            int emissionTexIndex = -1;

            if (options.AlbedoTexture != null)
            {
                byte[] pngBytes = options.AlbedoTexture.EncodeToPNG();
                if (pngBytes != null && pngBytes.Length > 0)
                {
                    int imgOffset = (int)binStream.Position;
                    binWriter.Write(pngBytes);
                    int imgLength = pngBytes.Length;
                    Pad4(binWriter);
                    bufferViewsJson.Add($"{{\"buffer\":0,\"byteOffset\":{imgOffset},\"byteLength\":{imgLength}}}");
                    int imgView = bufferViewIndex++;
                    imagesJson.Add($"{{\"bufferView\":{imgView},\"mimeType\":\"image/png\",\"name\":\"Albedo\"}}");
                    albedoTexIndex = texturesJson.Count;
                    texturesJson.Add($"{{\"sampler\":0,\"source\":{imagesJson.Count - 1}}}");
                }
            }

            if (options.EmissionTexture != null)
            {
                byte[] pngBytes = options.EmissionTexture.EncodeToPNG();
                if (pngBytes != null && pngBytes.Length > 0)
                {
                    int imgOffset = (int)binStream.Position;
                    binWriter.Write(pngBytes);
                    int imgLength = pngBytes.Length;
                    Pad4(binWriter);
                    bufferViewsJson.Add($"{{\"buffer\":0,\"byteOffset\":{imgOffset},\"byteLength\":{imgLength}}}");
                    int imgView = bufferViewIndex++;
                    imagesJson.Add($"{{\"bufferView\":{imgView},\"mimeType\":\"image/png\",\"name\":\"Emission\"}}");
                    emissionTexIndex = texturesJson.Count;
                    texturesJson.Add($"{{\"sampler\":0,\"source\":{imagesJson.Count - 1}}}");
                }
            }

            // Material JSON
            string pbrProps = "\"metallicFactor\":0.0,\"roughnessFactor\":0.8";
            if (albedoTexIndex >= 0)
            {
                pbrProps += $",\"baseColorTexture\":{{\"index\":{albedoTexIndex}}}";
            }
            string materialJson = $"{{\"name\":\"LOD_Material\",\"pbrMetallicRoughness\":{{{pbrProps}}}";
            if (emissionTexIndex >= 0)
            {
                materialJson += $",\"emissiveTexture\":{{\"index\":{emissionTexIndex}}},\"emissiveFactor\":[1,1,1]";
            }
            materialJson += "}";

            // Primitive JSON
            string attributesJson = $"\"POSITION\":{posAccessor},\"NORMAL\":{normAccessor}";
            if (uvAccessor >= 0)
            {
                attributesJson += $",\"TEXCOORD_0\":{uvAccessor}";
            }
            if (isRigged)
            {
                attributesJson += $",\"JOINTS_0\":{jointsAccessor},\"WEIGHTS_0\":{weightsAccessor}";
            }
            string primitiveJson = $"{{\"attributes\":{{{attributesJson}}},\"indices\":{indicesAccessor},\"material\":0}}";

            // Node hierarchy
            List<string> nodesJson = new List<string>();
            List<int> sceneNodeIndices = new List<int>();

            if (!isRigged)
            {
                nodesJson.Add($"{{\"name\":\"{EscapeJson(options.Name ?? "LOD_Mesh")}\",\"mesh\":0}}");
                sceneNodeIndices.Add(0);
            }
            else
            {
                // Node 0: Skinned Mesh
                nodesJson.Add($"{{\"name\":\"{EscapeJson(options.Name ?? "LOD_Mesh")}\",\"mesh\":0,\"skin\":0}}");
                sceneNodeIndices.Add(0);

                // Map transforms to bone indices
                Dictionary<Transform, int> boneToIndex = new Dictionary<Transform, int>();
                for (int b = 0; b < numBones; b++)
                {
                    if (options.Bones[b] != null && !boneToIndex.ContainsKey(options.Bones[b]))
                    {
                        boneToIndex[options.Bones[b]] = b;
                    }
                }

                List<int>[] childrenPerBone = new List<int>[numBones];
                for (int b = 0; b < numBones; b++) childrenPerBone[b] = new List<int>();

                List<int> rootJointIndices = new List<int>();

                for (int b = 0; b < numBones; b++)
                {
                    Transform bone = options.Bones[b];
                    int parentBoneIdx = -1;
                    if (bone != null)
                    {
                        Transform p = bone.parent;
                        while (p != null)
                        {
                            if (boneToIndex.TryGetValue(p, out int foundIdx))
                            {
                                parentBoneIdx = foundIdx;
                                break;
                            }
                            p = p.parent;
                        }
                    }

                    if (parentBoneIdx >= 0)
                    {
                        childrenPerBone[parentBoneIdx].Add(1 + b);
                    }
                    else
                    {
                        rootJointIndices.Add(1 + b);
                        sceneNodeIndices.Add(1 + b);
                    }
                }

                for (int b = 0; b < numBones; b++)
                {
                    Transform bone = options.Bones[b];
                    string bName = bone != null ? bone.name : $"Bone_{b}";

                    Vector3 pos = Vector3.zero;
                    Quaternion rot = Quaternion.identity;
                    Vector3 scale = Vector3.one;

                    if (bone != null)
                    {
                        int parentBoneIdx = -1;
                        Transform p = bone.parent;
                        while (p != null)
                        {
                            if (boneToIndex.TryGetValue(p, out int foundIdx))
                            {
                                parentBoneIdx = foundIdx;
                                break;
                            }
                            p = p.parent;
                        }

                        if (parentBoneIdx >= 0 && options.Bones[parentBoneIdx] != null)
                        {
                            Matrix4x4 rel = options.Bones[parentBoneIdx].worldToLocalMatrix * bone.localToWorldMatrix;
                            DecomposeMatrix(rel, out pos, out rot, out scale);
                        }
                        else
                        {
                            Transform rootAnchor = options.RootBone != null ? options.RootBone : bone.root;
                            if (rootAnchor != null && rootAnchor != bone)
                            {
                                Matrix4x4 rel = rootAnchor.worldToLocalMatrix * bone.localToWorldMatrix;
                                DecomposeMatrix(rel, out pos, out rot, out scale);
                            }
                            else
                            {
                                pos = bone.localPosition;
                                rot = bone.localRotation;
                                scale = bone.localScale;
                            }
                        }
                    }

                    Vector3 gltfPos = new Vector3(pos.x, pos.y, -pos.z);
                    Quaternion gltfRot = new Quaternion(-rot.x, -rot.y, rot.z, rot.w);
                    Vector3 gltfScale = scale;

                    StringBuilder sbNode = new StringBuilder();
                    sbNode.Append($"{{\"name\":\"{EscapeJson(bName)}\"");
                    sbNode.Append($",\"translation\":[{gltfPos.x.ToString("G7", CultureInfo.InvariantCulture)},{gltfPos.y.ToString("G7", CultureInfo.InvariantCulture)},{gltfPos.z.ToString("G7", CultureInfo.InvariantCulture)}]");
                    sbNode.Append($",\"rotation\":[{gltfRot.x.ToString("G7", CultureInfo.InvariantCulture)},{gltfRot.y.ToString("G7", CultureInfo.InvariantCulture)},{gltfRot.z.ToString("G7", CultureInfo.InvariantCulture)},{gltfRot.w.ToString("G7", CultureInfo.InvariantCulture)}]");
                    sbNode.Append($",\"scale\":[{gltfScale.x.ToString("G7", CultureInfo.InvariantCulture)},{gltfScale.y.ToString("G7", CultureInfo.InvariantCulture)},{gltfScale.z.ToString("G7", CultureInfo.InvariantCulture)}]");
                    if (childrenPerBone[b].Count > 0)
                    {
                        sbNode.Append($",\"children\":[{string.Join(",", childrenPerBone[b])}]");
                    }
                    sbNode.Append("}");
                    nodesJson.Add(sbNode.ToString());
                }
            }

            byte[] binChunk = binStream.ToArray();

            // Build JSON String
            StringBuilder json = new StringBuilder();
            json.Append("{");
            json.Append("\"asset\":{\"generator\":\"MeshLODGenerator\",\"version\":\"2.0\"},");
            json.Append($"\"scenes\":[{{\"nodes\":[{string.Join(",", sceneNodeIndices)}]}}],\"scene\":0,");
            json.Append($"\"nodes\":[{string.Join(",", nodesJson)}],");
            json.Append($"\"meshes\":[{{\"name\":\"{EscapeJson(mesh.name ?? "LOD")}\",\"primitives\":[{primitiveJson}]}}],");
            json.Append($"\"materials\":[{materialJson}],");

            if (isRigged)
            {
                List<int> jointIndices = new List<int>(numBones);
                for (int b = 0; b < numBones; b++) jointIndices.Add(1 + b);
                string skeletonProp = sceneNodeIndices.Count > 1 ? $",\"skeleton\":{sceneNodeIndices[1]}" : "";
                string skinJson = $"{{\"inverseBindMatrices\":{ibmAccessor},\"joints\":[{string.Join(",", jointIndices)}]{skeletonProp}}}";
                json.Append($"\"skins\":[{skinJson}],");
            }

            if (texturesJson.Count > 0)
            {
                json.Append($"\"textures\":[{string.Join(",", texturesJson)}],");
                json.Append($"\"images\":[{string.Join(",", imagesJson)}],");
                json.Append("\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":33071,\"wrapT\":33071}],");
            }

            json.Append($"\"accessors\":[{string.Join(",", accessorsJson)}],");
            json.Append($"\"bufferViews\":[{string.Join(",", bufferViewsJson)}],");
            json.Append($"\"buffers\":[{{\"byteLength\":{binChunk.Length}}}]");
            json.Append("}");

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json.ToString());
            int jsonPadding = (4 - (jsonBytes.Length % 4)) % 4;
            int jsonChunkLength = jsonBytes.Length + jsonPadding;

            int binPadding = (4 - (binChunk.Length % 4)) % 4;
            int binChunkLength = binChunk.Length + binPadding;

            int totalLength = 12 + (8 + jsonChunkLength) + (8 + binChunkLength);

            using MemoryStream glbStream = new MemoryStream(totalLength);
            using BinaryWriter glbWriter = new BinaryWriter(glbStream);

            // 1. Header
            glbWriter.Write(GlbMagic);
            glbWriter.Write(GlbVersion);
            glbWriter.Write((uint)totalLength);

            // 2. JSON Chunk
            glbWriter.Write((uint)jsonChunkLength);
            glbWriter.Write(ChunkTypeJson);
            glbWriter.Write(jsonBytes);
            for (int i = 0; i < jsonPadding; i++) glbWriter.Write((byte)0x20); // space padding

            // 3. BIN Chunk
            glbWriter.Write((uint)binChunkLength);
            glbWriter.Write(ChunkTypeBin);
            glbWriter.Write(binChunk);
            for (int i = 0; i < binPadding; i++) glbWriter.Write((byte)0x00);

            return glbStream.ToArray();
        }

        private static void DecomposeMatrix(Matrix4x4 m, out Vector3 pos, out Quaternion rot, out Vector3 scale)
        {
            pos = m.GetColumn(3);
            scale = new Vector3(
                m.GetColumn(0).magnitude,
                m.GetColumn(1).magnitude,
                m.GetColumn(2).magnitude
            );
            Matrix4x4 rotM = m;
            if (scale.x > 1e-6f) rotM.SetColumn(0, rotM.GetColumn(0) / scale.x);
            if (scale.y > 1e-6f) rotM.SetColumn(1, rotM.GetColumn(1) / scale.y);
            if (scale.z > 1e-6f) rotM.SetColumn(2, rotM.GetColumn(2) / scale.z);
            rot = rotM.rotation;
        }

        private static string EscapeJson(string str)
        {
            if (string.IsNullOrEmpty(str)) return "";
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "").Replace("\r", "");
        }

        private static void Pad4(BinaryWriter writer)
        {
            long pos = writer.BaseStream.Position;
            int pad = (int)((4 - (pos % 4)) % 4);
            for (int i = 0; i < pad; i++)
            {
                writer.Write((byte)0);
            }
        }
    }
}
