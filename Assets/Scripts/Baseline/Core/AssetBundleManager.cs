using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace HotUpdateDemo
{
    [Serializable]
    public sealed class ContentCatalog
    {
        public int schemaVersion = 1;
        public int buildNumber = 1;
        public string contentVersion = "1.0.0";
        public string builtAtUtc = "";
        public BundleEntry[] bundles = Array.Empty<BundleEntry>();
        public AssetEntry[] assets = Array.Empty<AssetEntry>();
    }

    [Serializable]
    public sealed class BundleEntry
    {
        public string name = "";
        public string file = "";
        public string sha256 = "";
        public long size;
        public string[] dependencies = Array.Empty<string>();
    }

    [Serializable]
    public sealed class AssetEntry
    {
        public string key = "";
        public string bundle = "";
        public string asset = "";
        public string type = "";
    }

    /// <summary>
    /// 手写的 AB 资源管理器，不依赖 Addressables。
    ///
    /// 它做四件事：
    /// 1. 读 StreamingAssets/AB/catalog.json（随包内置的初始版本）。
    /// 2. 拉取云端 catalog.json，按 buildNumber / SHA256 判断更新。
    /// 3. 把增量 bundle 下载到 persistentDataPath/AB/latest，失败则回退内置或上次缓存。
    /// 4. 按 catalog 里的依赖关系加载 AssetBundle，再按 key 取资源。
    ///
    /// 这是刻意写得「够小、能读懂」的版本；生产环境还应加入 CDN、断点续传、
    /// 签名校验、灰度发布、并发下载、磁盘清理和崩溃回滚。
    /// </summary>
    public sealed class AssetBundleManager
    {
        public const string CatalogFileName = "catalog.json";

        private readonly Dictionary<string, AssetBundle> _loadedBundles =
            new Dictionary<string, AssetBundle>(StringComparer.Ordinal);

        private AppConfig _config;
        private ContentCatalog _catalog;
        private string _activeRoot;

        public ContentCatalog Catalog => _catalog;
        public string ContentVersion => _catalog != null ? _catalog.contentVersion : "unknown";
        public int BuildNumber => _catalog != null ? _catalog.buildNumber : -1;
        public string Source { get; private set; } = "unknown";
        public string LastWarning { get; private set; }
        public string FatalError { get; private set; }
        public string ActiveRoot => _activeRoot;
        public int LoadedBundleCount => _loadedBundles.Count;

        private static string LocalRoot => Path.Combine(Application.streamingAssetsPath, "AB");
        private static string CacheRoot => Path.Combine(Application.persistentDataPath, "AB");
        private static string LatestCacheRoot => Path.Combine(CacheRoot, "latest");

        public IEnumerator Initialize(AppConfig config, Action<string> status, Action<float> progress = null)
        {
            _config = config;
            FatalError = null;
            LastWarning = null;
            _catalog = null;
            _activeRoot = null;

            status?.Invoke("读取内置 AB 清单…");
            Debug.Log("[AB] 内置目录: " + LocalRoot);
            ContentCatalog local = ReadCatalog(Path.Combine(LocalRoot, CatalogFileName));
            if (local == null)
            {
                FatalError = $"找不到或无法解析 {Path.Combine(LocalRoot, CatalogFileName)}。请先执行菜单 " +
                             "Hot Update Demo/2. Build Initial Content。";
                Debug.LogError("[AB] " + FatalError);
                yield break;
            }

            ValidateCatalogEntries(local, "内置清单");

            ContentCatalog cached = ReadCatalog(Path.Combine(LatestCacheRoot, CatalogFileName));
            if (cached != null && !ValidateCatalogFiles(cached, LatestCacheRoot))
            {
                LastWarning = "上次下载的缓存校验失败，忽略缓存。";
                cached = null;
            }

            status?.Invoke("检查云端热更清单…");
            ContentCatalog remote = null;
            string remoteUrl = CombineUrl(_config.contentBaseUrl, CatalogFileName);
            yield return FetchCatalog(remoteUrl, c => remote = c, e =>
            {
                LastWarning = e;
                Debug.LogWarning("[AB] 云端清单不可用，将使用本地内容：" + e);
            });

            ContentCatalog best = PickHighest(local, cached, remote);

            // 云端最新：下载到临时目录，校验全部 bundle 后再原子替换 latest。
            if (best == remote && remote != null)
            {
                status?.Invoke($"发现新版本 {remote.contentVersion}，准备下载…");
                bool downloaded = false;
                yield return DownloadCatalog(remote, progress, ok => downloaded = ok);
                if (downloaded)
                {
                    _catalog = remote;
                    _activeRoot = LatestCacheRoot;
                    Source = "cloud";
                    Debug.Log($"[AB] 已更新到云端内容 {remote.contentVersion}");
                    yield break;
                }

                LastWarning = "云端热更下载失败，已回退到可用版本。";
                Debug.LogWarning("[AB] " + LastWarning);
                best = PickHighest(local, cached, null);
            }

            if (best == cached && cached != null)
            {
                _catalog = cached;
                _activeRoot = LatestCacheRoot;
                Source = "cache";
                status?.Invoke($"使用本地缓存 {cached.contentVersion}");
                yield break;
            }

            _catalog = local;
            _activeRoot = LocalRoot;
            Source = "builtin";
            status?.Invoke($"使用随包内置内容 {local.contentVersion}");

            if (remote != null && remote.buildNumber > local.buildNumber)
            {
                LastWarning = $"云端已有 {remote.contentVersion}，但本次未能更新；重启后可重试。";
            }
        }

        public AssetEntry FindAsset(string key)
        {
            if (_catalog?.assets == null)
            {
                return null;
            }

            foreach (AssetEntry entry in _catalog.assets)
            {
                if (string.Equals(entry.key, key, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        /// <summary>
        /// 按逻辑 key 异步加载资源。协程返回后通过 callback 取值；
        /// UI、DLL、Sprite 都走这一条路径，便于统一管理引用计数。
        /// </summary>
        public IEnumerator LoadAsset<T>(string key, Action<T> onLoaded, Action<string> onError)
            where T : UnityEngine.Object
        {
            AssetEntry entry = FindAsset(key);
            if (entry == null)
            {
                onError?.Invoke($"catalog 中不存在资源 key: {key}");
                yield break;
            }

            string bundleError = null;
            yield return EnsureBundleLoaded(entry.bundle, e => bundleError = e);
            if (!string.IsNullOrEmpty(bundleError))
            {
                onError?.Invoke(bundleError);
                yield break;
            }

            AssetBundle bundle = _loadedBundles[entry.bundle];
            // 这里的 bundle 很小（几 KB 到几百 KB），同步加载更稳定，也便于在
            // Editor / IL2CPP 下排查路径问题。生产环境可以换成 LoadAssetAsync。
            T asset = bundle.LoadAsset<T>(entry.asset);
            if (asset == null)
            {
                // 不同 Unity 版本对 TextAsset 的 asset 名处理可能不同（带不带 .bytes）。
                // catalog 记的是编辑器里的名字；这里再按类型兜底一次，并打印真实 asset 名。
                string[] allAssetNames = bundle.GetAllAssetNames();
                foreach (string assetPath in allAssetNames)
                {
                    UnityEngine.Object candidate = bundle.LoadAsset(assetPath);
                    if (candidate is T typed)
                    {
                        asset = typed;
                        Debug.LogWarning($"[AB] 精确 key '{entry.asset}' 未命中，按类型兜底为 '{assetPath}'");
                        break;
                    }
                }

                if (asset == null)
                {
                    StringBuilder names = new StringBuilder();
                    foreach (string name in allAssetNames)
                    {
                        names.Append(name).Append(" | ");
                    }

                    onError?.Invoke($"在 bundle {entry.bundle} 中找不到 {typeof(T).Name} '{entry.asset}'。" +
                                    $"包含的资源: {names}");
                    yield break;
                }
            }

            Debug.Log($"[AB] 已加载 {key} <- {entry.bundle}/{entry.asset}");
            onLoaded?.Invoke(asset);
        }

        private IEnumerator EnsureBundleLoaded(string bundleName, Action<string> onError)
        {
            if (_loadedBundles.ContainsKey(bundleName))
            {
                onError?.Invoke(null);
                yield break;
            }

            BundleEntry info = FindBundle(bundleName);
            if (info == null)
            {
                onError?.Invoke($"catalog 中不存在 bundle: {bundleName}");
                yield break;
            }

            if (info.dependencies != null)
            {
                foreach (string dependency in info.dependencies)
                {
                    string dependencyError = null;
                    yield return EnsureBundleLoaded(dependency, e => dependencyError = e);
                    if (!string.IsNullOrEmpty(dependencyError))
                    {
                        onError?.Invoke(dependencyError);
                        yield break;
                    }
                }
            }

            string path = Path.Combine(_activeRoot, info.file);

            if (!File.Exists(path))
            {
                onError?.Invoke($"bundle 文件不存在: {path}");
                yield break;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                onError?.Invoke($"AssetBundle 加载失败（可能平台不匹配或文件损坏）: {path}");
                yield break;
            }

            _loadedBundles[bundleName] = bundle;
            Debug.Log($"[AB] bundle 已加载: {bundleName} <- {path}");
            onError?.Invoke(null);
        }

        private BundleEntry FindBundle(string name)
        {
            if (_catalog?.bundles == null)
            {
                return null;
            }

            foreach (BundleEntry bundle in _catalog.bundles)
            {
                if (string.Equals(bundle.name, name, StringComparison.Ordinal))
                {
                    return bundle;
                }
            }

            return null;
        }

        private IEnumerator FetchCatalog(string url, Action<ContentCatalog> onSuccess, Action<string> onError)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(AddCacheBuster(url)))
            {
                request.timeout = 5;
                request.downloadHandler = new DownloadHandlerBuffer();
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    onError?.Invoke($"请求云端清单失败: {request.error} ({url})");
                    yield break;
                }

                ContentCatalog catalog = ParseCatalog(request.downloadHandler.text);
                if (catalog == null)
                {
                    onError?.Invoke($"云端清单 JSON 格式错误: {url}");
                    yield break;
                }

                if (!ValidateCatalogEntries(catalog, "云端清单"))
                {
                    onError?.Invoke("云端清单包含非法文件名或空 bundle。");
                    yield break;
                }

                onSuccess?.Invoke(catalog);
            }
        }

        private IEnumerator DownloadCatalog(ContentCatalog catalog, Action<float> progress, Action<bool> onDone)
        {
            string tempRoot = LatestCacheRoot + ".downloading";
            string backupRoot = LatestCacheRoot + ".backup";

            progress?.Invoke(0f);

            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }

                Directory.CreateDirectory(tempRoot);
            }
            catch (Exception ex)
            {
                Debug.LogError("[AB] 无法创建下载目录: " + ex.Message);
                onDone?.Invoke(false);
                yield break;
            }

            bool failed = false;
            int index = 0;
            long totalBytes = 0;
            foreach (BundleEntry bundle in catalog.bundles)
            {
                totalBytes += Math.Max(1, bundle.size);
            }

            long completedBytes = 0;

            foreach (BundleEntry bundle in catalog.bundles)
            {
                index++;
                string destination = Path.Combine(tempRoot, bundle.file);

                // 如果上次缓存里已有同 hash 文件，直接复制，避免重复下载。
                string cachedPath = Path.Combine(LatestCacheRoot, bundle.file);
                if (File.Exists(cachedPath) && VerifyHash(cachedPath, bundle.sha256))
                {
                    File.Copy(cachedPath, destination, true);
                    Debug.Log($"[AB] 复用已缓存 bundle {bundle.file}");
                    completedBytes += Math.Max(1, bundle.size);
                    progress?.Invoke((float)completedBytes / totalBytes);
                    continue;
                }

                string url = CombineUrl(_config.contentBaseUrl, bundle.file);
                string tempFile = destination + ".download";
                Debug.Log($"[AB] 下载 {index}/{catalog.bundles.Length}: {url}");

                using (UnityWebRequest request = UnityWebRequest.Get(AddCacheBuster(url)))
                {
                    request.timeout = 120;
                    request.downloadHandler = new DownloadHandlerFile(tempFile);
                    request.SendWebRequest();

                    while (!request.isDone)
                    {
                        progress?.Invoke((completedBytes + Math.Max(1, bundle.size) * request.downloadProgress) /
                                         totalBytes);
                        yield return null;
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"[AB] 下载失败: {request.error} ({url})");
                        failed = true;
                        break;
                    }
                }

                if (!VerifyHash(tempFile, bundle.sha256))
                {
                    Debug.LogError($"[AB] SHA256 校验失败: {bundle.file}");
                    failed = true;
                    break;
                }

                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                File.Move(tempFile, destination);
                completedBytes += Math.Max(1, bundle.size);
                progress?.Invoke((float)completedBytes / totalBytes);
            }

            if (failed)
            {
                SafeDeleteDirectory(tempRoot);
                onDone?.Invoke(false);
                yield break;
            }

            try
            {
                File.WriteAllText(Path.Combine(tempRoot, CatalogFileName), JsonUtility.ToJson(catalog, true),
                    new UTF8Encoding(false));

                SafeDeleteDirectory(backupRoot);
                if (Directory.Exists(LatestCacheRoot))
                {
                    Directory.Move(LatestCacheRoot, backupRoot);
                }

                Directory.Move(tempRoot, LatestCacheRoot);
                SafeDeleteDirectory(backupRoot);
                progress?.Invoke(1f);
                onDone?.Invoke(true);
            }
            catch (Exception ex)
            {
                Debug.LogError("[AB] 安装新内容失败: " + ex.Message);
                if (!Directory.Exists(LatestCacheRoot) && Directory.Exists(backupRoot))
                {
                    try
                    {
                        Directory.Move(backupRoot, LatestCacheRoot);
                    }
                    catch (Exception restoreEx)
                    {
                        Debug.LogError("[AB] 恢复旧缓存失败: " + restoreEx.Message);
                    }
                }

                SafeDeleteDirectory(tempRoot);
                onDone?.Invoke(false);
            }
        }

        private bool ValidateCatalogFiles(ContentCatalog catalog, string root)
        {
            if (catalog?.bundles == null)
            {
                return false;
            }

            foreach (BundleEntry bundle in catalog.bundles)
            {
                string path = Path.Combine(root, bundle.file);
                if (!File.Exists(path))
                {
                    Debug.LogWarning("[AB] 缺少 bundle: " + path);
                    return false;
                }

                FileInfo info = new FileInfo(path);
                if (bundle.size > 0 && info.Length != bundle.size)
                {
                    Debug.LogWarning($"[AB] bundle 大小不符: {bundle.file} {info.Length} != {bundle.size}");
                    return false;
                }

                if (!VerifyHash(path, bundle.sha256))
                {
                    Debug.LogWarning("[AB] bundle 哈希不符: " + path);
                    return false;
                }
            }

            return true;
        }

        private static bool ValidateCatalogEntries(ContentCatalog catalog, string label)
        {
            if (catalog?.bundles == null || catalog.assets == null)
            {
                return false;
            }

            foreach (BundleEntry bundle in catalog.bundles)
            {
                if (string.IsNullOrWhiteSpace(bundle.name) ||
                    string.IsNullOrWhiteSpace(bundle.file) ||
                    !string.Equals(Path.GetFileName(bundle.file), bundle.file, StringComparison.Ordinal))
                {
                    Debug.LogError($"[AB] {label} 中存在非法 bundle 文件名。");
                    return false;
                }
            }

            return true;
        }

        private static ContentCatalog PickHighest(params ContentCatalog[] candidates)
        {
            ContentCatalog best = null;
            foreach (ContentCatalog candidate in candidates)
            {
                if (candidate == null)
                {
                    continue;
                }

                if (best == null || candidate.buildNumber > best.buildNumber)
                {
                    best = candidate;
                }
            }

            return best;
        }

        private static ContentCatalog ReadCatalog(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                return ParseCatalog(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AB] 读取清单失败 {path}: {ex.Message}");
                return null;
            }
        }

        private static ContentCatalog ParseCatalog(string json)
        {
            try
            {
                return JsonUtility.FromJson<ContentCatalog>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[AB] 清单解析失败: " + ex.Message);
                return null;
            }
        }

        private static string AddCacheBuster(string url)
        {
            return url + (url.Contains("?") ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks;
        }

        private static string CombineUrl(string baseUrl, string relative)
        {
            return (baseUrl ?? string.Empty).TrimEnd('/') + "/" + (relative ?? string.Empty).TrimStart('/');
        }

        private static bool VerifyHash(string path, string expectedSha256)
        {
            if (string.IsNullOrWhiteSpace(expectedSha256) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder builder = new StringBuilder(hash.Length * 2);
                    foreach (byte value in hash)
                    {
                        builder.Append(value.ToString("x2"));
                    }

                    return string.Equals(builder.ToString(), expectedSha256, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AB] 计算哈希失败 {path}: {ex.Message}");
                return false;
            }
        }

        private static void SafeDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AB] 删除目录失败 {path}: {ex.Message}");
            }
        }

        public void UnloadAll(bool unloadAllLoadedObjects = false)
        {
            foreach (AssetBundle bundle in _loadedBundles.Values)
            {
                bundle.Unload(unloadAllLoadedObjects);
            }

            _loadedBundles.Clear();
        }
    }
}
