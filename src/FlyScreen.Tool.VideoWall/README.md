# FlyScreen.Tool.VideoWall（视频墙）

独立工具工程骨架。**当前状态：契约、模块桩、源表单矩阵、源目录 CRUD 与封面缓存已编译通过并完成冒烟验证；播放后端与 UI 尚未接线。**

| 项 | 内容 |
|----|------|
| ToolId | `video-wall` |
| 显示名 | 视频墙（多源视频播放 · 九宫格 · 合流） |
| 规格 | [docs/video-wall.md](../../docs/video-wall.md) |
| 可行性 | [docs/video-wall-feasibility.md](../../docs/video-wall-feasibility.md) |
| 原型 | [docs/video-wall-prototype.md](../../docs/video-wall-prototype.md) |
| 源协议/编码矩阵 | [docs/video-wall-sources.md](../../docs/video-wall-sources.md) |

---

## 1. 当前已有什么

```text
src/FlyScreen.Tool.VideoWall/
├── manifest.json                       工具清单（id/权限/能力/入口）
├── FlyScreen.Tool.VideoWall.csproj     入口程序集（骨架只依赖 ToolSdk）
├── Module.cs                           IToolModule 实现（卡片/工作区入口，后端未接线）
├── VideoWallIds.cs                     工具标识与接线开关
├── VideoWallLocalization.cs            zh-CN/en-US 文案（未注册到宿主，见 TODO）
├── Contracts/
│   ├── VideoWallEnums.cs               源类型/状态机/错误码/音频策略/校验状态
│   ├── VideoWallModels.cs              端点、源、分组、布局、场景、统计、探测结果
│   └── IMediaBackend.cs                内核抽象（IMediaBackend / IMediaPlayer / IMediaCompositor）
└── Sources/
    ├── SourceFormSchema.cs             四类源的字段矩阵 + 条件显隐求值（§3.3 的唯一实现）
    ├── VideoSourceCatalog.cs           sources.json 原子读写 / CRUD / 地址去重 / 校验回写
    └── CoverCache.cs                   封面键（SHA256 前 16 位）/ 路径校验 / 孤儿清理
```

**能做什么**：
- `dotnet build` 0 警告 0 错误，可被 `Pack-FlyTools.ps1` 打包为 `.flytool`（入口类型已对齐）。
- 源管理的数据层已可独立工作：四类源的字段定义、增删改、JSON 往返、去重、封面键与清理，已用临时控制台工程跑过 37 项断言并全部通过（工程已删除，断言清单见 §6）。

**不能做什么**：
- 点开卡片不会播放（`VideoWallIds.BackendWired == false`，主操作直接返回「骨架：播放后端尚未接线」）。
- 没有抓帧代码，`CoverCache` 只有路径与清理，真实首帧仍待接入 `IMediaBackend`。
- 没有 UI：列表视图、卡片视图、添加弹窗、单条播放视图都只有文档线框。

---

## 2. 目标工程结构（接线后应补齐）

```text
src/
├── FlyScreen.Tool.VideoWall/                业务装配（不含任何 WPF/Avalonia 类型）
│   ├── Sources/
│   │   ├── SourceFormSchema.cs              ✅ 已实现
│   │   ├── VideoSourceCatalog.cs            ✅ 已实现
│   │   ├── CoverCache.cs                    ✅ 已实现
│   │   ├── VideoEndpointParser.cs           地址归一化（docs §6 的 12 条规则）
│   │   ├── CredentialStore.cs               DPAPI 凭据表
│   │   ├── NvrTemplateExpander.cs           {host}/{ch}/{ch2} 模板批量
│   │   ├── ProbeService.cs                  表单校验 + 「测试」按钮
│   │   └── CoverCaptureService.cs           抓首帧 → 320×180 JPEG → CoverCache
│   ├── Player/
│   │   └── SinglePlaybackController.cs      单条播放（P0-a，工作区内播放视图）
│   ├── Wall/
│   │   ├── VideoWallService.cs              墙的装配：布局、格、播放器生命周期、预算闸门
│   │   ├── TileSession.cs                   单格状态机（Idle→…→Failed）
│   │   ├── ReconnectScheduler.cs            指数退避重连
│   │   ├── AudioRouter.cs                   静音 / 独奏 / 混音（P2）
│   │   └── SceneStore.cs                    scenes.json + 启动加载
│   ├── Backend/
│   │   ├── Vlc/                             P0：LibVLCSharp，单 LibVLC 实例 + N 个 MediaPlayer
│   │   └── Ffmpeg/                          P1：单交换链合成、合流输出（LGPL 构建）
│   └── Contracts/                           ✅ 已实现
├── FlyScreen.Tool.VideoWall.Ui.Win/         WPF 工作区（列表/卡片/添加弹窗/播放视图/格布局/原生面）
├── FlyScreen.Tool.VideoWall.Ui.Desktop/     Avalonia 工作区（NativeControlHost）
└── FlyScreen.Tool.VideoWall.Ui.Maui/        一期不做（清单 platforms 仅 Windows）
```

