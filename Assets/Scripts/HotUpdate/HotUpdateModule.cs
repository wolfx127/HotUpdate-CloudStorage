using Baseline.Core;
using HotUpdateDemo;
using UnityEngine;

namespace HotUpdateDemo.HotUpdate
{
    public sealed class DemoHotUpdateModule : IHotUpdateModule
    {
        public string DisplayVersion => "1.4.0";

        public string Notes => "V1 · 热更代码，改这里即可";

        public int DamageBonus => 3;

        public int CalculateDamage(int baseDamage, int currentHp, int maxHp)
        {
            int raw = baseDamage + DamageBonus;
            return Mathf.Clamp(raw, 1, Mathf.Max(1, currentHp));
        }
    }
}