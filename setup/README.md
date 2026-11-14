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

- **打包目标**：Win WPF / MAUI Win / MAUI Android / Linux / Mac / MAUI Mac（可单选、多选、全选）
- **打包方式**：独立应用（沿用 `artifacts/release` 命名）或 **安装包**（仅 Win / MAUI Win）
- **部署模式**：Self-contained 或 依赖框架
- **安装包选项**：产品名、默认路径、许可链接、轮播图目录、快捷方式等（均有默认值）

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

## 卸载

安装完成后目录内有 `Uninstall.exe`，并写入「应用和功能」卸载项（**不**创建开始菜单卸载快捷方式）。

- 默认保留 `%AppData%\FlyScreen` 个人配置
- 可选删除个人配置
- 支持静默：`Uninstall.exe /S`

发布工具始终 `dotnet publish -c Release`，并剔除 `.pdb`；各端输出目录相互独立。

## 并行打包

队列并行数启动时按本机 **逻辑核 / 2** 与 **可用内存 ÷ 约 6GB** 取较小值，并限制在 1–4；启动日志与状态栏会显示该上限。每个任务使用独立工作区：

`artifacts/setup-work/{jobId}/`（应用暂存、payload、卸载器、安装壳输出、按工程隔离的 `obj`）

通过环境变量 `FlyScreenJobId` + 仓库根 `Directory.Build.props` 分流中间输出；安装壳以 `-p:FlyScreenPayloadZip` / `-p:FlyScreenInstallerJson` 注入载荷，避免抢 `FlyScreen.Installer\Payload`。任务结束后自动删除该工作区。
