# 玄鸟（FlyScreen）多端迭代规划

| 项 | 内容 |
|----|------|
| 文档版本 | 1.2 |
| 状态 | 生效 |
| 原则 | 协议先行、能力协商、平台隔离、**Windows 主干零回退** |

现有 Windows 能力（键鼠共享、剪贴板、文件传输、布局、更新、诊断）在任何迭代中必须保持可用。新平台以独立工程与分发包推进，失败不得拖垮 Win stable 发版。

相关文档：

- [**各端完成情况（以 Win 为基准）**](status.md)
- [设备形态 × 功能矩阵](device-matrix.md)
- [协议与能力位（概要）](protocol.md)
- [版本与兼容](compatibility.md)
- [Windows 回归门禁清单](runbooks/regression-windows.md)
- [Windows 发版说明](runbooks/release-windows.md)

---

## 1. 目标与约束

### 1.1 长期目标

局域网内按能力协同：

| 生态 | 形态 | 长期角色 |
|------|------|----------|
| Windows | PC | 主机 / 被控（完整） |
| Apple | Mac | 主机 / 被控（完整） |
| Linux | PC | 主机 / 被控（X11 完整；Wayland 降级） |
| 鸿蒙 | 手机 / 平板 / **电脑** | 手机平板：触控板 + 剪贴板 + 文件；**电脑：对标桌面完整能力** |
| Android | 手机 / 平板 / **电视** | 手机平板：触控板 + 剪贴板 + 文件；电视：被控指针 / 遥控器模式为主 |

### 1.2 硬约束（迭代红线）

1. **不影响现有 Windows 使用**：发版必须以 Win 回归通过为门禁（见 [regression-windows.md](runbooks/regression-windows.md)）。
2. **新平台失败不得拖垮旧端**：能力位协商；不支持则降级，不断连、不导致对端崩溃。
3. **协议向后兼容**：旧客户端可连新版本（至少基本能力）；破坏性变更必须升 `ProtocolVersion`。
4. **分发物隔离**：Win / Mac / Linux / Android / 鸿蒙 **分开构建、分开发布**。
5. **设备形态必须建模**：同一 OS 的 Phone / Tablet / Tv / Desktop 用 `DeviceClass` 区分。

### 1.3 非目标（本期不做或明确后置）

- 用现有 WPF 工程直接跑 Mac / Linux / Android / 鸿蒙。
- 第一期实现「手机边缘切换多屏布局」。
- 第一期 Wayland 完整全局键鼠捕获。
- 电视端完整「主机控多台 PC 布局」。
- iPhone / iPad（权限与形态不同，单列远期，不在本路线图 W1–W5）。

---

## 2. 架构原则（保证 Win 不被影响）

```
┌──────────────────────────────────────────────┐
│  FlyScreen.Core（协议/连接/布局/传输，无 UI）   │
└──────────────────────────────────────────────┘
          ▲                ▲               ▲
   Windows Adapter    Mac Adapter    …其他 Adapter
          ▲
   FlyScreen.App.Windows（现有 WPF）
```

| 层级 | 允许 | 禁止 |
|------|------|------|
| Core | `net8.0`、纯 BCL | WPF、WinForms、`DllImport`、各移动 SDK |
| Windows Adapter | Core + Win32 | 被 Mac/Android 工程直接引用实现细节 |
| App.Windows | Core + Windows Adapter + WPF | 新平台 UI 混写进 MainWindow |

**并行策略：**

- 新平台用**新工程 / 新目录**，禁止在现有 XAML 中堆 `#if MAC`。
- CI 中 Win 构建为 required；新平台 job 可为 `experimental`，失败不阻断 Win Release。

### 2.1 建议仓库结构（随阶段逐步创建）

