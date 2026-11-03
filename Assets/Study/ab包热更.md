# 一页纸：AB + HybridCLR 热更备忘

https://github.com/wolfx127/HotUpdate-CloudStorage

本项目涉及：云端保存数据、AB 包 + HybridCLR 热更（资源热更、代码热更）。

> 这是速查，不是教程。完整讲解看 `Docs/学习路线.md`，逐步操作看 `Docs/手动流程.md`。
> **当前版本**：代码 `1.4.0`、内容 `1.0.15`（`buildNumber = 16`）、扣血 `-8`、贴图 `ChineseHero`。
> 版本号只从两处读：`ServerData/AB/catalog.json` 和 `Assets/Scripts/HotUpdate/HotUpdateModule.cs`。

---

## 一、配环境（每台机器一次）

1. 装 Unity **6000.2.14f1** + **Windows Build Support (IL2CPP)**（HybridCLR 只能在 IL2CPP 上跑）。
2. `edit → Project Settings → Player → Other Settings`：
   - `Scripting Backend` = **IL2CPP**
   - `C++ Compiler Configuration` = **Release**
   - `Allow downloads over HTTP` = **Always Allowed**（本工程 `insecureHttpOption = 2`）
   - `Api Compatibility Level` = **.NET Standard 2.1**
3. `edit → Project Settings → Player → Optimization`：`Managed Stripping Level` 保持默认（Low）。
4. 装 Node.js 18+，项目根目录能跑 `node CloudServer/server.js`。
5. 如果 `HybridCLRData/` 不存在：菜单 `HybridCLR → Installer` 安装一次（它已被 gitignore）。

---

## 二、两个程序集（不要删、不要改名字）

| 程序集 | 位置 | 角色 |
| --- | --- | --- |
| `HotUpdateDemo` | `Assets/Scripts/Baseline/HotUpdateDemo.asmdef` | **AOT 基线**：Start/Play 的 UI、AB 管理器、云同步 |
| `HotUpdate` | `Assets/Scripts/HotUpdate/HotUpdate.asmdef` | **热更程序集**：只放伤害公式和版本号 |

- `HotUpdate` 引用 `HotUpdateDemo`（单向），基线**不**引用热更；
- `HotUpdate.asmdef` 已在 `ProjectSettings/HybridCLRSettings.asset` 的 `hotUpdateAssemblyDefinitions` 里登记；
- 热更代码只能实现 `Baseline.Core.IHotUpdateModule`，**不能**继承 MonoBehaviour、不能挂到场景上。

---

## 三、打第一版（新基线）

1. 菜单 `HotUpdate → 2. Build Content + Sync Baseline`
   - 编译 `HotUpdate.dll` → 覆盖 `Assets/HotUpdate/Build/HotUpdate.dll.bytes`
   - 自动写 `Assets/HotUpdate/ContentManifest.asset` 的 `hotUpdateDll`
   - 打 `ServerData/AB/content_boot.bundle`（SO + DLL + SO 引用的 Hero）
   - `buildNumber + 1`，写 `ServerData/AB/catalog.json`
   - 同步到 `Assets/StreamingAssets/AB`
2. 菜单 `HybridCLR → Generate → All`
3. `File → Build Profiles` → Windows x86_64 → `Build`（输出到 `打包/`）

> 打包会把当时的 `StreamingAssets/AB` 烤进包，所以**先打内容，再打 Player**。

---

## 四、发热更（最常见的日常）

1. 改内容，二选一或都改：
   - 改 `Assets/Scripts/HotUpdate/HotUpdateModule.cs`（`DisplayVersion` / `DamageBonus` / `CalculateDamage`）；
   - 换图：覆盖 `ContentManifest.heroSprite` 指向的那张（现在是 `Assets/Art/ChineseHero.png`）；
     换新文件则把新 Sprite 拖进 SO。
2. 菜单 `HotUpdate → 1. Build Content`（**不要**按 2，2 会改内置基线）。
3. 把 `ServerData/AB/content_boot.bundle` + `catalog.json` 覆盖到服务器。
4. **不重打 Player**，直接重启 exe：Start 出现进度条 → Play 显示新版本号/新伤害/新图。

**不需要做的事**：不用打 AB 标签、不用手写 catalog、不用改代码里的资源 key。

---

## 五、回退

- 内容改回旧版 → 菜单 1 → `buildNumber` 继续 +1（**不能改小**）；
- 已升级的客户端只认更大的 `buildNumber`，"降级"就是用更高版本号发旧内容。

---

## 六、云端

```powershell
node CloudServer/server.js          # 默认 8080
```

- 管理页 <http://127.0.0.1:8080>：建账号、删账号、改/删存档；
- 数据库 `CloudServer/data/db.json`（已被 gitignore）；清库要先停 node；
- 客户端地址在 `Assets/StreamingAssets/appconfig.json`，也可以启动时用 `-server http://ip:8080` 覆盖；
- 客户端缓存：`%USERPROFILE%\AppData\LocalLow\DefaultCompany\HotUpdate+CloudStorage\AB`，删掉即回到内置内容。

---

## 七、验收清单

| 检查 | 期望 |
| --- | --- |
| `ServerData/AB/catalog.json` | 有 `buildNumber` / `contentVersion` / `bundles[]`，**没有** `assets` 字段 |
| `Assets/StreamingAssets/AB/` | 与 `ServerData/AB/` 的 catalog + bundle 逐字节一致（菜单 2 之后） |
| `ContentManifest.asset` | `hotUpdateDll`、`heroSprite` 都非空 |
| Play 状态行 | `代码 1.4.x \| 内容 1.0.x \| builtin/cache/cloud` |
| 扣血按钮 | 数字 = `5 + DamageBonus`（当前 8） |
| `Player.log` | 有 `[AB] 已加载 ContentManifest`、`[HotUpdate] 已动态加载`、`[Cloud] 自动恢复存档` |

出问题先看 `Player.log`，常见错误对照表在 `Docs/手动流程.md` 第 5 节。
