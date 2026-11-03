using System.Collections;
using Baseline.Core;
using UnityEngine;
using UnityEngine.UIElements;

namespace Baseline.UI
{
    /// <summary>
    /// Play 场景顶部的一行 UI：
    /// 账号、HP、扣血按钮、上传云端按钮、热更版本和状态。
    /// 所有控件使用 UI Toolkit 默认样式，只做横向排版。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class GameHud : MonoBehaviour
    {
        [SerializeField] private UIDocument document;
        [SerializeField] private PlayerHealth player;
        [SerializeField] private CloudSyncService cloud;

        private Label _accountLabel;
        private Label _hpLabel;
        private Label _versionLabel;
        private Label _statusLabel;
        private Button _damageButton;
        private Button _uploadButton;

        private string _status = "加载中…";
        private bool _ready;
        private bool _busy;
        private bool _bound;

        private void OnEnable()
        {
            if (player != null)
            {
                player.Changed += OnPlayerChanged;
            }
        }

        private void OnDisable()
        {
            if (player != null)
            {
                player.Changed -= OnPlayerChanged;
            }

            UnbindButtons();
        }

        private IEnumerator Start()
        {
            yield return null;
            BindUI();
            RefreshAll();
        }

        private void BindUI()
        {
            if (document == null)
            {
                document = GetComponent<UIDocument>();
            }

            VisualElement root = document != null ? document.rootVisualElement : null;
            if (root == null)
            {
                Debug.LogError("[HUD] UIDocument root is null.");
                return;
            }

            _accountLabel = root.Q<Label>("account-label");
            _hpLabel = root.Q<Label>("hp-label");
            _versionLabel = root.Q<Label>("version-label");
            _statusLabel = root.Q<Label>("status-label");
            _damageButton = root.Q<Button>("damage-button");
            _uploadButton = root.Q<Button>("upload-button");

            if (_damageButton != null) _damageButton.clicked += OnDamageClicked;
            if (_uploadButton != null) _uploadButton.clicked += OnUploadClicked;

            _bound = true;
            RefreshAll();
        }

        private void UnbindButtons()
        {
            if (!_bound)
            {
                return;
            }

            if (_damageButton != null) _damageButton.clicked -= OnDamageClicked;
            if (_uploadButton != null) _uploadButton.clicked -= OnUploadClicked;
            _bound = false;
        }

        public void SetStatus(string message)
        {
            _status = message;
            ApplyStatus();
        }

        public void MarkReady(string message)
        {
            _ready = true;
            _status = message;
            ApplyStatus();
            ApplyButtons();
            RefreshHotUpdate();
        }

        public void RefreshHotUpdate()
        {
            if (!_bound)
            {
                return;
            }

            string code = HotUpdateRuntime.Current?.DisplayVersion ?? "AOT fallback";
            AssetBundleManager bundles = GameRoot.Instance != null ? GameRoot.Instance.Bundles : null;
            string content = bundles != null ? bundles.ContentVersion : "unknown";
            string source = bundles != null ? bundles.Source : "unknown";
            if (_versionLabel != null)
            {
                _versionLabel.text = $"代码 {code}  |  内容 {content}  |  {source}";
            }

            if (_accountLabel != null)
            {
                string suffix = PlayerSession.IsOnline ? string.Empty : "（离线）";
                _accountLabel.text = "用户名：" + PlayerSession.AccountName + suffix;
            }
        }

        private void RefreshAll()
        {
            if (!_bound)
            {
                return;
            }

            ApplyStatus();
            ApplyButtons();
            RefreshStats();
            RefreshHotUpdate();
        }

        private void RefreshStats()
        {
            if (!_bound || player == null)
            {
                return;
            }

            if (_hpLabel != null && player != null)
            {
                _hpLabel.text = $"HP {player.CurrentHp} / {player.MaxHp}";
            }

            if (_damageButton != null && player != null)
            {
                _damageButton.text = $"扣血 -{player.NextDamagePreview}";
            }
        }

        private void ApplyStatus()
        {
            if (_statusLabel != null)
            {
                _statusLabel.text = _status;
            }
        }

        private void ApplyButtons()
        {
            if (!_bound)
            {
                return;
            }

            if (_damageButton != null)
            {
                _damageButton.SetEnabled(_ready);
            }

            if (_uploadButton != null)
            {
                _uploadButton.SetEnabled(_ready && !_busy && cloud != null && cloud.IsConfigured);
            }
        }

        private void OnPlayerChanged(PlayerHealth health)
        {
            if (_bound)
            {
                RefreshStats();
            }
        }

        private void OnDamageClicked()
        {
            if (!_ready || player == null)
            {
                return;
            }

            int damage = player.TakeDamage(PlayerHealth.BaseDamagePerStrike);
            SetStatus($"扣血 {damage}，当前 HP {player.CurrentHp}/{player.MaxHp}");
        }

        private void OnUploadClicked()
        {
            if (!_ready || _busy || cloud == null || !cloud.IsConfigured)
            {
                return;
            }

            StartCoroutine(UploadRoutine());
        }

        private IEnumerator UploadRoutine()
        {
            _busy = true;
            ApplyButtons();
            SetStatus($"上传中：{player.CurrentHp}/{player.MaxHp}…");

            CloudResult result = null;
            yield return cloud.Upload(cloud.CreateSnapshot(player), r => result = r);

            _busy = false;
            ApplyButtons();

            if (result != null && result.success)
            {
                SetStatus($"上传成功：云端 {result.data?.hp}/{result.data?.maxHp}，代码 {result.data?.hotUpdateVersion}");
            }
            else
            {
                SetStatus("上传失败：" + (result?.message ?? "未知错误"));
            }
        }
    }
}
