using System.Collections;
using Baseline.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace Baseline.UI
{
    /// <summary>
    /// Start 场景：
    /// - 并行做两件事：检查/下载热更内容、探测账号服务器；
    /// - 注册/登录都走服务端 /api/v1/accounts/*，客户端不再用 PlayerPrefs；
    /// - 服务器不可达时可以「离线开局」，游戏照常运行，只是不云同步。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class StartSceneController : MonoBehaviour
    {
        [SerializeField] private string playSceneName = "Play";
        [SerializeField] private int serverProbeTimeoutSeconds = 3;

        private UIDocument _document;
        private TextField _accountField;
        private TextField _passwordField;
        private Button _registerButton;
        private Button _loginButton;
        private Button _offlineButton;
        private Button _dialogOkButton;
        private Label _updateStatus;
        private Label _serverStatus;
        private Label _dialogMessage;
        private ProgressBar _updateProgress;
        private VisualElement _dialogOverlay;

        private AppConfig _config;
        private bool _ready;
        private bool _busy;
        private bool _serverReachable;

        public bool IsReady => _ready;

        private IEnumerator Start()
        {
            yield return null; // 等 UIDocument 建好 VisualElement 树
            Bind();
            yield return BootFlow();
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
            _offlineButton = root.Q<Button>("offline-button");
            _dialogOkButton = root.Q<Button>("dialog-ok");
            _updateStatus = root.Q<Label>("update-status");
            _serverStatus = root.Q<Label>("server-status");
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
            if (_offlineButton != null) _offlineButton.clicked += PlayOffline;
            if (_dialogOkButton != null) _dialogOkButton.clicked += DismissDialog;

            HideDialog();
            SetAuthEnabled(false);
            if (_offlineButton != null) _offlineButton.SetEnabled(false);
            SetUpdateStatus("检查更新…");
            SetServerStatus("探测服务器…");
        }

        /// <summary>
        /// 内容检查和服务器探测并行跑，两边都结束后再打开按钮。
        /// 服务器挂了最多等一个 3 秒探针，不会卡在启动页。
        /// </summary>
        private IEnumerator BootFlow()
        {
            AppConfig config = null;
            string configWarning = null;
            yield return AppConfig.Load(c => config = c, w => configWarning = w);

            _config = config ?? new AppConfig();
            _config.Normalize();

            if (!string.IsNullOrEmpty(configWarning))
            {
                Debug.LogWarning("[Start] " + configWarning);
            }

            int pending = 2;
            StartCoroutine(CheckContent(() => pending--));
            StartCoroutine(ProbeServer(() => pending--));

            // 兜底：任何一个子协程异常退出，也不能把启动页卡死。
            float deadline = Time.realtimeSinceStartup + 20f;
            yield return new WaitUntil(() => pending <= 0 || Time.realtimeSinceStartup >= deadline);
            if (pending > 0)
            {
                Debug.LogWarning("[Start] 启动检查超时，直接启用按钮（可能仍在后台下载）。");
            }

            _ready = true;
            SetAuthEnabled(true);
            if (_offlineButton != null) _offlineButton.SetEnabled(true);

            SetServerStatus(_serverReachable
                ? $"服务器可用：{_config.cloudBaseUrl}"
                : "服务器不可达：可以点「离线开局」，游戏照常运行。");
        }

        private IEnumerator CheckContent(System.Action onDone)
        {
            AssetBundleManager updater = new AssetBundleManager();
            bool progressVisible = false;

            yield return updater.Initialize(
                _config,
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

                SetUpdateStatus(!string.IsNullOrEmpty(updater.LastWarning)
                    ? "未检测到可用更新，使用本地内容。"
                    : $"已是最新内容 {updater.ContentVersion}");
            }

            onDone?.Invoke();
        }

        private IEnumerator ProbeServer(System.Action onDone)
        {
            bool reachable = false;
            string detail = null;

            yield return AccountApi.ProbeHealth(
                _config.cloudBaseUrl,
                serverProbeTimeoutSeconds,
                (ok, message) => { reachable = ok; detail = message; });

            _serverReachable = reachable;
            if (!reachable)
            {
                Debug.LogWarning("[Start] 服务器探测失败: " + detail);
            }

            onDone?.Invoke();
        }

        public void Register()
        {
            if (!CanInteract())
            {
                return;
            }

            StartCoroutine(AuthRoutine(true));
        }

        public void Login()
        {
            if (!CanInteract())
            {
                return;
            }

            StartCoroutine(AuthRoutine(false));
        }

        private bool CanInteract()
        {
            return _ready && !_busy && _config != null;
        }

        private IEnumerator AuthRoutine(bool register)
        {
            string account = _accountField != null ? _accountField.value : string.Empty;
            string password = _passwordField != null ? _passwordField.value : string.Empty;

            _busy = true;
            SetAuthEnabled(false);
            if (_offlineButton != null) _offlineButton.SetEnabled(false);
            SetUpdateStatus(register ? "注册中…" : "登录中…");

            AccountResult result = null;
            yield return register
                ? AccountApi.Register(_config.cloudBaseUrl, account, password, _config.requestTimeoutSeconds, r => result = r)
                : AccountApi.Login(_config.cloudBaseUrl, account, password, _config.requestTimeoutSeconds, r => result = r);

            _busy = false;
            SetAuthEnabled(true);
            if (_offlineButton != null) _offlineButton.SetEnabled(true);

            if (result == null || !result.success)
            {
                SetUpdateStatus("注册/登录失败。");
                ShowDialog(result != null ? result.message : "网络错误");
                yield break;
            }

            if (register)
            {
                SetUpdateStatus($"账号 {result.account} 已创建。");
                ShowDialog("注册成功，请登录");
                yield break;
            }

            PlayerSession.EnterOnline(result.account, result.playerId);
            SceneManager.LoadScene(playSceneName);
        }

        public void PlayOffline()
        {
            if (_busy || !_ready)
            {
                return;
            }

            PlayerSession.EnterOffline();
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

        private void SetServerStatus(string message)
        {
            if (_serverStatus != null)
            {
                _serverStatus.text = message;
            }
        }

        private void OnDestroy()
        {
            if (_registerButton != null) _registerButton.clicked -= Register;
            if (_loginButton != null) _loginButton.clicked -= Login;
            if (_offlineButton != null) _offlineButton.clicked -= PlayOffline;
            if (_dialogOkButton != null) _dialogOkButton.clicked -= DismissDialog;
        }
    }
}
