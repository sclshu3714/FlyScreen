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
| Caps | `DesktopFull` | `AndroidMobile`（含 `EdgeMouse`/`InjectInput`/`AbsPointer`；无 `LayoutSync`） | `MacDesktopW1`（壳） |
| 设置 / 布局 / 发现 | ✅ | ✅（手机以触控板为主；发现支持双击添加） | ✅ UI 壳 |
| 布局同步（LayoutSync） | ✅ | ❌ 不宣告 | ⏳ |
| 边缘键鼠 / 热键（主机） | ✅ | —（被控见下） | ❌（走 Null；正式 Mac 见 Avalonia） |
| 被控注入（收 AbsPointer / SwitchFocus） | ✅ `EnsureAcceptsRemoteInput` | ✅ 无障碍服务（须授权） | ⏳ |
| 文本剪贴板 | ✅ | ✅ | ⏳ |
| 文件直传 / 收件箱 | ✅ | ✅ | ⏳ |
| 托盘 / 开机自启 | ✅ | ❌ | ❌ |
| 触控板控桌面 | — | ✅ `PhoneTouchpadPage` | — |

### Android 触控板（`PhoneTouchpadPage`）

| 操作 | 行为 |
|------|------|
| 滑动 | 相对 `MouseMove` + 虚拟光标点 |
| 触控区轻点 | 左键（Pan 未滑动完成时；与 Tap 去抖） |
| 左 / 右键区 | 对应按键 |
| 中间区轻点 | **中键**；上下滑 = 滚轮 |
| 「键盘」 | 系统输入法（含系统手写）；停顿后整段直发对端并清空本地，不留文稿 |
| 会话 | 手势开始发 `SwitchFocus`；桌面 AbsPointer 亦可自动进入被控 |

### Desktop → Android（Easy Mouse）

1. 双方握手 Caps 交集含 `EdgeMouse` + `InjectInput`（重装/重启后生效）。  
2. 手机：**设置 → 无障碍 →「玄鸟键鼠注入」** 开启（首次切入会弹窗引导）。  
3. 布局中手机与电脑相邻，鼠标移出对应边缘切入；点击/滑动由无障碍 `dispatchGesture` 注入。  
4. 完整系统键码注入受 Android 限制，部分键会降级（如 Esc→返回）。

## 目标平台

与 VS MAUI 模板一致（调试下拉才有完整目标）：

| TFM | 何时包含 |
|-----|----------|
| `net10.0-android` | **默认** |
| `net10.0-ios` | **默认** |
| `net10.0-maccatalyst` | **默认** |
| `net10.0-windows10.0.19041.0` | Windows 主机上自动追加 |

仅编 Windows（无 Android SDK / 快速 CI）：

```powershell
dotnet build ... -p:FlyScreenMauiTfms=windows
```

## 前置

```powershell
dotnet workload install maui
dotnet workload list
```

- **Android**：本机 MSVC + Android SDK  
- **iOS / Mac Catalyst**：真机或模拟器调试需配对 Mac；Windows 上仍可出现调试项，部署到设备须 Mac

## 编译 / 运行

```powershell
dotnet build src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release
dotnet run --project src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release -f net10.0-windows10.0.19041.0
```

版本号与 Win 同源：`build/FlyScreen.Version.targets`。

### Visual Studio 调试目标

`Properties/launchSettings.json` 配置名须为 **`Windows Machine`**（`commandName: Project`，配合 `WindowsPackageType=None`）。

改 TFM 后若下拉仍只有项目名/WSL：卸载并重新加载项目，或删除仓库根目录 `.vs` 后重开解决方案。

调试 **Android** 时工具栏配置须为 **Debug**（非 Release）。工程已在 Debug|android 下设置 `DebugSymbols` / `AndroidIncludeDebugSymbols`；若仍提示「此项目配置未启用调试」，确认活动框架为 `net10.0-android` 后重新加载项目。

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
| Windows | `…/maui-win-x64/*.exe`（单文件） | `maui-win-x64` | `FlyScreen-{ver}-maui-win-x64.exe` |
| Android | `…/maui-android/*.apk` | `maui-android` | `FlyScreen-{ver}-maui-android.apk` |
| Mac Catalyst | `…/maui-osx-arm64/` 单文件可执行 | `maui-osx-arm64` / `maui-osx-x64` | `FlyScreen-{ver}-maui-osx-arm64`（无扩展名） |

### Visual Studio（推荐：右键生成 = 发布）

MAUI Windows 为 **非打包**，VS 项目右键「发布」会灰色。各端统一用解决方案 **`build`** 下的发布项目（详见 [build/README.md](../../build/README.md)）：

| 右键生成 | 产物 |
|----------|------|
| `FlyScreen.Publish.MauiWin` | `…/maui-win-x64/*.exe` |
| `FlyScreen.Publish.MauiAndroid` | `…/maui-android/*.apk` |
| `FlyScreen.Publish.MauiMac` | `…/maui-osx-arm64/`（**仅 macOS**） |

另有 WPF / Avalonia：`Publish.Win`、`Publish.Linux`、`Publish.Mac`。均未勾进「生成解决方案」。

CLI 仍可用：`Publish-FlyScreenMaui.ps1 -Platforms Windows` 等。

脚本内部等价命令：

