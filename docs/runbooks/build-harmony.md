# 鸿蒙构建说明（W5）

纯血鸿蒙（HarmonyOS NEXT）使用 **ArkTS**，**不**依赖 Android APK。旧版可跑 APK 的设备可用 `mobile/android` 过渡，不计入鸿蒙电脑交付。

## 工程结构

```text
mobile/harmony/
  AppScope/           # 应用级元数据
  common/             # HAR：协议 / 发现 / 连接 / HarmonyAppHost
  phone/              # W5a entry：手机 + 平板（deviceTypes: phone, tablet）
  desktop/            # W5b entry：电脑（deviceTypes: 2in1, default）
```

| 产品 | DeviceClass | Caps |
|------|-------------|------|
| phone | Phone / Tablet | AbsPointer, ClipboardText, DirectTransfer |
| desktop | Desktop | 完整桌面 Caps；可一键降级为 HarmonyDesktopDegraded |

与 Win/Mac/Android **线级协议一致**（帧头 + PascalCase JSON）。

## 环境

- DevEco Studio 5.x（API 12+ / HarmonyOS NEXT）
- 本机 Node / Ohpm（随 DevEco）
- 复制 `local.properties.example` → `local.properties`（通常由 IDE 生成）

## 版本号（与 Win 对齐）

与桌面端同一 Git 标签规则。发版或拉取新 tag 后，在仓库根目录执行：

```powershell
powershell -File build\Sync-MobileVersion.ps1
# 覆盖：powershell -File build\Sync-MobileVersion.ps1 -Version 1.1.0
```

会更新 `AppScope/app.json5`、`common/oh-package.json5`、`FlyScreenVersion.ets`（握手 `AppVersion`）。

## 打开与编译

1. （可选）先跑 `Sync-MobileVersion.ps1`  
2. DevEco Studio → Open `mobile/harmony`  
3. Sync / Ohpm install  
4. 选择模块 **phone** 或 **desktop** 运行到对应设备/模拟器  

命令行（在 DevEco 终端，路径因版本而异）：

```bash
cd mobile/harmony
hvigorw assembleHap
```

## 发布包命名

按 [ROADMAP §4.2](../ROADMAP.md#42-平台包命名)：`FlyScreen-{version}-harmony.hap`（后缀以 hvigor 实际产物为准，若为 `.app`/`.zip` 则保留该后缀）。

```powershell
powershell -File build\Name-FlyScreenPackage.ps1 `
  -Source <path-to-signed.hap> `
  -Rid harmony
```

## 与 Windows 联调

### 5a 手机/平板

1. 同一安全密钥。  
2. 手机发现 Win → 应用并连接 → 触控板移动鼠标。  
3. Win 布局留空槽即可自动接入；手机**不占用**桌面四槽语义（见 device-matrix）。

### 5b 鸿蒙电脑

1. 槽位填写对端 Win/Mac 机器名，密钥一致。  
2. 发现并连接后互为桌面 peer。  
3. 若本机无法全局注入/捕获：点「降级桌面 Caps」，保持连接与剪贴板/布局方向后续能力，**不得**为此修改 Win 钩子语义。

## 后续迭代

| 项 | 模块 |
|----|------|
| 文本剪贴板 | common + UI |
| 文件收件箱 | phone |
| 全局键鼠注入/捕获 | desktop（系统 API） |
| 布局同步 LayoutSync | desktop |
| 持久化 preferences | common |

## 修订

| 日期 | 说明 |
|------|------|
| 2026-10-14 | W5 骨架：common + phone + desktop |
| 2026-10-15 | 版本与 Win Git 标签规则对齐（Sync-MobileVersion） |
