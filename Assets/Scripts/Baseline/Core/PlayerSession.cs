using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Baseline.Core
{
    /// <summary>
    /// Start 场景输入的账号会变成云端存档的 playerId。
    /// 这里只做演示：账号不做格式校验，密码不保存、不发送。
    /// 为了让任意中文/空格账号都能被服务端接受，playerId 用账号的 SHA256 前缀生成。
    /// </summary>
    public static class PlayerSession
    {
        private const string AccountKey = "demo.account";
        private const string GuestIdKey = "demo.guest.player.id";

        public static string AccountName { get; private set; } = "Guest";
        public static string PlayerId { get; private set; }

        public static void EnsureInitialized()
        {
            if (!string.IsNullOrEmpty(PlayerId))
            {
                return;
            }

            string savedAccount = PlayerPrefs.GetString(AccountKey, string.Empty);
            if (!string.IsNullOrWhiteSpace(savedAccount))
            {
                SetAccount(savedAccount);
                return;
            }

            AccountName = "Guest";
            PlayerId = PlayerPrefs.GetString(GuestIdKey, string.Empty);
            if (string.IsNullOrEmpty(PlayerId))
            {
                PlayerId = "player-" + Guid.NewGuid().ToString("N").Substring(0, 12);
                PlayerPrefs.SetString(GuestIdKey, PlayerId);
                PlayerPrefs.Save();
            }
        }

        public static void SetAccount(string account)
        {
            // 不检查格式；空账号也允许进入，统一记为 Guest。
            AccountName = string.IsNullOrWhiteSpace(account) ? "Guest" : account.Trim();
            PlayerId = "player-" + ShortHash(AccountName);
            PlayerPrefs.SetString(AccountKey, AccountName);
            PlayerPrefs.Save();
        }

        private static string ShortHash(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                StringBuilder builder = new StringBuilder(16);
                for (int i = 0; i < 6; i++)
                {
                    builder.Append(hash[i].ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