```powershell
# Windows 自包含单文件（RuntimeIdentifierOverride 为官方推荐）
dotnet publish src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release `
  -f net10.0-windows10.0.19041.0 `
  -p:RuntimeIdentifierOverride=win-x64 `
  -p:WindowsPackageType=None `
  -p:WindowsAppSDKSelfContained=true `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o src\FlyScreen.App.Maui\bin\publish\maui-win-x64

# Android Release APK（工程已默认 AndroidPackageFormat=apk；无需再传该属性）
dotnet publish src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release `
  -f net10.0-android `
  -o src\FlyScreen.App.Maui\bin\publish\maui-android

# Mac Catalyst 自包含单文件（仅 macOS）
dotnet publish src/FlyScreen.App.Maui/FlyScreen.App.Maui.csproj -c Release `
  -f net10.0-maccatalyst -r maccatalyst-arm64 `
  -p:FlyScreenEnableMauiMac=true --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o src/FlyScreen.App.Maui/bin/publish/maui-osx-arm64
```

手工命名：

```powershell
powershell -File build\Name-FlyScreenPackage.ps1 -Source <xxx.exe> -Rid maui-win-x64
powershell -File build\Name-FlyScreenPackage.ps1 -Source <xxx.apk> -Rid maui-android
powershell -File build\Name-FlyScreenPackage.ps1 -Source <单文件可执行> -Rid maui-osx-arm64
```

### 注意

- MAUI 包 **不替代** WPF `FlyScreen-{ver}-win-x64.exe` stable。  
- Android 正式上架签名 / Mac 公证（notarize）需另行配置；本脚本产出供内测分发。  
- Avalonia Mac 正式桌面包仍见 [build-macos.md](build-macos.md)（Rid：`osx-arm64` / `osx-x64`）。  
- **应用图标：** `Resources/AppIcon/appicon.png`（`MauiIcon` + `MauiSplashScreen`）；Android 须在 `AndroidManifest` / `MainActivity` 声明 `@mipmap/appicon`，否则启动器可能显示默认图标。换图标后建议清 `bin/obj` 再 `publish`。  
- **APK 签名：** Release 使用 `build/android/flyscreen-sideload.keystore`（内测旁加载）。请安装 **`FlyScreen.apk`** 或 **`com.xuanniao.flyscreen-Signed.apk`**（须为**已签名**包；勿把未签名 plain 当 Signed 覆盖安装，否则报 `INSTALL_PARSE_FAILED_NOT_APK`）。  
  - 构建目标 `FlyScreen_EnsureSignedApk`（`_Sign` / `SignAndroidPackage` / `Build` 后）调用 `build/Ensure-MauiSignedApk.ps1`：校验签名块，缺失时用 keystore `apksigner sign` 补齐 Signed + `FlyScreen.apk`，并登记 `FileWrites` 防止 VS `IncrementalClean` 删掉 Signed。  
  - 发布脚本 `Publish-FlyScreenMaui.ps1` 在 Android 发布后也会再跑一次该脚本。  
  - 详情见 [build/android/README.md](../../build/android/README.md)。上架 Play 需自备正式 keystore。  

- **XARDF7024（目录不是空的）：** 多为 `obj\...\lp` 等中间目录被占用。关闭正在运行的模拟器安装/资源管理器中打开的输出目录后重编；或先执行：
  ```powershell
  powershell -File build\Clean-MauiAndroid.ps1 -Configuration Release -All
  ```
  工程会在 Android 清理前自动调用该脚本做强制删除。  
- **启动闪退 `UnsatisfiedLinkError` / `n_onCreate`：** 多为 APK 缺少 `armeabi-v7a`（32 位）原生库。工程已包含 `android-arm;android-arm64;android-x64`。请安装 **`*-Signed.apk`**，卸载旧包后再装。

## UI 信息架构（对齐 WPF / Avalonia）

侧栏七页（彩色 Path 图标 + 文案，与 `App.Windows` / `App.Desktop` 一致）：

| 页 | 内容要点 |
|----|----------|
| 基础 | 电脑布局（1×4 / 2×2）、槽位 |
| 网络 | 网卡、发现、端口等 |
| 共享 | 文件传输 / 直传 / 收件箱 |
| 安全 | **安全密钥**（已从基础页迁出） |
| 高级 | 选项、热键、主题等 |
| 关于 | 版本与说明（页内，非对话框） |
| 帮助 | 工具栏：帮助文本 / 检查更新 / 问题检查；日志区 |

**窄屏 / 手机（非触控板专用 Phone 入口）：** 顶栏汉堡 + 抽屉侧栏；主区全宽，避免挤压。侧栏用底层 `Button` 命中 + 上层 Path/Label 展示，保证 Android 可点。

## 架构要点

- `MauiAppHost`：连接、布局同步、`FileTransferService`、对端键鼠消息分发。
- `IMauiPlatformServices`：Windows / Android 分别实现；Mac Catalyst 暂用 `NullMauiPlatformServices`。
- Windows 适配位于 `Platforms/Windows/`；Android 位于 `Platforms/Android/`；Mac Catalyst 入口位于 `Platforms/MacCatalyst/`。
- Phone：`App` 启动 `PhoneTouchpadPage`；桌面 / 平板启动 `AppShell`→`MainPage`。

## CI

`build-maui` 失败**不得** fail `build-win`。
