# 飞鼠 (FlyScreen)

仿 [Mouse without Borders](https://aka.ms/mm) 的跨电脑键鼠与剪贴板共享工具，界面为中文，并针对常见痛点做了增强。

## 相对 Mouse without Borders 的改进

1. **全中文界面** — 设置、状态、托盘菜单均为中文。
2. **更稳的双向连接** — 每台机器同时「监听 + 主动连出」，出入站双通道；心跳保活，任一方向可用即可收发，双方向通时显示「双向已连接」。
3. **可靠文件传输** — 复制文件后进入传输队列，显示进度；支持多文件、流式分块、无大小限制；对端收齐后写入剪贴板，避免粘贴到旧文件。
4. **多网卡自动识别** — 「网络设置」中选择通信网卡，绑定该 IP 监听/连出/UDP 发现，减少连错虚拟网卡导致的单向问题。

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

## 使用方法

1. 两台电脑安装并启动「飞鼠」。
2. 在 **机器设置** 中设置**相同的安全密钥**。
3. 在 **网络设置** 中选择正确的网卡（有线/无线），确认能在「局域网自动发现」中看到对方。
4. 将对方计算机名填入机器布局（或双击发现列表自动填入），点击 **应用**。
5. 状态显示「双向已连接」后，将鼠标移出屏幕边缘即可切换；复制文件可在 **文件传输** 页查看进度。

### 快捷键（默认）

| 快捷键 | 功能 |
|--------|------|
| Ctrl+Alt+F1–F4 | 切换到对应机器 |
| Ctrl+Alt+M | 打开设置 |
| Ctrl+Alt+R | 重新连接 |
| Ctrl+Alt+Q | 退出 |

## 防火墙

请允许程序在专用网络上使用 TCP `15155`、UDP `15156`（可在网络设置中修改）。

## 配置文件

`%AppData%\FlyScreen\settings.json`

接收的文件默认保存在：`%AppData%\FlyScreen\Inbox`
