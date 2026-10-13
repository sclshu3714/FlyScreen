# 玄鸟 (FlyScreen)

跨电脑键鼠与剪贴板共享工具。灵感来自 [Mouse without Borders](https://aka.ms/mm)，面向中文环境做了连接、布局同步与传输等增强；支持深色主题与多语言界面。

**最多 4 台电脑**共用一套键鼠；建议各机版本一致，并以**管理员身份**运行。

联系邮箱：`sclshu3714@163.com`  
更新发布：<https://github.com/sclshu3714/FlyScreen/releases>

## 相对 Mouse without Borders 的改进

1. **中文优先 + 多语言** — 默认简体中文；可在高级选项切换多种界面语言（未翻译词条回退英文/中文）。
2. **淡色 / 深色主题** — 高级选项中切换，点「应用」立即生效。
3. **更稳的双向连接** — 每台同时「监听 + 主动连出」；心跳保活，任一方向可用即可收发，双通显示「双向已连接」。
4. **按需文件传输** — 复制文件只宣告元数据；鼠标移到目标机后后台拉取，其它电脑不传文件；接收中不卡键鼠。
5. **直接传输** — 推送文件/文件夹到指定电脑的桌面、文档、下载、收件箱或自定义路径，保留目录结构。
6. **统一剪贴板** — 文本、HTML、RTF、CSV、截图：复制仅宣告，进入目标机后再拉取写入。
7. **布局同步** — 一台修改电脑布局并「应用」后，已连接对端同步槽位与 1×4 / 2×2。
8. **多网卡绑定** — 「网络设置」选择通信网卡，减少虚拟网卡导致的单向连接。
9. **开机自启** — 任务计划程序（最高权限）登录后启动；自启后托盘后台连接。
10. **单实例** — 再次启动会激活已有窗口（含托盘隐藏）。
11. **更新与诊断** — 帮助窗口可检查/自动更新（GitHub Release），以及连接一键检查/修复。

## 环境要求

| 场景 | 要求 |
|------|------|
| 运行（自包含发布包） | Windows 10/11 x64，**无需**再装 .NET |
| 运行（依赖框架构建） | Windows 10/11 + **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)**（须选 Desktop，不是普通 Runtime） |
| 开发编译 | .NET 8 SDK |
| 权限 | 建议管理员（全局键鼠钩子） |

## 编译运行

```powershell
cd d:\Source\Repos\FlyScreen
dotnet build src\FlyScreen\FlyScreen.csproj -c Release
dotnet run --project src\FlyScreen\FlyScreen.csproj -c Release
```

输出目录：`src\FlyScreen\bin\Release\net8.0-windows\`（依赖本机已安装的 Desktop Runtime，**不宜直接拷到其它电脑**）。

## 发布为单个独立 exe（推荐分发）

清单要求 **管理员权限**，**不能用 ClickOnce**。请用文件夹 / 单文件、自包含发布。

### 命令行

```powershell
cd d:\Source\Repos\FlyScreen
# 若发布目录中的 exe 正在运行，请先退出托盘中的玄鸟，否则会「访问被拒绝」
dotnet publish src\FlyScreen\FlyScreen.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o src\FlyScreen\bin\publish\win-x64
```

生成：`src\FlyScreen\bin\publish\win-x64\FlyScreen.exe`（约 70MB 级；目标机无需安装 .NET）。

也可使用配置：`src\FlyScreen\Properties\PublishProfiles\SingleFile-win-x64.pubxml`。

### Visual Studio

1. 右键项目 → **发布** → **文件夹**（勿选 ClickOnce）
2. 运行时 `win-x64`，部署模式 **独立**，勾选 **生成单个文件**
3. 发布前先退出正在运行的玄鸟

### 启动报 `PresentationFramework` / 闪退

说明拷贝的是 `bin\Release\...` 依赖框架版，而目标机只有 .NET Runtime、没有 Desktop Runtime。

1. **推荐**：改用上面的自包含 `publish` 产物；或  
2. 安装 [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)，用 `dotnet --list-runtimes` 确认存在 `Microsoft.WindowsDesktop.App 8.0.x`。

## 使用方法

1. 各电脑安装并启动「玄鸟」（**最多 4 台**），版本尽量一致。
2. **基础设置**：填写**相同的安全密钥**。
3. **网络设置**：选择真实通信网卡；确认「局域网自动发现」能看到对方。
4. **电脑布局**：从发现列表添加，或手动添加电脑名；按摆放调 1×4 / 2×2，点 **应用**（布局会同步到已连接电脑）。
5. 状态为「双向已连接」后，鼠标移出屏幕边缘即可切换（需开启「轻松鼠标」）。

### 高级选项（摘要）

- **功能 / 行为**：环形鼠标、共享剪贴板、文件传输、隐藏本机光标、同网段、开机自启、屏保阻挡、四角阻挡、轻松鼠标等（悬停有说明）。
- **键盘快捷键**：切换机器用 F1–F4 / 1–4 / 禁用；以及打开设置、重连、退出、锁定等单键（均为 Ctrl+Alt+）。
- **系统设置**：淡色/深色主题、界面语言。

### 剪贴板与文件

| 场景 | 行为 |
|------|------|
| 文本 / HTML / RTF / CSV / 截图 | 仅宣告；鼠标进入某台后再拉取到该机 |
| 复制文件后移到目标机 | 后台接收，完成后可粘贴；接收中不卡键鼠 |
| 直接传输 | 「文件传输」→「直接传输…」选路径与目标电脑 |

进度与历史在 **文件传输** 页。「清除已完成」只删接收方收件箱缓存，不删发送方本地文件。

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

- **帮助**：检查更新 / 自动更新（GitHub Release）；一键检查 / 一键修复连接。
- 启动时可静默提示新版本。
- **发版注意（自动更新能否生效的关键）**：
  1. 先提高 `FlyScreen.csproj` 中的 `<Version>`（须高于用户当前版本）。
  2. 用自包含命令 `dotnet publish ... --self-contained true` 生成 exe。
  3. 将**该次构建**的 `FlyScreen.exe`（或含它的 zip）上传到对应 tag 的 Release。
  4. 若标签是 `v1.0.1` 但资源里仍是 `1.0.0` 的程序集，安装后仍会提示更新；程序会在下载后检测并报错。
  5. 替换失败时可查看 `%TEMP%\xuanniao-update.log`。
- **关于**、托盘提示与右键菜单随界面语言更新。

## 功能一览

- 跨机键鼠（边缘切换、热键、Easy Mouse、环形鼠标）
- 电脑布局（拖拽互换、添加/移除、布局同步，最多 4 台）
- 剪贴板：文本、富文本、CSV、图片/截图
- 文件：粘贴按需拉取 + 直接推送
- 局域网发现、多网卡、开机自启、托盘、单实例
- 深色主题、多语言、GitHub 更新、连接诊断