原生库落到包内 `runtimes/win-x64/`（`libvlc.dll`、`libvlccore.dll`、裁剪后的 `plugins/`），由 `Pack-FlyTools.ps1` 的 `runtimes` 目录规则带入。

---

## 3. 接线清单（按顺序，每步可独立验证）

| # | 步骤 | 验收 |
|---|------|------|
| 1 | `FlyScreen.Core/…/ToolIds.cs` 加 `VideoWall = "video-wall"`；分组文案加 `media` | 工具页出现卡片（未安装态） |
| 2 | `Module.OnLoadedAsync` 注册 `IVideoWallService`；`Loc.RegisterPlugin(VideoWallLocalization.Instance)`；注册 `tool-video-wall` 图标 | 卡片名称/说明/图标正确 |
| 3 | `Tools/…` 权限白名单加 `networkStream` / `localFile.open`（见 docs §7） | 安装同意框能正确展示并安装成功 |
| 4 | `VideoEndpointParser` + 单测（12 条归一化规则），把 `SourceFormSchema` 的字段值组装成 `VideoSourceDescriptor` | 单测全绿；四类源都能生成记录 |
| 5 | `Backend/Vlc`：单实例多播放器、硬解回落、状态/统计事件 | 单路 RTSP 稳定播放 1 小时 |
| 6 | `CoverCaptureService`：`IMediaBackend.ProbeAsync` 取首帧 → JPEG → `CoverCache` → `UpdateValidation` | 好源出图并有封面，坏源给具体错误码 |
| 7 | `Ui.Win` 源管理页（列表/卡片双视图 + ＋添加 弹窗 4 类表单） | 能加一条、能删一条、能编辑 |
| 8 | `Ui.Win` 单条播放视图 + `Player/SinglePlaybackController` | 双击即播 30 分钟不中断；编辑后自动重连 |
| 9 | `VideoWallService` + `TileSession`：九宫格、空格、重连、预算 | 9 路并发 1 小时不崩 |
| 10 | 场景读写 + 启动加载 + 合流视图 | 保存/加载/删除可用 |
| 11 | `Pack-FlyTools.ps1` 加条目 + 插件裁剪 + 许可文本 | 包 ≤ 60MB，可安装可卸载 |

---

## 4. 已知缺口（务必不要当成已完成）

- `manifest.json` 里的 `networkStream` / `localFile.open` 权限**尚未在** `docs/tools.md` §5.5.3 白名单中登记；未登记前同意框不会解释这两项，安装校验行为也未定义。
- `VideoWallLocalization` 未注册到宿主（`Module` 里是 TODO）。`SourceFormSchema` 引用的 53 个 `Tool.VideoWall.*` 键已全部补齐到 zh-CN/en-US（两表各 96 键，脚本比对无缺失），但**注册之前界面仍取不到文案**。
- 没有 UI 卫星工程，`IToolUiContributor` / 工作区工厂均空缺，`UsesWorkspace=true` 但没有可渲染内容。
- 没有原生库、没有 NuGet 依赖（`LibVLCSharp` / `VideoLAN.LibVLC.Windows` 尚未引入）。
- `IMediaCompositor` 只有契约，无实现；P0 的合流按「二次渲染视图」口径交付。
- `manifest.json` 的 `platforms: ["Windows"]` 会同时覆盖 MAUI/WinUI；若 MAUI 也装了本工具，需要 UI 侧明确不展示（见 docs 可行性 §4）。
- 凭据尚未落地：`VideoSourceCatalog` 只存 `CredentialRef` 字符串，`CredentialStore`（DPAPI）未实现，所以目前**没有**可用的密码持久化路径。
- `VideoSourceCatalog` 没有文件级并发保护（单进程单实例假设）；多实例同时写会以「后写覆盖」告终。

