# macOS 构建说明（W1）

## 工程

| 项目 | 说明 |
|------|------|
| `FlyScreen.Core` | 协议 / 连接 / 发现（与 Win 共用） |
| `FlyScreen.Mac` | CGEvent 注入、辅助功能权限 |
| `FlyScreen.App.Mac` | Avalonia 入口（UI 来自 `FlyScreen.App.Desktop`） |
| `FlyScreen.App.Desktop` | 与 Linux 共用的主界面（对齐 Win 七页导航 + Path 侧栏） |

**不引用** `FlyScreen.Windows` / `FlyScreen.App.Windows`，Win 发版不受影响。

## 在 Windows 上（开发机）

可编译验证 Core + Mac 适配层与 Avalonia UI（注入在非 macOS 上为桩）：

```powershell
cd <repo>
dotnet build src\FlyScreen.App.Mac\FlyScreen.App.Mac.csproj -c Release
```

不要在 Windows 上做 `osx-*` 自包含发布作为日常验证；真机联调请在 Mac 上执行。

## 在 macOS 上

```bash
cd <repo>
dotnet build src/FlyScreen.App.Mac/FlyScreen.App.Mac.csproj -c Release
dotnet run --project src/FlyScreen.App.Mac/FlyScreen.App.Mac.csproj -c Release

# Apple Silicon 发布示例
dotnet publish src/FlyScreen.App.Mac/FlyScreen.App.Mac.csproj -c Release -r osx-arm64 \
  --self-contained true -o artifacts/osx-arm64

# Intel
dotnet publish src/FlyScreen.App.Mac/FlyScreen.App.Mac.csproj -c Release -r osx-x64 \
  --self-contained true -o artifacts/osx-x64

# 命名发布包（目录 → .zip；若再打成 .dmg 则对 .dmg 再命名）
# FlyScreen-{version}-osx-arm64.zip / osx-x64.zip
powershell -File build/Name-FlyScreenPackage.ps1 -Source artifacts/osx-arm64 -Rid osx-arm64
powershell -File build/Name-FlyScreenPackage.ps1 -Source artifacts/osx-x64 -Rid osx-x64
```

## 辅助功能

首次键鼠注入/捕获需在 **系统设置 → 隐私与安全性 → 辅助功能** 中勾选玄鸟。应用内「打开辅助功能设置」可跳转。

## 与 Windows 联调（最小）

1. 两端同一安全密钥。  
2. 布局槽位互相填写对方 `MachineName`（`Environment.MachineName`）。  
3. 同一局域网；必要时关闭「仅同网段」或对齐网卡。  
4. 确认握手日志中 Mac 侧 `Platform=macOS`、`ProtocolVersion=2`。  

完整 EdgeMouse 主机捕获与剪贴板见 ROADMAP W1 第 3–7 周。
