# FlyScreen.Tool.VideoWall（视频墙）

| 项 | 内容 |
|----|------|
| ToolId | `video-wall` |
| 显示名 | 视频墙（多源视频播放 · 九宫格 · 合流） |
| 规格 | [docs/video-wall.md](../../../../docs/video-wall.md) |
| 可行性 | [docs/video-wall-feasibility.md](../../../../docs/video-wall-feasibility.md) |
| 原型 | [docs/video-wall-prototype.md](../../../../docs/video-wall-prototype.md) |
| 源矩阵 | [docs/video-wall-sources.md](../../../../docs/video-wall-sources.md) |

## 工程

```text
src/Tools/VideoWall/
├── FlyScreen.Tool.VideoWall/           业务：Sources / Backend(VLC) / Wall / Player / Module
├── FlyScreen.Tool.VideoWall.Ui.Win/    WPF 工作区（源管理 + 单路 + 宫格 + 扫描）
├── FlyScreen.Tool.VideoWall.Ui.Desktop/ Avalonia 同构
└── FlyScreen.Tool.VideoWall.Ui.Maui/   MAUI 同构（Maui-Win 可 HWND 嵌入）
```

## 构建与打包

```powershell
dotnet build src/Tools/VideoWall/FlyScreen.Tool.VideoWall/FlyScreen.Tool.VideoWall.csproj -c Debug
pwsh build/Pack-FlyTools.ps1 -Configuration Debug
```

- 大包含 `libvlc/win-x64` + `LibVLCSharp*`，**不**加入 Bundled 种子（按可行性文档走 Release / 远程索引）。
- 宿主：`ToolIds.VideoWall`、`GetToolDataDirectory`、分组 `media`。

## 已接线能力

- 固定源 CRUD、地址归一化、凭据（DPAPI）、封面抓取、探测
- LibVLC 单实例多播放器；不可用时 Null 后端 + UI 提示
- 源浏览：列表 / 卡片 / 画布；1×1～4×4 布局绑定
- **局域网扫描**：`ListLocalCidrs` + TCP 端口探测（并发限额 + 进度节流 + 边扫边出）；Win / Desktop / Maui 扫描 UI
- **画布轮播**：自动翻页；悬停暂停；左右箭头；底部胶囊页码（跳转 / 高亮）
- **加载动画**：复用 `FlyScreen.Ui.*.LoadingOverlay`；连接/未出画缓冲时显示，首帧或 `Playing` 后隐藏（`TilePlayStateUi.ShowsLoading(hasPresentedFrame)`）
- 单路播放：软件帧 + chrome（进度 / 音量 / 画中画等，按端能力）

## 关键实现入口

| 能力 | 业务 | UI |
|------|------|-----|
| 轮播分页 | `Wall/WallCarousel.cs`、`IVideoWallService` 轮播 API | 各端 `VideoWallPanel` 墙宿主叠加层 |
| 局域网扫描 | `Sources/LanDeviceScanService.cs` | `ScanDevicesDialog` / `ScanDevicesWindow` / `VideoWallScanPage` |
| 加载态 | `Contracts/TilePlayStateUi.cs` + `FlyScreen.Ui.*.LoadingOverlay` | 单路/墙格复用共享控件 |
