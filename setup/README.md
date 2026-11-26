# FlyScreen 发布 / 安装包工具

`setup/` 下包含两个 WPF 程序：

| 工程 | 作用 |
|------|------|
| `FlyScreen.Setup` | 发布工具：勾选各端、选择独立应用/安装包、独立或依赖框架，一键打包 |
| `FlyScreen.Installer` | 自定义安装界面壳：轮播图、一键安装、协议、更多（路径/空间） |
| `FlyScreen.Uninstaller` | 卸载程序 `Uninstall.exe`：随安装包部署，支持保留/删除个人配置 |

## 运行发布工具

```powershell
dotnet run --project setup\FlyScreen.Setup\FlyScreen.Setup.csproj -c Release
```

或在 Visual Studio 中打开解决方案，运行 **FlyScreen.Setup**。

## 界面选项

- **打包目标**：Win WPF / MAUI Win / MAUI Android / Linux / Mac / MAUI Mac / **插件工具 (.flytool)** / **中继服务 (Relay)**（单选）
- **打包方式**：独立应用（沿用 `artifacts/release` 命名）或 **安装包**（Win Setup / Android APK / Linux tar.gz / 插件 zip / 中继 zip）
- **部署模式**：Self-contained 或 依赖框架（插件工具与 Docker 镜像不受此选项影响）
- **安装包选项**：产品名、默认路径、许可链接、轮播图目录、快捷方式等（均有默认值）

### 插件工具

调用 `build/Pack-FlyTools.ps1`，产出：

- `artifacts/release/FlyScreen-{ver}-flytools.zip`
- 独立应用模式另有同名目录副本；并同步到 `artifacts/flytools/` 供 App 发布复用

### 中继服务 (Relay)

- **运行时**选 `win-x64` / `linux-x64` 等：`dotnet publish` → `FlyScreen-{ver}-relay-{rid}.zip`（或单文件）
- **运行时**选 `docker`：产出 `FlyScreen-{ver}-relay-docker.tar` + `docker-compose.relay.yml`
  - **本机 / TCP**：`pack-docker.ps1`，需本机 docker 客户端
  - **SSH 密钥/密码**：Setup 用 SSH.NET 在远端 `docker build` / `save` 后拉回 tar，**无需本机 docker**；远端用户建议加入 `docker` 组（密码模式可回退 sudo -A）

## 安装包默认 UI

只有一个主界面：

1. 顶部轮播图组  
2. 「一键安装」按钮  
3. 协议复选框（可点「许可协议」查看本地全文或跳转 URL）  
4. 「更多」展开：安装路径、需要/剩余空间、快捷方式选项  

缺少轮播 PNG 时，发布工具会自动生成默认图；缺少 `license.txt` 时自动生成一份免责声明；安装工程能力也会自动校验补齐。

## 输出

- 独立应用：`artifacts/release/FlyScreen-{ver}-{rid}.*`（经 `Finish-PublishToArtifacts.ps1`）
- 安装包：`artifacts/release/FlyScreen-{ver}-{rid}-Setup.exe`（payload 内含 `Uninstall.exe`）
  - **请直接运行该 Setup**（可放在下载目录）；不要把 Setup.exe 拷进 `Program Files` 当主程序。
  - 若启动报 `FlyScreen-Setup.runtimeconfig.json` 为空：删掉 Setup 同目录下的 `*.runtimeconfig.json` / `*.runtimeconfig.dev.json` 后再开；重新用发布工具打安装包可避免。

## 卸载

安装完成后目录内有 `Uninstall.exe`，并写入「应用和功能」卸载项（**不**创建开始菜单卸载快捷方式）。

- 默认保留 `%AppData%\FlyScreen` 个人配置
- 可选删除个人配置
- 支持静默：`Uninstall.exe /S`

发布工具始终 `dotnet publish -c Release`，并剔除 `.pdb`；各端输出目录相互独立。

## 并行打包

队列并行数启动时按本机 **逻辑核 / 2** 与 **可用内存 ÷ 约 6GB** 取较小值，并限制在 1–4；启动日志与状态栏会显示该上限。每个任务使用独立工作区：

`artifacts/setup-work/{jobId}/`（应用暂存、payload、卸载器、安装壳输出、按工程隔离的 `obj`）

通过环境变量 `FlyScreenJobId` + 仓库根 `Directory.Build.props` 分流中间输出；安装壳以 `-p:FlyScreenPayloadZip` / `-p:FlyScreenInstallerJson` 注入载荷，避免抢 `FlyScreen.Installer\Payload`。任务结束后自动删除该工作区（含重试；并清扫其它任务残留）。

### artifacts 保留与清理

| 路径 | 用途 |
| --- | --- |
| `artifacts/release/` | 最终命名产物（安装包 / 独立应用 / zip） |
| `artifacts/flytools/` | 已组好的 `.flytool` + `tools-index.json` |
| `artifacts/setup-work/{jobId}/` | Setup 任务暂存，**任务结束删除** |
| `artifacts/flytools-stage/` | Pack-FlyTools 组包暂存，**脚本结束删除** |

其它 `artifacts/*`（如旧 RID 暂存、检查目录、根目录日志）视为中间产物：Setup 打包开始时会清扫；亦可手动：

```powershell
powershell -File build\Clean-Artifacts.ps1
```
