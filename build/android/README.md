# Android 内测签名与 APK 产物

| 文件 | 说明 |
|------|------|
| `flyscreen-sideload.keystore` | MAUI Release/Debug 旁加载签名（非 Play 正式库） |
| `../Ensure-MauiSignedApk.ps1` | 构建/发布后补齐 `*-Signed.apk` 与 `FlyScreen.apk` |

- **alias：** `flyscreen`  
- **store/key 密码：** `flyscreen`  

仅用于本地与内测安装。上架 Google Play 请使用独立正式 keystore，并通过 MSBuild 属性覆盖：

```powershell
dotnet publish ... -f net10.0-android `
  -p:AndroidSigningKeyStore=path\to\release.keystore `
  -p:AndroidSigningStorePass=... `
  -p:AndroidSigningKeyAlias=... `
  -p:AndroidSigningKeyPass=...
```

## 安装请用哪一个

优先：

- `bin\Release\net10.0-android\FlyScreen.apk`（稳定分发名）
- 或 `com.xuanniao.flyscreen-Signed.apk`

安装前可用：

```powershell
& "$env:LOCALAPPDATA\Android\Sdk\build-tools\<ver>\apksigner.bat" verify --verbose <apk>
```

未签名包常报 `packageInfo is null` 或 `INSTALL_PARSE_FAILED_NOT_APK`。

## Signed 消失 / 损坏（已知坑）

| 现象 | 原因 | 处理 |
|------|------|------|
| 只有 plain + `*-Signed.apk.idsig`，无 Signed 本体 | SDK / `IncrementalClean` 清掉未登记输出 | `Ensure-MauiSignedApk` + `FileWrites` 保护 |
| Signed 存在但无法安装（NOT_APK） | 曾用**未签名** plain **覆盖** Signed | 脚本改为校验 APK Sig Block；无效则 `apksigner sign` 重签，绝不盲拷 |
| MSBuild `Test-Path` 非法字符 | `-OutDir "...\"` 末尾 `\` 转义引号 | OutDir 参数去掉尾部反斜杠 |

工程目标：`FlyScreen_EnsureSignedApk`（`FlyScreen.App.Maui.csproj`）。发布：`build/Publish-FlyScreenMaui.ps1`。

## 无障碍（Desktop → Phone）

被电脑 Easy Mouse 控制时，须开启系统无障碍服务 **「玄鸟键鼠注入」**（`FlyScreenInputAccessibilityService`）。详见 [docs/runbooks/build-maui.md](../../docs/runbooks/build-maui.md)。
