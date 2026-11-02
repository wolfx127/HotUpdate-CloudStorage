using System;
using System.IO;
using HybridCLR.Editor.Installer;
using HybridCLR.Editor.Settings;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace HotUpdateDemo.EditorTools
{
    /// <summary>
    /// 一键搭建演示场景、UI、材质引用与 HybridCLR 设置。
    /// 菜单：Hot Update Demo/1. Setup Project
    /// 命令行：-executeMethod HotUpdateDemo.EditorTools.DemoProjectBuilder.SetupFromCommandLine
    /// </summary>
    public static class DemoProjectBuilder
    {
        public const string StartScenePath = "Assets/Scenes/Start.unity";
        public const string PlayScenePath = "Assets/Scenes/Play.unity";
        public const string HeroPath = "Assets/Art/Hero.png";
        public const string ArenaPath = "Assets/Art/Arena.png";
        public const string StartUxmlPath = "Assets/UI/StartUI.uxml";
        public const string PlayUxmlPath = "Assets/UI/PlayUI.uxml";
        public const string ThemePath = "Assets/UI/GameTheme.tss";
        public const string PanelSettingsPath = "Assets/UI/GamePanelSettings.asset";
        public const string HotUpdateAsmdefPath = "Assets/Scripts/HotUpdate/HotUpdate.asmdef";
        public const string ConfigPath = "Assets/StreamingAssets/appconfig.json";

        [MenuItem("Hot Update Demo/1. Setup Project", priority = 1)]
        public static void SetupProject()
        {
            Debug.Log("========== Hot Update Demo: Setup Project ==========");
            EnsureFolders();
            ConfigureArtImport();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);

            CreatePanelSettings();
            ConfigureHybridClrSettings();
            TryInstallHybridClr();
            CreateStartScene();
            CreatePlayScene();
            RepairUIDocument(StartScenePath, StartUxmlPath);
            RepairUIDocument(PlayScenePath, PlayUxmlPath);
            SetBuildScenes();
            RemoveLegacyScenesAndUi();
            EnsureConfigFile();
            EnsureLinkXml();
            EditorSceneManager.OpenScene(StartScenePath);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("========== Setup 完成。下一步：Hot Update Demo/2. Build Initial Content ==========");
            CheckEnvironment();
        }

        public static void SetupFromCommandLine()
        {
            try
            {
                SetupProject();
                EditorApplication.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorApplication.Exit(1);
            }
        }

        [MenuItem("Hot Update Demo/Check Environment", priority = 90)]
        public static void CheckEnvironment()
        {
            InstallerController installer = new InstallerController();
            bool installed = installer.HasInstalledHybridCLR();
            bool winIl2cpp = HasWindowsIl2CppModule();
            string panelState = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath) != null ? "OK" : "missing";
            string startSceneState = File.Exists(Path.GetFullPath(StartScenePath)) ? "OK" : "missing";
            string playSceneState = File.Exists(Path.GetFullPath(PlayScenePath)) ? "OK" : "missing";
            string contentState = File.Exists(Path.Combine(Application.streamingAssetsPath, "AB", "catalog.json"))
                ? "OK"
                : "missing (run Build Initial Content)";

            Debug.Log(
                "\n[Hot Update Demo 环境检查]\n" +
                $"Unity: {Application.unityVersion}\n" +
                $"Active build target: {EditorUserBuildSettings.activeBuildTarget}\n" +
                $"HybridCLR package: {installer.PackageVersion}, installed: {installed} ({installer.InstalledLibil2cppVersion ?? "none"})\n" +
                $"Windows IL2CPP build support: {(winIl2cpp ? "OK" : "MISSING")}\n" +
                $"PanelSettings: {panelState}\n" +
                $"Start scene: {startSceneState}\n" +
                $"Play scene: {playSceneState}\n" +
                $"StreamingAssets/AB: {contentState}\n" +
                $"Active backend: {PlayerSettings.GetScriptingBackend(NamedBuildTarget.Standalone)}\n" +
                "提示：HybridCLR 真机/独立包必须使用 IL2CPP；如果 IL2CPP 模块缺失，请先通过 Unity Hub 安装\n" +
                "      Windows Build Support (IL2CPP)。缺少模块时仍然可以在 Editor Play Mode 验证 AB + 动态 DLL 流程。");

            if (!winIl2cpp)
            {
                Debug.LogWarning("Windows IL2CPP 模块缺失：Build Player 会被拒绝，详见 Docs/操作指南.md。");
            }
        }

        [MenuItem("Hot Update Demo/Open Start Scene", priority = 92)]
        public static void OpenStartScene()
        {
            if (File.Exists(Path.GetFullPath(StartScenePath)))
            {
                EditorSceneManager.OpenScene(StartScenePath);
            }
        }

        public static bool HasWindowsIl2CppModule()
        {
            string variations = Path.Combine(EditorApplication.applicationContentsPath,
                "PlaybackEngines/WindowsStandaloneSupport/Variations");
            if (!Directory.Exists(variations))
            {
                variations = Path.Combine(EditorApplication.applicationContentsPath,
                    "PlaybackEngines/windowsstandalonesupport/Variations");
            }

            if (!Directory.Exists(variations))
            {
                return false;
            }

            return Directory.GetDirectories(variations, "*il2cpp*").Length > 0;
        }

        private static void EnsureFolders()
        {
            foreach (string folder in new[]
                     {
                         "Assets/Art", "Assets/UI", "Assets/Scenes", "Assets/StreamingAssets",
                         "Assets/Scripts/Editor", "Assets/HotUpdate/Build", "ServerData"
                     })
            {
                Directory.CreateDirectory(Path.GetFullPath(folder));
            }
        }

        private static void ConfigureArtImport()
        {
            if (!File.Exists(Path.GetFullPath(HeroPath)) || !File.Exists(Path.GetFullPath(ArenaPath)))
            {
                throw new FileNotFoundException("缺少 Assets/Art/Hero.png 或 Arena.png，请先运行 Tools/generate_art.py。");
            }

            ConfigureSprite(HeroPath, 1024);
            ConfigureSprite(ArenaPath, 2048);
        }

        private static void ConfigureSprite(string path, int maxSize)
        {
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException("无法读取 TextureImporter: " + path);
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = 100f;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.filterMode = FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = maxSize;
            importer.SaveAndReimport();
        }

        private static PanelSettings CreatePanelSettings()
        {
            ThemeStyleSheet theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            if (theme == null)
            {
                throw new InvalidOperationException("找不到 ThemeStyleSheet: " + ThemePath);
            }

            PanelSettings panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, PanelSettingsPath);
            }

            panel.themeStyleSheet = theme;
            panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(1280, 720);
            panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            panel.match = 0.5f;
            panel.sortingOrder = 10;
            EditorUtility.SetDirty(panel);
            Debug.Log("[Setup] PanelSettings OK");
            return panel;
        }

        private static void CreateStartScene()
        {
            VisualTreeAsset uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(StartUxmlPath);
            PanelSettings panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (uxml == null || panel == null)
            {
                throw new InvalidOperationException("找不到 StartUI.uxml 或 PanelSettings");
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            CreateCamera("Main Camera", new Color(0.05f, 0.06f, 0.08f, 1f));

            GameObject uiGo = new GameObject("Start UI");
            StartSceneController controller = uiGo.AddComponent<StartSceneController>();
            UIDocument document = uiGo.GetComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = uxml;
            document.sortingOrder = 10;
            EditorUtility.SetDirty(document);
            EditorUtility.SetDirty(controller);

            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(StartScenePath)) ?? "Assets/Scenes");
            EditorSceneManager.SaveScene(scene, StartScenePath);
            Debug.Log("[Setup] Start.unity generated");
        }

        private static void CreatePlayScene()
        {
            VisualTreeAsset uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(PlayUxmlPath);
            PanelSettings panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            Sprite hero = AssetDatabase.LoadAssetAtPath<Sprite>(HeroPath);
            Sprite arena = AssetDatabase.LoadAssetAtPath<Sprite>(ArenaPath);
            if (uxml == null || panel == null || hero == null || arena == null)
            {
                throw new InvalidOperationException("场景依赖资源缺失（PlayUI/PanelSettings/Hero/Arena）。");
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Camera camera = CreateCamera("Main Camera", new Color(0.012f, 0.031f, 0.055f, 1f));
            camera.orthographic = true;
            camera.orthographicSize = 5.2f;

            GameObject arenaGo = new GameObject("Arena Backdrop");
            SpriteRenderer arenaRenderer = arenaGo.AddComponent<SpriteRenderer>();
            arenaRenderer.sprite = arena;
            arenaRenderer.sortingOrder = -100;
            arenaGo.transform.position = new Vector3(0f, 0f, 2f);

            GameObject playerGo = new GameObject("Player");
            SpriteRenderer heroRenderer = playerGo.AddComponent<SpriteRenderer>();
            heroRenderer.sprite = hero;
            heroRenderer.sortingOrder = 1;
            playerGo.transform.position = new Vector3(0f, -0.20f, 0f);
            playerGo.transform.localScale = Vector3.one * 0.94f;
            PlayerHealth health = playerGo.AddComponent<PlayerHealth>();

            GameObject uiGo = new GameObject("HUD");
            GameHud hud = uiGo.AddComponent<GameHud>();
            UIDocument document = uiGo.GetComponent<UIDocument>();
            document.panelSettings = panel;
            document.visualTreeAsset = uxml;
            document.sortingOrder = 10;
            EditorUtility.SetDirty(document);
            EditorUtility.SetDirty(hud);

            GameObject rootGo = new GameObject("GameRoot");
            CloudSyncService cloud = rootGo.AddComponent<CloudSyncService>();
            GameRoot root = rootGo.AddComponent<GameRoot>();

            SetObjectReference(root, "player", health);
            SetObjectReference(root, "heroRenderer", heroRenderer);
            SetObjectReference(root, "hud", hud);
            SetObjectReference(root, "cloud", cloud);
            SetObjectReference(root, "fallbackHero", hero);

            SetObjectReference(hud, "document", document);
            SetObjectReference(hud, "player", health);
            SetObjectReference(hud, "cloud", cloud);

            EditorSceneManager.MarkSceneDirty(scene);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(PlayScenePath)) ?? "Assets/Scenes");
            EditorSceneManager.SaveScene(scene, PlayScenePath);
            Debug.Log("[Setup] Play.unity generated");
        }

        private static Camera CreateCamera(string name, Color background)
        {
            GameObject cameraGo = new GameObject(name);
            cameraGo.tag = "MainCamera";
            Camera camera = cameraGo.AddComponent<Camera>();
            cameraGo.AddComponent<AudioListener>();
            camera.orthographic = true;
            camera.orthographicSize = 5.2f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = background;
            camera.transform.position = new Vector3(0f, 0f, -10f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 100f;
            return camera;
        }

        private static void RepairUIDocument(string scenePath, string uxmlPath)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            UIDocument document = UnityEngine.Object.FindFirstObjectByType<UIDocument>();
            PanelSettings panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            VisualTreeAsset uxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
            if (document == null || panel == null || uxml == null)
            {
                throw new InvalidOperationException($"修复 UIDocument 失败: {scenePath}");
            }

            document.panelSettings = panel;
            document.visualTreeAsset = uxml;
            EditorUtility.SetDirty(document);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[Setup] UIDocument repaired: {scenePath}, panel={document.panelSettings != null}, uxml={document.visualTreeAsset != null}");
        }

        private static void SetBuildScenes()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(StartScenePath, true),
                new EditorBuildSettingsScene(PlayScenePath, true)
            };
        }

        private static void RemoveLegacyScenesAndUi()
        {
            foreach (string legacy in new[]
                     {
                         "Assets/Scenes/Main.unity",
                         "Assets/Scenes/SampleScene.unity",
                         "Assets/UI/GameUI.uxml",
                         "Assets/UI/GameUI.uss"
                     })
            {
                if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(legacy) != null)
                {
                    AssetDatabase.DeleteAsset(legacy);
                }
            }
        }

        private static void SetObjectReference(UnityEngine.Object target, string fieldName, UnityEngine.Object value)
        {
            SerializedObject serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(fieldName);
            if (property == null)
            {
                throw new InvalidOperationException($"{target.GetType().Name} 没有序列化字段 {fieldName}");
            }

            property.objectReferenceValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetUIDocumentAssets(UIDocument document, PanelSettings panel, VisualTreeAsset uxml)
        {
            SerializedObject serialized = new SerializedObject(document);
            SerializedProperty panelProperty = serialized.FindProperty("m_PanelSettings");
            SerializedProperty sourceProperty = serialized.FindProperty("sourceAsset");
            if (panelProperty == null || sourceProperty == null)
            {
                throw new InvalidOperationException("UIDocument 序列化字段名变化，无法设置 PanelSettings/sourceAsset。");
            }

            panelProperty.objectReferenceValue = panel;
            sourceProperty.objectReferenceValue = uxml;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(document);
        }


        private static void ConfigureHybridClrSettings()
        {
            AssemblyDefinitionAsset hotUpdateAssembly =
                AssetDatabase.LoadAssetAtPath<AssemblyDefinitionAsset>(HotUpdateAsmdefPath);
            if (hotUpdateAssembly == null)
            {
                throw new InvalidOperationException("找不到 HotUpdate.asmdef");
            }

            HybridCLRSettings settings = HybridCLRSettings.Instance;
            settings.enable = true;
            settings.useGlobalIl2cpp = false;
            settings.hotUpdateAssemblyDefinitions = new[] { hotUpdateAssembly };
            settings.hotUpdateAssemblies = Array.Empty<string>();
            settings.preserveHotUpdateAssemblies = Array.Empty<string>();
            settings.patchAOTAssemblies = Array.Empty<string>();
            settings.outputLinkFile = "HybridCLRGenerate/link.xml";
            settings.outputAOTGenericReferenceFile = "HybridCLRGenerate/AOTGenericReferences.cs";
            HybridCLRSettings.Save();
            Debug.Log("[Setup] HybridCLR settings saved: HotUpdate");
        }

        private static void TryInstallHybridClr()
        {
            InstallerController installer = new InstallerController();
            if (installer.HasInstalledHybridCLR() &&
                string.Equals(installer.PackageVersion, installer.InstalledLibil2cppVersion,
                    StringComparison.Ordinal))
            {
                Debug.Log($"[Setup] HybridCLR 已安装: {installer.InstalledLibil2cppVersion}");
                return;
            }

            Debug.Log("[Setup] 正在克隆 HybridCLR / il2cpp_plus（需要访问 gitee.com，首次会慢一些）…");
            try
            {
                installer.InstallDefaultHybridCLR();
                Debug.Log("[Setup] HybridCLR 安装完成: " + installer.InstalledLibil2cppVersion);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Setup] HybridCLR 安装失败，可在菜单 HybridCLR/Installer 里手动安装。\n" + ex);
            }
        }

        private static void EnsureConfigFile()
        {
            if (File.Exists(Path.GetFullPath(ConfigPath)))
            {
                return;
            }

            AppConfig config = new AppConfig();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ConfigPath)) ?? "Assets/StreamingAssets");
            File.WriteAllText(Path.GetFullPath(ConfigPath), JsonUtility.ToJson(config, true));
            AssetDatabase.ImportAsset(ConfigPath);
        }

        private static void EnsureLinkXml()
        {
            string path = "Assets/link.xml";
            if (!File.Exists(Path.GetFullPath(path)))
            {
                File.WriteAllText(Path.GetFullPath(path),
                    "<linker>\n" +
                    "  <assembly fullname=\"HotUpdateDemo.Baseline\" preserve=\"all\" />\n" +
                    "</linker>\n");
                AssetDatabase.ImportAsset(path);
            }
        }
    }
}
