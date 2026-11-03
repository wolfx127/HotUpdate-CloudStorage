# HybridCLR + 手写 AssetBundle + 云同步（极简 UI）

一个 Unity 6 教学项目：**C# 代码热更 + AssetBundle 资源热更 + 云端存档（进 Play 自动恢复 HP）**。

- **两个场景**：`Start` 负责更新检查、注册/登录；`Play` 是游戏场景。
- **极简 UI**：UI Toolkit 默认控件，没有自定义按钮配色、卡片、渐变、动效。
- **有热更时**：Start 场景显示下载进度条，完成后才允许注册/登录。
- **账号系统**：注册、登录、重名/密码错误弹窗；不检查账号密码格式。
- **Play 左侧**：顶部最左边显示用户名；右侧紧挨着「扣血」「上传云端」两个按钮。
- **代码热更**：HybridCLR，纯 C#，不使用 Lua。
- **资源热更**：手写 `AssetBundleManager`，**不使用 Addressables**。
- **数值直接写在 C# 里**：伤害公式在 `HotUpdate.dll`，不用 ScriptableObject / 配置表。
- **云服务**：Node.js 零依赖服务，保存 HP JSON，同时托管 AB 文件。
- **云存档**：进 `Play` 自动从云端恢复一次 HP；「上传云端」把当前 HP 写回。

> ⚠️ **已改成纯手动流程**：原来的一键菜单（Setup / Build Initial / Build Update / Build Player / Apply V2 / Reset V1 / 冒烟测试）全部移除，
> **工程里没有任何自定义编辑器菜单，也没有内置的 AB 打包入口**（`Assets/Scripts/Editor/` 不存在）。
> 改代码、换贴图、拷 DLL、打 AB、写 catalog、同步内置内容、打 Player 都由你手动完成；AB 用你自己选的方式打，只要满足下面「打 AB 的约定」。
> **完整步骤见 [Docs/手动流程.md](Docs/手动流程.md)**；下面的"快速运行 / 手动走一遍"章节保留作设计说明，实际操作以手动流程为准。

![Start：注册/登录](Docs/StartScene-V1.png)

![Start：有热更时显示进度条](Docs/StartScene-hot-update.png)

![Play：用户名在左，扣血/上传紧挨着](Docs/V2-hot-update-proof.png)

> 当前仓库是 **V1**：青色机器人、`代码 1.0.0 / 内容 1.0.0 / -7`。
> 截图是发布 V2 后、**不重打 Player、只重启游戏**的效果：金色机器人、`代码 1.2.0 / 内容 1.0.1 / -14`。

## 环境

| 组件 | 版本 |
| --- | --- |
| Unity | 6000.2.14f1 |
| HybridCLR | 8.15.0（embedded package） |
| Node.js | 18+ |
| Player 打包 | Windows Build Support (IL2CPP) |

> HybridCLR 必须跑在 IL2CPP 上。本机已装 Windows Build Support (IL2CPP)，
> 打包路径是 `HybridCLR → Generate → All` + `File → Build Profiles → Build`。

> `HybridCLRData/`（本地 il2cpp）已在 gitignore 里。换机器后先装 HybridCLR
> （菜单 `HybridCLR/Installer`），再编译热更 DLL。

## 快速运行

### 1. 启动云服务

在项目根目录执行：

```powershell
node CloudServer/server.js
```

浏览器打开 <http://127.0.0.1:8080> 可查看状态。

### 2. Start 场景

打开 `Assets/Scenes/Start.unity`，点 Play。

启动时会先检查云端 AB：

- 没有热更：显示「已是最新内容 1.0.0」，注册/登录按钮可用；
- 有热更：显示「下载热更 xx%」进度条，下载并校验完成后显示「热更完成 100%」；
- 检查/下载期间，注册和登录按钮是禁用的。

然后：

- **注册**：输入任意账户 ID 和密码，点注册。
  - 新账号：弹窗「注册成功，请登录」；
  - 已存在：取消注册，弹窗「用户存在」，不会覆盖原密码。
- **登录**：
  - 用户名不存在或密码错误：弹窗「账户或密码错误」；
  - 正确：进入 Play。
- 不检查格式；密码仅在本机保存 SHA256 摘要，不上传。

### 3. Play 场景

顶部最左边是用户名：

```text
用户名：xxx   HP 100 / 100   [扣血 -7] [上传云端]   代码 1.0.0 | 内容 1.0.0 | builtin
```

- **进 Play 会自动从云端恢复一次存档**：拉到了就把 HP 覆盖成云端值，状态行显示「已从云端恢复：85/100（存档版本 1.0.0）」；云端没档显示「云端暂无存档」；连不上则保持初始 100。
- `扣血 -7`：走热更 DLL 里的伤害公式，HP 变成 93。
- `上传云端`：紧挨在右侧，把当前 HP、热更代码版本、时间戳 PUT 到 Node 服务。
- 也可以选中 `Player`，在 Inspector 的 `PlayerHealth` 下点同样的扣血按钮。
- 状态行显示「上传成功：云端 93/100，代码 1.0.0」。

