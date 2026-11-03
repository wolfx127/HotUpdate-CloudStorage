using System.Collections;
using Baseline.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Baseline.UI
{
    /// <summary>
    /// Start 场景：
    /// - 先检查云端 AB；有热更时显示 ProgressBar 并下载；
    /// - 账户 ID / 密码下方是「注册」「登录」两个默认按钮；
    /// - 登录失败、注册重名用简单弹窗提示；
    /// - 密码不做格式校验，也不上传。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class StartSceneController : MonoBehaviour
    {
        [SerializeField] private string playSceneName = "Play";

        private UIDocument _document;
        private TextField _accountField;
        private TextField _passwordField;
        private Button _registerButton;
        private Button _loginButton;
        private Button _dialogOkButton;
        private Label _updateStatus;
        private Label _dialogMessage;
        private ProgressBar _updateProgress;
        private VisualElement _dialogOverlay;

        private bool _readyForAuth;

        public bool IsReadyForAuth => _readyForAuth;

        private IEnumerator Start()
        {
            yield return null; // 等 UIDocument 建好 VisualElement 树
            Bind();
            yield return CheckForUpdates();
        }

        private void Bind()
        {
            _document = GetComponent<UIDocument>();
            VisualElement root = _document != null ? _document.rootVisualElement : null;
            if (root == null)
            {
                Debug.LogError("[Start] UIDocument root is null.");
                return;
            }

            _accountField = root.Q<TextField>("account-field");
            _passwordField = root.Q<TextField>("password-field");
            _registerButton = root.Q<Button>("register-button");
            _loginButton = root.Q<Button>("login-button");
            _dialogOkButton = root.Q<Button>("dialog-ok");
            _updateStatus = root.Q<Label>("update-status");
            _dialogMessage = root.Q<Label>("dialog-message");
            _updateProgress = root.Q<ProgressBar>("update-progress");
            _dialogOverlay = root.Q<VisualElement>("dialog-overlay");

            if (_passwordField != null)
            {
                _passwordField.isPasswordField = true;
            }

            if (_updateProgress != null)
            {
                _updateProgress.lowValue = 0f;
                _updateProgress.highValue = 1f;
                _updateProgress.value = 0f;
                _updateProgress.style.display = DisplayStyle.None;
            }

            if (_registerButton != null) _registerButton.clicked += Register;
            if (_loginButton != null) _loginButton.clicked += Login;
            if (_dialogOkButton != null) _dialogOkButton.clicked += DismissDialog;

            HideDialog();
            SetAuthEnabled(false);
            SetUpdateStatus("检查更新…");
        }

        /// <summary>
        /// Start 场景内先检查并下载热更，这样 Play 场景可以直接用缓存内容。
        /// 只有确实发现新版本并开始下载时才显示进度条。
        /// </summary>
        private IEnumerator CheckForUpdates()
        {
            AppConfig config = null;
            string configWarning = null;
            yield return AppConfig.Load(c => config = c, w => configWarning = w);
            config = config ?? new AppConfig();
            config.Normalize();

            AssetBundleManager updater = new AssetBundleManager();
            bool progressVisible = false;

            yield return updater.Initialize(
                config,
                message => SetUpdateStatus(message),
                progress =>
                {
                    if (!progressVisible)
                    {
                        progressVisible = true;
                        if (_updateProgress != null)
                        {
                            _updateProgress.style.display = DisplayStyle.Flex;
                        }
                    }

                    if (_updateProgress != null)
                    {
                        _updateProgress.title = $"下载热更 {Mathf.RoundToInt(progress * 100f)}%";
                        _updateProgress.value = Mathf.Clamp01(progress);
                    }
                });

            if (!string.IsNullOrEmpty(updater.FatalError))
            {
                SetUpdateStatus("更新检查失败，将使用内置内容。");
                Debug.LogError("[Start] " + updater.FatalError);
            }
            else if (updater.Source == "cloud")
            {
                if (_updateProgress != null)
                {
                    _updateProgress.title = "热更完成 100%";
                    _updateProgress.value = 1f;
                    _updateProgress.style.display = DisplayStyle.Flex;
                }

                SetUpdateStatus($"热更完成：内容 {updater.ContentVersion}");
            }
            else
            {
                if (_updateProgress != null)
                {
                    _updateProgress.style.display = DisplayStyle.None;
                }

                if (!string.IsNullOrEmpty(updater.LastWarning))
                {
                    SetUpdateStatus("未检测到可用更新，使用本地内容。");
                }
                else
                {
                    SetUpdateStatus($"已是最新内容 {updater.ContentVersion}");
                }
            }

            if (!string.IsNullOrEmpty(configWarning))
            {
                Debug.LogWarning("[Start] " + configWarning);
            }

            _readyForAuth = true;
            SetAuthEnabled(true);
        }

        /// <summary>注册按钮。</summary>
        public void Register()
        {
            if (!_readyForAuth)
            {
                return;
            }

            string username = _accountField != null ? _accountField.value : string.Empty;
            string password = _passwordField != null ? _passwordField.value : string.Empty;

            if (!AccountStore.Register(username, password))
            {
                // 已存在：取消注册，不覆盖原密码。
                ShowDialog("用户存在");
                return;
            }

            ShowDialog("注册成功，请登录");
        }

        /// <summary>登录按钮。用户名不存在或密码错误都提示同一句话。</summary>
        public void Login()
        {
            if (!_readyForAuth)
            {
                return;
            }

            string username = _accountField != null ? _accountField.value : string.Empty;
            string password = _passwordField != null ? _passwordField.value : string.Empty;

            if (!AccountStore.Validate(username, password))
            {
                ShowDialog("账户或密码错误");
                return;
            }

            PlayerSession.SetAccount(username);
            SceneManager.LoadScene(playSceneName);
        }

        public void DismissDialog()
        {
            HideDialog();
        }

        private void ShowDialog(string message)
        {
            if (_dialogMessage != null)
            {
                _dialogMessage.text = message;
            }

            if (_dialogOverlay != null)
            {
                _dialogOverlay.style.display = DisplayStyle.Flex;
            }
        }

        private void HideDialog()
        {
            if (_dialogOverlay != null)
            {
                _dialogOverlay.style.display = DisplayStyle.None;
            }
        }

        private void SetAuthEnabled(bool enabled)
        {
            if (_registerButton != null) _registerButton.SetEnabled(enabled);
            if (_loginButton != null) _loginButton.SetEnabled(enabled);
        }

        private void SetUpdateStatus(string message)
        {
            if (_updateStatus != null)
            {
                _updateStatus.text = message;
            }
        }

        private void OnDestroy()
        {
            if (_registerButton != null) _registerButton.clicked -= Register;
            if (_loginButton != null) _loginButton.clicked -= Login;
            if (_dialogOkButton != null) _dialogOkButton.clicked -= DismissDialog;
        }
    }
}