---

## 5. 构建

```powershell
# 骨架单独编译（只依赖 ToolSdk）
dotnet build src/FlyScreen.Tool.VideoWall/FlyScreen.Tool.VideoWall.csproj -c Debug

# 接线后打包（需先在 Pack-FlyTools.ps1 注册 video-wall 条目）
pwsh build/Pack-FlyTools.ps1 -Configuration Release
```

> 一期**不把**本工具加入 `Bundled`（包体 30–55MB，会让每个安装包变重），走 Release + `tools-index.json` 按需下载，理由见 docs 可行性 §6。

---

## 6. 数据层的冒烟断言（37 项，已验证通过）

用一个临时 `net10.0` 控制台工程引用本工程，`dotnet run` 全部 PASS，覆盖：

- 目录：空文件加载、分组 Id 前缀、四类源新增、自动生成 `src-xxxxxxxx`、同地址去重拒绝、内存计数、文件落盘。
- 往返：重新加载后源/分组数量、自定义 Header、端点字段（transport/bufferMs）、校验状态、封面键、中文备注、`createdAt`。
- 校验回写：失败计数递增、成功归零。
- 编辑/删除：改地址允许、改成重复地址拒绝、删除生效。
- 封面：键长 16 位、`Exists`、`PathForKey` 拒绝 `../../etc`、孤儿清理且保留被引用封面。
- 表单：摄像头声明了 username 字段、`auth=none` 时隐藏、`auth=digest` 时可见、NVR 有 `addressTemplate` 且无裸 `address`、文件类型用 File 选择器、`protocol==rtsp` 大小写不敏感。

临时工程用后已删除，断言本身未进仓库——若要长期守护，应另建 `tests/` 项目并把上表转成正式单测。

| 项 | 内容 |
|----|------|
| ToolId | `video-wall` |
| 显示名 | 视频墙（多源视频播放 · 九宫格 · 合流） |
| 规格 | [docs/video-wall.md](../../docs/video-wall.md) |
| 可行性 | [docs/video-wall-feasibility.md](../../docs/video-wall-feasibility.md) |
| 原型 | [docs/video-wall-prototype.md](../../docs/video-wall-prototype.md) |
| 源协议/编码矩阵 | [docs/video-wall-sources.md](../../docs/video-wall-sources.md) |

---

## 1. 当前已有什么

```text
src/FlyScreen.Tool.VideoWall/
├── manifest.json                       工具清单（id/权限/能力/入口）
├── FlyScreen.Tool.VideoWall.csproj     入口程序集（骨架只依赖 ToolSdk）
├── Module.cs                           IToolModule 实现（卡片/工作区入口，后端未接线）
├── VideoWallIds.cs                     工具标识与接线开关
├── VideoWallLocalization.cs            zh-CN/en-US 文案（未注册到宿主，见 TODO）
└── Contracts/
    ├── VideoWallEnums.cs               源类型/状态机/错误码/音频策略
    ├── VideoWallModels.cs              端点、源、布局、场景、统计、探测结果
    └── IMediaBackend.cs                内核抽象（IMediaBackend / IMediaPlayer / IMediaCompositor）
```

**能做什么**：编译通过（`dotnet build` 0 警告 0 错误），可被 `Pack-FlyTools.ps1` 打包为 `.flytool`（入口类型已对齐）。
**不能做什么**：点开卡片不会播放（`VideoWallIds.BackendWired == false`，主操作直接返回「骨架：播放后端尚未接线」）。

---

## 2. 目标工程结构（接线后应补齐）

