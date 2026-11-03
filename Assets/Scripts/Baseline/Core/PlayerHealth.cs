using System;
using UnityEngine;

namespace Baseline.Core
{
    /// <summary>
    /// 玩家生命值。它属于 AOT 主工程，因此不能、也不需要在热更里重新定义。
    /// 扣血公式通过 IHotUpdateModule 委托给热更 DLL。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerHealth : MonoBehaviour
    {
        public const int BaseDamagePerStrike = 5;

        [Header("Runtime Stats")]
        [SerializeField, Min(1)] private int maxHp = 100;
        [SerializeField, Min(0)] private int currentHp = 100;

        /// <summary>HP 变化时触发，HUD 和 Inspector 都依赖这个事件刷新显示。</summary>
        public event Action<PlayerHealth> Changed;

        public int MaxHp => maxHp;
        public int CurrentHp => currentHp;
        public bool IsDead => currentHp <= 0;

        public int NextDamagePreview => PreviewDamage(BaseDamagePerStrike);

        public int PreviewDamage(int baseDamage)
        {
            if (currentHp <= 0)
            {
                return 0;
            }

            return Mathf.Min(currentHp, HotUpdateRuntime.CalculateDamage(baseDamage, currentHp, maxHp));
        }

        /// <summary>
        /// 所有扣血入口（Inspector 按钮、UI Toolkit 按钮）都会走这里。
        /// 返回值是这次实际扣掉的血量。
        /// </summary>
        public int TakeDamage(int baseDamage)
        {
            if (currentHp <= 0)
            {
                return 0;
            }

            int damage = PreviewDamage(baseDamage);
            currentHp = Mathf.Max(0, currentHp - damage);
            Changed?.Invoke(this);
            Debug.Log($"[HP] -{damage} => {currentHp}/{maxHp} " +
                      $"(code={HotUpdateRuntime.Current?.DisplayVersion ?? "AOT fallback"})");
            return damage;
        }

        public void RestoreToFull()
        {
            currentHp = maxHp;
            Changed?.Invoke(this);
        }

        /// <summary>云端回填只用这一个入口，避免外部直接改 private 字段。</summary>
        public void ApplyServerHp(int serverHp)
        {
            currentHp = Mathf.Clamp(serverHp, 0, maxHp);
            Changed?.Invoke(this);
        }

        private void OnValidate()
        {
            maxHp = Mathf.Max(1, maxHp);
            currentHp = Mathf.Clamp(currentHp, 0, maxHp);
        }
    }
}