```text
FlyScreen/
  docs/                          # 本目录
  proto/ 或 docs/protocol.md
  src/
    FlyScreen.Core/              # W0 起
    FlyScreen.Windows/           # W0 起
    FlyScreen.App.Windows/       # WPF Windows 应用
    FlyScreen.Mac/               # W1 适配层
    FlyScreen.App.Mac/           # W1 Avalonia 应用
    FlyScreen.Linux/             # W2 适配层
    FlyScreen.App.Linux/         # W2 GUI + CLI/Daemon
    FlyScreen.App.Desktop/       # W6 Avalonia 共用 UI（Mac/Linux）
    FlyScreen.App.Maui/          # W7 MAUI 跨平台 UI（Win/Android…）
  mobile/
    android/                     # W3 已建（phone/tablet flavor；W4 加 tv）
    harmony/                     # W5：common + phone(5a) + desktop(5b)
  tests/
    Protocol.Tests/
    Windows.Regression/          # 清单或自动化冒烟
```

---

## 3. 设备与能力模型

详见 [device-matrix.md](device-matrix.md)。摘要：

### 3.1 DeviceClass

- `Desktop` — Win PC / Mac / Linux PC / **鸿蒙电脑**
- `Phone` — Android / 鸿蒙手机
- `Tablet` — Android / 鸿蒙平板
- `Tv` — Android TV / 类大屏
- `Unknown`

### 3.2 能力协商

握手交换 `Caps[]`。双方按**交集**工作；无交集的非关键能力仅提示，不断开连接。

---

## 4. 版本号与发布通道

### 4.1 产品版本

延续现规则：**最新 Git tag `vX.Y.Z` → 构建版本 `X.Y.(Z+1)`**，与提交次数无关。发版时将当前构建上传到同名 Release（如构建为 `1.0.2` 则打 tag `v1.0.2`）。

### 4.2 平台包命名

统一格式：`FlyScreen-{version}-{rid}{实际后缀}`。

- `{version}`：与 §4.1 构建版本一致（如 `1.0.4`）
- `{rid}`：平台标识（`win-x64` / `osx-arm64` / `osx-x64` / `linux-x64` / `linux-arm64` / `android` / `android-tv` / `harmony` / `maui-win-x64` 等）
- **后缀以实际打包产物为准**（`.exe` / `.apk` / `.hap` / `.zip` / `.dmg` / `.AppImage` / `.msix` …），不以本表示例强制；目录发布默认打成 `.zip`

示例（后缀仅为常见形态，发版以构建结果为准）：

```text
FlyScreen-{version}-win-x64.exe
FlyScreen-{version}-osx-arm64.zip          # 或 .dmg / .app 打包结果
FlyScreen-{version}-osx-x64.zip
FlyScreen-{version}-linux-x64.zip          # 或 .AppImage
FlyScreen-{version}-android.apk
FlyScreen-{version}-android-tv.apk
FlyScreen-{version}-harmony.hap
FlyScreen-{version}-maui-win-x64.exe       # MAUI 双轨 beta 单文件，不替代 WPF stable
FlyScreen-{version}-maui-android.apk
FlyScreen-{version}-maui-osx-arm64         # MAUI Mac Catalyst 单文件；Avalonia Mac 仍用 osx-*
```


辅助脚本：`build/Name-FlyScreenPackage.ps1 -Source <产物> -Rid <rid>` → 输出到 `artifacts/release/`。

### 4.3 协议版本

| ProtocolVersion | 说明 |
|-----------------|------|
| 1 | 当前 Win 行为（兼容基线） |
| 2 | 增加 `DeviceClass` + `Caps`；旧端忽略未知字段 |
| 3+ | 破坏性变更；写明最低客户端版本 |

### 4.4 发布通道

| 通道 | 用途 |
|------|------|
| `stable` | 通过 Win 全量回归 |
| `beta` | 新平台 / 实验能力 |
| `dev` | 内部日构建 |

Windows 用户默认只跟 `stable`；新平台初期可只发 `beta`。

---

## 5. 迭代总览

