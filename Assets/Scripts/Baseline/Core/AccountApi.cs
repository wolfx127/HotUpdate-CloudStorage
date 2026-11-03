using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Baseline.Core
{
    [Serializable]
    internal sealed class AccountRequest
    {
        public string account = "";
        public string password = "";
    }

    [Serializable]
    internal sealed class AccountPayload
    {
        public string account = "";
        public string playerId = "";
    }

    [Serializable]
    internal sealed class AccountEnvelope
    {
        public bool ok;
        public AccountPayload data;
        public string error = "";
    }

    [Serializable]
    internal sealed class HealthEnvelope
    {
        public bool ok;
        public string service = "";
    }

    public sealed class AccountResult
    {
        public bool success;
        public string message;
        public string account;
        public string playerId;

        public static AccountResult Ok(string account, string playerId)
        {
            return new AccountResult
            {
                success = true,
                message = "",
                account = account ?? "",
                playerId = playerId ?? ""
            };
        }

        public static AccountResult Fail(string message)
        {
            return new AccountResult { success = false, message = message ?? "未知错误" };
        }
    }

    /// <summary>
    /// 服务端账号接口。账号、密码、存档全部在服务端 db.json，
    /// 客户端不再用 PlayerPrefs，也不在本机保存任何账号信息。
    /// </summary>
    public static class AccountApi
    {
        public const string ServiceName = "ember-guardian-cloud";

        /// <summary>
        /// 启动探针：只有返回 ok 且 service 匹配才算"服务器可用"。
        /// 只看 HTTP 200 不够——换个地址可能返回的是别人的网页。
        /// </summary>
        public static IEnumerator ProbeHealth(string baseUrl, int timeoutSeconds, Action<bool, string> onDone)
        {
            string url = (baseUrl ?? string.Empty).TrimEnd('/') + "/api/v1/health";

            UnityWebRequest request;
            try
            {
                request = UnityWebRequest.Get(url);
            }
            catch (Exception ex)
            {
                onDone?.Invoke(false, "无效的服务器地址: " + ex.Message);
                yield break;
            }

            using (request)
            {
                request.timeout = Mathf.Clamp(timeoutSeconds, 1, 30);
                request.downloadHandler = new DownloadHandlerBuffer();
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    onDone?.Invoke(false, request.error);
                    yield break;
                }

                HealthEnvelope health = null;
                try
                {
                    health = JsonUtility.FromJson<HealthEnvelope>(request.downloadHandler.text);
                }
                catch (Exception)
                {
                    health = null;
                }

                bool ok = health != null && health.ok &&
                          string.Equals(health.service, ServiceName, StringComparison.Ordinal);
                onDone?.Invoke(ok, ok ? health.service : "服务端标识不匹配");
            }
        }

        public static IEnumerator Register(string baseUrl, string account, string password,
                                           int timeoutSeconds, Action<AccountResult> onDone)
        {
            return Post(baseUrl, "register", account, password, timeoutSeconds, onDone);
        }

        public static IEnumerator Login(string baseUrl, string account, string password,
                                        int timeoutSeconds, Action<AccountResult> onDone)
        {
            return Post(baseUrl, "login", account, password, timeoutSeconds, onDone);
        }

        private static IEnumerator Post(string baseUrl, string action, string account, string password,
                                        int timeoutSeconds, Action<AccountResult> onDone)
        {
            string url = (baseUrl ?? string.Empty).TrimEnd('/') + "/api/v1/accounts/" + action;
            byte[] body = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new AccountRequest
            {
                account = account ?? string.Empty,
                password = password ?? string.Empty
            }));

            UnityWebRequest request;
            try
            {
                request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
            }
            catch (Exception ex)
            {
                onDone?.Invoke(AccountResult.Fail("无效的服务器地址: " + ex.Message));
                yield break;
            }

            using (request)
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = Mathf.Clamp(timeoutSeconds, 3, 120);

                yield return request.SendWebRequest();

                AccountEnvelope envelope = null;
                string text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        envelope = JsonUtility.FromJson<AccountEnvelope>(text);
                    }
                    catch (Exception)
                    {
                        envelope = null;
                    }
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string message = envelope != null && !string.IsNullOrEmpty(envelope.error)
                        ? envelope.error
                        : $"网络错误 ({(long)request.responseCode}): {request.error}";
                    onDone?.Invoke(AccountResult.Fail(message));
                    yield break;
                }

                if (envelope == null || !envelope.ok || envelope.data == null)
                {
                    onDone?.Invoke(AccountResult.Fail(envelope?.error ?? "服务端返回了无法解析的内容。"));
                    yield break;
                }

                onDone?.Invoke(AccountResult.Ok(
                    string.IsNullOrEmpty(envelope.data.account) ? account : envelope.data.account,
                    envelope.data.playerId));
            }
        }
    }
}
