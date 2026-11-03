using System;
using UnityEngine;

namespace Baseline.Core
{
    public enum SessionMode
    {
        None,
        Online,
        Offline
    }

    /// <summary>
    /// 当前会话。刻意只放在内存里：
    /// - 账号、密码、存档全部在服务端（CloudServer/data/db.json）；
    /// - 客户端不再用 PlayerPrefs 保存任何账号信息；
    /// - 每次启动都要重新登录；
    /// - 服务器不可达时可以进入离线模式，用游客身份把游戏跑起来。
    /// </summary>
    public static class PlayerSession
    {
        public static SessionMode Mode { get; private set; } = SessionMode.None;

        public static string AccountName { get; private set; } = "游客";

        /// <summary>在线时是服务端返回的 playerId；离线时是本进程随机生成的游客 id。</summary>
        public static string PlayerId { get; private set; } = string.Empty;

        public static bool IsOnline => Mode == SessionMode.Online;

        public static bool HasSession => Mode != SessionMode.None;

        public static void EnterOnline(string account, string playerId)
        {
            Mode = SessionMode.Online;
            AccountName = string.IsNullOrWhiteSpace(account) ? "游客" : account.Trim();
            PlayerId = playerId ?? string.Empty;
            Debug.Log($"[Session] online account={AccountName}, playerId={PlayerId}");
        }

        public static void EnterOffline()
        {
            Mode = SessionMode.Offline;
            AccountName = "游客";
            PlayerId = "guest-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            Debug.Log($"[Session] offline guestId={PlayerId}");
        }

        public static void Clear()
        {
            Mode = SessionMode.None;
            AccountName = "游客";
            PlayerId = string.Empty;
        }
    }
}
