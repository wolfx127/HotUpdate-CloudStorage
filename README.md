# HybridCLR + 手写 AssetBundle + 云同步（极简 UI）

一个 Unity 6 教学项目：**C# 代码热更 + AssetBundle 资源热更 + 云端存档（进 Play 自动恢复 HP）**。

- **两个场景**：`Start` 负责更新检查、注册/登录；`Play` 是游戏场景。
- **极简 UI**：UI Toolkit 默认控件，没有自定义按钮配色、卡片、渐变、动效。
- **有热更时**：Start 场景显示下载进度条，更新检查和服务器探测都完成后才启用注册/登录。
- **账号系统**：账号、密码、存档都在服务端 `CloudServer/data/db.json`，客户端不再用 `PlayerPrefs`；服务器不可达时可「离线开局」。
- **Play 左侧**：顶部最左边显示用户名；右侧紧挨着「扣血」「上传云端」两个按钮。
- **代码热更**：HybridCLR，纯 C#，不使用 Lua。
- **资源热更**：手写 `AssetBundleManager`，**不使用 Addressables**。
- **数值直接写在 C# 里**：伤害公式在 `HotUpdate.dll`，不用 ScriptableObject / 配置表。
- **云服务**：Node.js 零依赖服务，保存 HP JSON，同时托管 AB 文件。
- **云存档**：进 `Play` 自动从云端恢复一次 HP；「上传云端」把当前 HP 写回。

> ✅ **内容打包由工程内菜单完成**：`HotUpdate/1. Build Content`、`HotUpdate/2. Build Content + Sync Baseline`。
> 资源归属由 `Assets/HotUpdate/ContentManifest.asset` 的直接引用决定：**不用 AssetBundle 标签，不手写 catalog，也不在代码里写资源 key**。
> 菜单会编译 `HotUpdate.dll`、自动绑定 SO、打一个 `content_boot.bundle`、递增 `buildNumber` 并写 catalog。
> **完整步骤见 [Docs/手动流程.md](Docs/手动流程.md)**。

![Start：注册/登录](Docs/StartScene-V1.png)

![Start：有热更时显示进度条](Docs/StartScene-hot-update.png)

![Play：用户名在左，扣血/上传紧挨着](Docs/V2-hot-update-proof.png)

## 当前工程状态（build 16）

文档里的数字以这一节为准；截图是早期版本的界面，数值不代表现在。

| 项 | 当前值 | 从哪里看 |
| --- | --- | --- |
| 热更代码版本 | `1.4.0`（`DamageBonus = 3`，基础伤害 5 → 扣血 **-8**） | `Assets/Scripts/HotUpdate/HotUpdateModule.cs` |
| 内容版本 | `1.0.15`（`buildNumber = 16`） | `ServerData/AB/catalog.json` |
| 启动包 | `content_boot.bundle`，201 476 B（约 197 KB） | 同上 `bundles[0]` |
| 热更贴图 | `Assets/Art/ChineseHero.png` | `Assets/HotUpdate/ContentManifest.asset` 的 `heroSprite` |
| 发布目录 | `ServerData/AB/`（`catalog.json` + `content_boot.bundle`） | 由菜单自动生成 |
| 内置基线 | `Assets/StreamingAssets/AB/`，与发布目录**逐字节一致** | 菜单 `2` 同步 |
| Player 输出 | `打包/`（本机 2026-11-03 打的 exe，内置的是更早一版基线） | `打包/HotUpdate+CloudStorage.exe` |

> 截图左上角的 `代码 1.0.0 / 内容 1.0.0 / -7`（V1，青色机器人）和 `代码 1.2.0 / 内容 1.0.1 / -14`（V2，金色机器人）都是历史版本。
> 想复现 V1/V2，把 `Tools/PatchV1`、`Tools/PatchV2` 里的 `DemoHotUpdateModule.cs` 覆盖到 `Assets/Scripts/HotUpdate/`，再走一遍菜单即可。

## 环境

| 组件 | 版本 |
| --- | --- |
| Unity | 6000.2.14f1 |
| HybridCLR | 8.15.0（embedded package） |
| Node.js | 18+（`Dockerfile` 里用的是 `node:20-alpine`） |
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

浏览器打开 <http://127.0.0.1:8080> 是管理页：可以查看账号、创建账号、删除账号、修改/删除存档（本地演示，无鉴权）。

### 2. Start 场景

打开 `Assets/Scenes/Start.unity`，点 Play。

启动时并行做两件事：检查云端 AB、探测账号服务器。