```text
src/
├── FlyScreen.Tool.VideoWall/                业务装配（不含任何 WPF/Avalonia 类型）
│   ├── Sources/
│   │   ├── VideoEndpointParser.cs           地址归一化（docs §6 的 12 条规则）
│   │   ├── VideoSourceStore.cs              sources.json 读写、分组、去重
│   │   ├── CredentialStore.cs               DPAPI 凭据表
│   │   ├── NvrTemplateExpander.cs           {host}/{ch}/{ch2} 模板批量
│   │   └── ProbeService.cs                  「测试」按钮
│   ├── Wall/
│   │   ├── VideoWallService.cs              墙的装配：布局、格、播放器生命周期、预算闸门
│   │   ├── TileSession.cs                   单格状态机（Idle→…→Failed）
│   │   ├── ReconnectScheduler.cs            指数退避重连
│   │   ├── AudioRouter.cs                   静音 / 独奏 / 混音（P2）
│   │   └── SceneStore.cs                    scenes.json + 启动加载
│   ├── Backend/
│   │   ├── Vlc/                             P0：LibVLCSharp，单 LibVLC 实例 + N 个 MediaPlayer
│   │   └── Ffmpeg/                          P1：单交换链合成、合流输出（LGPL 构建）
│   └── Contracts/                           已存在
├── FlyScreen.Tool.VideoWall.Ui.Win/         WPF 工作区（格布局、原生面 HwndHost、对话框）
├── FlyScreen.Tool.VideoWall.Ui.Desktop/     Avalonia 工作区（NativeControlHost）
└── FlyScreen.Tool.VideoWall.Ui.Maui/        一期不做（清单 platforms 仅 Windows）
```

原生库落到包内 `runtimes/win-x64/`（`libvlc.dll`、`libvlccore.dll`、裁剪后的 `plugins/`），由 `Pack-FlyTools.ps1` 的 `runtimes` 目录规则带入。

---

## 3. 接线清单（按顺序，每步可独立验证）

| # | 步骤 | 验收 |
|---|------|------|
| 1 | `FlyScreen.Core/…/ToolIds.cs` 加 `VideoWall = "video-wall"`；分组文案加 `media` | 工具页出现卡片（未安装态） |
| 2 | `Module.OnLoadedAsync` 注册 `IVideoWallService`；`Loc.RegisterPlugin(VideoWallLocalization.Instance)`；注册 `tool-video-wall` 图标 | 卡片名称/说明/图标正确 |
| 3 | `Tools/…` 权限白名单加 `networkStream` / `localFile.open`（见 docs §7） | 安装同意框能正确展示并安装成功 |
| 4 | `VideoEndpointParser` + 单测（12 条归一化规则） | 单测全绿 |
| 5 | `Backend/Vlc`：单实例多播放器、硬解回落、状态/统计事件 | 单路 RTSP 稳定播放 1 小时 |
| 6 | `VideoWallService` + `TileSession`：布局、空格、重连、预算 | 9 路并发 1 小时不崩 |
| 7 | `Ui.Win` 工作区线框（docs 原型 §2） | 可拖拽、可放大、可全屏 |
| 8 | 场景读写 + 启动加载 | 保存/加载/删除可用 |
| 9 | `Pack-FlyTools.ps1` 加条目 + 插件裁剪 + 许可文本 | 包 ≤ 60MB，可安装可卸载 |

---

## 4. 已知缺口（务必不要当成已完成）

- `manifest.json` 里的 `networkStream` / `localFile.open` 权限**尚未在** `docs/tools.md` §5.5.3 白名单中登记；未登记前同意框不会解释这两项，安装校验行为也未定义。
- `VideoWallLocalization` 未注册到宿主（`Module` 里是 TODO），界面文案目前取不到。
- 没有 UI 卫星工程，`IToolUiContributor` / 工作区工厂均空缺，`UsesWorkspace=true` 但没有可渲染内容。
- 没有原生库、没有 NuGet 依赖（`LibVLCSharp` / `VideoLAN.LibVLC.Windows` 尚未引入）。
- `IMediaCompositor` 只有契约，无实现；P0 的合流按「二次渲染视图」口径交付。
- `manifest.json` 的 `platforms: ["Windows"]` 会同时覆盖 MAUI/WinUI；若 MAUI 也装了本工具，需要 UI 侧明确不展示（见 docs 可行性 §4）。

---

## 5. 构建

```powershell
# 骨架单独编译（只依赖 ToolSdk）
dotnet build src/FlyScreen.Tool.VideoWall/FlyScreen.Tool.VideoWall.csproj -c Debug

# 接线后打包（需先在 Pack-FlyTools.ps1 注册 video-wall 条目）
pwsh build/Pack-FlyTools.ps1 -Configuration Release
```

> 一期**不把**本工具加入 `Bundled`（包体 30–55MB，会让每个安装包变重），走 Release + `tools-index.json` 按需下载，理由见 docs 可行性 §6。
