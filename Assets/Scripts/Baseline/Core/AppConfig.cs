using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Baseline.Core
{
    /// <summary>
    /// 运行时可编辑的配置。放在 StreamingAssets/appconfig.json，
    /// 打包后无需重新编译即可把地址换成本机、局域网或真正的云服务器。
    /// 注意：这里只放“连接信息”，不放伤害数值；伤害数值属于热更代码。
    /// </summary>
    [Serializable]
    public sealed class AppConfig
    {
        public string cloudBaseUrl = "http://127.0.0.1:8080";
        public string contentBaseUrl = "http://127.0.0.1:8080/ServerData/AB";
        public string apiToken = "dev-token";
        public string playerIdOverride = "";
        public int requestTimeoutSeconds = 15;

        public const string FileName = "appconfig.json";

        public void Normalize()
        {
            cloudBaseUrl = (cloudBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            contentBaseUrl = (contentBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            apiToken = apiToken ?? string.Empty;
            playerIdOverride = (playerIdOverride ?? string.Empty).Trim();
            requestTimeoutSeconds = Mathf.Clamp(requestTimeoutSeconds, 3, 120);

            if (string.IsNullOrEmpty(cloudBaseUrl))
            {
                cloudBaseUrl = "http://127.0.0.1:8080";
            }

            if (string.IsNullOrEmpty(contentBaseUrl))
            {
                contentBaseUrl = cloudBaseUrl + "/ServerData/AB";
            }
        }

        public static IEnumerator Load(Action<AppConfig> onDone, Action<string> onWarning)
        {
            AppConfig config = new AppConfig();
            string path = Path.Combine(Application.streamingAssetsPath, FileName);

            if (!File.Exists(path))
            {
                onWarning?.Invoke($"未找到 {FileName}，使用内置默认地址。");
                config.Normalize();
                onDone?.Invoke(config);
                yield break;
            }

            string json = null;
            string error = null;

            // Standalone / Editor 可以直接读文件；Android 的 StreamingAssets 在 jar 里，需要 UWR。
            if (Application.platform == RuntimePlatform.Android)
            {
                using (UnityWebRequest request = UnityWebRequest.Get(path))
                {
                    yield return request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        json = request.downloadHandler.text;
                    }
                    else
                    {
                        error = request.error;
                    }
                }
            }
            else
            {
                try
                {
                    json = File.ReadAllText(path);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }

            if (!string.IsNullOrEmpty(error))
            {
                onWarning?.Invoke($"读取 {FileName} 失败，使用默认地址：{error}");
            }
            else
            {
                try
                {
                    AppConfig parsed = JsonUtility.FromJson<AppConfig>(json);
                    if (parsed != null)
                    {
                        config = parsed;
                    }
                }
                catch (Exception ex)
                {
                    onWarning?.Invoke($"{FileName} JSON 解析失败，使用默认地址：{ex.Message}");
                }
            }

            // 打包后不重新发版也能换服务器：Game.exe -server http://10.0.0.5:8080
            string commandLineServer = ReadCommandLineServer();
            if (!string.IsNullOrWhiteSpace(commandLineServer))
            {
                config.cloudBaseUrl = commandLineServer.Trim();
                config.contentBaseUrl = config.cloudBaseUrl + "/ServerData/AB";
                Debug.Log("[Config] 使用命令行指定的服务器：" + config.cloudBaseUrl);
            }

            config.Normalize();
            onDone?.Invoke(config);
        }

        private static string ReadCommandLineServer()
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "-server", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }
    }
}
