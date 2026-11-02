using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace HotUpdateDemo.EditorTools
{
    /// <summary>
    /// Start 场景自动化测试：
    /// 注册 -> 重复注册弹窗 -> 错误密码登录弹窗 -> 正确登录 -> Play 扣血/上传。
    /// </summary>
    public static class RuntimeSmokeTest
    {
        [MenuItem("Hot Update Demo/Run Play Mode Smoke Test", priority = 91)]
        public static void RunFromCommandLine()
        {
            EditorSceneManager.OpenScene(DemoProjectBuilder.StartScenePath);
            Type gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
            if (gameViewType != null)
            {
                EditorWindow.GetWindow(gameViewType).Show();
            }

            EditorApplication.isPlaying = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoStart()
        {
            if (!Environment.GetCommandLineArgs().Any(a =>
                    string.Equals(a, "-smokeTest", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            GameObject host = new GameObject("SmokeTestHost");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<SmokeTestHost>();
        }
    }

    public sealed class SmokeTestHost : MonoBehaviour
    {
        private readonly string _testAccount = "测试账号-" + DateTime.UtcNow.Ticks.ToString("x");
        private const string WrongPassword = "wrong-password";
        private const string TestPassword = "123456";

        private readonly StringBuilder _report = new StringBuilder();
        private GameRoot _root;
        private GameHud _hud;
        private UIDocument _playDocument;
        private DialogProbe _dialog;

        private IEnumerator Start()
        {
            Debug.Log("[Smoke] host started");
            float deadline = Time.realtimeSinceStartup + 35f;

            StartSceneController startController = null;
            UIDocument startDocument = null;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (SceneManager.GetActiveScene().name == "Start")
                {
                    startController = UnityEngine.Object.FindFirstObjectByType<StartSceneController>();
                    startDocument = startController != null ? startController.GetComponent<UIDocument>() : null;
                    if (startDocument != null && startDocument.rootVisualElement != null &&
                        startDocument.rootVisualElement.Q<Button>("login-button") != null &&
                        startController.IsReadyForAuth)
                    {
                        break;
                    }
                }

                yield return null;
            }

            if (startController == null || startDocument == null || !startController.IsReadyForAuth)
            {
                Fail("Start scene did not become ready for login within 35s");
                yield break;
            }

            TextField accountField = startDocument.rootVisualElement.Q<TextField>("account-field");
            TextField passwordField = startDocument.rootVisualElement.Q<TextField>("password-field");
            Button dialogOk = startDocument.rootVisualElement.Q<Button>("dialog-ok");
            Label updateStatus = startDocument.rootVisualElement.Q<Label>("update-status");
            if (accountField == null || passwordField == null || dialogOk == null)
            {
                Fail("Start UI controls missing");
                yield break;
            }

            _dialog = new DialogProbe(startDocument.rootVisualElement.Q<VisualElement>("dialog-overlay"),
                startDocument.rootVisualElement.Q<Label>("dialog-message"), dialogOk);

            accountField.value = _testAccount;
            passwordField.value = TestPassword;
            _report.AppendLine("testAccount=" + _testAccount);
            _report.AppendLine("passwordMasked=" + passwordField.isPasswordField);
            _report.AppendLine("updateStatus=" + (updateStatus != null ? updateStatus.text : "missing"));
            _report.AppendLine("updateProgressVisible=" +
                               (startDocument.rootVisualElement.Q<ProgressBar>("update-progress")?.style.display.value
                                != DisplayStyle.None));
            yield return new WaitForSecondsRealtime(0.3f);
            yield return CaptureAndWait("Docs/StartScene.png");

            // 1. 注册成功
            startController.Register();
            yield return WaitForDialog("注册成功，请登录");
            startController.DismissDialog();
            _report.AppendLine("registerNew=注册成功，请登录");

            // 2. 重复注册：用户存在
            startController.Register();
            yield return WaitForDialog("用户存在");
            yield return CaptureAndWait("Docs/StartScene-dialog.png");
            startController.DismissDialog();
            _report.AppendLine("registerDuplicate=用户存在");

            // 3. 错误密码登录：账户或密码错误
            passwordField.value = WrongPassword;
            startController.Login();
            yield return WaitForDialog("账户或密码错误");
            startController.DismissDialog();
            _report.AppendLine("wrongPassword=账户或密码错误");

            // 4. 正确密码登录
            passwordField.value = TestPassword;
            startController.Login();
            float loginDeadline = Time.realtimeSinceStartup + 5f;
            while (Time.realtimeSinceStartup < loginDeadline && SceneManager.GetActiveScene().name != "Play")
            {
                yield return null;
            }

            if (SceneManager.GetActiveScene().name != "Play")
            {
                Fail("did not enter Play scene after valid login");
                yield break;
            }

            Debug.Log("[Smoke] entered Play scene");
            deadline = Time.realtimeSinceStartup + 35f;

            while (Time.realtimeSinceStartup < deadline)
            {
                _root = UnityEngine.Object.FindFirstObjectByType<GameRoot>();
                if (_root != null && _root.IsBooted)
                {
                    break;
                }

                yield return null;
            }

            if (_root == null || !_root.IsBooted)
            {
                Fail("GameRoot did not boot within 35s");
                yield break;
            }

            _hud = UnityEngine.Object.FindFirstObjectByType<GameHud>();
            _playDocument = _hud != null ? _hud.GetComponent<UIDocument>() : null;

            float bindDeadline = Time.realtimeSinceStartup + 10f;
            while (Time.realtimeSinceStartup < bindDeadline &&
                   (_playDocument == null || _playDocument.rootVisualElement == null ||
                    _playDocument.rootVisualElement.Q<Button>("upload-button") == null))
            {
                yield return null;
            }

            if (_playDocument == null || _playDocument.rootVisualElement == null)
            {
                Fail("Play UIDocument rootVisualElement is null");
                yield break;
            }

            Label accountLabel = _playDocument.rootVisualElement.Q<Label>("account-label");
            if (PlayerSession.AccountName != _testAccount)
            {
                Fail($"account not carried to Play: expected '{_testAccount}', got '{PlayerSession.AccountName}'");
                yield break;
            }

            _report.AppendLine("playAccount=" + PlayerSession.AccountName);
            _report.AppendLine("playerId=" + PlayerSession.PlayerId);
            _report.AppendLine("hudAccount=" + (accountLabel != null ? accountLabel.text : "missing"));

            bool moduleLoaded = HotUpdateRuntime.HasModule;
            string codeVersion = HotUpdateRuntime.Current?.DisplayVersion ?? "none";
            bool expectV2 = Environment.GetCommandLineArgs().Any(a =>
                string.Equals(a, "-smokeV2", StringComparison.OrdinalIgnoreCase));
            string expectedCode = expectV2 ? "1.2.0" : "1.0.0";
            string expectedContent = expectV2 ? "1.0.1" : "1.0.0";
            int expectedDamage = expectV2 ? 14 : 7;

            _report.AppendLine("expectedMode=" + (expectV2 ? "V2" : "V1"));
            _report.AppendLine("moduleLoaded=" + moduleLoaded);
            _report.AppendLine("codeVersion=" + codeVersion);
            _report.AppendLine("contentVersion=" + _root.Bundles.ContentVersion);
            _report.AppendLine("contentSource=" + _root.Bundles.Source);
            _report.AppendLine("bundleCount=" + _root.Bundles.LoadedBundleCount);

            if (!moduleLoaded)
            {
                Fail("HotUpdate.dll was not loaded: " + HotUpdateRuntime.LastError);
                yield break;
            }

            if (codeVersion != expectedCode)
            {
                Fail($"expected code version {expectedCode}, got {codeVersion}");
                yield break;
            }

            if (_root.Bundles.ContentVersion != expectedContent)
            {
                Fail($"expected content version {expectedContent}, got {_root.Bundles.ContentVersion}");
                yield break;
            }

            if (expectV2 && _root.Bundles.Source != "cloud" && _root.Bundles.Source != "cache")
            {
                Fail("V2 should be downloaded in Start (cloud/cache), source=" + _root.Bundles.Source);
                yield break;
            }

            if (_root.Bundles.LoadedBundleCount < 2)
            {
                Fail("expected 2 loaded bundles, got " + _root.Bundles.LoadedBundleCount);
                yield break;
            }

            if (!_root.HotUpdateSpriteApplied)
            {
                Fail("hot-update sprite was not applied: " + _root.HotUpdateSpriteError);
                yield break;
            }

            SpriteRenderer renderer = (SpriteRenderer)GetField(_root, "heroRenderer");
            if (renderer == null || renderer.sprite == null)
            {
                Fail("hero sprite missing");
                yield break;
            }

            _report.AppendLine("sprite=" + renderer.sprite.name);

            PlayerHealth player = _root.Player;
            int before = player.CurrentHp;
            int damage = player.TakeDamage(PlayerHealth.BaseDamagePerStrike);
            _report.AppendLine("hpBefore=" + before);
            _report.AppendLine("damage=" + damage);
            _report.AppendLine("hpAfter=" + player.CurrentHp);

            if (before != 100 || damage != expectedDamage || player.CurrentHp != 100 - expectedDamage)
            {
                Fail($"damage expectation failed: before={before}, damage={damage}, " +
                     $"after={player.CurrentHp}, expectedDamage={expectedDamage}");
                yield break;
            }

            yield return null;
            yield return new WaitForSecondsRealtime(0.4f);
            yield return CaptureAndWait("Docs/PlayScene-1-local-damage.png");

            MethodInfo upload = typeof(GameHud).GetMethod("OnUploadClicked",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (upload == null)
            {
                Fail("GameHud.OnUploadClicked not found");
                yield break;
            }

            upload.Invoke(_hud, null);
            Debug.Log("[Smoke] upload clicked");

            Label status = _playDocument.rootVisualElement.Q<Label>("status-label");
            float uploadDeadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < uploadDeadline)
            {
                if (status != null && (status.text.StartsWith("上传成功") || status.text.StartsWith("上传失败")))
                {
                    break;
                }

                yield return null;
            }

            if (status == null)
            {
                Fail("status label missing");
                yield break;
            }

            _report.AppendLine("cloudStatus=" + status.text.Replace("\n", " | "));

            if (!status.text.StartsWith("上传成功"))
            {
                Fail("cloud upload failed: " + status.text);
                yield break;
            }

            yield return null;
            yield return new WaitForSecondsRealtime(0.4f);
            yield return CaptureAndWait("Docs/PlayScene-2-cloud-synced.png");
            yield return new WaitForSecondsRealtime(1.0f);

            _report.AppendLine("RESULT=PASS");
            WriteReport();
            Debug.Log("[Smoke] PASS\n" + _report);
            Finish(0);
        }

        private IEnumerator WaitForDialog(string expected)
        {
            float deadline = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (_dialog != null && _dialog.IsVisible && _dialog.Message == expected)
                {
                    yield break;
                }

                yield return null;
            }

            Fail($"dialog expected '{expected}', got '{(_dialog != null ? _dialog.Message : "none")}'");
        }

        private static object GetField(object target, string fieldName)
        {
            return target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(target);
        }

        private static void Capture(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? "Docs");
                ScreenCapture.CaptureScreenshot(path);
                Debug.Log("[Smoke] screenshot requested: " + path);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[Smoke] screenshot failed: " + ex.Message);
            }
        }

        private static IEnumerator CaptureAndWait(string path)
        {
            Capture(path);
            yield return new WaitForSecondsRealtime(1.2f);
        }

        private void Fail(string reason)
        {
            _report.AppendLine("RESULT=FAIL");
            _report.AppendLine("reason=" + reason);
            WriteReport();
            Debug.LogError("[Smoke] FAIL: " + reason + "\n" + _report);
            Finish(1);
        }

        private void WriteReport()
        {
            Directory.CreateDirectory("Docs");
            File.WriteAllText(Path.GetFullPath("Docs/smoke-test.txt"), _report.ToString(),
                new UTF8Encoding(false));
        }

        private void Finish(int code)
        {
            AccountStore.Delete(_testAccount);
            EditorApplication.Exit(code);
        }
    }

    internal sealed class DialogProbe
    {
        private readonly VisualElement _overlay;
        private readonly Label _message;
        private readonly Button _ok;

        public DialogProbe(VisualElement overlay, Label message, Button ok)
        {
            _overlay = overlay;
            _message = message;
            _ok = ok;
        }

        public bool IsVisible => _overlay != null && _overlay.style.display.value == DisplayStyle.Flex;
        public string Message => _message != null ? _message.text : string.Empty;

        public void Dismiss()
        {
            if (_ok != null)
            {
                _ok.SendEvent(new ClickEvent());
            }
        }
    }
}
