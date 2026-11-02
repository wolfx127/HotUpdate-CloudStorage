using UnityEditor;
using UnityEngine;

namespace HotUpdateDemo.EditorTools
{
    /// <summary>
    /// 运行时在 Inspector 上提供扣血按钮。
    /// 按钮不直接改字段，而是调用和 UI 完全相同的 PlayerHealth.TakeDamage()，
    /// 这样两套入口共享热更公式，才能验证代码热更真的生效。
    /// </summary>
    [CustomEditor(typeof(PlayerHealth))]
    public sealed class PlayerHealthEditor : Editor
    {
        private PlayerHealth Player => (PlayerHealth)target;

        private void OnEnable()
        {
            if (target is PlayerHealth health)
            {
                health.Changed += OnHealthChanged;
            }
        }

        private void OnDisable()
        {
            if (target is PlayerHealth health)
            {
                health.Changed -= OnHealthChanged;
            }
        }

        private void OnHealthChanged(PlayerHealth health)
        {
            Repaint();
        }

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("热更代码 / Hot Update", EditorStyles.boldLabel);

            IHotUpdateModule module = HotUpdateRuntime.Current;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("当前代码版本", module != null ? module.DisplayVersion : "AOT fallback");
                EditorGUILayout.LabelField("伤害加成", module != null ? "+" + module.DamageBonus : "0");
                EditorGUILayout.LabelField("下一次扣血", Application.isPlaying ? Player.NextDamagePreview.ToString() : "-");
                if (module != null)
                {
                    EditorGUILayout.LabelField("说明", module.Notes, EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                GUI.backgroundColor = new Color(1f, 0.55f, 0.25f);
                string label = Application.isPlaying
                    ? $"扣血  -{Player.NextDamagePreview}  （走热更公式）"
                    : "进入 Play Mode 后可扣血";
                if (GUILayout.Button(label, GUILayout.Height(36)))
                {
                    Undo.RecordObject(Player, "Hot Update Demo Damage");
                    int damage = Player.TakeDamage(PlayerHealth.BaseDamagePerStrike);
                    EditorUtility.SetDirty(Player);
                    Debug.Log($"[Inspector] 扣血 {damage}，剩余 {Player.CurrentHp}/{Player.MaxHp}");
                }

                GUI.backgroundColor = Color.white;
                if (GUILayout.Button("回满血 / Reset HP", GUILayout.Height(24)))
                {
                    Undo.RecordObject(Player, "Hot Update Demo Reset HP");
                    Player.RestoreToFull();
                    EditorUtility.SetDirty(Player);
                }
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("按钮只在 Play Mode 下可用。运行后可以在 Inspector 直接观察 currentHp 的变化。",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox("Inspector 按钮和 UI Toolkit 的 STRIKE 按钮调用同一个方法；" +
                                        "热更 DLL 替换后，这里的公式和预览会自动变化。", MessageType.None);
            }

            serializedObject.Update();
        }
    }
}
