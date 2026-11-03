using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Baseline.Core
{
    /// <summary>
    /// AOT 主工程和热更程序集之间的唯一契约。
    /// 主工程只认识这个接口，不认识任何热更类，因此热更 DLL 可以随时替换。
    /// </summary>
    public interface IHotUpdateModule
    {
        /// <summary>热更代码版本号，显示在 HUD 和 Inspector 上，改代码后应一起修改。</summary>
        string DisplayVersion { get; }

        /// <summary>一段简短说明，用来证明当前运行的是哪一份 DLL。</summary>
        string Notes { get; }

        /// <summary>额外的伤害加成。V2 热更会直接改这个值。</summary>
        int DamageBonus { get; }

        /// <summary>
        /// 真正的扣血公式。把公式放在热更程序集里，替换 DLL 就能替换逻辑。
        /// </summary>
        int CalculateDamage(int baseDamage, int currentHp, int maxHp);
    }

    /// <summary>
    /// 用反射把热更 DLL 装进进程，并暴露当前模块。
    /// 这里刻意不引用 HotUpdate 程序集，保证主工程可以独立编译/打包。
    /// </summary>
    public static class HotUpdateRuntime
    {
        public static IHotUpdateModule Current { get; private set; }
        public static string CurrentAssemblyPath { get; private set; }
        public static string LastError { get; private set; }

        public static bool HasModule => Current != null;

        public static bool TryInitialize(byte[] assemblyBytes, out string error)
        {
            error = null;
            LastError = null;
            Current = null;

            if (assemblyBytes == null || assemblyBytes.Length == 0)
            {
                error = "热更 DLL 字节为空。";
                LastError = error;
                return false;
            }

            try
            {
                Assembly assembly = Assembly.Load(assemblyBytes);
                Type moduleType = assembly.GetTypes()
                    .FirstOrDefault(t => typeof(IHotUpdateModule).IsAssignableFrom(t)
                                         && !t.IsAbstract
                                         && t.GetConstructor(Type.EmptyTypes) != null);

                if (moduleType == null)
                {
                    error = "HotUpdate.dll 里找不到实现 IHotUpdateModule 的无参构造类。";
                    LastError = error;
                    return false;
                }

                Current = (IHotUpdateModule)Activator.CreateInstance(moduleType);
                CurrentAssemblyPath = assembly.Location;
                Debug.Log($"[HotUpdate] 已动态加载 {assembly.GetName().Name} {assembly.GetName().Version} " +
                          $"-> {moduleType.FullName} / {Current.DisplayVersion}");
                return true;
            }
            catch (Exception ex)
            {
                // 编辑器里 HotUpdate 程序集可能已经被 Unity 编译并加载；此时打印完整异常方便排查。
                error = $"动态加载热更 DLL 失败: {ex.GetType().Name}: {ex.Message}";
                LastError = error;
                Debug.LogException(ex);
                return false;
            }
        }

        public static int CalculateDamage(int baseDamage, int currentHp, int maxHp)
        {
            return Current != null
                ? Mathf.Max(1, Current.CalculateDamage(baseDamage, currentHp, maxHp))
                : Mathf.Max(1, baseDamage);
        }

        public static int PreviewDamage(int baseDamage, int currentHp, int maxHp)
        {
            return CalculateDamage(baseDamage, currentHp, maxHp);
        }

        public static void Reset()
        {
            Current = null;
            CurrentAssemblyPath = null;
            LastError = null;
        }
    }
}