云数据保存在 `CloudServer/data/players.json`。

## 手动走一遍（改代码 / 换贴图 / 打包 / 热更 / 回退）

**完整步骤见 [Docs/手动流程.md](Docs/手动流程.md)**，这里是骨架：

| 阶段 | 你要做的事 |
| --- | --- |
| **打第一版** | 编译 DLL → 拷成 `Assets/HotUpdate/Build/HotUpdate.dll.bytes` → 给 `Hero.png` 和 `.bytes` 打 AB 标签 → 用你自己的方式打 AB（工程不内置） → 手写 `ServerData/AB/catalog.json` → 同步到 `Assets/StreamingAssets/AB` → `HybridCLR/Generate/All` → `File/Build Profiles` 打包 |
| **发热更（V2）** | 手改 `DemoHotUpdateModule.cs`（版本号 + 伤害公式）+ 覆盖 `Hero.png` → 重编 DLL → 重打 AB → 手改 catalog（`buildNumber` +1）→ **只**覆盖 `ServerData/AB` → 重启 exe |
| **回退（V1）** | 代码/贴图改回去 → 重编重打 → catalog 用**更大**的 buildNumber（如 3，内容版本 1.0.2）→ 重启 exe |

> 铁律：`buildNumber` 只能递增。想回退就"用更高版本号发旧内容"，不能把数字改小，否则已升级的客户端不会降级。

## 打 AB 的约定（工程不提供打包入口）

`Assets/Scripts/Editor/` 已删除，工程里没有任何 `[MenuItem]`。AB 由你自己选的方式打（AssetBundle Browser、自写的编辑器脚本、命令行 batchmode 都行），只要满足下面三条：

| 约定 | 内容 |
| --- | --- |
| 输出位置 | 先落到 `ServerData/AB/`（服务器侧）；需要更新内置基线时再复制到 `Assets/StreamingAssets/AB/` |
| 文件名 | `hotupdate_code.bundle`（装 `HotUpdate.dll.bytes`）、`hotupdate_assets.bundle`（装 `Hero`） |
| catalog | `catalog.json` 里的 `name` / `file` / `sha256` / `size` 必须和实际文件一致；资源 key 固定为 `hotupdate_dll`、`hero_sprite` |

HybridCLR 官方菜单照常可用：`HybridCLR/CompileDll/Win64`（或 `ActiveBuildTarget`）、`HybridCLR/Generate/All`。这些是包自带的，不属于本工程。

## 代码入口

| 看什么 | 文件 |
| --- | --- |
| Start 注册/登录/进度条 | [`StartSceneController.cs`](Assets/Scripts/Baseline/UI/StartSceneController.cs) |
| 本地账号库 | [`AccountStore.cs`](Assets/Scripts/Baseline/Core/AccountStore.cs) |
| 用户名 → 云端 playerId | [`PlayerSession.cs`](Assets/Scripts/Baseline/Core/PlayerSession.cs) |
| Play 顶部 UI | [`GameHud.cs`](Assets/Scripts/Baseline/UI/GameHud.cs) |
| 启动、热更加载、云存档恢复 | [`GameRoot.cs`](Assets/Scripts/Baseline/Core/GameRoot.cs) |
| 手写 AB 管理器（含进度） | [`AssetBundleManager.cs`](Assets/Scripts/Baseline/Core/AssetBundleManager.cs) |
| 动态加载 DLL | [`HotUpdateContracts.cs`](Assets/Scripts/Baseline/Core/HotUpdateContracts.cs) |
| HP 与热更公式 | [`PlayerHealth.cs`](Assets/Scripts/Baseline/Core/PlayerHealth.cs) |
| 云同步 | [`CloudSyncService.cs`](Assets/Scripts/Baseline/Core/CloudSyncService.cs) |
| 热更数值 V1 | [`DemoHotUpdateModule.cs`](Assets/Scripts/HotUpdate/DemoHotUpdateModule.cs) |
| 热更数值 V2 | [`Tools/PatchV2`](Tools/PatchV2/DemoHotUpdateModule.cs) |
| 手动流程说明 | [`Docs/手动流程.md`](Docs/手动流程.md) |
| 云服务 | [`server.js`](CloudServer/server.js) |

UI 资源：

```text
Assets/UI/StartUI.uxml    账号、密码、注册/登录、进度条、弹窗
Assets/UI/PlayUI.uxml     用户名、HP、扣血、上传云端
Assets/UI/SimpleUI.uss    只有排版；按钮/输入框用默认主题
```

## 教程

- [学习路线（完整讲义）](Docs/学习路线.md)：按顺序讲清为什么、替代方案、底层原理、代码与实现思想

## 没有使用

- 没有 Lua / xLua / ILRuntime。
- 没有 Addressables。
- 没有用 ScriptableObject 或配置表存伤害数值。
