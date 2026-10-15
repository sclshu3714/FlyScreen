# .NET MAUI 跨平台 UI（W7 / 双轨 beta）

## 工程

| 项目 | 说明 |
|------|------|
| `FlyScreen.Core` | 协议 / 连接 / 发现 / 设置 / 文件传输（共用） |
| `FlyScreen.Windows` | Win32 Native / 开机自启（**仅** MAUI-Windows 条件引用） |
| `FlyScreen.App.Maui` | MAUI UI + 平台适配；**不引用** `FlyScreen.App.Windows` |

**WPF（`App.Windows`）仍为 stable，本通道不得改其代码或默认替换其安装包。**

## 能力矩阵

| 能力 | MAUI-Windows | MAUI-Android |
|------|--------------|--------------|
| Caps | `DesktopFull` | `AndroidMobile` |
| 设置 / 布局 / 发现 / 布局同步 | ✅ | ✅（手机以触控板为主，设置二级页） |
| 边缘键鼠 / 注入 / 热键 | ✅（`Platforms/Windows` 对照移植） | ❌（Caps 不含） |
| 文本剪贴板 | ✅ | ✅ |
| 文件直传 / 收件箱 | ✅ | ✅ |
| 托盘 / 开机自启 | ✅ | ❌ |
| 触控板控桌面 | — | ✅ `PhoneTouchpadPage` |

## 目标平台

| TFM | 状态 |
|-----|------|
| `net10.0-windows10.0.19041.0` | **默认** |
| `net10.0-android` | `-p:FlyScreenEnableMauiAndroid=true` |

## 前置

```powershell
dotnet workload install maui
dotnet workload list
```

### Android（可选）

需本机 MSVC + Android SDK，然后：

```powershell
dotnet build src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release `
  -p:FlyScreenEnableMauiAndroid=true -f net10.0-android
```

## 编译 / 运行

```powershell
dotnet build src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release
dotnet run --project src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release -f net10.0-windows10.0.19041.0
```

版本号与 Win 同源：`build/FlyScreen.Version.targets`。

## 发布包命名

MAUI 为双轨 **beta**，命名仍遵循 [ROADMAP §4.2](../ROADMAP.md#42-平台包命名)，**不替代** WPF `win-x64` stable。

```powershell
# 例：单文件 / 解包 exe 或 msix，后缀以实际为准
powershell -File build\Name-FlyScreenPackage.ps1 `
  -Source <path-to-maui-artifact> `
  -Rid maui-win-x64
# Android MAUI：-Rid maui-android ，后缀多为 .apk
```

## 架构要点

- `MauiAppHost`：连接、布局同步、`FileTransferService`、对端键鼠消息分发。
- `IMauiPlatformServices`：Windows / Android 分别实现；禁止依赖 WPF 类型。
- Windows 适配位于 `Platforms/Windows/`（InputRouter / Injector / 托盘 / 文本剪贴板监视）。
- Phone：`App` 启动 `PhoneTouchpadPage`；桌面启动 `AppShell`→`MainPage`。

## CI

`build-maui` 失败**不得** fail `build-win`。
