using HotUpdateDemo;
using UnityEngine;

namespace HotUpdateDemo.HotUpdate
{
    /// <summary>
    /// V2 热更代码：不重新打包 exe，只替换 AB 里的 HotUpdate.dll。
    ///
    /// 和 V1 的差别：
    /// - DisplayVersion / Notes 改了（证明运行的是新 DLL）；
    /// - DamageBonus 从 2 改成 9；
    /// - 增加“残血狂暴”：HP 低于 35% 时伤害翻倍。
    ///
    /// 这些数值全部是代码常量，不使用 ScriptableObject。
    /// </summary>
    public sealed class DemoHotUpdateModule : IHotUpdateModule
    {
        public string DisplayVersion => "1.2.0";

        public string Notes => "V2 hotfix · +9 damage, enrage x2 below 35% HP";

        public int DamageBonus => 9;

        public string HeroAssetKey => "hero_sprite";

        public int CalculateDamage(int baseDamage, int currentHp, int maxHp)
        {
            int raw = baseDamage + DamageBonus;
            bool enraged = maxHp > 0 && currentHp <= Mathf.CeilToInt(maxHp * 0.35f);
            if (enraged)
            {
                raw *= 2;
            }

            return Mathf.Clamp(raw, 1, Mathf.Max(1, currentHp));
        }
    }
}
