# Android 构建说明（W3）

## 工程位置

```text
mobile/android/          # 独立 Gradle/Kotlin 工程（不进 FlyScreen.sln）
  app/                   # phone / tablet productFlavor
```

与桌面共用**线级协议**（帧头 + JSON 字段 PascalCase），**不引用** .NET 程序集。

> **说明：** 日常内测/联调请优先使用 **MAUI** 工程（`FlyScreen.App.Maui`，见 [build-maui.md](build-maui.md)）。本目录 `mobile/android` 为早期 Kotlin 骨架，Caps 与能力落后于 MAUI。

## 能力（Caps）— Kotlin 骨架

| 项 | 值 |
|----|-----|
| Platform | `Android` |
| DeviceClass | `Phone` 或 `Tablet`（按 smallestWidthDp≥600） |
| Caps | `AbsPointer`, `ClipboardText`, `DirectTransfer` |
| 不含 | `EdgeMouse`, `LayoutSync`, `CaptureInput`, `InjectInput` |

MAUI 当前 Caps 见 `PeerCaps.AndroidMobile`（另含 EdgeMouse/InjectInput）。主路径：**触控板 → 控制桌面鼠标**。

## 环境要求

- Android Studio Hedgehog+（或兼容 AGP 8.2）
- JDK **17**
- Android SDK 34
- 在 `mobile/android/local.properties` 配置 `sdk.dir=...`（可参考 `local.properties.example`）

## 版本号（与 Win 对齐）

与桌面端相同：`Version = 最新 vX.Y.Z 的 patch + 1`（`mobile/android/flyscreen-version.gradle.kts`）。  
Sync / 构建时自动读仓库 Git 标签；握手 `AppVersion` 使用无 flavor 后缀的 `BuildConfig.FLYSREEN_VERSION`。

覆盖：`.\gradlew.bat :app:assemblePhoneDebug -Pflyscreen.version=1.1.0`

## 编译安装

```bash
cd mobile/android
# Android Studio: Open 本目录，Sync，运行 app
./gradlew :app:assemblePhoneDebug
./gradlew :app:installPhoneDebug
```

Windows（已装 wrapper 后）：

```powershell
cd mobile\android
.\gradlew.bat :app:assemblePhoneDebug
```

若尚无 `gradlew`，用 Android Studio 打开工程会自动生成 Wrapper。

## 与 Windows 联调

1. 电脑端玄鸟与手机 **同一安全密钥**。  
2. 电脑布局留至少一个空槽（入站可自动填入手机名），或手机发现后出站连接电脑。  
3. 同一 Wi-Fi；关闭「仅同网段」若网段不一致。  
4. 手机点「应用并连接」，在触控板滑动；电脑光标应移动。  
5. 通知栏前台服务保持连接；断开只影响该 Android peer。

## 发布包命名

按 [ROADMAP §4.2](../ROADMAP.md#42-平台包命名)：`FlyScreen-{version}-android.apk`（TV：`android-tv`；后缀以 Gradle 实际产物为准）。

```powershell
# 先 assembleRelease，再对生成的 apk 命名
powershell -File build\Name-FlyScreenPackage.ps1 `
  -Source mobile\android\app\build\outputs\apk\phone\release\app-phone-release.apk `
  -Rid android
# TV flavor：-Rid android-tv
```

## 后续（本阶段未完成）

- 剪贴板宣告/拉取（文/HTML/图等；细表见 [clipboard.md](../clipboard.md)）  
- 文件收件箱（DirectTransfer）  
- 软键盘 → KeyEvent  
- TV flavor → W4  

## 修订

| 日期 | 说明 |
|------|------|
| 2026-10-14 | W3 骨架：协议、发现、握手、触控板、前台服务 |
| 2026-10-15 | 版本与 Win Git 标签规则对齐（Gradle 自动解析） |
| 2026-10-16 | 注明主交付转 MAUI；本工程 Caps 仍为旧集合 |
