# 玄鸟 (FlyScreen)

跨电脑键鼠与剪贴板共享工具。灵感来自 [Mouse without Borders](https://www.microsoft.com/en-us/download/details.aspx?id=35460)，也可以搜索 [Mouse without Borders](https://aka.ms/mm)。

默认简体中文界面，并支持多语言与淡色/深色主题。相对原作强化了双向连接与心跳保活、电脑布局同步、剪贴板与文件的按需传输（复制只宣告元数据，进入目标机后再拉取），以及直接推送、穿边拖文件、扩展屏/投影等能力。

**最多 4 台电脑**共用一套键鼠；建议各机版本一致，并以**管理员身份**运行。

联系邮箱：`sclshu3714@163.com`  
更新发布：<https://github.com/sclshu3714/FlyScreen/releases>

## 多端迭代规划

各端**已实现能力**见下方 [各端能力一览](#各端能力一览)。  
后续继续补齐 macOS / Linux 键鼠主路径，以及 Android TV、鸿蒙等。规划与门禁文档：

- **[docs/README.md](docs/README.md)** — 文档索引  
- **[docs/ROADMAP.md](docs/ROADMAP.md)** — 版本阶段与周期（W0–W7，含 MAUI）  
- **[docs/status.md](docs/status.md)** — 各端完成情况（以 Win 为基准）  
- **[docs/clipboard.md](docs/clipboard.md)** — 共享剪贴板各端格式对标  
- **[docs/edge-file-drag.md](docs/edge-file-drag.md)** — 穿边拖文件  
- **[docs/extended-display.md](docs/extended-display.md)** — 电脑扩展屏 / 投影  
- **[docs/THIRD_PARTY.md](docs/THIRD_PARTY.md)** — 第三方依赖与许可说明

原则：新平台独立工程与分发包；任何 stable 发版必须通过 Windows 回归清单。  
当前 **stable** 仍为 Windows WPF（**.NET 10**）；Avalonia（Mac/Linux）与 MAUI 为双轨 beta，UI 与 Win **同构**（侧栏七页：基础 / 网络 / 共享 / 安全 / 高级 / 关于 / 帮助）。  
MAUI Android：桌面控手机与**用户程序（Fly API / OCR）**均需开启「玄鸟键鼠注入」无障碍（详见 [docs/runbooks/programs-android.md](docs/runbooks/programs-android.md)）。

## 相对 Mouse without Borders 的改进

1. **中文优先 + 多语言** — 默认简体中文；可在高级选项切换多种界面语言（未翻译词条回退英文/中文）。
2. **淡色 / 深色主题** — 高级选项中切换，点「应用」立即生效。
3. **更稳的双向连接** — 每台同时「监听 + 主动连出」；心跳保活，任一方向可用即可收发，双通显示「双向已连接」。
4. **按需文件传输** — 复制文件只宣告元数据；鼠标移到目标机后后台拉取，其它电脑不传文件；接收中不卡键鼠。
5. **直接传输** — 推送文件/文件夹到指定电脑的桌面、文档、下载、收件箱或自定义路径，保留目录结构。
6. **统一剪贴板** — 文本、HTML、RTF、CSV、截图/图片：复制仅宣告，进入目标机后再拉取写入（Win ↔ Maui-Win 全格式对齐；其它端见 [docs/clipboard.md](docs/clipboard.md)）。
7. **布局同步** — 一台修改电脑布局并「应用」后，已连接对端同步槽位与 1×4 / 2×2。
8. **多网卡绑定** — 「网络设置」选择通信网卡，减少虚拟网卡导致的单向连接。
9. **开机自启** — 任务计划程序（最高权限）登录后启动；自启后托盘后台连接。
10. **单实例** — 再次启动会激活已有窗口（含托盘隐藏）。
11. **更新与诊断** — 侧栏「帮助」页可检查/自动更新（优先 GitHub Release，失败自动改用 Gitee），以及连接一键检查/修复。
12. **电脑扩展屏 / 投影** — 标题栏一键复制或扩展投到对端全屏；可选声音同步、远程回控、去黑边；1 对 1 互斥；手机可复制投到电脑（详见 [docs/extended-display.md](docs/extended-display.md)）。
13. **穿边拖文件** — 拖着文件/文件夹穿边到另一台电脑松开即复制；同名冲突可替换/跳过；目标占用时双方失败提示一致（详见 [docs/edge-file-drag.md](docs/edge-file-drag.md)）。

## 环境要求

| 场景 | 要求 |
|------|------|
| 运行（自包含发布包） | Windows 10/11 x64，**无需**再装 .NET |
| 运行（依赖框架构建） | Windows 10/11 + **[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)**（须选 Desktop，不是普通 Runtime） |
| 开发编译 | **.NET 10 SDK**（含 Windows Desktop；MAUI 另需对应 workload） |
| 权限 | Win / Maui-Win **须管理员**（全局键鼠钩子；非提权穿边易丢事件） |

各端目标框架统一为 `net10.0` / `net10.0-windows7.0` / MAUI `net10.0-android` 等，详见 [docs/ROADMAP.md](docs/ROADMAP.md)。

## 编译运行

```powershell
cd d:\Source\Repos\FlyScreen
dotnet build FlyScreen.sln -c Release
dotnet run --project src\FlyScreen.App.Windows\FlyScreen.App.Windows.csproj -c Release
```

解决方案含：

| 项目 | 说明 |
|------|------|
| `FlyScreen.Core` | 跨平台核心（协议 / 连接 / 发现 / 传输） |
| `FlyScreen.Windows` + `FlyScreen.App.Windows` | Windows 适配 + WPF（**stable**，产物仍为 `FlyScreen.exe`） |
| `FlyScreen.App.Desktop` | Avalonia 共用主界面（Mac / Linux；对齐 Win 七页导航） |
| `FlyScreen.Mac` + `FlyScreen.App.Mac` | macOS 适配 + Avalonia 入口（W1） |
| `FlyScreen.Linux` + `FlyScreen.App.Linux` | Linux：自动 GUI / CLI / Daemon（W2） |
| `FlyScreen.App.Maui` | .NET MAUI 跨平台 UI（W7 beta：Win / Android / …） |
| `mobile/android` | Android Kotlin：触控板控桌面（W3） |
| `mobile/harmony` | 鸿蒙 ArkTS：手机/平板 + 电脑（W5） |

日常 Win 开发打开 `FlyScreen.sln` 即可。  
**发布（VS）**：展开 `build`，右键 `FlyScreen.Publish.*` → **生成**；最终单文件在 `artifacts/release/`（见 [build/README.md](build/README.md)）。  
macOS / Linux / Android / 鸿蒙 / MAUI 构建说明见 [docs/README.md](docs/README.md)（MAUI 见 [docs/runbooks/build-maui.md](docs/runbooks/build-maui.md)）。

输出目录：`src\FlyScreen.App.Windows\bin\Release\net10.0-windows7.0\`（依赖本机已安装的 Desktop Runtime，**不宜直接拷到其它电脑**）。

## 版本号（跟最新 Git 标签走，与提交无关）

Windows / Linux / macOS / MAUI 共用 `build/FlyScreen.Version.targets`；Android / 鸿蒙用同一 Git 标签规则（Gradle 自动解析；鸿蒙发版前跑 `build/Sync-MobileVersion.ps1`）。

规则：**项目版本 = 最新 `vX.Y.Z` 标签的 patch + 1**。多次提交、不打新 tag，版本号不变。`versionCode` = `major*10000+minor*100+patch`。

| 最新 GitHub / 本地 tag | 构建出的 Version |
|------------------------|------------------|
| `v1.0.1` | 一直是 **1.0.2** |
| 打上 `v1.0.2` 之后 | 一直是 **1.0.3** |
| `v1.0.9` | **1.0.10** |
| `v1.1.0` | **1.1.1** |

发版推荐流程（以当前最新 tag 为 `v1.0.1`、构建为 `1.0.2` 为例）：

```powershell
cd d:\Source\Repos\FlyScreen
# 1. publish（版本自动为 1.0.2，多次提交也仍是 1.0.2）
dotnet publish src\FlyScreen.App.Windows\FlyScreen.App.Windows.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o src\FlyScreen.App.Windows\bin\publish\win-x64

# 2. 上传 FlyScreen.exe 到 GitHub Release，并创建/推送同名标签
git tag v1.0.2
git push origin v1.0.2

# 3. 之后本地构建会自动变成 1.0.3，准备下一轮发版
```

若要发次版本 `1.1.0`：先打 `v1.1.0` 并上传对应包时，可用覆盖：  
`dotnet publish ... -p:Version=1.1.0`，然后再把 tag `v1.1.0` 推上去；之后未覆盖的构建会变成 `1.1.1`。

## 发布为单个独立 exe（推荐分发）

清单要求 **管理员权限**，**不能用 ClickOnce**。请用文件夹 / 单文件、自包含发布。

### 命令行

```powershell
cd d:\Source\Repos\FlyScreen
# 若发布目录中的 exe 正在运行，请先退出托盘中的玄鸟，否则会「访问被拒绝」
dotnet publish src\FlyScreen.App.Windows\FlyScreen.App.Windows.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o src\FlyScreen.App.Windows\bin\publish\win-x64
```

生成：`src\FlyScreen.App.Windows\bin\publish\win-x64\FlyScreen.exe`（约 70MB 级；目标机无需安装 .NET）。

也可使用配置：`src\FlyScreen.App.Windows\Properties\PublishProfiles\SingleFile-win-x64.pubxml`。

### Visual Studio

1. 右键项目 → **发布** → **文件夹**（勿选 ClickOnce）
2. 运行时 `win-x64`，部署模式 **独立**，勾选 **生成单个文件**
3. 发布前先退出正在运行的玄鸟

### 启动报 `PresentationFramework` / 闪退

说明拷贝的是 `bin\Release\...` 依赖框架版，而目标机只有 .NET Runtime、没有 Desktop Runtime。

1. **推荐**：改用上面的自包含 `publish` 产物；或  
2. 安装 [.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0)，用 `dotnet --list-runtimes` 确认存在 `Microsoft.WindowsDesktop.App 10.0.x`。

## 使用方法

1. 各电脑安装并启动「玄鸟」（**最多 4 台**），版本尽量一致。
2. **安全**：填写各机**相同的安全密钥**（生成/显示在此页）。
3. **网络**：选择真实通信网卡；确认局域网发现能看到对方。
4. **基础**：从发现列表或手动添加电脑；按摆放调 1×4 / 2×2，点 **应用**（布局会同步到已连接电脑）。
5. 状态为「双向已连接」后，鼠标移出屏幕边缘即可切换（需开启「轻松鼠标」）。

侧栏页面：**基础** · **网络** · **共享** · **安全** · **高级** · **关于** · **帮助**。

### 高级选项（摘要）

- **功能 / 行为 / 穿边模式**：宽屏三列并排，窄屏自动纵向堆叠。含环形鼠标、共享剪贴板、文件传输、隐藏光标、同网段、开机自启、屏保/四角阻挡、轻松鼠标；穿边可选无障碍 / 双击 / 极速（说明随选项切换，点「应用」后同步到已连接对端）。
- **键盘快捷键**：切换机器用 F1–F4 / 1–4 / 禁用；以及打开设置、重连、退出、锁定等单键（均为 Ctrl+Alt+）。
- **系统设置**：淡色/深色主题、界面语言。

### 安全

- 密钥输入与「显示」同一行；**「生成新密钥」在标题栏右侧**，避免窄屏把输入框挤裁。

### 剪贴板与文件

| 场景 | 行为 |
|------|------|
| 文本 / HTML / RTF / CSV / 截图·图片 | 仅宣告；鼠标/焦点进入目标机后再拉取并写入该机系统剪贴板 |
| 复制文件后移到目标机 | Win / Maui-Win：虚拟剪贴板，粘贴时再拉；接收中不卡键鼠 |
| 直接传输 | 「共享」→「直接传输…」选路径与目标电脑 |
| 穿边拖文件 | 拖文件穿边到对端松开后复制；同名冲突在目标机选择；投影占用对端时不可拖入 |
| 目标文件被占用 | 接收失败并通知发送端，避免「发端完成、收端卡住」 |

**各端格式对标**（完整 / 尽力 / 降级 / 半通）见 **[docs/clipboard.md](docs/clipboard.md)**。摘要：Win ↔ Maui-Win 内联与文件主路径已齐；Android 支持文本/HTML/图，RTF·CSV 粘贴降级；Mac/Linux Desktop 多格式尽力，文件粘贴靠「接收剪贴板文件」。穿边拖见 **[docs/edge-file-drag.md](docs/edge-file-drag.md)**。

进度与历史在 **共享**（传输列表）页，列与 Win/MAUI 对齐：方向、文件名、对端、大小、进度、状态、删除。「打开收件箱」打开本机 Inbox 目录；「清除已完成」与行内删除只删接收方收件箱缓存，不删发送方本地文件。

### 投影（扩展屏）

标题栏「投影」→ 选复制/扩展与目标电脑 → 开始。可选：**声音同步**、**远程控制**、**去黑边**。投影中标题栏变为「投影中」，再点或 Esc 停止。同一时刻每台最多一条投影会话。细节见 **[docs/extended-display.md](docs/extended-display.md)**。

### 快捷键（默认）

| 快捷键 | 功能 |
|--------|------|
| Ctrl+Alt+F1–F4（或 1–4） | 切换到对应机器 |
| Ctrl+Alt+M | 打开设置 |
| Ctrl+Alt+R | 重新连接 |
| Ctrl+Alt+Q | 退出 |
| Ctrl+Alt+L | 锁定机器（可在选项中改键） |

## 防火墙

允许专用网络上的 TCP `15155`、UDP `15156`（可在网络设置中修改）。帮助里的「一键修复」可尝试添加「玄鸟」防火墙规则。

## 配置与数据

| 路径 | 说明 |
|------|------|
| `%AppData%\FlyScreen\settings.json` | 密钥、布局、网卡、端口、主题、语言等 |
| `%AppData%\FlyScreen\Inbox` | 剪贴板粘贴接收的文件缓存 |
| `%AppData%\FlyScreen\PasteStaging` | 粘贴时原名暂存 |
| `%AppData%\FlyScreen\transfer-history.json` | 传输历史 |

## 更新与诊断

- **帮助**页：帮助文本 / 检查更新（可自动更新）/ 问题检查（一键修复）。
- 启动时可静默提示新版本。
- **发版注意（自动更新）**：构建版本 = 最新 tag 的 patch+1；publish 后把 exe 上传到 **同名** GitHub Release（如版本 `1.0.2` → tag `v1.0.2`）。详见 [docs/compatibility.md](docs/compatibility.md)、[docs/runbooks/release-windows.md](docs/runbooks/release-windows.md)。替换失败见 `%TEMP%\xuanniao-update.log`。
- **关于**页、托盘提示与右键菜单随界面语言更新。

## 各端能力一览

图例：**完整** = 与 Win 对标可用 · **尽力** = 已实现、效果视系统而定 · **半通** = 能传但不能像本机一样挂系统剪贴板 · **骨架** = 工程/协议已通、主路径未齐 · **—** = 不适用或未做。  
更细对标见 [docs/status.md](docs/status.md)、[docs/clipboard.md](docs/clipboard.md)。

### 平台总览

| 平台 | 工程 | 状态 | 一句话 |
|------|------|------|--------|
| **Windows 桌面** | `App.Windows`（WPF） | **stable** | 完整产品：键鼠、布局、剪贴板、文件、投影、穿边拖、更新、诊断 |
| **MAUI-Windows** | `App.Maui` | beta | 与 WPF 主路径对齐（剪贴板全格式、投影 Host/Sink、穿边拖） |
| **MAUI-Android** | `App.Maui` | beta | 触控板/键盘控桌面；剪贴板文/HTML/图；复制投屏 Host；无障碍被控；用户程序 Fly/OCR |
| **macOS** | `App.Mac` + Avalonia | beta | 剪贴板尽力 + 直传/Inbox；可收投影 + 穿边拖；键鼠注入/捕获仍骨架 |
| **Linux** | `App.Linux` + Avalonia | beta | 同 Mac 传输路径；可收投影 + 穿边拖；CLI/Daemon；键鼠主路径骨架 |
| 鸿蒙手机 / 电脑 | `mobile/harmony` | 骨架 | 触控板/设置壳；剪贴板与键鼠未齐 |

### 能力对照（桌面 / MAUI）

| 能力 | Win | Maui-Win | Maui-Android | Mac | Linux |
|------|-----|----------|--------------|-----|-------|
| 边缘键鼠 / Easy Mouse（主机） | 完整 | 完整 | —（被控见下） | 骨架 | 骨架 |
| 被控注入键鼠 | 完整 | 完整 | 完整（须开无障碍） | 骨架 | 骨架 |
| 触控板控桌面（AbsPointer） | 可收 | 可收 | **完整**（含中键/滚轮） | 可收 | 可收 |
| 远程 IME 直发对端 | — | — | **完整** | — | — |
| 1×4 / 2×2 布局同步 | 完整 | 完整 | ❌ 不宣告 | 骨架 | 骨架 |
| 剪贴板 文 / HTML / 图 | 完整 | 完整 | 完整 | 尽力 | 尽力 |
| 剪贴板 RTF / CSV | 完整 | 完整 | 粘贴降级文本 | 尽力 | 尽力 |
| 文件剪贴板（按需拉取） | 完整（虚拟粘贴） | 完整 | 半通 | 半通 | 半通 |
| 直接传输 / 收件箱 | 完整 | 完整 | 完整 | 骨架 | 骨架 |
| 穿边拖文件 | 完整 | 完整 | — | 完整 | 完整 |
| 投影 Host（复制/扩展） | 完整 | 完整 | 复制投屏 Host | — | — |
| 投影 Sink（全屏收流） | 完整 | 完整 | ❌ | 完整 | 完整 |
| 声音同步 / 远程回控 / 去黑边 | 完整 | 完整 | Host 侧可选 | Sink 侧 | Sink 侧 |
| 托盘 / 开机自启 / 单实例 | 完整 | 完整 | — | — | Daemon³ |
| 主题 / 多语言 / 更新 / 诊断 | 完整 | 完整 | 部分 | — | — |
| 用户程序编辑 / 共享 | 完整 | 完整 | 完整 | UI | UI |
| 用户程序设备控制（`Fly.*` / OCR） | — | — | **完整**（须无障碍） | — | — |
| 七页 UI 同构 | 完整 | 完整 | 完整（窄屏抽屉） | 完整 | 完整 |

³ Linux 支持 `--daemon` + systemd 示例，无桌面托盘。

### 手机 ↔ 电脑（摘要）

| 方向 | 已实现 |
|------|--------|
| 手机 → 电脑 | 触控板相对移动、左右键/中键/滚轮；系统输入法整段直发；剪贴板文/HTML/图；文件直传收件箱；**复制投屏**（可选声音/回控） |
| 电脑 → 手机 | Easy Mouse 边缘切入 + 点击/滑动（须开启「玄鸟键鼠注入」无障碍）；组字浮层；完整键码仍受限 |
| 用户程序 | 三端可编辑/共享；**仅 Android** 可跑 `Fly.HasText` / OCR / 手势等本机自动化 |

## 特别鸣谢

本项目以 **Apache-2.0** 开源。许可与依赖明细见 **[docs/THIRD_PARTY.md](docs/THIRD_PARTY.md)**。

### 开源组件（排名不分先后）

| 项目 | 用途 |
|------|------|
| [.NET](https://dotnet.microsoft.com/) / [.NET MAUI](https://github.com/dotnet/maui) | 运行时与跨平台 UI（Win / Android 等） |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) | macOS / Linux 桌面 UI |
| [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) / [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) | 用户程序源代码编辑器 |
| [Monaco Editor](https://github.com/microsoft/monaco-editor) | MAUI 用户程序编辑器 |
| [Jint](https://github.com/sebastienros/jint) | 用户程序 JavaScript 引擎 |
| [Roslyn](https://github.com/dotnet/roslyn)（`Microsoft.CodeAnalysis.CSharp.Scripting`） | 用户程序 C# 脚本 |
| [AndroidX](https://developer.android.com/jetpack/androidx) | Android 平台支持库 |
| [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common) | Windows 图像与 GDI 相关能力 |
| [Virtual Display Driver (VirtualDrivers / MikeTheTech)](https://github.com/VirtualDrivers/Virtual-Display-Driver) | Windows 虚拟副屏（MIT，离线包可再分发） |
| [Noto Sans SC](https://github.com/notofonts/noto-cjk) | Linux 随包中文字体（SIL OFL，`Fonts/`） |
| [xUnit](https://github.com/xunit/xunit) | Core 单元测试 |
| [Mouse without Borders](https://aka.ms/mm) | 产品灵感来源（未纳入其代码） |

### 专有依赖

| 项 | 说明 |
|----|------|
| [Google ML Kit Text Recognition](https://developers.google.com/ml-kit/vision/text-recognition)（含中文） | **正式功能**，默认启用（Android `Fly.Ocr`）。SDK/模型为专有，受 [ML Kit Terms](https://developers.google.com/ml-kit/terms) 约束；**可以**用于开源应用，但不能把 ML Kit 本身再标成 Apache-2.0。可选 `-p:FlyEnableMlKit=false` 做无 Google ML 包构建 |

若遗漏署名或许可说明有误，请通过 Issues / 邮件告知，我们会尽快更正。
