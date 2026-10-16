# Android 内测签名

| 文件 | 说明 |
|------|------|
| `flyscreen-sideload.keystore` | MAUI Release/Debug 旁加载签名（非 Play 正式库） |

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

安装请优先使用：

- `bin\Release\net10.0-android\FlyScreen.apk`（构建后稳定副本）
- 或 `com.xuanniao.flyscreen-Signed.apk`

未签名包常报 `packageInfo is null`。若目录里只有 `com.xuanniao.flyscreen.apk` 与 `*-Signed.apk.idsig`、没有 Signed 本体，可用 `apksigner verify` 检查 plain apk：许多情况下它已是签名包。