| 阶段 | 版本锚点 | 周期 | 主题 | 对 Win 影响 |
|------|----------|------|------|-------------|
| **W0** | v1.1.x | 3–4 周 | 抽 Core + Caps 协议，Win 行为不变 | 重构，用户可感知功能零变化 |
| **W1** | v1.2.x | 6–8 周 | macOS 桌面（键鼠 + 文本剪贴板） | 无（独立工程） |
| **W2** | v1.3.x | 6–8 周 | Linux 桌面（X11 优先） | 无 |
| **W3** | v1.4.x | 6–8 周 | Android 手机 / 平板 | 无 |
| **W4** | v1.5.x | 4–6 周 | Android TV | 无 |
| **W5** | v1.6.x | 8–10 周 | 鸿蒙手机 / 平板 + **鸿蒙电脑** | 无 |
| **W6** | v2.0.x | 持续 | Avalonia 统一桌面 UI、能力对齐 | Mac/Linux 已共用 `FlyScreen.App.Desktop`；Win 仍可 WPF 双轨 |
| **W7** | v2.1.x | 6–10 周 | **.NET MAUI 跨平台 UI**（对齐 Win 分区） | 无（独立工程 / beta）；**不替换** WPF stable |

W1–W5 可与 Win 热修并行；热修优先进 Windows 层，合入 Core 须评审。

---

## 6. 分阶段详细规划

### 6.0 阶段 W0 — 地基

**周期：** 3–4 周 · **版本：** 1.1.x  
**进度：已完成（100%）** — 拆分 + Caps 宣告/交集协商 + 单元测试；Adapter 深度迁徙不阻塞 W0 签收（仍可在后续清理）。

#### 目标

- 结构可接多平台；**用户侧 Win 体验与现网一致**。
- 协议可宣告 `Platform` / `DeviceClass` / `Caps`（扩展字段可选）。

#### 做

1. ~~新建 `FlyScreen.Core`：Protocol、Connection、Discovery、Settings、Loc、单实例。~~ ✅
2. ~~新建 `FlyScreen.Windows`：NativeMethods、开机自启。~~ ✅（剪贴板 / 钩子注入 / 诊断仍在 App.Windows，随后续 Adapter 再迁）
3. ~~`FlyScreen.App.Windows` 引用 Core + Windows；`UiMarshal` 注入 UI 调度。~~ ✅
4. ~~握手可选扩展；旧端无 Caps 时按 legacy 兼容；新端存对端身份并 `IntersectCaps`。~~ ✅
5. ~~Win 构建 + Caps 单测；protocol/regression 收口。~~ ✅（`dotnet test tests/FlyScreen.Core.Tests`）

#### 当前仓库结构（拆分后）

```text
FlyScreen.sln
src/
  FlyScreen.Core/       # net8.0：协议、网络、模型、本地化、UiMarshal
  FlyScreen.Windows/    # net8.0-windows：Win32 Native、AutoStart
  FlyScreen.App.Windows/            # WPF App：UI、AppHost、Input、Clipboard、诊断、更新
```

#### 不做

- 任何 Mac / Android / 鸿蒙 UI。
- 改变默认端口、密钥算法、布局语义。

#### 验收（Win 门禁全过）

- 两台 Win：双向连接、边缘切换、热键。
- 文本 / 图片剪贴板、文件拉取、直接传输。
- 布局同步、托盘、单实例、更新检查。
- 与上一 stable（1.0.x）交叉连接成功。

#### 回滚

抽离导致回归失败则暂缓发版，Win 继续旧结构热修。

---

### 6.1 阶段 W1 — Apple macOS

**周期：** 6–8 周 · **版本：** 1.2.x  
**工程：** `FlyScreen.Mac` + `FlyScreen.App.Mac`（Avalonia）  
**进度：** 周 1–2 骨架已落地（见下）；键鼠捕获 / 剪贴板 / 托盘待续。构建说明：[runbooks/build-macos.md](runbooks/build-macos.md)。

#### 设备范围

- Mac Desktop（Apple Silicon 优先，Intel 次之）。
- 不含 iPhone / iPad。

#### 目标能力

