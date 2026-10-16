# 版本与兼容策略

## 1. 产品版本（程序集）

规则（见 `FlyScreen.csproj`）：

- 读取本地 Git **最新** tag：`vX.Y.Z`
- 构建版本 = `X.Y.(Z+1)`（**与提交次数无关**）
- 发版：将构建产物上传到 GitHub Release，并打 tag `v{构建版本}`

示例：最新 tag `v1.0.1` → 构建始终为 `1.0.2` → 发版打 `v1.0.2` → 之后构建变为 `1.0.3`。

覆盖版本（紧急热修）：`dotnet publish ... -p:Version=1.0.2`（仍建议随后打对应 tag）。

## 2. 协议兼容

| 客户端 A | 客户端 B | 期望 |
|----------|----------|------|
| Win 1.0.x (PV1) | Win 1.1.x (PV1/2 扩展可选) | 互通，行为与 1.0 一致 |
| Win PV1 | Mac PV2 | 互通；Mac 特有 Caps 在 Win 忽略未知字段 |
| PV2 Desktop | PV2 Phone | 按 Caps 交集；MAUI Android 可含 EdgeMouse+InjectInput（须无障碍）；Kotlin 旧包通常无 EdgeMouse |
| PV1 | PV3（破坏性） | 可拒绝并提示升级（实现时明确） |

## 3. 平台包与自动更新

Release 资源命名：`FlyScreen-{version}-{rid}{后缀}`（见 ROADMAP §4.2），发版时各端**各上传一个单文件**。

| 端 | rid 示例 | 检查更新 | 自动安装 |
|----|----------|----------|----------|
| WPF Windows stable | `win-x64`（`.exe`） | ✅ 排除 maui | ✅ 下载后自替换 exe 并重启 |
| MAUI Windows | `maui-win-x64` | ✅ | ✅ 同左（`SelfReplaceInstaller`） |
| MAUI Android | `maui-android` | ✅ | ✅ 下载 APK → 系统安装界面（需允许未知来源） |
| Avalonia Mac/Linux | `osx-*` / `linux-*` | ✅ | ✅ 下载后 bash 自替换并重启 |
| MAUI Mac | `maui-osx-*` | ✅ | ✅ 自替换 |

实现：`FlyScreen.Core/Services/Update/`（`GitHubReleaseClient` + `SelfReplaceInstaller` + 通道匹配）；Android 另见 `AndroidApkUpdater`。  
同一 Release 挂多端单文件；各端只下载自己的 rid。产物：`artifacts/release/`。

## 4. 支持矩阵（发版维护）

发版时在 Release 说明中更新下表（模板）：

| 产品版本 | 协议 | Win | Mac | Linux | Android | 鸿蒙 |
|----------|------|-----|-----|-------|---------|------|
| 1.0.x | 1 | ✅ stable | — | — | — | — |
| 1.1.x | 1–2 | ✅ | — | — | — | — |
| 1.2.x | 2 | ✅ | beta | — | — | — |
| … | | | | | | |

## 5. 破坏性变更清单

（预留；每次升 ProtocolVersion 时追加）

| 协议版本 | 变更 | 最低客户端 | 日期 |
|----------|------|------------|------|
| （暂无） | | | |

## 6. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-10-13 | 首版 |
| 2026-10-16 | Phone Caps：MAUI 可宣告 EdgeMouse/InjectInput；与旧「无 EdgeMouse」表述区分 |
