using System.Collections;
using UnityEngine;

namespace HotUpdateDemo
{
    /// <summary>
    /// Play 场景启动编排：
    /// appconfig -> 手写 AB 管理器 -> 动态加载 HotUpdate.dll -> 加载热更贴图。
    /// </summary>
    public sealed class GameRoot : MonoBehaviour
    {
        [SerializeField] private PlayerHealth player;
        [SerializeField] private SpriteRenderer heroRenderer;
        [SerializeField] private GameHud hud;
        [SerializeField] private CloudSyncService cloud;
        [SerializeField] private Sprite fallbackHero;

        private readonly AssetBundleManager _bundles = new AssetBundleManager();
        private AppConfig _config;
        private bool _booted;

        public static GameRoot Instance { get; private set; }
        public AssetBundleManager Bundles => _bundles;
        public AppConfig Config => _config;
        public PlayerHealth Player => player;
        public bool IsBooted => _booted;
        public bool HotUpdateSpriteApplied { get; private set; }
        public string HotUpdateSpriteError { get; private set; }

        private void Awake()
        {
            Instance = this;
        }

        private IEnumerator Start()
        {
            hud?.SetStatus("读取 appconfig.json…");

            AppConfig loaded = null;
            string configWarning = null;
            yield return AppConfig.Load(c => loaded = c, w => configWarning = w);
            _config = loaded ?? new AppConfig();
            _config.Normalize();

            if (cloud != null)
            {
                cloud.Configure(_config);
            }

            if (player != null && fallbackHero != null && heroRenderer != null)
            {
                heroRenderer.sprite = fallbackHero;
            }

            hud?.SetStatus("初始化 AssetBundle 运行时…");
            yield return _bundles.Initialize(_config, status => hud?.SetStatus(status));

            if (!string.IsNullOrEmpty(_bundles.FatalError))
            {
                _booted = true;
                hud?.MarkReady("AB 初始化失败，使用 AOT 兜底逻辑。");
                hud?.SetStatus(_bundles.FatalError);
                Debug.LogError("[Boot] " + _bundles.FatalError);
                yield break;
            }

            hud?.SetStatus("加载 HotUpdate.dll…");
            TextAsset dllAsset = null;
            string dllError = null;
            yield return _bundles.LoadAsset<TextAsset>("hotupdate_dll", a => dllAsset = a, e => dllError = e);

            if (dllAsset == null)
            {
                _booted = true;
                HotUpdateRuntime.Reset();
                hud?.MarkReady("热更 DLL 加载失败，使用 AOT 默认逻辑。");
                hud?.SetStatus(dllError ?? "HotUpdate.dll load failed.");
                Debug.LogError("[Boot] HotUpdate.dll load failed: " + dllError);
                yield break;
            }

            bool codeLoaded = HotUpdateRuntime.TryInitialize(dllAsset.bytes, out string codeError);
            if (!codeLoaded)
            {
                Debug.LogError("[Boot] " + codeError);
            }

            string heroKey = HotUpdateRuntime.Current?.HeroAssetKey;
            if (string.IsNullOrEmpty(heroKey) || _bundles.FindAsset(heroKey) == null)
            {
                heroKey = "hero_sprite";
            }

            hud?.SetStatus("加载热更 2D 贴图…");
            Sprite hero = null;
            string spriteError = null;
            yield return _bundles.LoadAsset<Sprite>(heroKey, a => hero = a, e => spriteError = e);

            if (hero != null && heroRenderer != null)
            {
                heroRenderer.sprite = hero;
                HotUpdateSpriteApplied = true;
                Debug.Log($"[Boot] 已应用热更贴图: {hero.name}");
            }
            else if (!string.IsNullOrEmpty(spriteError))
            {
                HotUpdateSpriteError = spriteError;
                Debug.LogWarning("[Boot] 热更贴图加载失败，保留内置兜底图: " + spriteError);
            }

            _booted = true;
            string summary = codeLoaded
                ? $"启动完成：代码 {HotUpdateRuntime.Current.DisplayVersion}，内容 {_bundles.ContentVersion}（{_bundles.Source}）"
                : $"启动完成（AOT 兜底），内容 {_bundles.ContentVersion}（{_bundles.Source}）";
            hud?.MarkReady(summary);

            if (!string.IsNullOrEmpty(_bundles.LastWarning))
            {
                hud?.SetStatus(_bundles.LastWarning);
            }
            else if (!string.IsNullOrEmpty(configWarning))
            {
                hud?.SetStatus(configWarning);
            }
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }

            // 不销毁已加载对象，因为 HUD/场景可能还引用 Sprite。
            _bundles.UnloadAll(false);
        }

        private void OnApplicationQuit()
        {
            _bundles.UnloadAll(false);
        }
    }
}
