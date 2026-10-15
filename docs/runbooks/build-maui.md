# .NET MAUI 跨平台 UI（W7 / 双轨 beta）

## 工程

| 项目 | 说明 |
|------|------|
| `FlyScreen.Core` | 协议 / 连接 / 发现 / 设置 / 文件传输（共用） |
| `FlyScreen.Windows` | Win32 Native / 开机自启（**仅** MAUI-Windows 条件引用） |
| `FlyScreen.App.Maui` | MAUI UI + 平台适配；**不引用** `FlyScreen.App.Windows` |

**WPF（`App.Windows`）仍为 stable，本通道不得改其代码或默认替换其安装包。**  
**Avalonia Mac（`App.Mac`）仍为 W1 桌面轨；MAUI Mac Catalyst 为双轨 beta 壳，键鼠注入尚未接线。**

## 能力矩阵

| 能力 | MAUI-Windows | MAUI-Android | MAUI-Mac Catalyst |
|------|--------------|--------------|-------------------|
| Caps | `DesktopFull` | `AndroidMobile` | `MacDesktopW1`（壳） |
| 设置 / 布局 / 发现 / 布局同步 | ✅ | ✅（手机以触控板为主） | ✅ UI 壳 |
| 边缘键鼠 / 注入 / 热键 | ✅ | ❌ | ❌（走 Null；正式 Mac 见 Avalonia） |
| 文本剪贴板 | ✅ | ✅ | ⏳ |
| 文件直传 / 收件箱 | ✅ | ✅ | ⏳ |
| 托盘 / 开机自启 | ✅ | ❌ | ❌ |
| 触控板控桌面 | — | ✅ `PhoneTouchpadPage` | — |

## 目标平台

| TFM | 启用方式 |
|-----|----------|
| `net10.0-windows10.0.19041.0` | **默认** |
| `net10.0-android` | `-p:FlyScreenEnableMauiAndroid=true` |
| `net10.0-maccatalyst` | `-p:FlyScreenEnableMauiMac=true`（须在 macOS 上） |

## 前置

```powershell
dotnet workload install maui
dotnet workload list
```

- **Android**：本机 MSVC + Android SDK  
- **Mac Catalyst**：在 **macOS** 上安装 `maui` workload（Windows 无法交叉发布 Mac）

## 编译 / 运行

```powershell
dotnet build src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release
dotnet run --project src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release -f net10.0-windows10.0.19041.0
```

版本号与 Win 同源：`build/FlyScreen.Version.targets`。

---

## 发布自包含包（Windows / Android / Mac）

一键脚本（推荐）：

```powershell
cd <repo>

# Windows 开发机：发布 Win + Android（Mac 自动跳过并提示）
powershell -File build\Publish-FlyScreenMaui.ps1

# 只发某一端
powershell -File build\Publish-FlyScreenMaui.ps1 -Platforms Windows
powershell -File build\Publish-FlyScreenMaui.ps1 -Platforms Android

# 在 macOS 上发 Mac Catalyst（Apple Silicon 默认）
pwsh -File build/Publish-FlyScreenMaui.ps1 -Platforms Mac -MacRid maccatalyst-arm64
# Intel Mac：
pwsh -File build/Publish-FlyScreenMaui.ps1 -Platforms Mac -MacRid maccatalyst-x64
```

### 产物路径与命名（ROADMAP §4.2）

| 平台 | 发布输出 | Rid | 命名结果（后缀以实际为准） |
|------|----------|-----|---------------------------|
| Windows | `src/FlyScreen.App.Maui/bin/publish/maui-win-x64/` | `maui-win-x64` | `artifacts/release/FlyScreen-{ver}-maui-win-x64.zip` |
| Android | `…/maui-android/*.apk` | `maui-android` | `FlyScreen-{ver}-maui-android.apk` |
| Mac Catalyst | `…/maui-osx-arm64/` 或 `maui-osx-x64/` | `maui-osx-arm64` / `maui-osx-x64` | `FlyScreen-{ver}-maui-osx-arm64.zip` 等 |

脚本内部等价命令：

```powershell
# Windows 自包含解包（WindowsPackageType=None）
dotnet publish src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release `
  -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained true `
  -o src\FlyScreen.App.Maui\bin\publish\maui-win-x64

# Android Release APK
dotnet publish src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release `
  -f net10.0-android -p:FlyScreenEnableMauiAndroid=true `
  -p:AndroidPackageFormat=apk `
  -o src\FlyScreen.App.Maui\bin\publish\maui-android

# Mac Catalyst（仅 macOS）
dotnet publish src/FlyScreen.App.Maui/FlyScreen.App.Maui.csproj -c Release `
  -f net10.0-maccatalyst -r maccatalyst-arm64 `
  -p:FlyScreenEnableMauiMac=true --self-contained true `
  -o src/FlyScreen.App.Maui/bin/publish/maui-osx-arm64
```

手工命名：

```powershell
powershell -File build\Name-FlyScreenPackage.ps1 -Source <产物或目录> -Rid maui-win-x64
powershell -File build\Name-FlyScreenPackage.ps1 -Source <xxx.apk> -Rid maui-android
powershell -File build\Name-FlyScreenPackage.ps1 -Source <产物或目录> -Rid maui-osx-arm64
```

### 注意

- MAUI 包 **不替代** WPF `FlyScreen-{ver}-win-x64.exe` stable。  
- Android 正式上架签名 / Mac 公证（notarize）需另行配置；本脚本产出供内测分发。  
- Avalonia Mac 正式桌面包仍见 [build-macos.md](build-macos.md)（Rid：`osx-arm64` / `osx-x64`）。

## 架构要点

- `MauiAppHost`：连接、布局同步、`FileTransferService`、对端键鼠消息分发。
- `IMauiPlatformServices`：Windows / Android 分别实现；Mac Catalyst 暂用 `NullMauiPlatformServices`。
- Windows 适配位于 `Platforms/Windows/`；Android 位于 `Platforms/Android/`；Mac Catalyst 入口位于 `Platforms/MacCatalyst/`。
- Phone：`App` 启动 `PhoneTouchpadPage`；桌面启动 `AppShell`→`MainPage`。

## CI

`build-maui` 失败**不得** fail `build-win`。
