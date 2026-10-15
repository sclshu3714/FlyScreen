# .NET MAUI 跨平台 UI（W7）

## 工程

| 项目 | 说明 |
|------|------|
| `FlyScreen.Core` | 协议 / 连接 / 发现 / 设置（共用） |
| `FlyScreen.App.Maui` | **MAUI 单一 UI**：对齐 Win「基础 / 网络 / 共享 / 高级」分区 |

**不替换**现有 WPF（`App.Windows`）与 Avalonia（`App.Mac` / `App.Linux` / `App.Desktop`）。W7 为跨平台 UI **双轨 / beta** 通道；Win stable 仍以 WPF 门禁为准。

## 目标平台（分期）

| TFM | 状态 |
|-----|------|
| `net10.0-windows10.0.19041.0` | **默认**桌面验证（对齐 maui 10） |
| `net10.0-android` | 需显式开启 `FlyScreenEnableMauiAndroid=true` |
| `net10.0-ios` / `net10.0-maccatalyst` | 有完整 Apple workload 后启用 |

> 说明：工程使用 **.NET 10 + MAUI 10**。默认不含 Android，以免本机缺 **MSVC**（`VC\Tools\MSVC`）或 **Android SDK** 时 VS 报 `GetLatestMSVCVersion` / XA5300，并波及同解决方案其它工程。业务仍引用 `FlyScreen.Core`（`net8.0`）。

## 前置

```powershell
# 安装 MAUI 工作负载（一次性）
dotnet workload install maui

# 清单有、pack 未落盘时常见 MSB4019（AutoImport.props），按报错补装：
dotnet workload install macos              # Microsoft.macOS.Sdk.*
dotnet workload install wasm-tools-net6    # Microsoft.NET.Runtime.WebAssembly.Sdk 6.0.36
# 或：dotnet workload repair

dotnet workload list
```

### Windows 目标

- VS：「.NET 多平台应用 UI 开发」
- 若内网 NuGet 缺包：确保可访问 `https://api.nuget.org/v3/index.json`

### Android（可选）

1. VS 安装程序勾选 **使用 C++ 的桌面开发**（提供 `VC\Tools\MSVC`，否则 `GetLatestMSVCVersion` 失败）
2. 安装完整 Android SDK（platforms / build-tools / platform-tools；仅有 emulator 不够）
3. 构建时打开开关：

```powershell
dotnet build src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release `
  -p:FlyScreenEnableMauiAndroid=true -f net10.0-android
```

若 SDK 不在默认路径，另加 `-p:AndroidSdkDirectory=...`。

## 编译 / 运行

```powershell
# Windows（默认）
dotnet build src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release
dotnet run --project src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release -f net10.0-windows10.0.19041.0
```

## UI 对齐原则

- 侧栏：基础 / 网络 / 共享 / 安全 / 高级  
- 顶栏：基础设置 / 高级选项 / 网络设置 / 文件传输  
- 配色与 Win 淡色主题一致（`Resources/Styles/Colors.xaml`）  
- 业务经 `MauiAppHost` → `ConnectionManager` / `LayoutSyncHelper`（Core）

## 与现有端关系

```text
Win WPF (stable) ──┐
Avalonia Mac/Linux ─┼─ 共用 FlyScreen.Core
MAUI (W7 beta) ─────┘
```

键鼠注入 / 边缘捕获 / 剪贴板文件等仍按各平台 Adapter 落地；MAUI 首期保证**设置、布局槽位、发现、握手、布局同步**路径可用。