- AB：没有热更显示「已是最新内容」，有热更显示「下载热更 xx%」进度条；
- 服务器：`GET /api/v1/health` 通了才显示「服务器可用」；
- 两边都结束后，注册/登录/离线按钮才可用（最长等 20 秒，超时也会放开按钮，避免卡死启动页）。

然后：

- **注册**：账号和密码发送到 `POST /api/v1/accounts/register`。
  - 新账号：弹窗「注册成功，请登录」；
  - 已存在：弹窗「用户存在」，不会覆盖原密码。
- **登录**：`POST /api/v1/accounts/login`。
  - 账号不存在或密码错误：弹窗「账户或密码错误」；
  - 正确：拿到服务端返回的 `playerId`，进入 Play。
- **离线开局**：服务器不可达时点这个按钮，以游客身份进入 Play，不云同步。
- 客户端不再用 `PlayerPrefs`：账号、密码、存档全部保存在服务端 `CloudServer/data/db.json`。

### 3. Play 场景

顶部最左边是用户名：

```text
用户名：xxx   HP 100 / 100   [扣血 -8] [上传云端]   代码 1.4.0 | 内容 1.0.15 | builtin
```

- **进 Play 会自动从云端恢复一次存档**：拉到了就把 HP 覆盖成云端值，状态行显示「已从云端恢复：85/100（存档版本 1.4.0）」；云端没档显示「云端暂无存档」；连不上则保持初始 100。
- `扣血 -8`：走热更 DLL 里的伤害公式（基础 5 + `DamageBonus` 3），HP 变成 92。
- `上传云端`：紧挨在右侧，把当前 HP、热更代码版本、时间戳 `PUT` 到 Node 服务。
- 状态行显示「上传成功：云端 92/100，代码 1.4.0」。

账号与存档都保存在 `CloudServer/data/db.json`（首次启动会从旧的 `players.json` 自动迁移）。

## 手动走一遍（改代码 / 换贴图 / 打包 / 热更 / 回退）

**完整步骤见 [Docs/手动流程.md](Docs/手动流程.md)**，这里是骨架：

| 阶段 | 你要做的事 |
| --- | --- |
| **打第一版** | 菜单 `HotUpdate/2. Build Content + Sync Baseline`（编译 DLL + 绑定 SO + 打 `content_boot.bundle` + 写 catalog）→ `HybridCLR/Generate/All` → `File/Build Profiles` 打包 |
| **发热更（换图 / 改代码）** | 改 `HotUpdateModule.cs` 或换 `ContentManifest.heroSprite` 指向的图（当前是 `ChineseHero.png`）→ 菜单 `HotUpdate/1. Build Content` → **只**覆盖 `ServerData/AB` → 重启 exe |
| **回退** | 内容改回去 → 菜单 1 → `buildNumber` 依然递增（删掉的资源不要复用旧号）→ 重启 exe |

> 铁律：`buildNumber` 只能递增。想回退就"用更高版本号发旧内容"，不能把数字改小，否则已升级的客户端不会降级。

## 内容打包约定（ContentManifest + 单个 boot bundle）

打包入口在 [`BuildAssetBundles.cs`](Assets/Editor/BuildAssetBundles.cs)，菜单 `HotUpdate/1. Build Content`：

1. `HybridCLR` 编译 `HotUpdate.dll`，复制成 `Assets/HotUpdate/Build/HotUpdate.dll.bytes`；
2. 自动绑定到 `Assets/HotUpdate/ContentManifest.asset` 的 `hotUpdateDll` 字段；
3. 把 `ContentManifest.asset`、`.bytes`、SO 里的 `heroSprite` 显式打进 `content_boot.bundle`；
4. 读旧 catalog，`buildNumber + 1`，重算 `sha256/size`，写 `ServerData/AB/catalog.json`。

| 约定 | 内容 |
| --- | --- |
| 输出位置 | `ServerData/AB/`；需要更新内置基线时用菜单 2 同步到 `Assets/StreamingAssets/AB/` |
| 文件名 | `content_boot.bundle`（固定，见 `ContentManifest.BundleName`） |
| catalog | 只有 `schemaVersion` / `buildNumber` / `contentVersion` / `builtAtUtc` / `bundles`，**没有 `assets` 路由表** |
| 资源身份 | 由 `ContentManifest` 的直接引用决定；代码只写 `manifest.heroSprite` / `manifest.hotUpdateDll` |
| 标签 | **不需要**；资源上若还留着旧标签，构建只警告，显式打包会忽略 |

catalog 的实际长相（`ServerData/AB/catalog.json`）：

