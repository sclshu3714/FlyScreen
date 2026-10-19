# Linux 构建与运行（W2）

## 工程

| 项目 | 说明 |
|------|------|
| `FlyScreen.Core` | 协议 / 连接 / 发现（与 Win/Mac 共用） |
| `FlyScreen.Linux` | 会话探测、Caps、X11/Wayland/无头注入 |
| `FlyScreen.App.Linux` | 单一入口：自动选择 GUI 或 CLI/Daemon |
| `FlyScreen.App.Desktop` | Avalonia 共用主界面类库（供 Mac/Linux 引用与 publish） |

**不引用** Windows / Mac 工程。GUI 由 `App.Desktop` 提供，布局/文案与 Windows 七页一致：基础 / 网络 / 共享 / 安全 / 高级 / 关于 / 帮助（侧栏彩色 Path 图标；密钥在安全页）。

## 启动模式

| 条件 / 参数 | 模式 |
|-------------|------|
| 默认且存在 `DISPLAY` 或 `WAYLAND_DISPLAY` | **GUI**（Avalonia） |
| 默认且无图形会话 | **CLI** |
| `--gui` | 强制 GUI（失败则回退 CLI） |
| `--cli` / `-c` | 强制交互命令行 |
| `--daemon` / `-d` | 后台（无交互，适合 systemd） |

GUI 与 CLI **共用** `LinuxAppHost`（设置、连接、发现、注入路由）。

被控接收与 Win/MAUI 对齐：`EnsureAcceptsRemoteInput` — 已 `SwitchFocus` 或对端具备 `AbsPointer` 时可自动进入被控，再注入 `MouseMove`/`MouseButton`/`MouseWheel`/`KeyEvent`。

## Caps 与会话

| 会话 | Caps 倾向 |
|------|-----------|
| X11 | EdgeMouse、注入/捕获、剪贴板文本/文件、直传、布局 |
| Wayland | 无 EdgeMouse；剪贴板文本/文件、直传、有限注入 |
| 无头 | 无键鼠；`ClipboardText` + `ClipboardFiles` + `DirectTransfer`（CLI：`send` / `inbox` / `clip set`；无系统粘贴） |

依赖（X11 注入）：`libX11.so.6`、`libXtst.so.6`（Debian/Ubuntu: `libx11-6 libxtst6`）。

## GUI 中文乱码（WSL / 无桌面字体）

Avalonia **默认必须用嵌入 Inter**（`fonts:Inter#Inter`），否则在无系统字体的 WSL 下会报：
`Could not create glyphTypeface. Font family: $Default (key: )` 并回退 CLI。

汉字靠 FontFallbacks（微软雅黑 / Noto CJK 等）。启动时会：

1. 若存在 `/mnt/c/Windows/Fonts/msyh.ttc`，复制到用户（及 `SUDO_USER`）的 `~/.local/share/fonts/flyscreen/` 并 `fc-cache`
2. 用 `fc-list` 探测已安装族名，只挂可用的 CJK 回退

仍乱码（能开窗但方框）时：

```bash
sudo apt install -y fonts-noto-cjk fonts-wqy-zenhei
fc-cache -fv
# 或: cp /mnt/c/Windows/Fonts/msyh*.ttc ~/.local/share/fonts/flyscreen/ && fc-cache -fv
```

调试 GUI 建议 **不要 sudo**（`./FlyScreen --gui`），避免 X11/`$HOME`/DISPLAY 被清掉，且数据目录会落到 `/root/.config/FlyScreen/`。

WSL2 + WSLg（Windows 11）：

```bash
# 普通用户（推荐）
cd ~/xuanniao
chmod +x FlyScreen
export DISPLAY=:0          # 若 echo $DISPLAY 为空
./FlyScreen --gui

# 若必须 root：保留环境或显式指定
sudo -E DISPLAY=:0 ./FlyScreen --gui
```

`Session=Headless; DISPLAY=(none)` 且 `XOpenDisplay failed` = 当前进程看不到 X 服务器，不是程序缺库。

### 标题栏中文 / 子窗风格

Linux/X11（含 WSL）系统标题栏常无法正确绘制 CJK，只显示「玄」或方框。主窗与「添加/编辑程序」等对话框在 Linux 上使用**自绘客户区标题栏**（`SystemDecorations=None`），与主界面主题一致。

### 打开收件箱（共享页）

「打开收件箱」在 Linux 上**不能**对目录路径 `UseShellExecute`（会失败）。实现顺序大致为：

1. **WSL**：优先 `explorer.exe`（`wslpath -w` 或 `\\wsl$\发行版\…`）
2. **sudo**：降权到 `SUDO_USER` 并带上 `DISPLAY` / `DBUS_SESSION_BUS_ADDRESS` 再 `xdg-open`
3. 本机：`xdg-open` / nautilus / dolphin / thunar / `gio open`

