using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Baseline.Core;
using HybridCLR.Editor.Commands;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    /// <summary>
    /// 构建热更内容：
    ///   编译 HotUpdate.dll
    ///   → 复制成 Assets 下的 .bytes
    ///   → 自动绑定到 ContentManifest（DLL 由脚本绑，Hero 由 SO 引用决定）
    ///   → 显式打一个 content_boot.bundle（SO + DLL + Hero）
    ///   → buildNumber + 1，写 catalog.json
    ///
    /// 不使用 AssetBundle 标签：资源归属由 ContentManifest 的直接引用和这里的 assetNames 决定。
    /// </summary>
    public static class BuildAssetBundles
    {
        private const string OutputDir = "ServerData/AB";
        private const string BaselineDir = "Assets/StreamingAssets/AB";
        private const string CatalogName = "catalog.json";
        private const string ManifestPath = "Assets/HotUpdate/ContentManifest.asset";
        private const string HotDllDir = "HybridCLRData/HotUpdateDlls/StandaloneWindows64";
        private const string HotDllName = "HotUpdate.dll";
        private const string BytesPath = "Assets/HotUpdate/Build/HotUpdate.dll.bytes";
        private const BuildTarget Target = BuildTarget.StandaloneWindows64;

        // 第一次建 SO 时的默认 Hero 候选，按顺序找第一个 Sprite。
        private static readonly string[] FallbackHeroPaths =
        {
            "Assets/Art/Hero.png",
            "Assets/Art/AndroidHero.png",
        };

        [MenuItem("HotUpdate/1. Build Content", priority = 1)]
        public static void BuildAll()
        {
            Build(false);
        }

        [MenuItem("HotUpdate/2. Build Content + Sync Baseline", priority = 2)]
        public static void BuildAndSyncBaseline()
        {
            Build(true);
        }

        private static void Build(bool syncBaseline)
        {
            // 先读旧 catalog 的版本号，再清目录（清完就没了）。
            ContentCatalog old = ReadCatalog(Path.Combine(OutputDir, CatalogName))
                                 ?? ReadCatalog(Path.Combine(BaselineDir, CatalogName));
            int buildNumber = old != null ? old.buildNumber + 1 : 1;

            CompileHotUpdateDll();
            ContentManifest manifest = EnsureManifest();

            string heroPath = AssetDatabase.GetAssetPath(manifest.heroSprite);
            if (string.IsNullOrEmpty(heroPath))
            {
                throw new Exception("[Content] ContentManifest.heroSprite 不是工程内的资源。");
            }

            WarnIfLabeled(ManifestPath);
            WarnIfLabeled(BytesPath);
            WarnIfLabeled(heroPath);

            var build = new AssetBundleBuild
            {
                assetBundleName = ContentManifest.BundleName,
                assetNames = new[]
                {
                    ManifestPath,
                    BytesPath,
                    heroPath,
                }
            };

            CleanDirectory(OutputDir);

            AssetBundleManifest built = BuildPipeline.BuildAssetBundles(
                OutputDir,
                new[] { build },
                BuildAssetBundleOptions.ChunkBasedCompression |
                BuildAssetBundleOptions.ForceRebuildAssetBundle,
                Target);

            if (built == null)
            {
                throw new Exception("[Content] BuildAssetBundles 失败，看 Console 里的报错。");
            }

            ContentCatalog catalog = MakeCatalog(built, buildNumber);
            File.WriteAllText(
                Path.Combine(OutputDir, CatalogName),
                JsonUtility.ToJson(catalog, true) + "\n",
                new UTF8Encoding(false));

            if (syncBaseline)
            {
                SyncBaseline(catalog);
            }

            AssetDatabase.Refresh();

            Debug.Log(
                $"[Content] 完成：build={catalog.buildNumber}, content={catalog.contentVersion}, " +
                $"bundle={catalog.bundles.Length}, baseline={syncBaseline}");
        }

        /// <summary>
        /// 编译热更程序集，并把产物复制成 Assets 下的 .bytes。
        /// 固定路径覆盖，GUID 不变，SO 里的引用不会失效。
        /// </summary>
        private static void CompileHotUpdateDll()
        {
            CompileDllCommand.CompileDll(Target, false);

            string source = Path.Combine(HotDllDir, HotDllName);
            if (!File.Exists(source))
            {
                throw new Exception("没有编出 " + source + "，看 Console 里的编译报错。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(BytesPath));
            File.Copy(source, BytesPath, true);
            AssetDatabase.ImportAsset(BytesPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
        }

        /// <summary>
        /// 取（或创建）ContentManifest，并把 DLL 自动绑进去；
        /// heroSprite 为空时按约定路径自动找一张 Sprite，找不到就报错。
        /// </summary>
        private static ContentManifest EnsureManifest()
        {
            string dir = Path.GetDirectoryName(ManifestPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            ContentManifest manifest = AssetDatabase.LoadAssetAtPath<ContentManifest>(ManifestPath);
            if (manifest == null)
            {
                manifest = ScriptableObject.CreateInstance<ContentManifest>();
                AssetDatabase.CreateAsset(manifest, ManifestPath);
                Debug.Log("[Content] 已创建 " + ManifestPath);
            }

            TextAsset dll = AssetDatabase.LoadAssetAtPath<TextAsset>(BytesPath);
            if (dll == null)
            {
                throw new Exception("[Content] HotUpdate.dll.bytes 导入失败: " + BytesPath);
            }

            manifest.hotUpdateDll = dll;

            if (manifest.heroSprite == null)
            {
                foreach (string path in FallbackHeroPaths)
                {
                    Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                    if (sprite != null)
                    {
                        manifest.heroSprite = sprite;
                        Debug.Log("[Content] 自动绑定 heroSprite: " + path);
                        break;
                    }
                }
            }

            if (manifest.heroSprite == null)
            {
                throw new Exception("[Content] ContentManifest.heroSprite 为空。" +
                                    "请打开 " + ManifestPath + " 把 Hero Sprite 拖进 heroSprite 字段。");
            }

            EditorUtility.SetDirty(manifest);
            AssetDatabase.SaveAssets();
            return manifest;
        }

        private static ContentCatalog MakeCatalog(AssetBundleManifest built, int buildNumber)
        {
            string bundleName = ContentManifest.BundleName;
            string filePath = Path.Combine(OutputDir, bundleName);
            if (!File.Exists(filePath))
            {
                throw new Exception("[Content] 没有生成 " + filePath);
            }

            var bundles = new List<BundleEntry>
            {
                new BundleEntry
                {
                    name = bundleName,
                    file = bundleName,
                    sha256 = Sha256(filePath),
                    size = new FileInfo(filePath).Length,
                    dependencies = built.GetAllDependencies(bundleName) ?? Array.Empty<string>()
                }
            };

            return new ContentCatalog
            {
                schemaVersion = 2,
                buildNumber = buildNumber,
                contentVersion = "1.0." + (buildNumber - 1),
                builtAtUtc = DateTime.UtcNow.ToString("o"),
                bundles = bundles.ToArray()
            };
        }

        private static void SyncBaseline(ContentCatalog catalog)
        {
            CleanDirectory(BaselineDir);

            File.Copy(
                Path.Combine(OutputDir, CatalogName),
                Path.Combine(BaselineDir, CatalogName),
                true);

            foreach (BundleEntry bundle in catalog.bundles)
            {
                File.Copy(
                    Path.Combine(OutputDir, bundle.file),
                    Path.Combine(BaselineDir, bundle.file),
                    true);
            }

            AssetDatabase.Refresh();
        }

        private static void WarnIfLabeled(string assetPath)
        {
            AssetImporter importer = AssetImporter.GetAtPath(assetPath);
            if (importer != null && !string.IsNullOrEmpty(importer.assetBundleName))
            {
                Debug.LogWarning($"[Content] {assetPath} 还带着旧 AB 标签 " +
                                 $"'{importer.assetBundleName}'；显式打包会忽略它，建议清空。");
            }
        }

        private static void CleanDirectory(string dir)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }

            Directory.CreateDirectory(dir);
        }

        private static ContentCatalog ReadCatalog(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                return JsonUtility.FromJson<ContentCatalog>(File.ReadAllText(path));
            }
            catch
            {
                return null;
            }
        }

        private static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    builder.Append(value.ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
