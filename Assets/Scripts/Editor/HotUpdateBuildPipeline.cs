using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using HybridCLR.Editor.Commands;
using HybridCLR.Editor.Installer;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HotUpdateDemo.EditorTools
{
    /// <summary>
    /// 代码热更 + 资源热更的构建入口。
    ///
    /// 核心顺序：
    /// 1. HybridCLR 编译出 HybridCLRData/HotUpdateDlls/StandaloneWindows64/HotUpdate.dll；
    /// 2. 把 DLL 改名成 .bytes（TextAsset），和 Sprite 一起用 BuildPipeline 打成两个 AB；
    /// 3. 生成自己的 catalog.json（版本号、SHA256、资源 key）；
    /// 4. 初始包复制到 StreamingAssets/AB，更新包只写 ServerData/AB；
    /// 5. 重启游戏后，手写 AssetBundleManager 会拉新 catalog、下载并校验 AB、加载新 DLL/Sprite。
    /// </summary>
    public static class HotUpdateBuildPipeline
    {
        private const string ProjectSettingsFile = "ProjectSettings/HotUpdateBuildSettings.json";
        private const string ServerAbDir = "ServerData/AB";
        private const string StreamingAbDir = "Assets/StreamingAssets/AB";
        private const string DllBytesPath = "Assets/HotUpdate/Build/HotUpdate.dll.bytes";
        private const string HeroAssetPath = "Assets/Art/Hero.png";
        private const string CodeBundleName = "hotupdate_code.bundle";
        private const string AssetBundleName = "hotupdate_assets.bundle";

        [Serializable]
        private sealed class BuildSettings
        {
            public int buildNumber = 1;
        }

        [Serializable]
        private sealed class BuildInfo
        {
            public int buildNumber;
            public string contentVersion;
            public string builtAtUtc;
            public string codeBundle;
            public string assetBundle;
        }

        [MenuItem("Hot Update Demo/2. Build Initial Content (copy into StreamingAssets)", priority = 2)]
        public static void BuildInitialContent()
        {
            BuildContent(copyToStreamingAssets: true, buildNumber: 1);
            SaveBuildNumber(1);
            DemoProjectBuilder.CheckEnvironment();
        }

        [MenuItem("Hot Update Demo/3. Build Update Content (server only, bump version)", priority = 3)]
        public static void BuildUpdateContent()
        {
            BuildSettings settings = LoadBuildSettings();
            int next = Mathf.Max(2, settings.buildNumber + 1);
            BuildContent(copyToStreamingAssets: false, buildNumber: next);
            SaveBuildNumber(next);
            DemoProjectBuilder.CheckEnvironment();
        }

        [MenuItem("Hot Update Demo/4. Build Windows IL2CPP Player", priority = 4)]
        public static void BuildPlayer()
        {
            if (!DemoProjectBuilder.HasWindowsIl2CppModule())
            {
                throw new BuildFailedException(
                    "当前 Unity 安装缺少 Windows Build Support (IL2CPP) 模块。" +
                    "HybridCLR 不能使用 Mono 打包。请打开 Unity Hub -> Installs -> 6000.2.14f1 -> Add modules，" +
                    "安装 Windows Build Support (IL2CPP)，再回到 Unity 执行本菜单。");
            }

            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                throw new BuildFailedException("请先把 Build Target 切换到 Windows x86_64，再执行打包。");
            }

            InstallerController installer = new InstallerController();
            if (!installer.HasInstalledHybridCLR())
            {
                throw new BuildFailedException("HybridCLR 尚未安装。请先执行 Hot Update Demo/1. Setup Project。");
            }

            // 必须在 Generate/All 之前切到 IL2CPP：AOT 裁剪/桥接生成依赖 IL2CPP 工具链。
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.Standalone, Il2CppCompilerConfiguration.Release);

            // 生成 AOT 裁剪、bridge、补充元数据等；必须在 IL2CPP 打包前执行。
            PrebuildCommand.GenerateAll();

            // 把当前版本内容放进随包 StreamingAssets，保证断网也能启动。
            BuildSettings settings = LoadBuildSettings();
            BuildContent(copyToStreamingAssets: true, buildNumber: Mathf.Max(1, settings.buildNumber));

            List<string> scenes = new List<string>();
            foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
            {
                if (scene.enabled)
                {
                    scenes.Add(scene.path);
                }
            }

            string output = Path.GetFullPath("Builds/Windows/EmberGuardian.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(output) ?? "Builds/Windows");

            BuildPlayerOptions options = new BuildPlayerOptions
            {
                scenes = scenes.ToArray(),
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new BuildFailedException($"打包失败: {report.summary.result}, errors={report.summary.totalErrors}");
            }

            Debug.Log($"[Build] Player OK -> {output}, size={report.summary.totalSize / (1024 * 1024)} MB");
        }

        [MenuItem("Hot Update Demo/Apply V2 Demo Patch (code + image)", priority = 20)]
        public static void ApplyV2Patch()
        {
            string sourceCode = Path.GetFullPath("Tools/PatchV2/DemoHotUpdateModule.cs");
            string sourceArt = Path.GetFullPath("Tools/Art/HeroHotfix.png");
            if (!File.Exists(sourceCode) || !File.Exists(sourceArt))
            {
                throw new FileNotFoundException("找不到 Tools/PatchV2 或 Tools/Art/HeroHotfix.png");
            }

            File.Copy(sourceCode, Path.GetFullPath("Assets/Scripts/HotUpdate/DemoHotUpdateModule.cs"), true);
            File.Copy(sourceArt, Path.GetFullPath("Assets/Art/Hero.png"), true);
            AssetDatabase.ImportAsset("Assets/Scripts/HotUpdate/DemoHotUpdateModule.cs", ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset("Assets/Art/Hero.png", ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            Debug.Log("[Patch] 已切换到 V2 源码与金色贴图。等编译完成后执行 " +
                      "Hot Update Demo/3. Build Update Content，然后重启游戏（保持 CloudServer 运行）。");
        }

        [MenuItem("Hot Update Demo/Reset V1 Demo Patch (code + image)", priority = 21)]
        public static void ResetV1Patch()
        {
            File.Copy(Path.GetFullPath("Tools/PatchV1/DemoHotUpdateModule.cs"),
                Path.GetFullPath("Assets/Scripts/HotUpdate/DemoHotUpdateModule.cs"), true);
            File.Copy(Path.GetFullPath("Tools/Art/HeroDefault.png"),
                Path.GetFullPath("Assets/Art/Hero.png"), true);
            AssetDatabase.ImportAsset("Assets/Scripts/HotUpdate/DemoHotUpdateModule.cs", ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset("Assets/Art/Hero.png", ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            Debug.Log("[Patch] 已恢复 V1 源码与青色贴图。");
        }

        [MenuItem("Hot Update Demo/Open ServerData Folder", priority = 80)]
        public static void OpenServerData()
        {
            Directory.CreateDirectory(Path.GetFullPath(ServerAbDir));
            EditorUtility.RevealInFinder(Path.GetFullPath(ServerAbDir));
        }

        [MenuItem("Hot Update Demo/Open Persistent Cache Folder", priority = 81)]
        public static void OpenPersistentCache()
        {
            string path = Path.Combine(Application.persistentDataPath, "AB");
            Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        [MenuItem("Hot Update Demo/Reset Build Counter", priority = 82)]
        public static void ResetBuildCounter()
        {
            SaveBuildNumber(1);
            Debug.Log("[Build] buildNumber reset to 1; 下次 Build Update 会生成 1.0.1。");
        }

        [MenuItem("Hot Update Demo/Clear Downloaded AB Cache", priority = 83)]
        public static void ClearDownloadedCache()
        {
            string cache = Path.Combine(Application.persistentDataPath, "AB");
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, true);
                Debug.Log("[AB] 已删除下载缓存: " + cache);
            }
            else
            {
                Debug.Log("[AB] 没有下载缓存。");
            }
        }

        private static void BuildContent(bool copyToStreamingAssets, int buildNumber)
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
            {
                throw new BuildFailedException(
                    $"当前 Active Build Target 是 {EditorUserBuildSettings.activeBuildTarget}。" +
                    "本示例的 AB 固定按 StandaloneWindows64 构建，请先切换平台。");
            }

            Debug.Log($"[Build] Step 1/4: compile HybridCLR hot update DLL (buildNumber={buildNumber})");
            CompileDllCommand.CompileDll(BuildTarget.StandaloneWindows64, false);

            string compiledDll = Path.GetFullPath(
                "HybridCLRData/HotUpdateDlls/StandaloneWindows64/HotUpdate.dll");
            if (!File.Exists(compiledDll))
            {
                throw new FileNotFoundException(
                    "HybridCLR 没有产出 HotUpdate.dll。检查 ProjectSettings/HybridCLRSettings.asset 中是否包含 HotUpdate，以及控制台编译错误。",
                    compiledDll);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DllBytesPath)) ?? "Assets/HotUpdate/Build");
            File.Copy(compiledDll, Path.GetFullPath(DllBytesPath), true);
            AssetDatabase.ImportAsset(DllBytesPath, ImportAssetOptions.ForceUpdate);
            TextAsset dllText = AssetDatabase.LoadAssetAtPath<TextAsset>(DllBytesPath);
            Sprite heroSprite = AssetDatabase.LoadAssetAtPath<Sprite>(HeroAssetPath);
            if (dllText == null || heroSprite == null)
            {
                throw new InvalidOperationException(
                    $"资源导入失败。dllText={dllText != null}, heroSprite={heroSprite != null}");
            }

            string outputAbs = Path.GetFullPath(ServerAbDir);
            if (Directory.Exists(outputAbs))
            {
                Directory.Delete(outputAbs, true);
            }

            Directory.CreateDirectory(outputAbs);
            Debug.Log($"[Build] Step 2/4: build AB -> {outputAbs}");

            AssetBundleBuild codeBundle = new AssetBundleBuild
            {
                assetBundleName = CodeBundleName,
                assetNames = new[] { DllBytesPath }
            };

            AssetBundleBuild artBundle = new AssetBundleBuild
            {
                assetBundleName = AssetBundleName,
                assetNames = new[] { HeroAssetPath }
            };

            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                outputAbs,
                new[] { codeBundle, artBundle },
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                throw new BuildFailedException("BuildAssetBundles 返回 null，请查看 Console。");
            }

            Debug.Log($"[Build] Step 3/4: write catalog (DLL asset name='{dllText.name}', sprite='{heroSprite.name}')");
            string contentVersion = "1.0." + Mathf.Max(0, buildNumber - 1);
            ContentCatalog catalog = new ContentCatalog
            {
                schemaVersion = 1,
                buildNumber = buildNumber,
                contentVersion = contentVersion,
                builtAtUtc = DateTime.UtcNow.ToString("o"),
                bundles = new[]
                {
                    MakeBundleEntry(manifest, outputAbs, CodeBundleName),
                    MakeBundleEntry(manifest, outputAbs, AssetBundleName)
                },
                assets = new[]
                {
                    new AssetEntry
                    {
                        key = "hotupdate_dll",
                        bundle = CodeBundleName,
                        asset = dllText.name,
                        type = "TextAsset"
                    },
                    new AssetEntry
                    {
                        key = "hero_sprite",
                        bundle = AssetBundleName,
                        asset = heroSprite.name,
                        type = "Sprite"
                    }
                }
            };

            string catalogJson = JsonUtility.ToJson(catalog, true) + "\n";
            File.WriteAllText(Path.Combine(outputAbs, "catalog.json"), catalogJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outputAbs, "build-info.json"),
                JsonUtility.ToJson(new BuildInfo
                {
                    buildNumber = buildNumber,
                    contentVersion = contentVersion,
                    builtAtUtc = catalog.builtAtUtc,
                    codeBundle = CodeBundleName,
                    assetBundle = AssetBundleName
                }, true) + "\n", new UTF8Encoding(false));

            if (copyToStreamingAssets)
            {
                Debug.Log("[Build] Step 4/4: copy AB to Assets/StreamingAssets/AB");
                string streamingAbs = Path.GetFullPath(StreamingAbDir);
                if (Directory.Exists(streamingAbs))
                {
                    Directory.Delete(streamingAbs, true);
                }

                FileUtil.CopyFileOrDirectory(outputAbs, streamingAbs);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }
            else
            {
                Debug.Log("[Build] Step 4/4: server-only update (StreamingAssets unchanged)");
            }

            Debug.Log(
                $"[Build] 完成: contentVersion={contentVersion}, buildNumber={buildNumber}, " +
                $"mode={(copyToStreamingAssets ? "initial" : "update")}\n" +
                $"catalog: {Path.Combine(outputAbs, "catalog.json")}\n" +
                "提示：更新包必须让 CloudServer 指向项目根目录，并在重启游戏前刷新网页确认 /ServerData/AB/catalog.json。");
        }

        private static BundleEntry MakeBundleEntry(AssetBundleManifest manifest, string outputAbs, string bundleName)
        {
            string file = Path.Combine(outputAbs, bundleName);
            if (!File.Exists(file))
            {
                throw new FileNotFoundException("构建产物缺失: " + file);
            }

            return new BundleEntry
            {
                name = bundleName,
                file = bundleName,
                sha256 = Sha256(file),
                size = new FileInfo(file).Length,
                dependencies = manifest.GetAllDependencies(bundleName) ?? Array.Empty<string>()
            };
        }

        private static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder builder = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        private static BuildSettings LoadBuildSettings()
        {
            try
            {
                if (File.Exists(Path.GetFullPath(ProjectSettingsFile)))
                {
                    BuildSettings settings = JsonUtility.FromJson<BuildSettings>(
                        File.ReadAllText(Path.GetFullPath(ProjectSettingsFile)));
                    if (settings != null)
                    {
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Build] 读取 build settings 失败，使用默认值: " + ex.Message);
            }

            return new BuildSettings();
        }

        private static void SaveBuildNumber(int buildNumber)
        {
            BuildSettings settings = new BuildSettings { buildNumber = buildNumber };
            string path = Path.GetFullPath(ProjectSettingsFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "ProjectSettings");
            File.WriteAllText(path, JsonUtility.ToJson(settings, true));
        }
    }
}