| 能力 | 1.2 |
|------|-----|
| 与 Win 互为键鼠主机 / 被控 | ✅ |
| 边缘切换 + 布局 | ✅ |
| 文本 / 图片剪贴板 | ✅ |
| 文件剪贴板 / 直接传输 | ⚠️ 可放 1.2.1 |
| 菜单栏托盘、辅助功能引导 | ✅ |

#### 不影响 Win

- 独立 RID：`osx-arm64` / `osx-x64`。
- CI：`build-mac` 失败不 fail `build-win`。
- 禁止为迁就 Mac 删除或改语义 Win 钩子路径。
- Mac 工程**不引用** `FlyScreen.Windows` / `FlyScreen.App.Windows`。

#### 里程碑（参考）

| 周 | 内容 | 状态 |
|----|------|------|
| 1–2 | 壳、发现、TCP、Hello/Caps | ✅ 骨架：`FlyScreen.Mac` + Avalonia App；握手 v2 可选字段；被控注入 API + 权限引导入口 |
| 3–5 | CGEvent 注入完善 / 捕获、权限 UX | ⏳ 注入可编译桩已有；EventTap 捕获待做 |
| 6–7 | 剪贴板、布局、托盘 | ⏳ |
| 8 | 与 Win 联调、签名公证、beta | ⏳ |

#### 验收

- [ ] Mac↔Win 双向键鼠稳定（建议 ≥ 30 分钟）
- [ ] 权限未授予时有引导，且不导致 Win 崩溃
- [ ] **Win 回归门禁全过**

---

### 6.2 阶段 W2 — Linux

**周期：** 6–8 周 · **版本：** 1.3.x  
**工程：** `FlyScreen.Linux` + `FlyScreen.App.Linux`  
**进度：** 骨架已落地 — 启动自动识别 X11 / Wayland / 无头；GUI（Avalonia）与 CLI/Daemon 共用 `LinuxAppHost`。说明：[runbooks/build-linux.md](runbooks/build-linux.md)。

| 环境 | 目标 | 现状 |
|------|------|------|
| X11 桌面 | 完整 EdgeMouse + 注入 | Caps + XTest 注入骨架；捕获/边缘后续 |
| Wayland 桌面 | 文本剪贴板 + 文件；EdgeMouse **不承诺** | Caps 降级；注入占位 |
| 无桌面 / 服务器 | 网络连接 + 后续文件；无键鼠 Caps | CLI / `--daemon` + systemd 示例 |

验收含：X11 下 Linux↔Win 键鼠；Wayland/无头降级不断会话；Win 门禁全过。

---

### 6.3 阶段 W3 — Android 手机 / 平板

**周期：** 6–8 周 · **版本：** 1.4.x · **工程：** `mobile/android`（Kotlin）  
**进度：** 骨架已落地 — 协议帧对齐 Core、UDP 发现、TCP 握手（Caps）、触控板相对鼠标、前台服务。剪贴板/文件/软键盘待续。见 [runbooks/build-android.md](runbooks/build-android.md)。

| 形态 | 角色 | 功能 | 现状 |
|------|------|------|------|
| Phone | 控 PC 为主 | 触控板、滚轮、可选软键盘；文本剪贴板；收文件 | ✅ 触控板+滚轮+连接；⏳ 剪贴板/文件/软键盘 |
| Tablet | 同 Phone | 更大触控板 UI | ✅ flavor `tablet`；布局后续可调 |
| TV | **本阶段不做** | → W4 | — |

- `DeviceClass=Phone|Tablet`；**无** `EdgeMouse` / `LayoutSync`；有 `AbsPointer`。
- **主路径：Android 控桌面**；桌面控 Android 不做承诺。
- 独立 APK；前台服务保活；断线只摘该 peer。
- **不进入**桌面 1×4/2×2 布局槽（见 device-matrix）。

验收：Phone/Tablet→Win 触控板；文本剪贴板；Win 门禁全过。

---

### 6.4 阶段 W4 — Android TV

**周期：** 4–6 周 · **版本：** 1.5.x  
`DeviceClass=Tv`；优先「遥控方向键 → PC」；剪贴板低优先级。  
可用 productFlavor：`mobile` / `tv`，避免拖垮手机包。

