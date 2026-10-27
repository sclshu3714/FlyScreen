# 发布项目（VS 右键生成 = 发布）

解决方案文件夹 **`build`** 下各 `FlyScreen.Publish.*`：**右键 → 生成** 即发布。  
中间产物在各项目 `bin/publish/…`，**最终单个文件**复制到：

`artifacts/release/FlyScreen-{version}-{rid}{后缀}`

（规则见 `docs/ROADMAP.md` §4.2 / `Name-FlyScreenPackage.ps1`）

| 项目 | rid | 最终包形态 |
|------|-----|------------|
| `FlyScreen.Publish.Win` | `win-x64` | `.exe`（WPF stable） |
| `FlyScreen.Publish.MauiWin` | `maui-win-x64` | `.exe`（内部 `-p:FlyScreenMauiTfms=windows`，不要求本机装齐 android workload） |
| `FlyScreen.Publish.MauiAndroid` | `maui-android` | `.apk` |
| `FlyScreen.Publish.Linux` | `linux-x64` | 无扩展名可执行文件 |
| `FlyScreen.Publish.Mac` | `osx-arm64` | 无扩展名可执行（**仅 macOS**） |
| `FlyScreen.Publish.MauiMac` | `maui-osx-arm64` | 无扩展名可执行（**仅 macOS**） |

未勾进「生成解决方案」。上传 GitHub Release 时请按 rid 上传对应单文件，供各端更新器匹配。

### 自动更新

各端「帮助 → 检查/应用更新」会按 rid 下载**本端**包：

| 端 | 行为 |
|----|------|
| WPF / MAUI Win / Avalonia Mac·Linux / MAUI Mac | 下载单文件 → 退出 → 脚本覆盖 → 自动重启 |
| MAUI Android | 下载 APK → 拉起系统安装界面（需允许安装未知应用） |

Core：`FlyScreen.Core/Services/Update/`。详见 `docs/compatibility.md` §3。
