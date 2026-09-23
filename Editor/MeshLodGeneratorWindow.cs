using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MeshLODGenerator
{
    /// <summary>
    /// Editor Window tool to generate lower-detail LOD versions of any rigged or non-rigged GameObject.
    /// Provides interactive 3D preview, atlas inspection, and export to .fbx, .glb, .obj and .png.
    /// </summary>
    public class MeshLodGeneratorWindow : EditorWindow
    {
        [MenuItem("Tools/Mesh LOD Generator", false, 50)]
        public static void Open()
        {
            MeshLodGeneratorWindow window = GetWindow<MeshLodGeneratorWindow>("Mesh LOD Generator");
            window.minSize = new Vector2(420f, 620f);
        }

        [SerializeField] private GameObject _targetObject;
        [SerializeField] private MeshLodGenerator.GenerationSettings _settings = new MeshLodGenerator.GenerationSettings();
        [SerializeField] private int _tab = 0;
        [SerializeField] private int _atlasSubTab = 0;
        [SerializeField] private bool _showIgnoredMeshes = true;

        // Viewport camera controls
        [SerializeField] private float _orbitYaw = 135f;
        [SerializeField] private float _orbitPitch = 15f;
        [SerializeField] private float _orbitZoom = 1f;

        private MeshLodGenerator.MeshLodResult _result;
        private PreviewRenderUtility _previewRender;
        private Vector2 _scroll;
        private string _lastError;
        private static readonly string[] TabNames = { "3D View", "Atlas Maps", "Stats & Info" };
        private static readonly string[] AtlasTabNames = { "Albedo", "Emission", "Custom Channel" };

        private void OnEnable()
        {
            if (_settings == null) _settings = new MeshLodGenerator.GenerationSettings();
        }

        private void OnDisable()
        {
            _previewRender?.Cleanup();
            _previewRender = null;
        }

        private void OnGUI()
        {
            MeshLodUI.Header("Mesh LOD Generator", "Create optimized lower-detail LODs for any rigged or non-rigged mesh.");

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            DrawSourceSection();
            DrawSettingsSection();

            if (!string.IsNullOrEmpty(_lastError))
            {
                MeshLodUI.Help(_lastError, MessageType.Error);
            }

            if (_result != null && _result.LodMesh != null)
            {
                EditorGUILayout.Space(6);
                _tab = GUILayout.Toolbar(_tab, TabNames);
                EditorGUILayout.Space(4);

                switch (_tab)
                {
                    case 0: DrawViewportTab(); break;
                    case 1: DrawAtlasTab(); break;
                    default: DrawInfoTab(); break;
                }

                DrawExportSection();
                DrawSceneActionsSection();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawSourceSection()
        {
            MeshLodUI.SectionTitle("Target Object");
            MeshLodUI.BeginCard();

            _targetObject = (GameObject)EditorGUILayout.ObjectField("Target GameObject", _targetObject, typeof(GameObject), true);

            if (_targetObject == null && Selection.activeGameObject != null)
            {
                if (GUILayout.Button($"Use Selection: {Selection.activeGameObject.name}"))
                {
                    _targetObject = Selection.activeGameObject;
                }
            }

            if (_targetObject != null)
            {
                Renderer[] renderers = _targetObject.GetComponentsInChildren<Renderer>(false);
                int triCount = 0;
                int vertCount = 0;
                bool isRigged = false;
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                    {
                        isRigged = true;
                        triCount += smr.sharedMesh.triangles.Length / 3;
                        vertCount += smr.sharedMesh.vertexCount;
                    }
                    else if (renderers[i] is MeshRenderer mr && mr.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
                    {
                        triCount += mf.sharedMesh.triangles.Length / 3;
                        vertCount += mf.sharedMesh.vertexCount;
                    }
                }

                string rigInfo = isRigged ? "Rigged (SkinnedMeshRenderer)" : "Static Mesh (MeshFilter)";
                EditorGUILayout.LabelField("Type", rigInfo, EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField("Renderers", $"{renderers.Length} found", EditorStyles.miniLabel);
                EditorGUILayout.LabelField("Source Geometry", $"{triCount:N0} tris, {vertCount:N0} verts", EditorStyles.miniLabel);
            }
            else
            {
                MeshLodUI.Note("Select or drag any GameObject with MeshFilter or SkinnedMeshRenderer components.");
            }

            MeshLodUI.EndCard();
        }

        private void DrawSettingsSection()
        {
            MeshLodUI.SectionTitle("Decimation & Atlas Settings");
            MeshLodUI.BeginCard();

            _settings.UsePercentage = EditorGUILayout.Toggle("Decimate by Percentage", _settings.UsePercentage);
            if (_settings.UsePercentage)
            {
                _settings.TargetTrianglePercentage = EditorGUILayout.Slider("Target Ratio", _settings.TargetTrianglePercentage, 0.05f, 0.90f);
                MeshLodUI.Note($"Will reduce mesh to ~{_settings.TargetTrianglePercentage * 100:0}% of original triangles.");
            }
            else
            {
                _settings.TargetTriangleCount = EditorGUILayout.IntSlider("Target Triangles", _settings.TargetTriangleCount, 300, 25000);
            }

            _settings.AtlasSize = EditorGUILayout.IntPopup("Atlas Texture Size", _settings.AtlasSize,
                new[] { "256", "512", "1024", "2048", "4096" },
                new[] { 256, 512, 1024, 2048, 4096 });

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Decimation Topology & Materials", EditorStyles.boldLabel);
            _settings.EnableSymmetricDecimation = EditorGUILayout.Toggle(
                new GUIContent("Symmetric Decimation", "Pairs mirror vertices across X=0 and collapses edges symmetrically to maintain 1:1 bilateral wireframe symmetry."),
                _settings.EnableSymmetricDecimation);
            if (_settings.EnableSymmetricDecimation)
            {
                _settings.SymmetryTolerance = EditorGUILayout.Slider(
                    new GUIContent("Symmetry Tolerance (m)", "Max distance on X=0 plane or between mirror vertices. 0.001 = 1mm."),
                    _settings.SymmetryTolerance, 0.0001f, 0.01f);
            }

            _settings.SeparateByMaterial = EditorGUILayout.Toggle(
                new GUIContent("Separate by Material", "Separates mesh into parts based on Material/Submesh, decimates them with locked boundary loops to prevent seams, then merges them back together."),
                _settings.SeparateByMaterial);

            if (_settings.SeparateByMaterial)
            {
                EditorGUI.indentLevel++;
                _settings.PreserveSubmeshMaterials = EditorGUILayout.Toggle(
                    new GUIContent("Preserve Original Materials", "Keeps original materials across multiple submeshes instead of baking everything into a single-draw-call texture atlas."),
                    _settings.PreserveSubmeshMaterials);
                if (_settings.PreserveSubmeshMaterials)
                {
                    MeshLodUI.Note("Multi-submesh LOD will be generated. Atlas baking will be bypassed.");
                }
                EditorGUI.indentLevel--;
            }

            _settings.CullHiddenGeometry = EditorGUILayout.Toggle(new GUIContent("Cull Occluded Geometry", "Removes internal or hidden triangles (e.g. inner body under clothes, cavities) to maximize visible triangle budget."), _settings.CullHiddenGeometry);
            _settings.ComputeAmbientOcclusion = EditorGUILayout.Toggle("Bake Contact AO", _settings.ComputeAmbientOcclusion);

            Animator anim = _targetObject != null ? _targetObject.GetComponentInChildren<Animator>() : null;
            if (anim != null && anim.avatar != null && anim.avatar.isHuman)
            {
                _settings.EnforceTPose = EditorGUILayout.Toggle(new GUIContent("Enforce T-Pose", "Applies standardized humanoid T-Pose before snapshotting. Uncheck to preserve the avatar's current scene pose."), _settings.EnforceTPose);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Texture Channels", EditorStyles.boldLabel);
            _settings.BakeEmission = EditorGUILayout.Toggle("Bake Emission Map", _settings.BakeEmission);
            _settings.BakeCustomChannel = EditorGUILayout.Toggle("Bake Custom Channel", _settings.BakeCustomChannel);
            if (_settings.BakeCustomChannel)
            {
                _settings.CustomChannelProperty = EditorGUILayout.TextField("Shader Property", _settings.CustomChannelProperty);
                MeshLodUI.Note("E.g. _MetallicGlossMap, _BumpMap, or custom texture slot name.");
            }

            EditorGUILayout.Space(4);
            _showIgnoredMeshes = EditorGUILayout.Foldout(_showIgnoredMeshes, $"Ignored Meshes ({_settings.IgnoredRenderers.Count})", true, EditorStyles.foldoutHeader);
            if (_showIgnoredMeshes)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.HelpBox("Specify Renderers or GameObjects to exclude from LOD generation.", MessageType.None);

                EditorGUILayout.BeginHorizontal();
                UnityEngine.Object toAdd = EditorGUILayout.ObjectField("Add To Ignore", null, typeof(UnityEngine.Object), true);
                if (toAdd != null)
                {
                    if (toAdd is GameObject go)
                    {
                        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        {
                            if (!_settings.IgnoredRenderers.Contains(r)) _settings.IgnoredRenderers.Add(r);
                        }
                    }
                    else if (toAdd is Renderer ren)
                    {
                        if (!_settings.IgnoredRenderers.Contains(ren)) _settings.IgnoredRenderers.Add(ren);
                    }
                }
                EditorGUILayout.EndHorizontal();

                for (int i = _settings.IgnoredRenderers.Count - 1; i >= 0; i--)
                {
                    EditorGUILayout.BeginHorizontal();
                    _settings.IgnoredRenderers[i] = (Renderer)EditorGUILayout.ObjectField(_settings.IgnoredRenderers[i], typeof(Renderer), true);
                    if (GUILayout.Button("✕", GUILayout.Width(24)))
                    {
                        _settings.IgnoredRenderers.RemoveAt(i);
                    }
                    EditorGUILayout.EndHorizontal();
                }

                if (_settings.IgnoredRenderers.Count > 0)
                {
                    if (GUILayout.Button("Clear All Ignored", EditorStyles.miniButton))
                    {
                        _settings.IgnoredRenderers.Clear();
                    }
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(_targetObject == null))
            {
                if (MeshLodUI.PrimaryButton("Generate LOD", 32f))
                {
                    Generate();
                    GUIUtility.ExitGUI();
                }
            }

            MeshLodUI.EndCard();
        }

        private void Generate()
        {
            _lastError = null;
            _result = null;

            try
            {
                _result = MeshLodGenerator.Generate(_targetObject, _settings);
                if (_result == null)
                {
                    _lastError = "Generation failed or returned no mesh. Check the Console for details.";
                }
                else
                {
                    _tab = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _lastError = $"Error during generation: {ex.Message}";
            }
        }

        private void DrawViewportTab()
        {
            if (_result?.LodMesh == null) return;

            float side = Mathf.Clamp(position.width - 24f, 220f, 480f);
            Rect rect = GUILayoutUtility.GetRect(side, side, GUILayout.ExpandWidth(false));
            HandleOrbitInput(rect);

            if (Event.current.type == EventType.Repaint)
            {
                _previewRender ??= new PreviewRenderUtility();
                _previewRender.camera.fieldOfView = 30f;

                Bounds bounds = _result.LodMesh.bounds;
                float distance = bounds.extents.magnitude * 2.2f / Mathf.Max(_orbitZoom, 0.05f) + 0.1f;
                Quaternion orbit = Quaternion.Euler(_orbitPitch, _orbitYaw, 0f);

                _previewRender.BeginPreview(rect, GUIStyle.none);
                Camera cam = _previewRender.camera;
                cam.transform.SetPositionAndRotation(bounds.center + orbit * (Vector3.back * distance), orbit);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = distance * 6f + 20f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.18f, 0.19f, 0.22f, 1f);

                _previewRender.lights[0].intensity = 1.2f;
                _previewRender.lights[0].transform.rotation = Quaternion.Euler(40f, _orbitYaw - 30f, 0f);
                _previewRender.lights[1].intensity = 0.4f;
                _previewRender.ambientColor = new Color(0.35f, 0.35f, 0.38f, 1f);

                if (_result.LodMaterials != null && _result.LodMaterials.Length > 1)
                {
                    for (int sm = 0; sm < _result.LodMesh.subMeshCount; sm++)
                    {
                        Material mat = sm < _result.LodMaterials.Length ? _result.LodMaterials[sm] : _result.LodMaterial;
                        if (mat != null)
                        {
                            _previewRender.DrawMesh(_result.LodMesh, Matrix4x4.identity, mat, sm);
                        }
                    }
                }
                else if (_result.LodMaterial != null)
                {
                    _previewRender.DrawMesh(_result.LodMesh, Matrix4x4.identity, _result.LodMaterial, 0);
                }

                cam.Render();

                Texture previewTex = _previewRender.EndPreview();
                GUI.DrawTexture(rect, previewTex, ScaleMode.StretchToFill, false);
            }

            MeshLodUI.Note("Drag mouse to orbit, scroll wheel to zoom.");
        }

        private void HandleOrbitInput(Rect rect)
        {
            Event e = Event.current;
            if (!rect.Contains(e.mousePosition)) return;

            if (e.type == EventType.MouseDrag && (e.button == 0 || e.button == 1))
            {
                _orbitYaw += e.delta.x * 0.6f;
                _orbitPitch = Mathf.Clamp(_orbitPitch + e.delta.y * 0.6f, -85f, 85f);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.ScrollWheel)
            {
                _orbitZoom = Mathf.Clamp(_orbitZoom * (1f - e.delta.y * 0.04f), 0.15f, 6f);
                e.Use();
                Repaint();
            }
        }

        private void DrawAtlasTab()
        {
            _atlasSubTab = GUILayout.Toolbar(_atlasSubTab, AtlasTabNames);
            EditorGUILayout.Space(4);

            Texture2D activeTex = _atlasSubTab switch
            {
                0 => _result.AlbedoAtlas,
                1 => _result.EmissionAtlas,
                _ => _result.CustomChannelAtlas
            };

            if (activeTex == null)
            {
                MeshLodUI.Help("No texture baked for this channel (was it enabled in settings?).", MessageType.Info);
                return;
            }

            float side = Mathf.Clamp(position.width - 24f, 220f, 480f);
            Rect rect = GUILayoutUtility.GetRect(side, side, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(rect, activeTex);
            MeshLodUI.Note($"{activeTex.name}: {activeTex.width}x{activeTex.height}, Format: {activeTex.format}");
        }

        private void DrawInfoTab()
        {
            MeshLodUI.BeginCard();
            var rep = _result.Report;
            EditorGUILayout.LabelField("Triangles", $"{rep.ResultTriangles:N0} (from {rep.SourceTriangles:N0})");
            float reduction = rep.SourceTriangles > 0 ? (1f - (float)rep.ResultTriangles / rep.SourceTriangles) * 100f : 0f;
            EditorGUILayout.LabelField("Triangle Reduction", $"{reduction:0.0}%");
            EditorGUILayout.LabelField("Vertices", $"{rep.ResultVertices:N0} (from {rep.SourceVertices:N0})");
            EditorGUILayout.LabelField("Generation Time", $"{rep.TotalSeconds:0.00} seconds");
            EditorGUILayout.LabelField("Skinning", _result.IsRigged ? $"Rigged ({_result.Bones?.Length ?? 0} bones)" : "Static Mesh");
            Bounds b = _result.LodMesh.bounds;
            EditorGUILayout.LabelField("Mesh Bounds", $"Size: ({b.size.x:0.00}, {b.size.y:0.00}, {b.size.z:0.00})m");
            MeshLodUI.EndCard();
        }

        private void DrawExportSection()
        {
            MeshLodUI.SectionTitle("Export Options");
            MeshLodUI.BeginCard();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Export .GLB"))
            {
                ExportGlb();
            }
            if (GUILayout.Button("Export .FBX"))
            {
                ExportFbx();
            }
            if (GUILayout.Button("Export .OBJ"))
            {
                ExportObj();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Save Albedo (.png)"))
            {
                SaveTexturePng(_result.AlbedoAtlas, "Albedo");
            }
            using (new EditorGUI.DisabledScope(_result.EmissionAtlas == null))
            {
                if (GUILayout.Button("Save Emission (.png)"))
                {
                    SaveTexturePng(_result.EmissionAtlas, "Emission");
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);
            if (GUILayout.Button("Save as Unity Asset (.asset)"))
            {
                SaveAsUnityAsset();
            }

            if (MeshLodUI.PrimaryButton("Export All (Mesh + Textures)...", 26f))
            {
                ExportAll();
            }

            MeshLodUI.EndCard();
        }

        private void DrawSceneActionsSection()
        {
            MeshLodUI.SectionTitle("Scene Actions");
            MeshLodUI.BeginCard();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Spawn LOD in Scene"))
            {
                Vector3 offset = Vector3.right * (_result.LodMesh.bounds.size.x * 1.25f + 0.25f);
                MeshLodGenerator.SpawnLodInstance(_targetObject, _result, offset);
            }
            if (GUILayout.Button("Setup LODGroup on Target"))
            {
                SetupLodGroup();
            }
            EditorGUILayout.EndHorizontal();

            MeshLodUI.EndCard();
        }

        private void ExportGlb()
        {
            string path = EditorUtility.SaveFilePanel("Export GLB", "", $"{_targetObject.name}_LOD.glb", "glb");
            if (string.IsNullOrEmpty(path)) return;

            MeshLodGlbExporter.ExportOptions opt = new MeshLodGlbExporter.ExportOptions
            {
                Name = $"{_targetObject.name}_LOD",
                Mesh = _result.LodMesh,
                AlbedoTexture = _result.AlbedoAtlas,
                EmissionTexture = _result.EmissionAtlas,
                Bones = _result.Bones,
                RootBone = _result.RootBone
            };
            MeshLodGlbExporter.ExportGlb(path, opt);
            EditorUtility.RevealInFinder(path);
        }

        private void ExportFbx()
        {
            string path = EditorUtility.SaveFilePanel("Export FBX", "", $"{_targetObject.name}_LOD.fbx", "fbx");
            if (string.IsNullOrEmpty(path)) return;

            string dir = Path.GetDirectoryName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);
            string texPath = null;
            if (_result.AlbedoAtlas != null)
            {
                texPath = Path.Combine(dir, $"{baseName}_Albedo.png");
                File.WriteAllBytes(texPath, _result.AlbedoAtlas.EncodeToPNG());
            }

            MeshLodFbxExporter.ExportOptions opt = new MeshLodFbxExporter.ExportOptions
            {
                Name = $"{_targetObject.name}_LOD",
                Mesh = _result.LodMesh,
                AlbedoTexturePath = texPath,
                TargetGameObject = _targetObject,
                Bones = _result.Bones,
                RootBone = _result.RootBone,
                LodMaterial = _result.LodMaterial,
                LodMaterials = _result.LodMaterials
            };
            MeshLodFbxExporter.ExportFbx(path, opt);
            EditorUtility.RevealInFinder(path);
        }

        private void ExportObj()
        {
            string path = EditorUtility.SaveFilePanel("Export OBJ", "", $"{_targetObject.name}_LOD.obj", "obj");
            if (string.IsNullOrEmpty(path)) return;

            string dir = Path.GetDirectoryName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);
            string texPath = null;
            if (_result.AlbedoAtlas != null)
            {
                texPath = Path.Combine(dir, $"{baseName}_Albedo.png");
                File.WriteAllBytes(texPath, _result.AlbedoAtlas.EncodeToPNG());
            }

            MeshLodObjExporter.ExportObj(path, _result.LodMesh, texPath);
            EditorUtility.RevealInFinder(path);
        }

        private void SaveTexturePng(Texture2D tex, string suffix)
        {
            if (tex == null) return;
            string path = EditorUtility.SaveFilePanel($"Save {suffix} PNG", "", $"{_targetObject.name}_{suffix}.png", "png");
            if (string.IsNullOrEmpty(path)) return;

            byte[] png = tex.EncodeToPNG();
            File.WriteAllBytes(path, png);
            Debug.Log($"[MeshLodGenerator] Saved PNG: {path}");
            EditorUtility.RevealInFinder(path);
        }

        private void SaveAsUnityAsset()
        {
            string path = EditorUtility.SaveFilePanelInProject("Save Mesh Asset", $"{_targetObject.name}_LOD.asset", "asset", "Select location in Assets");
            if (string.IsNullOrEmpty(path)) return;

            string dir = Path.GetDirectoryName(path);
            string baseName = Path.GetFileNameWithoutExtension(path);

            if (_result.AlbedoAtlas != null)
            {
                string texPath = Path.Combine(dir, $"{baseName}_Albedo.png").Replace('\\', '/');
                File.WriteAllBytes(texPath, _result.AlbedoAtlas.EncodeToPNG());
                AssetDatabase.ImportAsset(texPath);
                Texture2D importedTex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);
                if (importedTex != null && _result.LodMaterial != null)
                {
                    if (_result.LodMaterial.HasProperty("_BaseMap")) _result.LodMaterial.SetTexture("_BaseMap", importedTex);
                    else if (_result.LodMaterial.HasProperty("_MainTex")) _result.LodMaterial.SetTexture("_MainTex", importedTex);
                }
            }

            if (_result.EmissionAtlas != null)
            {
                string emPath = Path.Combine(dir, $"{baseName}_Emission.png").Replace('\\', '/');
                File.WriteAllBytes(emPath, _result.EmissionAtlas.EncodeToPNG());
                AssetDatabase.ImportAsset(emPath);
                Texture2D importedEm = AssetDatabase.LoadAssetAtPath<Texture2D>(emPath);
                if (importedEm != null && _result.LodMaterial != null && _result.LodMaterial.HasProperty("_EmissionMap"))
                {
                    _result.LodMaterial.SetTexture("_EmissionMap", importedEm);
                }
            }

            if (_result.LodMaterial != null)
            {
                string matPath = Path.Combine(dir, $"{baseName}_Material.mat").Replace('\\', '/');
                AssetDatabase.CreateAsset(_result.LodMaterial, matPath);
            }

            AssetDatabase.CreateAsset(_result.LodMesh, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorGUIUtility.PingObject(_result.LodMesh);
            Debug.Log($"[MeshLodGenerator] Saved Unity assets to: {dir}");
        }

        private void ExportAll()
        {
            string folder = EditorUtility.SaveFolderPanel("Select Export Folder", "", "");
            if (string.IsNullOrEmpty(folder)) return;

            string baseName = $"{_targetObject.name}_LOD";

            // Save PNGs
            string albedoPath = null;
            if (_result.AlbedoAtlas != null)
            {
                albedoPath = Path.Combine(folder, $"{baseName}_Albedo.png");
                File.WriteAllBytes(albedoPath, _result.AlbedoAtlas.EncodeToPNG());
            }
            if (_result.EmissionAtlas != null)
            {
                string emissionPath = Path.Combine(folder, $"{baseName}_Emission.png");
                File.WriteAllBytes(emissionPath, _result.EmissionAtlas.EncodeToPNG());
            }
            if (_result.CustomChannelAtlas != null)
            {
                string customPath = Path.Combine(folder, $"{baseName}_Channel.png");
                File.WriteAllBytes(customPath, _result.CustomChannelAtlas.EncodeToPNG());
            }

            // Save GLB
            string glbPath = Path.Combine(folder, $"{baseName}.glb");
            MeshLodGlbExporter.ExportGlb(glbPath, new MeshLodGlbExporter.ExportOptions
            {
                Name = baseName,
                Mesh = _result.LodMesh,
                AlbedoTexture = _result.AlbedoAtlas,
                EmissionTexture = _result.EmissionAtlas,
                Bones = _result.Bones,
                RootBone = _result.RootBone
            });

            // Save FBX
            string fbxPath = Path.Combine(folder, $"{baseName}.fbx");
            MeshLodFbxExporter.ExportFbx(fbxPath, new MeshLodFbxExporter.ExportOptions
            {
                Name = baseName,
                Mesh = _result.LodMesh,
                AlbedoTexturePath = albedoPath,
                TargetGameObject = _targetObject,
                Bones = _result.Bones,
                RootBone = _result.RootBone,
                LodMaterial = _result.LodMaterial,
                LodMaterials = _result.LodMaterials
            });

            EditorUtility.RevealInFinder(folder);
            Debug.Log($"[MeshLodGenerator] Exported all LOD assets to: {folder}");
        }

        private void SetupLodGroup()
        {
            LODGroup group = _targetObject.GetComponent<LODGroup>();
            if (group == null) group = Undo.AddComponent<LODGroup>(_targetObject);

            GameObject lodChild = MeshLodGenerator.SpawnLodInstance(_targetObject, _result, Vector3.zero);
            lodChild.transform.SetParent(_targetObject.transform, false);

            Renderer[] origRenderers = _targetObject.GetComponentsInChildren<Renderer>(false);
            Renderer lodRenderer = lodChild.GetComponent<Renderer>();

            List<Renderer> lod0List = new List<Renderer>();
            for (int i = 0; i < origRenderers.Length; i++)
            {
                if (origRenderers[i] != lodRenderer) lod0List.Add(origRenderers[i]);
            }

            LOD[] lods = new LOD[2];
            lods[0] = new LOD(0.6f, lod0List.ToArray());
            lods[1] = new LOD(0.15f, new[] { lodRenderer });

            group.SetLODs(lods);
            group.RecalculateBounds();
            Debug.Log($"[MeshLodGenerator] Configured LODGroup on {_targetObject.name} with LOD0 (full) and LOD1 ({_result.Report.ResultTriangles} tris).");
        }
    }
}