```json
{
  "schemaVersion": 2,
  "buildNumber": 16,
  "contentVersion": "1.0.15",
  "builtAtUtc": "2026-11-03T11:02:44.1187212Z",
  "bundles": [
    {
      "name": "content_boot.bundle",
      "file": "content_boot.bundle",
      "sha256": "1ab799b43a74c300ef615ded3727396c708890238b6cc372651011948b8cab4f",
      "size": 201476,
      "dependencies": []
    }
  ]
}
```

HybridCLR 官方菜单照常可用：`HybridCLR/CompileDll/Win64`、`HybridCLR/Generate/All`。

## 代码入口

| 看什么 | 文件 |
| --- | --- |
| Start 注册/登录/进度条 | [`StartSceneController.cs`](Assets/Scripts/Baseline/UI/StartSceneController.cs) |
| 账号接口（注册/登录/探针） | [`AccountApi.cs`](Assets/Scripts/Baseline/Core/AccountApi.cs) |
| 会话（在线/离线，纯内存） | [`PlayerSession.cs`](Assets/Scripts/Baseline/Core/PlayerSession.cs) |
| 服务器地址 | [`appconfig.json`](Assets/StreamingAssets/appconfig.json) |
| Play 顶部 UI | [`GameHud.cs`](Assets/Scripts/Baseline/UI/GameHud.cs) |
| 启动、热更加载、云存档恢复 | [`GameRoot.cs`](Assets/Scripts/Baseline/Core/GameRoot.cs) |
| 手写 AB 管理器（含进度） | [`AssetBundleManager.cs`](Assets/Scripts/Baseline/Core/AssetBundleManager.cs) |
| 内容清单（SO，直接引用） | [`ContentManifest.cs`](Assets/Scripts/Baseline/Core/ContentManifest.cs) |
| 打包菜单 | [`BuildAssetBundles.cs`](Assets/Editor/BuildAssetBundles.cs) |
| 动态加载 DLL | [`HotUpdateContracts.cs`](Assets/Scripts/Baseline/Core/HotUpdateContracts.cs) |
| HP 与热更公式 | [`PlayerHealth.cs`](Assets/Scripts/Baseline/Core/PlayerHealth.cs) |
| 云同步 | [`CloudSyncService.cs`](Assets/Scripts/Baseline/Core/CloudSyncService.cs) |
| 热更数值 | [`HotUpdateModule.cs`](Assets/Scripts/HotUpdate/HotUpdateModule.cs) |
| 热更数值 V2 | [`Tools/PatchV2`](Tools/PatchV2/DemoHotUpdateModule.cs) |
| 云服务 | [`server.js`](CloudServer/server.js) |

UI 资源：

```text
Assets/UI/StartUI.uxml    账号、密码、注册/登录、进度条、弹窗
Assets/UI/PlayUI.uxml     用户名、HP、扣血、上传云端
Assets/UI/SimpleUI.uss    只有排版；按钮/输入框用默认主题
```

## 文档地图

| 文档 | 讲什么 |
| --- | --- |
| [README.md](README.md) | 项目是什么、当前版本、怎么跑起来 |
| [Docs/手动流程.md](Docs/手动流程.md) | 改代码 / 换贴图 / 打包 / 热更 / 回退的具体操作 |
| [Docs/学习路线.md](Docs/学习路线.md) | 按顺序讲清为什么、替代方案、底层原理、实现思想 |
| [Docs/云服务端解读.md](Docs/云服务端解读.md) | `server.js` 逐段讲解 + JS 语法速查（给写 C# 的人） |
| [Assets/Study/调用链全解析.md](Assets/Study/调用链全解析.md) | 五条核心调用链：启动服务、注册、登录、上传、热更 |
| [Assets/Study/ab包热更.md](Assets/Study/ab包热更.md) | 一页纸操作备忘（配环境 / 打包 / 热更） |

## 已知限制（教学 Demo，别照搬到线上）

- 客户端与服务端之间是**明文 HTTP**，`appconfig.json` 里的 `apiToken` 是共享的 `dev-token`；
- 热更包**没有签名**，`catalog.json` 和 `bundle` 同源同机，谁控制服务器谁就能换掉热更 DLL；
- 账号密码在 `db.json` 里是**明文**，`playerId` 由账号名哈希得到、可预测；
- 管理页 `/` 无鉴权，只适合本机演示；
- `Android` 相关配置存在，但 AB 用的是 `File` + `AssetBundle.LoadFromFile`，APK 内的 `StreamingAssets` 读不到，**当前只验证过 Windows**。

生产化要改什么，见 [Docs/学习路线.md](Docs/学习路线.md) 第 14 章。

## 没有使用

- 没有 Lua / xLua / ILRuntime。
- 没有 Addressables。
- 没有用 ScriptableObject 或配置表存伤害数值。
