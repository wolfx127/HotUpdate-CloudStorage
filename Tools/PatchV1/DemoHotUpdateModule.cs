using HotUpdateDemo;
using UnityEngine;

namespace HotUpdateDemo.HotUpdate
{
    /// <summary>
    /// V1 备份：手动复制到 Assets/Scripts/HotUpdate/DemoHotUpdateModule.cs 覆盖即可回退。
    /// </summary>
    public sealed class DemoHotUpdateModule : IHotUpdateModule
    {
        public string DisplayVersion => "1.0.0";

        public string Notes => "V1 · code loaded from AB / HotUpdate.dll";

        public int DamageBonus => 2;

        public string HeroAssetKey => "hero_sprite";

        public int CalculateDamage(int baseDamage, int currentHp, int maxHp)
        {
            int raw = baseDamage + DamageBonus;
            return Mathf.Clamp(raw, 1, Mathf.Max(1, currentHp));
        }
    }
}
