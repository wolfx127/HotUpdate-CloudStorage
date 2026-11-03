using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace Baseline.Core
{
    [Serializable]
    public sealed class PlayerSave
    {
        public string playerId;
        public int hp;
        public int maxHp;
        public string hotUpdateVersion;
        public string clientTimeUtc;
        public int schemaVersion = 1;
        public string serverTimeUtc;
    }

    [Serializable]
    public sealed class CloudEnvelope
    {
        public bool ok;
        public PlayerSave data;
        public string error;
        public string serverTimeUtc;
    }

    public sealed class CloudResult
    {
        public bool success;
        public string message;
        public PlayerSave data;
        public long httpStatus;

        public static CloudResult Ok(string message, PlayerSave data, long status)
        {
            return new CloudResult { success = true, message = message, data = data, httpStatus = status };
        }

        public static CloudResult Fail(string message, long status)
        {
            return new CloudResult { success = false, message = message, httpStatus = status };
        }
    }

    /// <summary>
    /// 极简的 REST 云同步客户端。默认接 CloudServer/ 里的 Node 服务，
    /// 只依赖 UnityWebRequest + JsonUtility，不依赖任何第三方 SDK。
    ///
    /// 生产环境不要直接把 long-lived token 放到客户端；应该登录后换取短期 JWT，
    /// 并在服务端做鉴权、限流、版本冲突检查和审计。
    /// </summary>
    public sealed class CloudSyncService : MonoBehaviour
    {
        private AppConfig _config;
        private string _baseUrl;
        private string _token;
        private string _playerId;

        public bool IsConfigured => PlayerSession.IsOnline &&
                                    !string.IsNullOrEmpty(_baseUrl) &&
                                    !string.IsNullOrEmpty(_playerId);
        public string PlayerId => _playerId;
        public string BaseUrl => _baseUrl;

        private void Awake()
        {
            // 账号在服务端，PlayerSession 只保存本次运行的内存状态。
            _playerId = PlayerSession.PlayerId;
        }

        public void Configure(AppConfig config)
        {
            _config = config;
            _baseUrl = config.cloudBaseUrl.TrimEnd('/');
            _token = config.apiToken;

            if (!string.IsNullOrWhiteSpace(config.playerIdOverride))
            {
                _playerId = config.playerIdOverride.Trim();
            }
            else if (!string.IsNullOrWhiteSpace(PlayerSession.PlayerId))
            {
                _playerId = PlayerSession.PlayerId;
            }

            Debug.Log($"[Cloud] endpoint={_baseUrl}, account={PlayerSession.AccountName}, playerId={_playerId}");
        }

        public PlayerSave CreateSnapshot(PlayerHealth health)
        {
            return new PlayerSave
            {
                playerId = _playerId,
                hp = health.CurrentHp,
                maxHp = health.MaxHp,
                hotUpdateVersion = HotUpdateRuntime.Current?.DisplayVersion ?? "AOT fallback",
                clientTimeUtc = DateTime.UtcNow.ToString("o"),
                schemaVersion = 1
            };
        }

        public IEnumerator Upload(PlayerSave save, Action<CloudResult> onDone)
        {
            if (!IsConfigured)
            {
                onDone?.Invoke(CloudResult.Fail("云同步尚未初始化。", 0));
                yield break;
            }

            string url = $"{_baseUrl}/api/v1/players/{UnityWebRequest.EscapeURL(_playerId)}";
            string json = JsonUtility.ToJson(save);
            byte[] body = Encoding.UTF8.GetBytes(json);

            using (UnityWebRequest request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT))
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "Bearer " + _token);
                request.timeout = _config.requestTimeoutSeconds;

                yield return request.SendWebRequest();
                onDone?.Invoke(ParseResponse(request, "上传成功，云端已保存。"));
            }
        }

        public IEnumerator Download(Action<CloudResult> onDone)
        {
            if (!IsConfigured)
            {
                onDone?.Invoke(CloudResult.Fail("云同步尚未初始化。", 0));
                yield break;
            }

            string url = $"{_baseUrl}/api/v1/players/{UnityWebRequest.EscapeURL(_playerId)}";
            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.SetRequestHeader("Authorization", "Bearer " + _token);
                request.timeout = _config.requestTimeoutSeconds;

                yield return request.SendWebRequest();

                if (request.responseCode == 404)
                {
                    onDone?.Invoke(CloudResult.Fail("云端还没有这个玩家的存档，请先上传一次。", 404));
                    yield break;
                }

                onDone?.Invoke(ParseResponse(request, "已从云端拉取存档。"));
            }
        }

        private static CloudResult ParseResponse(UnityWebRequest request, string successMessage)
        {
            string text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            CloudEnvelope envelope = null;

            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    envelope = JsonUtility.FromJson<CloudEnvelope>(text);
                }
                catch (Exception)
                {
                    // 不是 JSON 时下面统一按 HTTP 错误处理。
                }
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                string serverError = envelope != null && !string.IsNullOrEmpty(envelope.error)
                    ? envelope.error
                    : request.error;

                return CloudResult.Fail($"请求失败 ({(long)request.responseCode}): {serverError}",
                    (long)request.responseCode);
            }

            if (envelope == null)
            {
                return CloudResult.Fail("服务端返回了无法解析的内容。", (long)request.responseCode);
            }

            if (!envelope.ok)
            {
                return CloudResult.Fail(envelope.error ?? "服务端返回失败。", (long)request.responseCode);
            }

            if (envelope.data != null && string.IsNullOrEmpty(envelope.data.serverTimeUtc))
            {
                envelope.data.serverTimeUtc = envelope.serverTimeUtc;
            }

            return CloudResult.Ok(successMessage, envelope.data, (long)request.responseCode);
        }
    }
}
