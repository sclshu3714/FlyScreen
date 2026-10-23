# Windows 发版 Runbook

## 1. 发版前

1. 确认改动已合并；跑通 [regression-windows.md](regression-windows.md)。  
2. 查看当前构建版本（最新 tag 的 patch+1）：
   ```powershell
   cd <repo>
   git tag -l "v*.*.*" --sort=-v:refname | Select-Object -First 1
   dotnet build src\FlyScreen.App.Windows\FlyScreen.App.Windows.csproj -c Release
   # 构建日志中有「最新 vX.Y.Z → 项目 Version=...」
   ```
3. **退出**本机正在运行的玄鸟（含托盘），避免 publish 覆盖失败。

## 2. 发布自包含包

```powershell
cd <repo>
dotnet publish src\FlyScreen.App.Windows\FlyScreen.App.Windows.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o src\FlyScreen.App.Windows\bin\publish\win-x64
```

产物：

- `src\FlyScreen.App.Windows\bin\publish\win-x64\FlyScreen.exe`
- `version.txt`（若 Publish 目标已执行）

按 [ROADMAP §4.2](../ROADMAP.md#42-平台包命名) 命名（后缀用实际产物，此处为 `.exe`）：

```powershell
powershell -File build\Name-FlyScreenPackage.ps1 `
  -Source src\FlyScreen.App.Windows\bin\publish\win-x64\FlyScreen.exe `
  -Rid win-x64
# → artifacts\release\FlyScreen-{version}-win-x64.exe
```

## 3. Git 标签与 GitHub Release

假设构建版本为 `1.0.2`：

```powershell
git tag v1.0.2
git push origin v1.0.2
```

在 GitHub 创建 Release **v1.0.2**，上传上述 exe。  
Release 说明中注明：

- 协议版本（1 / 2…）
- Win 行为是否有用户可见变化
- 是否已通过回归门禁

## 4. 发版后

1. 用上一 stable 与本版交叉连接冒烟。  
2. 确认自动更新能检测到新版本（标签版本 > 用户本地程序集版本）。  
3. 若替换失败，查 `%TEMP%\xuanniao-update.log`。

## 5. 热修

- 仅 Win 且不改协议：可在同一小版本策略下发新包，或 patch+1 再发。  
- 改协议字段：先更新 [protocol.md](../protocol.md) 与兼容表，再发版。

## 6. 与多平台关系

- 本 Runbook **只负责 Win**。  
- Mac / Linux / Android / 鸿蒙不得替换 Win 资源文件名。  
- 新平台 CI 失败不得阻断本流程的 stable 发布（除非变更来自共享 Core 且未过 Win 门禁）。