`sudo` 下若仍失败（如 `gio: No application is registered`），请改用普通用户启动；Inbox 路径为 `~/.config/FlyScreen/Inbox`（root 则为 `/root/.config/FlyScreen/Inbox`）。

### UI 与 Win/MAUI 对齐要点（Desktop）

| 区域 | 行为 |
|------|------|
| 高级 | 功能 / 行为 / 穿边模式三列；窄屏纵向堆叠；切换穿边时说明文案即时更新 |
| 安全 | 「生成新密钥」在标题行工具栏；输入框 +「显示」单独一行，避免窄屏裁切 |
| 共享 | 传输列表列：方向、文件名、对端、大小、进度、状态、删除 |
| 程序 | 编辑器关闭时若仍在运行/调试，后台异步停止；行号断点按固定行高命中 |

## WSL 桥接与局域网发现

WSL 与 Windows 宿主机默认共用同一个 `Environment.MachineName`（如 `DESKTOP-xxx`）。
发现身份会自动加发行版后缀（如 `DESKTOP-xxx-Ubuntu`），避免与宿主机撞名。

常见表现与对应修复（逻辑对真实 Linux 同样适用，非 WSL 特例）：

| 现象 | 原因 |
|------|------|
| WSL 布局里看不到宿主机，对端却显示 WSL「仅出站」 | 曾误把与 `Environment.MachineName` 同名的槽位清空（宿主机合法名被删） |
| 对端「仅出站」、本机「仅入站」 | UDP 发现不对称；现已从入站 TCP/握手学习对端 IP 并主动出站 |

排查清单：

1. WSL 端「网络设置」绑定 IP 选桥接地址（如 `10.10.1.14`），点「应用」
2. 三台安全密钥一致；UDP `15156` / TCP `15155` 防火墙放行
3. 三台布局中应同时出现宿主机名与带后缀的 WSL 名；单向时点「刷新连接」
4. 若仍撞名，可在 settings.json 写 `"MachineNameOverride": "my-wsl"` 后重启

主窗已自绘标题栏后，任务栏/Alt-Tab 可用 ASCII 名；客户区内标题显示完整「玄鸟」。若仍见系统装饰乱码，确认未强制恢复系统标题栏。客户端内文字走 Avalonia 字体回退（见上文 apt / 拷贝雅黑）。

## 编译

Windows 开发机可编译验证：

```powershell
dotnet build src\FlyScreen.App.Linux\FlyScreen.App.Linux.csproj -c Release
```

Linux 真机：

```bash
dotnet build src/FlyScreen.App.Linux/FlyScreen.App.Linux.csproj -c Release
dotnet run --project src/FlyScreen.App.Linux/FlyScreen.App.Linux.csproj -c Release

# 无桌面服务器
dotnet run --project src/FlyScreen.App.Linux/FlyScreen.App.Linux.csproj -c Release -- --cli

# 发布
dotnet publish src/FlyScreen.App.Linux/FlyScreen.App.Linux.csproj -c Release -r linux-x64 \
  --self-contained true -o artifacts/linux-x64
dotnet publish src/FlyScreen.App.Linux/FlyScreen.App.Linux.csproj -c Release -r linux-arm64 \
  --self-contained true -o artifacts/linux-arm64

# 命名发布包（目录 → .zip；若改为 AppImage 则对 .AppImage 文件再跑一遍）
# FlyScreen-{version}-linux-x64.zip / linux-arm64.zip
powershell -File build/Name-FlyScreenPackage.ps1 -Source artifacts/linux-x64 -Rid linux-x64
powershell -File build/Name-FlyScreenPackage.ps1 -Source artifacts/linux-arm64 -Rid linux-arm64
```

## systemd 示例（无头 Daemon）

`/etc/systemd/system/flyscreen.service`：

```ini
[Unit]
Description=FlyScreen (玄鸟) Linux daemon
After=network-online.target

[Service]
Type=simple
User=YOUR_USER
WorkingDirectory=/opt/flyscreen
ExecStart=/opt/flyscreen/FlyScreen --daemon
Restart=on-failure
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0

[Install]
WantedBy=multi-user.target
```

密钥与槽位可先：

```bash
./FlyScreen --cli --key='your-key' --slot='WIN-PC-NAME'
```

配置文件默认：`~/.config/FlyScreen/settings.json`（或 .NET `ApplicationData` 对应路径）。

## 修订

| 日期 | 说明 |
|------|------|
| 2026-10-19 | 自绘标题栏/子窗；打开收件箱 WSL explorer + sudo 降权；高级三列与安全/共享列表对齐说明 |
| 2026-10-16 | AbsPointer 被控：EnsureAcceptsRemoteInput 对齐 Win/MAUI |

