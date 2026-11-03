using System.Collections;
using Baseline.UI;
using UnityEngine;

namespace Baseline.Core
{
    /// <summary>
    /// Play 场景启动编排：
    /// appconfig -> 手写 AB 管理器 -> 读取 ContentManifest -> 动态加载 HotUpdate.dll
    /// -> 应用热更贴图 ->（启动完成后）自动从云端恢复一次存档。
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
            if (cloud != null)
            {
                StartCoroutine(AutoLoadCloudSave());
            }

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

            hud?.SetStatus("加载热更内容清单…");
            ContentManifest content = null;
            string manifestError = null;
            yield return _bundles.LoadBootContent(m => content = m, e => manifestError = e);

            if (content == null)
            {
                _booted = true;
                HotUpdateRuntime.Reset();
                HotUpdateSpriteError = manifestError;
                hud?.MarkReady("热更内容加载失败，使用 AOT 兜底逻辑。");
                hud?.SetStatus(manifestError ?? "ContentManifest load failed.");
                Debug.LogError("[Boot] ContentManifest load failed: " + manifestError);
                yield break;
            }

            bool codeLoaded = false;
            if (content.hotUpdateDll == null)
            {
                Debug.LogError("[Boot] ContentManifest.hotUpdateDll 为空。");
            }
            else
            {
                codeLoaded = HotUpdateRuntime.TryInitialize(content.hotUpdateDll.bytes, out string codeError);
                if (!codeLoaded)
                {
                    Debug.LogError("[Boot] " + codeError);
                }
            }

            hud?.SetStatus("应用热更 2D 贴图…");
            Sprite hero = content.heroSprite;
            if (hero != null && heroRenderer != null)
            {
                heroRenderer.sprite = hero;
                HotUpdateSpriteApplied = true;
                Debug.Log($"[Boot] 已应用热更贴图: {hero.name}");
            }
            else
            {
                HotUpdateSpriteError = hero == null
                    ? "ContentManifest.heroSprite 为空"
                    : "heroRenderer 未绑定";
                Debug.LogWarning("[Boot] 未应用热更贴图: " + HotUpdateSpriteError);
            }

            _booted = true;
            string modeSuffix = PlayerSession.IsOnline ? string.Empty : " · 离线模式";
            string summary = codeLoaded
                ? $"启动完成：代码 {HotUpdateRuntime.Current.DisplayVersion}，内容 {_bundles.ContentVersion}（{_bundles.Source}）{modeSuffix}"
                : $"启动完成（AOT 兜底），内容 {_bundles.ContentVersion}（{_bundles.Source}）{modeSuffix}";
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

        /// <summary>
        /// 进入 Play 后自动从云端拉一次存档——这是"云存档"的读取端，上传仍由 HUD 的按钮触发。
        /// 拉不到（404 / 超时 / 鉴权失败）不阻塞开局，保持场景里的初始 HP。
        /// </summary>
        private IEnumerator AutoLoadCloudSave()
        {
            // 等启动流程走完：appconfig -> cloud.Configure -> AB -> DLL -> 贴图。
            while (!_booted)
            {
                yield return null;
            }

            // 再让一帧，先让启动摘要显示出来，避免一开局就被下面的状态覆盖。
            yield return null;

            if (!PlayerSession.IsOnline)
            {
                SetCloudStatus("离线模式：不读取云存档，也不会上传。");
                Debug.Log("[Cloud] 离线会话，跳过自动恢复。");
                yield break;
            }

            if (cloud == null || player == null || !cloud.IsConfigured)
            {
                yield break;
            }

            CloudResult result = null;
            yield return cloud.Download(r => result = r);

            if (result == null)
            {
                yield break;
            }

            if (!result.success)
            {
                if (result.httpStatus == 404)
                {
                    SetCloudStatus($"云端暂无存档，保持初始 HP {player.CurrentHp}/{player.MaxHp}。");
                    Debug.Log("[Cloud] 云端没有这个玩家的存档，跳过自动恢复。");
                }
                else
                {
                    SetCloudStatus("云端存档读取失败：" + result.message + "（保持初始 HP）");
                    Debug.LogWarning("[Cloud] 自动读取存档失败: " + result.message);
                }

                yield break;
            }

            if (result.data == null)
            {
                yield break;
            }

            player.ApplyServerHp(result.data.hp);
            SetCloudStatus($"已从云端恢复：{player.CurrentHp}/{player.MaxHp}（存档版本 {result.data.hotUpdateVersion}）");
            Debug.Log($"[Cloud] 自动恢复存档: hp={result.data.hp}, maxHp={result.data.maxHp}, " +
                      $"code={result.data.hotUpdateVersion}");
        }

        private void SetCloudStatus(string message)
        {
            // 启动有致命错误（比如内置 catalog 缺失）时，状态行留给错误信息，云端结果只进日志。
            if (string.IsNullOrEmpty(_bundles.FatalError))
            {
                hud?.SetStatus(message);
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
