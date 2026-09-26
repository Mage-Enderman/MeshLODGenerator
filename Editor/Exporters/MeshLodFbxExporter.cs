using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace MeshLODGenerator
{
    /// <summary>
    /// Binary FBX (7.4/7400) exporter for mesh LODs.
    /// Checks for Unity FBX Exporter package first, and falls back to a clean
    /// standalone Binary FBX 7.4 writer supporting static meshes, rigged armatures (LimbNodes, Skin, Clusters),
    /// and embedded/linked textures without external packages.
    /// </summary>
    public static class MeshLodFbxExporter
    {
        public struct ExportOptions
        {
            public string Name;
            public Mesh Mesh;
            public string AlbedoTexturePath;
            public string EmissionTexturePath;
            public GameObject TargetGameObject;
            public Transform RootTransform;
            public Transform[] Bones;
            public Transform RootBone;
            public Material LodMaterial;
            public Material[] LodMaterials;
        }

        public static bool ExportFbx(string filePath, ExportOptions options)
        {
            if (string.IsNullOrEmpty(filePath) || options.Mesh == null)
            {
                Debug.LogError("[MeshLodFbxExporter] Invalid path or mesh is null.");
                return false;
            }

            // 1. Try Unity's official FBX Exporter package if installed
            if (TryUnityFbxExporter(filePath, options))
            {
                return true;
            }

            // 2. Fall back to standalone Binary FBX exporter
            return ExportBinaryFbx(filePath, options);
        }

        private static bool TryUnityFbxExporter(string filePath, ExportOptions options)
        {
            if (options.TargetGameObject == null) return false;
            try
            {
                Type modelExporterType = Type.GetType("UnityEditor.Formats.Fbx.Exporter.ModelExporter, Unity.Formats.Fbx.Editor");
                if (modelExporterType != null)
                {
                    MethodInfo exportMethod = modelExporterType.GetMethod("ExportObject", new[] { typeof(string), typeof(UnityEngine.Object) });
                    if (exportMethod != null)
                    {
                        // Spawn a temporary LOD GameObject so that the exporter exports the decimated LOD
                        GameObject tempLod = MeshLodGenerator.SpawnLodInstance(options.TargetGameObject, new MeshLodGenerator.MeshLodResult
                        {
                            LodMesh = options.Mesh,
                            LodMaterial = options.LodMaterial,
                            LodMaterials = options.LodMaterials != null ? options.LodMaterials : (options.LodMaterial != null ? new Material[] { options.LodMaterial } : null),
                            IsRigged = options.Bones != null && options.Bones.Length > 0,
                            Bones = options.Bones,
                            RootBone = options.RootBone
                        }, Vector3.zero);

                        try
                        {
                            object result = exportMethod.Invoke(null, new object[] { filePath, tempLod });
                            if (result is string s && !string.IsNullOrEmpty(s))
                            {
                                Debug.Log($"[MeshLodFbxExporter] Exported via Unity FBX Exporter: {filePath}");
                                return true;
                            }
                        }
                        finally
                        {
                            if (tempLod != null)
                            {
                                UnityEngine.Object.DestroyImmediate(tempLod);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MeshLodFbxExporter] Unity FBX Exporter reflection failed ({ex.Message}), falling back to internal Binary exporter.");
            }
            return false;
        }

        public static bool ExportBinaryFbx(string filePath, ExportOptions options)
        {
            try
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
                                mesh.boneWeights != null && mesh.boneWeights.Length == vertices.Length;

                int numBones = isRigged ? options.Bones.Length : 0;
                BoneWeight[] boneWeights = isRigged ? mesh.boneWeights : null;

                long geomId = 1000001;
                long modelId = 1000002;
                long matId = 1000003;
                long texId = 1000004;
                long vidId = 1000005;
                long skinId = 1000006;
                long poseId = 1000007;
                long armatureId = 1000008;
                long armatureAttrId = 1000009;

                long boneModelBase = 2000000;
                long boneAttrBase = 3000000;
                long clusterBase = 4000000;

                using MemoryStream ms = new MemoryStream();
                using FbxBinaryWriter writer = new FbxBinaryWriter(ms);

                // --- 1. FBXHeaderExtension ---
                writer.BeginNode("FBXHeaderExtension");
                writer.WriteNodeInt("FBXHeaderVersion", 1003);
                writer.WriteNodeInt("FBXVersion", 7400);
                writer.WriteNodeInt("EncryptionType", 0);

                writer.BeginNode("CreationTimeStamp");
                writer.WriteNodeInt("Version", 1000);
                DateTime now = DateTime.UtcNow;
                writer.WriteNodeInt("Year", now.Year);
                writer.WriteNodeInt("Month", now.Month);
                writer.WriteNodeInt("Day", now.Day);
                writer.WriteNodeInt("Hour", now.Hour);
                writer.WriteNodeInt("Minute", now.Minute);
                writer.WriteNodeInt("Second", now.Second);
                writer.WriteNodeInt("Millisecond", 0);
                writer.EndNode(); // CreationTimeStamp

                writer.WriteNodeString("Creator", "MeshLODGenerator");

                writer.BeginNode("SceneInfo");
                writer.AddPropertyString("GlobalInfo\0\x01SceneInfo");
                writer.AddPropertyString("UserData");
                writer.WriteNodeString("Type", "UserData");
                writer.WriteNodeInt("Version", 100);
                writer.BeginNode("MetaData");
                writer.WriteNodeInt("Version", 100);
                writer.WriteNodeString("Title", "");
                writer.WriteNodeString("Subject", "");
                writer.WriteNodeString("Author", "");
                writer.WriteNodeString("Keywords", "");
                writer.WriteNodeString("Revision", "");
                writer.WriteNodeString("Comment", "");
                writer.EndNode(); // MetaData

                writer.BeginNode("Properties70");
                WritePropertyP(writer, "DocumentUrl", "KString", "Url", "", filePath.Replace('\\', '/'));
                WritePropertyP(writer, "SrcDocumentUrl", "KString", "Url", "", filePath.Replace('\\', '/'));
                writer.EndNode(); // Properties70
                writer.EndNode(); // SceneInfo

                writer.EndNode(); // FBXHeaderExtension

                // --- Top-level nodes required by Autodesk FBX SDK ---
                writer.BeginNode("FileId");
                writer.AddPropertyRawBytes(FileIdBytes);
                writer.EndNode();

                writer.WriteNodeString("CreationTime", "1970-01-01 10:00:00:000");

                writer.WriteNodeString("Creator", "MeshLODGenerator");

                // --- 2. GlobalSettings ---
                writer.BeginNode("GlobalSettings");
                writer.WriteNodeInt("Version", 1000);
                writer.BeginNode("Properties70");
                WritePropertyP(writer, "UpAxis", "int", "Integer", "", 1);
                WritePropertyP(writer, "UpAxisSign", "int", "Integer", "", 1);
                WritePropertyP(writer, "FrontAxis", "int", "Integer", "", 2);
                WritePropertyP(writer, "FrontAxisSign", "int", "Integer", "", 1);
                WritePropertyP(writer, "CoordAxis", "int", "Integer", "", 0);
                WritePropertyP(writer, "CoordAxisSign", "int", "Integer", "", 1);
                WritePropertyP(writer, "OriginalUpAxis", "int", "Integer", "", -1);
                WritePropertyP(writer, "OriginalUpAxisSign", "int", "Integer", "", 1);
                WritePropertyP(writer, "UnitScaleFactor", "double", "Number", "", 100.0);
                WritePropertyP(writer, "OriginalUnitScaleFactor", "double", "Number", "", 100.0);
                writer.EndNode(); // Properties70
                writer.EndNode(); // GlobalSettings

                // --- 3. Documents ---
                writer.BeginNode("Documents");
                writer.WriteNodeInt("Count", 1);
                writer.BeginNode("Document");
                writer.AddPropertyLong(1000000);
                writer.AddPropertyString("Scene");
                writer.AddPropertyString("Scene");
                writer.BeginNode("Properties70");
                writer.EndNode();
                writer.WriteNodeLong("RootNode", 0);
                writer.EndNode(); // Document
                writer.EndNode(); // Documents

                // --- 4. References ---
                writer.BeginNode("References");
                writer.EndNode();

                // --- 5. Definitions ---
                writer.BeginNode("Definitions");
                writer.WriteNodeInt("Version", 100);
                int totalObjects = 1 /* GlobalSettings */
                    + 1 /* Geometry */
                    + (1 + (isRigged ? (numBones + 1) : 0)) /* Model (Mesh + Bones + Armature) */
                    + 1 /* Material */
                    + (!string.IsNullOrEmpty(options.AlbedoTexturePath) ? 2 : 0) /* Texture + Video */
                    + (isRigged ? (numBones + 1) : 0) /* NodeAttribute (Bones + Armature) */
                    + (isRigged ? (1 + numBones) : 0) /* Deformer (Skin + Clusters) */
                    + (isRigged ? 1 : 0) /* Pose */;
                writer.WriteNodeInt("Count", totalObjects);

                WriteObjectTypeCount(writer, "GlobalSettings", 1);
                WriteObjectTypeCount(writer, "Geometry", 1);
                WriteObjectTypeCount(writer, "Model", 1 + (isRigged ? (numBones + 1) : 0));
                WriteObjectTypeCount(writer, "Material", 1);
                if (!string.IsNullOrEmpty(options.AlbedoTexturePath))
                {
                    WriteObjectTypeCount(writer, "Texture", 1);
                    WriteObjectTypeCount(writer, "Video", 1);
                }
                if (isRigged)
                {
                    WriteObjectTypeCount(writer, "NodeAttribute", numBones + 1);
                    WriteObjectTypeCount(writer, "Deformer", 1 + numBones);
                    WriteObjectTypeCount(writer, "Pose", 1);
                }
                writer.EndNode(); // Definitions

                // --- 6. Objects ---
                writer.BeginNode("Objects");

                // 6a. Geometry
                writer.BeginNode("Geometry");
                writer.AddPropertyLong(geomId);
                writer.AddPropertyString("Geometry::\0\x01Geometry");
                writer.AddPropertyString("Mesh");

                writer.BeginNode("Properties70");
                writer.EndNode();
                writer.WriteNodeInt("GeometryVersion", 124);

                // Vertices: in FBX/Blender root coordinate system (Z-up), rotated by [-90, -180, 0] at Model level
                double[] vertCoords = new double[vertices.Length * 3];
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 v = vertices[i];
                    vertCoords[i * 3 + 0] = v.x;
                    vertCoords[i * 3 + 1] = v.z;
                    vertCoords[i * 3 + 2] = v.y;
                }
                writer.BeginNode("Vertices");
                writer.AddPropertyDoubleArray(vertCoords);
                writer.EndNode();

                // PolygonVertexIndex (winding order matches standard FBX)
                int[] polyIndices = new int[triangles.Length];
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    polyIndices[i + 0] = triangles[i + 0];
                    polyIndices[i + 1] = triangles[i + 2];
                    polyIndices[i + 2] = ~triangles[i + 1];
                }
                writer.BeginNode("PolygonVertexIndex");
                writer.AddPropertyIntArray(polyIndices);
                writer.EndNode();

                // Normals (mapped to [x, z, y] to match vertex orientation)
                double[] normCoords = new double[triangles.Length * 3];
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int a = triangles[i + 0];
                    int b = triangles[i + 2];
                    int c = triangles[i + 1];

                    Vector3 na = normals[a];
                    Vector3 nb = normals[b];
                    Vector3 nc = normals[c];

                    normCoords[i * 3 + 0] = na.x;
                    normCoords[i * 3 + 1] = na.z;
                    normCoords[i * 3 + 2] = na.y;

                    normCoords[i * 3 + 3] = nb.x;
                    normCoords[i * 3 + 4] = nb.z;
                    normCoords[i * 3 + 5] = nb.y;

                    normCoords[i * 3 + 6] = nc.x;
                    normCoords[i * 3 + 7] = nc.z;
                    normCoords[i * 3 + 8] = nc.y;
                }
                writer.BeginNode("LayerElementNormal");
                writer.AddPropertyInt(0);
                writer.WriteNodeInt("Version", 101);
                writer.WriteNodeString("Name", "");
                writer.WriteNodeString("MappingInformationType", "ByPolygonVertex");
                writer.WriteNodeString("ReferenceInformationType", "Direct");
                writer.BeginNode("Normals");
                writer.AddPropertyDoubleArray(normCoords);
                writer.EndNode();
                writer.EndNode(); // LayerElementNormal

                // UVs
                if (uvs != null && uvs.Length == vertices.Length)
                {
                    double[] uvCoords = new double[triangles.Length * 2];
                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        int a = triangles[i + 0];
                        int b = triangles[i + 2];
                        int c = triangles[i + 1];

                        Vector2 uva = uvs[a];
                        Vector2 uvb = uvs[b];
                        Vector2 uvc = uvs[c];

                        uvCoords[i * 2 + 0] = uva.x;
                        uvCoords[i * 2 + 1] = uva.y;

                        uvCoords[i * 2 + 2] = uvb.x;
                        uvCoords[i * 2 + 3] = uvb.y;

                        uvCoords[i * 2 + 4] = uvc.x;
                        uvCoords[i * 2 + 5] = uvc.y;
                    }
                    writer.BeginNode("LayerElementUV");
                    writer.AddPropertyInt(0);
                    writer.WriteNodeInt("Version", 101);
                    writer.WriteNodeString("Name", "UVMap");
                    writer.WriteNodeString("MappingInformationType", "ByPolygonVertex");
                    writer.WriteNodeString("ReferenceInformationType", "Direct");
                    writer.BeginNode("UV");
                    writer.AddPropertyDoubleArray(uvCoords);
                    writer.EndNode();
                    writer.EndNode(); // LayerElementUV
                }

                // Material Layer
                writer.BeginNode("LayerElementMaterial");
                writer.AddPropertyInt(0);
                writer.WriteNodeInt("Version", 101);
                writer.WriteNodeString("Name", "");
                writer.WriteNodeString("MappingInformationType", "AllSame");
                writer.WriteNodeString("ReferenceInformationType", "IndexToDirect");
                writer.BeginNode("Materials");
                writer.AddPropertyIntArray(new[] { 0 });
                writer.EndNode();
                writer.EndNode(); // LayerElementMaterial

                // Layer
                writer.BeginNode("Layer");
                writer.AddPropertyInt(0);
                writer.WriteNodeInt("Version", 100);

                writer.BeginNode("LayerElement");
                writer.WriteNodeString("Type", "LayerElementNormal");
                writer.WriteNodeInt("TypedIndex", 0);
                writer.EndNode();

                if (uvs != null && uvs.Length == vertices.Length)
                {
                    writer.BeginNode("LayerElement");
                    writer.WriteNodeString("Type", "LayerElementUV");
                    writer.WriteNodeInt("TypedIndex", 0);
                    writer.EndNode();
                }

                writer.BeginNode("LayerElement");
                writer.WriteNodeString("Type", "LayerElementMaterial");
                writer.WriteNodeInt("TypedIndex", 0);
                writer.EndNode();

                writer.EndNode(); // Layer

                writer.EndNode(); // Geometry

                // 6b. Mesh Model
                // 6b. Mesh Model
                string nodeName = options.Name ?? "LOD_Mesh";
                writer.BeginNode("Model");
                writer.AddPropertyLong(modelId);
                writer.AddPropertyString($"{nodeName}\0\x01Model");
                writer.AddPropertyString("Mesh");
                writer.WriteNodeInt("Version", 232);
                writer.BeginNode("Properties70");
                WritePropertyPVector3D(writer, "Lcl Translation", "Lcl Translation", "", "A", 0.0, 0.0, 0.0);
                WritePropertyPVector3D(writer, "Lcl Rotation", "Lcl Rotation", "", "A", -90.0, -180.0, 0.0);
                WritePropertyPVector3D(writer, "Lcl Scaling", "Lcl Scaling", "", "A", 1.0, 1.0, 1.0);
                WritePropertyP(writer, "InheritType", "enum", "", "", 1);
                WritePropertyP(writer, "DefaultAttributeIndex", "int", "Integer", "", 0);
                writer.EndNode();
                writer.WriteNodeBool("Shading", true);
                writer.WriteNodeString("Culling", "CullingOff");
                writer.EndNode(); // Model (Mesh)

                // Bone map & relative transforms
                Dictionary<Transform, int> boneToIndex = new Dictionary<Transform, int>();
                int[] parentBoneIndices = new int[numBones];
                for (int b = 0; b < numBones; b++)
                {
                    if (options.Bones[b] != null && !boneToIndex.ContainsKey(options.Bones[b]))
                    {
                        boneToIndex[options.Bones[b]] = b;
                    }
                }

                for (int b = 0; b < numBones; b++)
                {
                    Transform bone = options.Bones[b];
                    int pIdx = -1;
                    if (bone != null)
                    {
                        Transform p = bone.parent;
                        while (p != null)
                        {
                            if (boneToIndex.TryGetValue(p, out int found))
                            {
                                pIdx = found;
                                break;
                            }
                            p = p.parent;
                        }
                    }
                    parentBoneIndices[b] = pIdx;
                }

                // 6c. Bones Model & NodeAttribute (if rigged)
                if (isRigged)
                {
                    // Armature NodeAttribute & Model (encapsulates root bones)
                    writer.BeginNode("NodeAttribute");
                    writer.AddPropertyLong(armatureAttrId);
                    writer.AddPropertyString("NodeAttribute::\0\x01NodeAttribute");
                    writer.AddPropertyString("Null");
                    writer.WriteNodeInt("Version", 100);
                    writer.EndNode();

                    writer.BeginNode("Model");
                    writer.AddPropertyLong(armatureId);
                    writer.AddPropertyString("Armature\0\x01Model");
                    writer.AddPropertyString("Null");
                    writer.WriteNodeInt("Version", 232);
                    writer.BeginNode("Properties70");
                    WritePropertyPVector3D(writer, "Lcl Translation", "Lcl Translation", "", "A", 0.0, 0.0, 0.0);
                    WritePropertyPVector3D(writer, "Lcl Rotation", "Lcl Rotation", "", "A", -90.0, -180.0, 0.0);
                    WritePropertyPVector3D(writer, "Lcl Scaling", "Lcl Scaling", "", "A", 1.0, 1.0, 1.0);
                    writer.EndNode();
                    writer.WriteNodeBool("Shading", true);
                    writer.WriteNodeString("Culling", "CullingOff");
                    writer.EndNode(); // Model (Armature)

                    // Mesh world matrix in FBX space (rotated [-90, -180, 0])
                    Matrix4x4 fbxMeshWorld = new Matrix4x4(
                        new Vector4(-1f, 0f, 0f, 0f),
                        new Vector4(0f, 0f, 1f, 0f),
                        new Vector4(0f, 1f, 0f, 0f),
                        new Vector4(0f, 0f, 0f, 1f)
                    );
                    Matrix4x4 fbxArmWorldInv = fbxMeshWorld.inverse;

                    for (int b = 0; b < numBones; b++)
                    {
                        Transform bone = options.Bones[b];
                        string bName = bone != null ? bone.name : $"Bone_{b}";

                        Vector3 localPos = Vector3.zero;
                        Vector3 fbxEuler = Vector3.zero;
                        Vector3 localScale = Vector3.one;

                        int pIdx = parentBoneIndices[b];
                        if (pIdx >= 0 && options.Bones[pIdx] != null)
                        {
                            // Child bone: local transform relative to parent bone
                            Matrix4x4 rel = options.Bones[pIdx].worldToLocalMatrix * bone.localToWorldMatrix;
                            DecomposeMatrix(rel, out localPos, out Quaternion localRot, out localScale);

                            Matrix4x4 rotM = Matrix4x4.Rotate(localRot);
                            Matrix4x4 S = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
                            Matrix4x4 fbxRotM = S * rotM * S;
                            fbxEuler = MatrixToEulerXYZ(fbxRotM);
                            localPos.z = -localPos.z;
                        }
                        else
                        {
                            // Root bone: parented to Armature
                            Matrix4x4 unityBindpose = (mesh.bindposes != null && b < mesh.bindposes.Length)
                                ? mesh.bindposes[b]
                                : Matrix4x4.identity;
                            Matrix4x4 unityBoneWorld = (mesh.bindposes != null && b < mesh.bindposes.Length)
                                ? unityBindpose.inverse
                                : (bone != null ? bone.localToWorldMatrix : Matrix4x4.identity);

                            Matrix4x4 fbxBoneWorld = GetFbxBoneWorldMatrix(unityBoneWorld);
                            Matrix4x4 rootLocalMat = fbxArmWorldInv * fbxBoneWorld;
                            DecomposeMatrix(rootLocalMat, out localPos, out Quaternion localRot, out localScale);
                            fbxEuler = MatrixToEulerXYZ(Matrix4x4.Rotate(localRot));
                        }

                        // Bone Model (LimbNode)
                        writer.BeginNode("Model");
                        writer.AddPropertyLong(boneModelBase + b);
                        writer.AddPropertyString($"{bName}\0\x01Model");
                        writer.AddPropertyString("LimbNode");
                        writer.WriteNodeInt("Version", 232);
                        writer.BeginNode("Properties70");
                        WritePropertyPVector3D(writer, "Lcl Translation", "Lcl Translation", "", "A", localPos.x, localPos.y, localPos.z);
                        WritePropertyPVector3D(writer, "Lcl Rotation", "Lcl Rotation", "", "A", fbxEuler.x, fbxEuler.y, fbxEuler.z);
                        WritePropertyPVector3D(writer, "Lcl Scaling", "Lcl Scaling", "", "A", localScale.x, localScale.y, localScale.z);
                        writer.EndNode();
                        writer.WriteNodeBool("Shading", true);
                        writer.WriteNodeString("Culling", "CullingOff");
                        writer.EndNode(); // Model (Bone)

                        // NodeAttribute (LimbNode)
                        writer.BeginNode("NodeAttribute");
                        writer.AddPropertyLong(boneAttrBase + b);
                        writer.AddPropertyString("NodeAttribute::\0\x01NodeAttribute");
                        writer.AddPropertyString("LimbNode");
                        writer.BeginNode("Properties70");
                        WritePropertyP(writer, "TypeFlags", "KString", "", "", "Skeleton");
                        WritePropertyP(writer, "Size", "double", "Number", "", 100.0);
                        writer.EndNode();
                        writer.WriteNodeString("TypeFlags", "Skeleton");
                        writer.EndNode(); // NodeAttribute
                    }

                    // Deformer (Skin)
                    writer.BeginNode("Deformer");
                    writer.AddPropertyLong(skinId);
                    writer.AddPropertyString("Deformer::Skin\0\x01Deformer");
                    writer.AddPropertyString("Skin");
                    writer.WriteNodeInt("Version", 101);
                    writer.WriteNodeDouble("Link_DeformAcuracy", 50.0);
                    writer.WriteNodeString("SkinningType", "Linear");
                    writer.EndNode(); // Skin

                    // Group vertices by bone
                    List<int>[] boneVertIndices = new List<int>[numBones];
                    List<double>[] boneVertWeights = new List<double>[numBones];
                    for (int b = 0; b < numBones; b++)
                    {
                        boneVertIndices[b] = new List<int>();
                        boneVertWeights[b] = new List<double>();
                    }

                    for (int v = 0; v < vertices.Length; v++)
                    {
                        BoneWeight bw = boneWeights[v];
                        AddWeight(bw.boneIndex0, bw.weight0, v, numBones, boneVertIndices, boneVertWeights);
                        AddWeight(bw.boneIndex1, bw.weight1, v, numBones, boneVertIndices, boneVertWeights);
                        AddWeight(bw.boneIndex2, bw.weight2, v, numBones, boneVertIndices, boneVertWeights);
                        AddWeight(bw.boneIndex3, bw.weight3, v, numBones, boneVertIndices, boneVertWeights);
                    }

                    // Clusters
                    for (int b = 0; b < numBones; b++)
                    {
                        Transform bone = options.Bones[b];
                        string bName = bone != null ? bone.name : $"Bone_{b}";

                        Matrix4x4 unityBindpose = (mesh.bindposes != null && b < mesh.bindposes.Length)
                            ? mesh.bindposes[b]
                            : Matrix4x4.identity;

                        Matrix4x4 unityBoneWorld = (mesh.bindposes != null && b < mesh.bindposes.Length)
                            ? unityBindpose.inverse
                            : (bone != null ? bone.localToWorldMatrix : Matrix4x4.identity);

                        Matrix4x4 fbxBoneWorld = GetFbxBoneWorldMatrix(unityBoneWorld);
                        double[] boneMatrix = Matrix4x4ToFbxArray(fbxBoneWorld);

                        // Cluster Transform: bind pose in bone space (TransformLink^-1 * MeshWorld)
                        Matrix4x4 clusterTransform = fbxBoneWorld.inverse * fbxMeshWorld;
                        double[] clusterTransformArray = Matrix4x4ToFbxArray(clusterTransform);

                        writer.BeginNode("Deformer");
                        writer.AddPropertyLong(clusterBase + b);
                        writer.AddPropertyString($"{bName}\0\x01SubDeformer");
                        writer.AddPropertyString("Cluster");
                        writer.WriteNodeInt("Version", 100);

                        writer.BeginNode("UserData");
                        writer.AddPropertyString("");
                        writer.AddPropertyString("");
                        writer.EndNode();

                        writer.BeginNode("Indexes");
                        writer.AddPropertyIntArray(boneVertIndices[b].ToArray());
                        writer.EndNode();

                        writer.BeginNode("Weights");
                        writer.AddPropertyDoubleArray(boneVertWeights[b].ToArray());
                        writer.EndNode();

                        writer.BeginNode("Transform");
                        writer.AddPropertyDoubleArray(clusterTransformArray);
                        writer.EndNode();

                        writer.BeginNode("TransformLink");
                        writer.AddPropertyDoubleArray(boneMatrix);
                        writer.EndNode();

                        writer.EndNode(); // Cluster
                    }

                    // Pose (BindPose)
                    writer.BeginNode("Pose");
                    writer.AddPropertyLong(poseId);
                    writer.AddPropertyString("Pose::BIND_POSES\0\x01Pose");
                    writer.AddPropertyString("BindPose");
                    writer.WriteNodeString("Type", "BindPose");
                    writer.WriteNodeInt("Version", 100);
                    writer.WriteNodeInt("NbPoseNodes", 2 + numBones);

                    double[] meshMatrixArray = Matrix4x4ToFbxArray(fbxMeshWorld);

                    // PoseNode for Mesh
                    writer.BeginNode("PoseNode");
                    writer.WriteNodeLong("Node", modelId);
                    writer.BeginNode("Matrix");
                    writer.AddPropertyDoubleArray(meshMatrixArray);
                    writer.EndNode();
                    writer.EndNode();

                    // PoseNode for Armature
                    writer.BeginNode("PoseNode");
                    writer.WriteNodeLong("Node", armatureId);
                    writer.BeginNode("Matrix");
                    writer.AddPropertyDoubleArray(meshMatrixArray);
                    writer.EndNode();
                    writer.EndNode();

                    // PoseNode for each bone
                    for (int b = 0; b < numBones; b++)
                    {
                        Matrix4x4 unityBindpose = (mesh.bindposes != null && b < mesh.bindposes.Length)
                            ? mesh.bindposes[b]
                            : Matrix4x4.identity;
                        Matrix4x4 unityBoneWorld = (mesh.bindposes != null && b < mesh.bindposes.Length)
                            ? unityBindpose.inverse
                            : (options.Bones[b] != null ? options.Bones[b].localToWorldMatrix : Matrix4x4.identity);
                        Matrix4x4 fbxBoneWorld = GetFbxBoneWorldMatrix(unityBoneWorld);
                        double[] boneMatrix = Matrix4x4ToFbxArray(fbxBoneWorld);

                        writer.BeginNode("PoseNode");
                        writer.WriteNodeLong("Node", boneModelBase + b);
                        writer.BeginNode("Matrix");
                        writer.AddPropertyDoubleArray(boneMatrix);
                        writer.EndNode();
                        writer.EndNode();
                    }

                    writer.EndNode(); // Pose
                }

                // 6d. Material
                writer.BeginNode("Material");
                writer.AddPropertyLong(matId);
                writer.AddPropertyString("Material::LOD_Material\0\x01Material");
                writer.AddPropertyString("");
                writer.WriteNodeInt("Version", 102);
                writer.WriteNodeString("ShadingModel", "phong");
                writer.WriteNodeInt("MultiLayer", 0);
                writer.BeginNode("Properties70");
                WritePropertyPColor(writer, "DiffuseColor", "Color", "", "A", 1.0, 1.0, 1.0);
                WritePropertyPColor(writer, "SpecularColor", "Color", "", "A", 0.2, 0.2, 0.2);
                writer.EndNode();
                writer.EndNode(); // Material

                // 6e. Texture & Video
                if (!string.IsNullOrEmpty(options.AlbedoTexturePath))
                {
                    string absPath = Path.GetFullPath(options.AlbedoTexturePath).Replace('\\', '/');
                    string relPath = Path.GetFileName(options.AlbedoTexturePath);

                    writer.BeginNode("Texture");
                    writer.AddPropertyLong(texId);
                    writer.AddPropertyString("Texture::Albedo\0\x01Texture");
                    writer.AddPropertyString("");
                    writer.WriteNodeString("Type", "TextureVideoClip");
                    writer.WriteNodeInt("Version", 202);
                    writer.WriteNodeString("TextureName", "Texture::Albedo");
                    writer.BeginNode("Properties70");
                    WritePropertyP(writer, "CurrentTextureBlendMode", "enum", "", "", 0);
                    writer.EndNode();
                    writer.WriteNodeString("FileName", absPath);
                    writer.WriteNodeString("RelativeFilename", relPath);
                    writer.WriteNodeString("Texture_Alpha_Source", "None");
                    writer.EndNode(); // Texture

                    writer.BeginNode("Video");
                    writer.AddPropertyLong(vidId);
                    writer.AddPropertyString("Video::Albedo\0\x01Video");
                    writer.AddPropertyString("Clip");
                    writer.WriteNodeString("Type", "Clip");
                    writer.BeginNode("Properties70");
                    writer.EndNode();
                    writer.WriteNodeInt("UseMipMap", 0);
                    writer.WriteNodeString("Filename", absPath);
                    writer.WriteNodeString("RelativeFilename", relPath);
                    writer.EndNode(); // Video
                }

                writer.EndNode(); // Objects

                // --- 7. Connections ---
                writer.BeginNode("Connections");

                // Mesh Geometry -> Mesh Model
                writer.WriteConnection("OO", geomId, modelId);
                // Mesh Model -> Root
                writer.WriteConnection("OO", modelId, 0);
                // Material -> Mesh Model
                writer.WriteConnection("OO", matId, modelId);

                if (!string.IsNullOrEmpty(options.AlbedoTexturePath))
                {
                    // Texture -> Material DiffuseColor
                    writer.WriteConnectionProp("OP", texId, matId, "DiffuseColor");
                    // Video -> Texture
                    writer.WriteConnection("OO", vidId, texId);
                }

                if (isRigged)
                {
                    // Armature NodeAttribute -> Armature Model
                    writer.WriteConnection("OO", armatureAttrId, armatureId);
                    // Armature Model -> Root
                    writer.WriteConnection("OO", armatureId, 0);

                    // Skin -> Geometry
                    writer.WriteConnection("OO", skinId, geomId);

                    for (int b = 0; b < numBones; b++)
                    {
                        long bModel = boneModelBase + b;
                        long bAttr = boneAttrBase + b;
                        long bCluster = clusterBase + b;

                        // Cluster -> Skin
                        writer.WriteConnection("OO", bCluster, skinId);
                        // Bone Model -> Cluster
                        writer.WriteConnection("OO", bModel, bCluster);
                        // NodeAttribute -> Bone Model
                        writer.WriteConnection("OO", bAttr, bModel);

                        // Bone hierarchy
                        int pIdx = parentBoneIndices[b];
                        if (pIdx >= 0)
                        {
                            writer.WriteConnection("OO", bModel, boneModelBase + pIdx);
                        }
                        else
                        {
                            writer.WriteConnection("OO", bModel, armatureId);
                        }
                    }
                }

                writer.EndNode(); // Connections

                // --- 8. Takes ---
                writer.BeginNode("Takes");
                writer.WriteNodeString("Current", "");
                writer.EndNode();

                // Finish file (writes top-level null record and footer)
                writer.Finish();

                File.WriteAllBytes(filePath, ms.ToArray());
                Debug.Log($"[MeshLodFbxExporter] Exported Binary FBX to: {filePath} ({ms.Length / 1024} KB)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MeshLodFbxExporter] Binary FBX export failed: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private static void AddWeight(int boneIndex, float weight, int vertIndex, int numBones, List<int>[] vertIndices, List<double>[] vertWeights)
        {
            if (weight > 1e-4f && boneIndex >= 0 && boneIndex < numBones)
            {
                vertIndices[boneIndex].Add(vertIndex);
                vertWeights[boneIndex].Add(weight);
            }
        }

        private static Matrix4x4 GetFbxBoneWorldMatrix(Matrix4x4 unityBoneWorld)
        {
            Matrix4x4 sLeft = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
            Matrix4x4 sRight = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
            return sLeft * unityBoneWorld * sRight;
        }

        private static double[] Matrix4x4ToFbxArray(Matrix4x4 m)
        {
            double[] result = new double[16];
            for (int c = 0; c < 4; c++)
            {
                for (int r = 0; r < 4; r++)
                {
                    result[c * 4 + r] = m[r, c];
                }
            }
            return result;
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

        private static Vector3 MatrixToEulerXYZ(Matrix4x4 m)
        {
            float sb = Mathf.Clamp(-m.m20, -1f, 1f);
            float beta = Mathf.Asin(sb);
            float cb = Mathf.Cos(beta);
            float alpha, gamma;
            if (Mathf.Abs(cb) > 1e-5f)
            {
                alpha = Mathf.Atan2(m.m21, m.m22);
                gamma = Mathf.Atan2(m.m10, m.m00);
            }
            else
            {
                alpha = Mathf.Atan2(-m.m12, m.m11);
                gamma = 0f;
            }
            return new Vector3(alpha * Mathf.Rad2Deg, beta * Mathf.Rad2Deg, gamma * Mathf.Rad2Deg);
        }

        private static void WriteObjectTypeCount(FbxBinaryWriter writer, string name, int count)
        {
            writer.BeginNode("ObjectType");
            writer.AddPropertyString(name);
            writer.WriteNodeInt("Count", count);
            writer.EndNode();
        }

        private static void WritePropertyP(FbxBinaryWriter writer, string name, string type1, string type2, string flag, int value)
        {
            writer.BeginNode("P");
            writer.AddPropertyString(name);
            writer.AddPropertyString(type1);
            writer.AddPropertyString(type2);
            writer.AddPropertyString(flag);
            writer.AddPropertyInt(value);
            writer.EndNode();
        }

        private static void WritePropertyP(FbxBinaryWriter writer, string name, string type1, string type2, string flag, double value)
        {
            writer.BeginNode("P");
            writer.AddPropertyString(name);
            writer.AddPropertyString(type1);
            writer.AddPropertyString(type2);
            writer.AddPropertyString(flag);
            writer.AddPropertyDouble(value);
            writer.EndNode();
        }

        private static void WritePropertyP(FbxBinaryWriter writer, string name, string type1, string type2, string flag, string value)
        {
            writer.BeginNode("P");
            writer.AddPropertyString(name);
            writer.AddPropertyString(type1);
            writer.AddPropertyString(type2);
            writer.AddPropertyString(flag);
            writer.AddPropertyString(value);
            writer.EndNode();
        }

        private static void WritePropertyPVector3D(FbxBinaryWriter writer, string name, string type1, string type2, string flag, double x, double y, double z)
        {
            writer.BeginNode("P");
            writer.AddPropertyString(name);
            writer.AddPropertyString(type1);
            writer.AddPropertyString(type2);
            writer.AddPropertyString(flag);
            writer.AddPropertyDouble(x);
            writer.AddPropertyDouble(y);
            writer.AddPropertyDouble(z);
            writer.EndNode();
        }

        private static void WritePropertyPColor(FbxBinaryWriter writer, string name, string type1, string type2, string flag, double r, double g, double b)
        {
            writer.BeginNode("P");
            writer.AddPropertyString(name);
            writer.AddPropertyString(type1);
            writer.AddPropertyString(type2);
            writer.AddPropertyString(flag);
            writer.AddPropertyDouble(r);
            writer.AddPropertyDouble(g);
            writer.AddPropertyDouble(b);
            writer.EndNode();
        }

        private static readonly byte[] FileIdBytes = new byte[]
        {
            0x28, 0xB3, 0x2A, 0xEB, 0xB6, 0x24, 0xCC, 0xC2, 0xBF, 0xC8, 0xB0, 0x2A, 0xA9, 0x2B, 0xFC, 0xF1
        };

        /// <summary>
        /// Helper for writing FBX 7.4 Binary format.
        /// </summary>
        private sealed class FbxBinaryWriter : IDisposable
        {
            private readonly Stream _stream;
            private readonly BinaryWriter _writer;
            private readonly Stack<NodeState> _stack = new Stack<NodeState>();

            private static readonly byte[] HeaderMagic = Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0\x1a\0");
            private static readonly byte[] FooterMagic1 = new byte[]
            {
                0xFA, 0xBC, 0xAB, 0x09, 0xD0, 0xC8, 0xD4, 0x66, 0xB1, 0x76, 0xFB, 0x83, 0x1C, 0xF7, 0x26, 0x7E
            };
            private static readonly byte[] FooterMagic2 = new byte[]
            {
                0xF8, 0x5A, 0x8C, 0x6A, 0xDE, 0xF5, 0xD9, 0x7E, 0xEC, 0xE9, 0x0C, 0xE3, 0x75, 0x8F, 0x29, 0x0B
            };

            private struct NodeState
            {
                public long StartOffset;
                public uint PropertyCount;
                public long PropertyListStart;
                public uint PropertyListLen;
                public bool HasChildren;
            }

            public FbxBinaryWriter(Stream stream)
            {
                _stream = stream;
                _writer = new BinaryWriter(stream);

                // Write 27-byte FBX Header
                _writer.Write(HeaderMagic);
                _writer.Write((uint)7400);
            }

            public void BeginNode(string name)
            {
                if (_stack.Count > 0)
                {
                    NodeState parent = _stack.Pop();
                    if (!parent.HasChildren)
                    {
                        parent.PropertyListLen = (uint)(_stream.Position - parent.PropertyListStart);
                        parent.HasChildren = true;
                    }
                    _stack.Push(parent);
                }

                long start = _stream.Position;
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);

                _writer.Write((uint)0); // EndOffset placeholder
                _writer.Write((uint)0); // NumProperties placeholder
                _writer.Write((uint)0); // PropertyListLen placeholder
                _writer.Write((byte)nameBytes.Length);
                _writer.Write(nameBytes);

                _stack.Push(new NodeState
                {
                    StartOffset = start,
                    PropertyCount = 0,
                    PropertyListStart = _stream.Position,
                    PropertyListLen = 0,
                    HasChildren = false
                });
            }

            public void EndNode()
            {
                NodeState node = _stack.Pop();
                if (node.HasChildren)
                {
                    _writer.Write(new byte[13]); // 13 null bytes terminating child nodes
                }
                else
                {
                    node.PropertyListLen = (uint)(_stream.Position - node.PropertyListStart);
                }

                long endOffset = _stream.Position;
                _stream.Seek(node.StartOffset, SeekOrigin.Begin);
                _writer.Write((uint)endOffset);
                _writer.Write(node.PropertyCount);
                _writer.Write(node.PropertyListLen);
                _stream.Seek(endOffset, SeekOrigin.Begin);
            }

            public void AddPropertyInt(int value)
            {
                _writer.Write((byte)'I');
                _writer.Write(value);
                IncrementPropCount();
            }

            public void AddPropertyLong(long value)
            {
                _writer.Write((byte)'L');
                _writer.Write(value);
                IncrementPropCount();
            }

            public void AddPropertyDouble(double value)
            {
                _writer.Write((byte)'D');
                _writer.Write(value);
                IncrementPropCount();
            }

            public void AddPropertyFloat(float value)
            {
                _writer.Write((byte)'F');
                _writer.Write(value);
                IncrementPropCount();
            }

            public void AddPropertyBool(bool value)
            {
                _writer.Write((byte)'C');
                _writer.Write((byte)(value ? 1 : 0));
                IncrementPropCount();
            }

            public void AddPropertyString(string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                _writer.Write((byte)'S');
                _writer.Write((uint)bytes.Length);
                _writer.Write(bytes);
                IncrementPropCount();
            }

            public void AddPropertyRawBytes(byte[] bytes)
            {
                _writer.Write((byte)'R');
                _writer.Write((uint)bytes.Length);
                _writer.Write(bytes);
                IncrementPropCount();
            }

            public void AddPropertyDoubleArray(double[] array)
            {
                _writer.Write((byte)'d');
                _writer.Write((uint)array.Length);
                _writer.Write((uint)0); // Uncompressed
                _writer.Write((uint)(array.Length * 8));
                for (int i = 0; i < array.Length; i++)
                {
                    _writer.Write(array[i]);
                }
                IncrementPropCount();
            }

            public void AddPropertyIntArray(int[] array)
            {
                _writer.Write((byte)'i');
                _writer.Write((uint)array.Length);
                _writer.Write((uint)0); // Uncompressed
                _writer.Write((uint)(array.Length * 4));
                for (int i = 0; i < array.Length; i++)
                {
                    _writer.Write(array[i]);
                }
                IncrementPropCount();
            }

            public void WriteNodeInt(string name, int value)
            {
                BeginNode(name);
                AddPropertyInt(value);
                EndNode();
            }

            public void WriteNodeLong(string name, long value)
            {
                BeginNode(name);
                AddPropertyLong(value);
                EndNode();
            }

            public void WriteNodeDouble(string name, double value)
            {
                BeginNode(name);
                AddPropertyDouble(value);
                EndNode();
            }

            public void WriteNodeBool(string name, bool value)
            {
                BeginNode(name);
                AddPropertyBool(value);
                EndNode();
            }

            public void WriteNodeString(string name, string value)
            {
                BeginNode(name);
                AddPropertyString(value);
                EndNode();
            }

            public void WriteConnection(string type, long childId, long parentId)
            {
                BeginNode("C");
                AddPropertyString(type);
                AddPropertyLong(childId);
                AddPropertyLong(parentId);
                EndNode();
            }

            public void WriteConnectionProp(string type, long childId, long parentId, string propName)
            {
                BeginNode("C");
                AddPropertyString(type);
                AddPropertyLong(childId);
                AddPropertyLong(parentId);
                AddPropertyString(propName);
                EndNode();
            }

            public void Finish()
            {
                // Top-level null record (13 zero bytes)
                _writer.Write(new byte[13]);

                // FBX 7.4 binary footer with 16-byte alignment
                _writer.Write(FooterMagic1);
                _writer.Write(new byte[4]);

                long ofs = _stream.Position;
                int pad = (int)(((ofs + 15) & ~15) - ofs);
                if (pad == 0) pad = 16;
                _writer.Write(new byte[pad]);

                _writer.Write((uint)7400);
                _writer.Write(new byte[120]);
                _writer.Write(FooterMagic2);
            }

            private void IncrementPropCount()
            {
                if (_stack.Count > 0)
                {
                    NodeState s = _stack.Pop();
                    s.PropertyCount++;
                    _stack.Push(s);
                }
            }

            public void Dispose()
            {
                _writer?.Dispose();
            }
        }
    }
}
