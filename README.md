# 玄鸟 (FlyScreen)

仿 [Mouse without Borders](https://aka.ms/mm) 的跨电脑键鼠与剪贴板共享工具，界面为中文，并针对常见痛点做了增强。

## 相对 Mouse without Borders 的改进

1. **全中文界面** — 设置、状态、托盘菜单均为中文。
2. **更稳的双向连接** — 每台机器同时「监听 + 主动连出」，出入站双通道；心跳保活，任一方向可用即可收发，双方向通时显示「双向已连接」。
3. **按需文件传输** — 复制文件只宣告元数据；鼠标移到某台再粘贴时，才从源端拉到该机，其它电脑不传文件。
4. **直接传输** — 可指定文件/文件夹推送到某台电脑的指定目录（桌面、文档、下载、收件箱或自定义路径），保留文件夹结构。
5. **统一剪贴板** — 文本、HTML、RTF、CSV、截图/图片：复制仅宣告，进入目标机后再拉取写入；本地粘贴不受影响。
6. **布局同步** — 一台电脑修改电脑布局并「应用」后，已连接对端自动同步槽位与 1×4 / 2×2 模式。
7. **多网卡自动识别** — 「网络设置」中选择通信网卡，绑定该 IP 监听/连出/UDP 发现，减少连错虚拟网卡导致的单向问题。
8. **开机自启** — 通过「任务计划程序（最高权限）」登录后自动启动（因需管理员权限，注册表 Run 无法可靠自启）；自启后托盘后台连接。
9. **单实例** — 禁止多开；再次启动会激活已有窗口（含托盘隐藏状态）。

## 环境要求

- Windows 10/11
- .NET 8 运行时（开发需 SDK）
- 建议以管理员身份运行（全局键鼠钩子）

## 编译运行

```powershell
cd d:\Source\Repos\FlyScreen
dotnet build src\FlyScreen\FlyScreen.csproj -c Release
dotnet run --project src\FlyScreen\FlyScreen.csproj -c Release
```

输出目录：`src\FlyScreen\bin\Release\net8.0-windows\`

## 发布为单个独立 exe

本程序清单要求 **管理员权限**（全局键鼠钩子），**不能用 ClickOnce**（会报：`ClickOnce 不支持 requireAdministrator`）。请用「文件夹 / 单文件」发布。

### 命令行（推荐）

```powershell
cd d:\Source\Repos\FlyScreen
dotnet publish src\FlyScreen\FlyScreen.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o src\FlyScreen\bin\publish\win-x64
```

生成文件：`src\FlyScreen\bin\publish\win-x64\FlyScreen.exe`  
（自包含，目标电脑无需单独安装 .NET；首次运行可能短暂解压原生库到临时目录。体积通常约 100MB+。）

> **不要**只拷贝 `bin\Release\net8.0-windows\FlyScreen.exe` 到其它电脑：那是依赖框架的构建。  
> 若目标机只装了「.NET Runtime」而没有「**.NET Desktop Runtime**」，会报 `PresentationFramework` / `FileNotFoundException` 并闪退。

### 若启动报 PresentationFramework 找不到

任选其一：

1. **推荐**：用上面的 `dotnet publish ... --self-contained true` 重新发布，把 `bin\publish\win-x64\FlyScreen.exe` 拷到目标机。  
2. 或在目标机安装 [.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0)（注意选 **Desktop**，不是普通 Runtime）。  
   可用 `dotnet --list-runtimes` 确认存在一行 `Microsoft.WindowsDesktop.App 8.0.x`。

### Visual Studio

1. 右键项目 → **发布**
2. 选择 **文件夹**（不要选 ClickOnce）
3. 目标运行时：`win-x64`
4. 部署模式：**独立**（Self-contained）
5. 文件发布选项 → 勾选 **生成单个文件**
6. 发布

也可直接选用已有配置：`Properties\PublishProfiles\SingleFile-win-x64.pubxml`

## 使用方法

1. 多台电脑安装并启动「玄鸟」（**最多 4 台**），**版本保持一致**。
2. 在 **机器设置** 中设置**相同的安全密钥**。
3. 在 **网络设置** 中选择正确的网卡（有线/无线），确认能在「局域网自动发现」中看到对方。
4. 将对方计算机名填入 **电脑布局**（或从发现列表「添加」），调整 1×4 / 2×2 摆放，点击 **应用**（布局会同步到其它已连接电脑）。
5. 状态显示「双向已连接」后，将鼠标移出屏幕边缘即可切换控制。

### 剪贴板与文件

| 场景 | 行为 |
|------|------|
| 复制文本 / HTML / RTF / CSV / 截图 | 仅宣告；鼠标进入某台后再拉取到该机剪贴板 |
| 复制文件后移到目标机 | 目标机后台异步接收，完成后才可粘贴；接收中不卡键鼠 |
| 直接传输 | 「文件传输」→「直接传输…」选文件/文件夹、目标电脑与保存路径 |

传输进度与历史在 **文件传输** 页查看。「清除已完成」只删接收方收件箱缓存，不删发送方本地文件。

### 快捷键（默认）

| 快捷键 | 功能 |
|--------|------|
| Ctrl+Alt+F1–F4 | 切换到对应机器 |
| Ctrl+Alt+M | 打开设置 |
| Ctrl+Alt+R | 重新连接 |
| Ctrl+Alt+Q | 退出 |

可在选项中改为数字键或关闭切换热键。

## 防火墙

请允许程序在专用网络上使用 TCP `15155`、UDP `15156`（可在网络设置中修改）。

## 配置与数据

| 路径 | 说明 |
|------|------|
| `%AppData%\FlyScreen\settings.json` | 密钥、布局、网卡、端口等 |
| `%AppData%\FlyScreen\Inbox` | 剪贴板粘贴接收的文件缓存 |
| `%AppData%\FlyScreen\PasteStaging` | 粘贴时原名暂存（触发系统覆盖/重命名对话框） |
| `%AppData%\FlyScreen\transfer-history.json` | 传输历史 |

## 更新与诊断

- 帮助窗口支持 **检查更新 / 自动更新**（GitHub Release：`https://github.com/sclshu3714/FlyScreen/releases`）
- 启动时可静默检查新版本；发布时请在 Release 中上传 `FlyScreen.exe` 或 `*-win-x64*.zip`
- **一键检查 / 一键修复**：诊断网卡、发现、防火墙与出入站链路，并尝试放行端口、重启网络、重连

## 主要功能一览

- 跨机键鼠（边缘切换、热键切换、Easy Mouse）
- 电脑布局（拖拽互换、添加/移除、布局同步）
- 剪贴板：文本、富文本、表格 CSV、图片/截图
- 文件：粘贴按需拉取 + 直接推送到指定路径
- 局域网 UDP 发现、多网卡绑定、开机自启、托盘、单实例
- GitHub Release 更新、连接一键检查/修复
