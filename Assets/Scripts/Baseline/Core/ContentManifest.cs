using UnityEngine;

namespace Baseline.Core
{
    /// <summary>
    /// 热更内容的唯一入口：代码只认这里的直接引用，不认 bundle 名、key 或标签。
    /// 该资源必须打进 AB（不能只放在工程里，也不能放 Resources），
    /// 运行时从 boot bundle 里读出它，再用 hotUpdateDll / heroSprite。
    /// </summary>
    [CreateAssetMenu(menuName = "HotUpdate/Content Manifest", fileName = "ContentManifest")]
    public sealed class ContentManifest : ScriptableObject
    {
        /// <summary>启动包的固定名字，仅作为传输层标识，不参与资源查找。</summary>
        public const string BundleName = "content_boot.bundle";

        [Header("热更代码")]
        public TextAsset hotUpdateDll;

        [Header("热更资源")]
        public Sprite heroSprite;
    }
}
