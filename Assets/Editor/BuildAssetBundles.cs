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
    /// 按 AssetBundle 标签打包，并自动生成 catalog.json。
    ///
    /// 约定（方案 A）：
    ///   标签名去掉 .bundle 就是 catalog 里的 key。
    ///   例如 hotupdate_dll.bundle -> key = hotupdate_dll
    ///        hero_sprite.bundle    -> key = hero_sprite
    ///
    /// 脚本里不写任何资源路径、不写哈希、不写版本号。
    /// </summary>
    public static class BuildAssetBundles
    {
        private const string OutputDir = "ServerData/AB";
        private const string BaselineDir = "Assets/StreamingAssets/AB";
        private const string CatalogName = "catalog.json";
        private const string BundleSuffix = ".bundle";
        private const string HotDllDir = "HybridCLRData/HotUpdateDlls/StandaloneWindows64";
        private const string HotDllName = "HotUpdate.dll";
        private const string BytesPath = "Assets/HotUpdate/Build/HotUpdate.dll.bytes";
        private const BuildTarget Target = BuildTarget.StandaloneWindows64;

        [MenuItem("HotUpdate/1. Build AssetBundles", priority = 1)]
        public static void BuildAll()
        {
            Build(false);
        }

        [MenuItem("HotUpdate/2. Build + Sync Baseline", priority = 2)]
        public static void BuildAndSyncBaseline()
        {
            Build(true);
        }

        private static void Build(bool syncBaseline)
        {
            // 先读旧 catalog 的版本号，再清目录（清完就没了）。
            ContentCatalog old = ReadCatalog(Path.Combine(OutputDir, CatalogName));
            int buildNumber = (old != null ? old.buildNumber : 0) + 1;

            AssetBundleBuild[] builds = CollectBuilds();
            if (builds.Length == 0)
            {
                Debug.LogError("[AB] 没有任何 AssetBundle 标签。先在 Inspector 最下面给资源打标签。");
                return;
            }

            CompileHotUpdateDll();
            CleanDirectory(OutputDir);

            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                OutputDir,
                builds,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                Target);

            if (manifest == null)
            {
                Debug.LogError("[AB] BuildAssetBundles 失败，看 Console 里的报错。");
                return;
            }

            ContentCatalog catalog = MakeCatalog(manifest, buildNumber);
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
                $"[AB] 完成：build={catalog.buildNumber}, content={catalog.contentVersion}, " +
                $"bundle={catalog.bundles.Length}, asset={catalog.assets.Length}, baseline={syncBaseline}");
        }

        /// <summary>
        /// 编译热更程序集，并把产物复制成 Assets 下的 .bytes。
        /// 这样点一次菜单就能把“改代码”带进 AB，不用手动复制改名。
        /// </summary>
        private static void CompileHotUpdateDll()
        {
            CompileDllCommand.CompileDll(Target, EditorUserBuildSettings.development);

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

        private static AssetBundleBuild[] CollectBuilds()
        {
            var list = new List<AssetBundleBuild>();

            foreach (string bundleName in AssetDatabase.GetAllAssetBundleNames())
            {
                if (string.IsNullOrWhiteSpace(bundleName))
                {
                    continue;
                }

                string[] assets = AssetDatabase.GetAssetPathsFromAssetBundle(bundleName);
                if (assets == null || assets.Length == 0)
                {
                    Debug.LogWarning($"[AB] 标签 {bundleName} 没有挂任何资源，已跳过。");
                    continue;
                }

                list.Add(new AssetBundleBuild
                {
                    assetBundleName = bundleName,
                    assetNames = assets
                });
            }

            list.Sort((a, b) => string.CompareOrdinal(a.assetBundleName, b.assetBundleName));
            return list.ToArray();
        }

        private static ContentCatalog MakeCatalog(AssetBundleManifest manifest, int buildNumber)
        {
            string[] bundleNames = manifest.GetAllAssetBundles();
            Array.Sort(bundleNames, StringComparer.Ordinal);

            var bundles = new List<BundleEntry>();
            var assets = new List<AssetEntry>();

            foreach (string bundleName in bundleNames)
            {
                string filePath = Path.Combine(OutputDir, bundleName);
                var info = new FileInfo(filePath);

                bundles.Add(new BundleEntry
                {
                    name = bundleName,
                    file = bundleName,
                    sha256 = Sha256(filePath),
                    size = info.Length,
                    dependencies = manifest.GetAllDependencies(bundleName) ?? Array.Empty<string>()
                });

                string key = bundleName.EndsWith(BundleSuffix, StringComparison.OrdinalIgnoreCase)
                    ? bundleName.Substring(0, bundleName.Length - BundleSuffix.Length)
                    : bundleName;

                string[] assetPaths = AssetDatabase.GetAssetPathsFromAssetBundle(bundleName);
                if (assetPaths.Length > 1)
                {
                    Debug.LogWarning($"[AB] 包 {bundleName} 里有 {assetPaths.Length} 个资源，key 会重复，只有第一个能按 key 取到。");
                }

                foreach (string assetPath in assetPaths)
                {
                    Type type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
                    assets.Add(new AssetEntry
                    {
                        key = key,
                        bundle = bundleName,
                        asset = Path.GetFileNameWithoutExtension(assetPath),
                        type = type != null ? type.Name : string.Empty
                    });
                }
            }

            return new ContentCatalog
            {
                schemaVersion = 1,
                buildNumber = buildNumber,
                contentVersion = "1.0." + (buildNumber - 1),
                builtAtUtc = DateTime.UtcNow.ToString("o"),
                bundles = bundles.ToArray(),
                assets = assets.ToArray()
            };
        }

        private static void SyncBaseline(ContentCatalog catalog)
        {
            CleanDirectory(BaselineDir);
            Directory.CreateDirectory(BaselineDir);

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
