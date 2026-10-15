# Linux 构建与运行（W2）

## 工程

| 项目 | 说明 |
|------|------|
| `FlyScreen.Core` | 协议 / 连接 / 发现（与 Win/Mac 共用） |
| `FlyScreen.Linux` | 会话探测、Caps、X11/Wayland/无头注入 |
| `FlyScreen.App.Linux` | 单一入口：自动选择 GUI 或 CLI/Daemon |
| `FlyScreen.App.Desktop` | Avalonia 共用主界面类库（供 Mac/Linux 引用与 publish） |

**不引用** Windows / Mac 工程。GUI 布局/文案与 Windows「基础 / 网络 / 共享 / 高级」一致。

## 启动模式

| 条件 / 参数 | 模式 |
|-------------|------|
| 默认且存在 `DISPLAY` 或 `WAYLAND_DISPLAY` | **GUI**（Avalonia） |
| 默认且无图形会话 | **CLI** |
| `--gui` | 强制 GUI（失败则回退 CLI） |
| `--cli` / `-c` | 强制交互命令行 |
| `--daemon` / `-d` | 后台（无交互，适合 systemd） |

GUI 与 CLI **共用** `LinuxAppHost`（设置、连接、发现、注入路由）。

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

调试 GUI 建议 **不要 sudo**（`./FlyScreen --gui`），避免 X11/`$HOME` 权限问题。

标题栏只显示「玄」时：由 X11 窗口管理器字体绘制，需系统已有 CJK（上面 apt / 拷贝雅黑后再开一次窗口）。客户端内文字走 Avalonia 字体回退。

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