---

### 6.5 阶段 W5 — 鸿蒙（手机 / 平板 / 电脑）

**周期：** 8–10 周（可拆 5a / 5b）· **版本：** 1.6.x  
**工程：** `mobile/harmony`（ArkTS：`common` + `phone` + `desktop`）  
**进度：** 骨架已落地 — 共享协议/发现/连接；手机触控板壳；电脑设置/槽位壳 + Caps 降级入口。见 [runbooks/build-harmony.md](runbooks/build-harmony.md)。

| 形态 | DeviceClass | 对标 | 说明 | 现状 |
|------|-------------|------|------|------|
| 鸿蒙手机 | Phone | Android Phone | ArkTS 原生；勿假设跑 APK | ✅ phone 模块触控板 + 连接 |
| 鸿蒙平板 | Tablet | Android Tablet | 大屏触控板 UI | ✅ 同 phone（deviceTypes 含 tablet） |
| **鸿蒙电脑** | **Desktop** | Win/Mac | 完整键鼠 + 布局 + 剪贴板 | ✅ desktop 壳；⏳ 注入/捕获/布局 |

- **5a：** 手机 / 平板（对标 W3）  
- **5b：** 电脑（对标 W1）；API 不足则 Caps 降级，**禁止**改 Win 注入语义  

旧版「可装 APK 的鸿蒙」可用 Android 包过渡，**不计入**纯血鸿蒙电脑交付。

---

### 6.6 阶段 W6 — Avalonia 统一桌面 UI（v2.0）

- Mac/Linux 共用 `FlyScreen.App.Desktop`，布局/文案对齐 Win。
- Win 可 WPF / Avalonia **双轨**一个大版本。
- 各 Desktop 文件传输能力对齐；移动端有限被控。
- **仅当**新 Win UI 通过与 WPF 同等回归后，才切换 stable 默认包。

**进度（UI 壳）：** 侧栏彩色 Path 图标 + 七页导航（基础 / 网络 / 共享 / 安全 / 高级 / 关于 / 帮助）已与 WPF 对齐；密钥在安全页；关于/帮助为页内内容（非独立窗）。键鼠主路径仍见 W1/W2。

---

### 6.7 阶段 W7 — .NET MAUI 跨平台 UI（v2.1）

**周期：** 6–10 周 · **版本：** 2.1.x  
**工程：** `FlyScreen.App.Maui`（引用 `FlyScreen.Core`）  
**TFM：** 默认含 android/ios/maccatalyst；Windows 主机自动追加 `net10.0-windows*`  
**进度：** 双轨 beta 能力接线（Win DesktopFull + Android 触控板/剪贴板/直传）+ **UI 与 Win 同构**。说明：[runbooks/build-maui.md](runbooks/build-maui.md)。

#### 目标

| 项 | 说明 |
|----|------|
| 单一 UI 工程 | Windows / Android / Mac Catalyst（可选 TFM）；发布见 `build/Publish-FlyScreenMaui.ps1` |
| 用户体验 | 侧栏七页 + Path 彩标；安全密钥 / 电脑布局 / 网络 / 共享 / 高级 / 关于 / 帮助，与 Win **同一信息架构**；窄屏抽屉导航 |
| 业务 | 经 Core + `Platforms/Windows|Android` 适配；**禁止**引用 `App.Windows` |
| 分发 | 独立包 / `beta` 通道；**禁止**默认替换 WPF stable |

#### 做

1. ~~新建 `FlyScreen.App.Maui`，配色与导航对齐 Win。~~ ✅
2. ~~`MauiAppHost`：设置、发现、布局、FileTransfer、消息分发。~~ ✅
3. ~~Windows：钩子/注入/托盘/自启/文本剪贴板（对照移植，不耦合 WPF 工程）。~~ ✅
4. ~~Android：触控板 AbsPointer + 文本剪贴板 + 直传；Phone 启动分流。~~ ✅
5. ~~侧栏 Path 彩标、安全/关于/帮助页、窄屏抽屉与 Android 侧栏可点。~~ ✅
6. ⏳ Windows / Android 与 WPF 真机交叉联调；图标/启动图；文件剪贴板 OLE 体验打磨  

