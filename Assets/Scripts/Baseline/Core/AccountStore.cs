using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Baseline.Core
{
    /// <summary>
    /// 本地演示账号库。
    ///
    /// 只用于学习注册/登录流程：
    /// - 账号不做格式校验；
    /// - 密码只保存 SHA256 摘要，不保存明文；
    /// - 数据存在 PlayerPrefs，换机器不会同步。
    ///
    /// 真实项目必须把注册/登录放到服务端，并使用加盐哈希、HTTPS、限流和会话 token。
    /// </summary>
    public static class AccountStore
    {
        private const string KeyPrefix = "demo.auth.";

        public static bool Exists(string username)
        {
            return PlayerPrefs.HasKey(KeyPrefix + Hash(username));
        }

        /// <summary>注册成功返回 true；用户名已存在返回 false，并保持原账号不变。</summary>
        public static bool Register(string username, string password)
        {
            if (Exists(username))
            {
                return false;
            }

            PlayerPrefs.SetString(KeyPrefix + Hash(username), HashPassword(username, password));
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>用户名不存在或密码错误都返回 false。</summary>
        public static bool Validate(string username, string password)
        {
            string key = KeyPrefix + Hash(username);
            if (!PlayerPrefs.HasKey(key))
            {
                return false;
            }

            string expected = PlayerPrefs.GetString(key, string.Empty);
            return expected == HashPassword(username, password);
        }

        /// <summary>仅用于清理自动化测试账号。</summary>
        public static void Delete(string username)
        {
            PlayerPrefs.DeleteKey(KeyPrefix + Hash(username));
            PlayerPrefs.Save();
        }

        private static string HashPassword(string username, string password)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes((username ?? string.Empty) + "\n" + (password ?? string.Empty));
                return ToHex(sha.ComputeHash(bytes));
            }
        }

        private static string Hash(string username)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(username ?? string.Empty);
                return ToHex(sha.ComputeHash(bytes));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder builder = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
