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
├── FlyScreen.Tool.VideoWall.Ui.Win/    WPF 工作区（源管理 + 单路 VideoView + 宫格）
├── FlyScreen.Tool.VideoWall.Ui.Desktop/ Avalonia 同构（画面面占位，P0）
└── FlyScreen.Tool.VideoWall.Ui.Maui/   占位：「一期仅 Windows 桌面」
```

## 构建与打包

```powershell
dotnet build src/Tools/VideoWall/FlyScreen.Tool.VideoWall/FlyScreen.Tool.VideoWall.csproj -c Debug
pwsh build/Pack-FlyTools.ps1 -Configuration Debug
```

- 大包含 `libvlc/win-x64` + `LibVLCSharp*`，**不**加入 Bundled 种子（按可行性文档走 Release / 远程索引）。
- 宿主：`ToolIds.VideoWall`、`GetToolDataDirectory`、分组 `media`。

## 已接线能力（P0 骨架）

- 固定源 CRUD、地址归一化、凭据（DPAPI）、封面抓取、探测
- LibVLC 单实例多播放器；不可用时 Null 后端 + UI 提示
- Win：列表/卡片、添加表单、单路播放、1/4/9/16 布局绑定
- Desktop：源/墙 UI；Maui：桌面-only 提示