#### 不做

- 删除或停更 WPF / Avalonia 工程。  
- 为迁就 MAUI 改 Win **钩子/Caps 语义**或破坏兼容。  
- 让 MAUI **依赖** `FlyScreen.App.Windows` 实现细节（WPF 自身 UI 改进可并行，不经本工程引用）。  

#### 不影响 Win

- 独立工程与 RID；CI 中 `build-maui` 失败不 fail `build-win`。  
- 禁止引用 `FlyScreen.App.Windows` 实现细节。  

#### 验收

- Windows 目标：可编译运行；密钥/槽位/发现/应用后与 Win 端握手成功。  
- Android 目标：可安装；发现与连接状态可见；窄屏侧栏可切换页面。  
- UI 分区与 Win 一致：基础 / 网络 / 共享 / 安全 / 高级 / 关于 / 帮助。  

---

## 7. 每迭代固定流程

```text
需求冻结 → 设计（Caps / DeviceClass）→ 实现（平台分支）
  → 单平台测试 → Win 回归门禁 → 签收
  → 打 tag / 分发对应平台包 → 发布说明（写明 Win 是否有行为变化）
```

### 7.1 协议变更门禁

- 新增字段：必须可选、默认兼容。
- 删除或改义：升 `ProtocolVersion`，旧客户端提示升级。

---

## 8. 测试矩阵（摘要）

| 对端 \\ 本端 | Win | Mac | Linux X11 | Android Phone | Android TV | 鸿蒙手机 | 鸿蒙电脑 |
|--------------|-----|-----|-----------|---------------|------------|----------|----------|
| Win | 全量 | W1 | W2 | W3 | W4 | W5a | W5b |
| Mac | | 全量 | 抽样 | 抽样 | — | 抽样 | 抽样 |

移动端重点：**Phone/Tablet 控 Desktop**；Tv 重点：**遥控 Desktop**。

---

## 9. 角色建议

| 角色 | 关注 |
|------|------|
| 平台-Win | 回归门禁、热修 |
| 平台-Apple | W1 |
| 平台-Linux | W2 |
| 移动-Android | W3–W4 |
| 移动-鸿蒙 | W5a / 5b（电脑单独负责人更佳） |
| 协议 / Core | 全程评审，防止破坏兼容 |

节奏建议：每 2 周可演示增量；每阶段结束可安装 beta。

---

## 10. 待产品确认（附录）

1. Android / 鸿蒙一期是否接受「只控 PC、PC 不控手机」？（规划默认：**接受**）
2. Linux Wayland 是否接受长期降级？（规划默认：**接受**）
3. 鸿蒙电脑与 Mac 的优先级？（规划默认：Mac W1 → 鸿蒙电脑 W5b）
4. Win UI 是否在 v2 前保持 WPF？（规划默认：**保持**，W6 Avalonia / W7 MAUI 均为双轨，stable 仍 WPF）
5. W7 MAUI 是否作为长期统一壳，还是仅移动+实验桌面？（规划默认：**跨平台壳候选**，与 Avalonia 并存至回归通过）

确认结果请回写本节并更新日期。

---

## 11. 修订记录

| 日期 | 版本 | 说明 |
|------|------|------|
| 2026-10-13 | 1.0 | 首版：多端路线图与 Win 零回退门禁 |
| 2026-10-14 | 1.1 | 增补 W7：.NET MAUI 跨平台 UI（`FlyScreen.App.Maui`） |
| 2026-10-14 | 1.1.1 | W7 TFM 对齐 net10 + 补装 macos workload，消除 MSB4019 AutoImport |
| 2026-10-15 | 1.2 | 三端 UI 同构：七页导航、安全页密钥、关于/帮助页内化、Path 侧栏；W6/W7 进度与验收更新 |
