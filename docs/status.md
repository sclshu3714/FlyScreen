# 各端完成情况（以 Windows 为基准）

| 项 | 内容 |
|----|------|
| 基准 | **Windows Desktop**（`FlyScreen.App.Windows`，stable 唯一交付） |
| 统计日 | 2026-10-16 |
| 图例 | **完整** = 与 Win 对标可用 · **骨架** = 工程/协议已通、主路径未齐 · **占位** = API/UI 有入口未落地 · **—** = 非本阶段目标 · **❌** = 明确不做 |

相对完成度按「对标 Win 的必达能力」粗算，仅供排期，非精确工时。

---

## 1. 总览

| 平台 | 阶段 | 工程 | 相对 Win | 一句话 |
|------|------|------|----------|--------|
| **Windows** | stable | `App.Windows` + `Windows` + `Core` | **100%（基准）** | 完整产品：键鼠、布局、剪贴板、文件、更新、诊断 |
| macOS | W1 | `Mac` + `App.Mac` | **~45%** | Avalonia 壳 + Caps 全量；文本剪贴板/直传/Inbox；注入/捕获仍骨架 |
| Linux X11 | W2 | `Linux` + `App.Linux` | **~45%** | GUI + 文本剪贴板/直传/Inbox；CLI `send`/`inbox`/`clip`；无边缘捕获 |
| Linux Wayland | W2 | 同上 | **~35%** | 同 X11 传输路径；无 EdgeMouse；注入占位 |
| Linux 无头 | W2 | 同上 | **~35%** | CLI/Daemon：直传落盘 + 文本宣告；无系统粘贴、无键鼠 |
| Android Phone/Tablet | W3 / W7 | `App.Maui`（主）+ `mobile/android` | **~55%** | MAUI：触控板/键盘直发、剪贴板文本、直传；可选无障碍被桌面控；Kotlin 工程仍骨架 |
| Android TV | W4 | — | **0%** | 未开工 |
| 鸿蒙手机/平板 | W5a | `mobile/harmony/phone` | **~30%** | ArkTS 触控板壳 + 共享连接层；剪贴板/文件未做 |
| 鸿蒙电脑 | W5b | `mobile/harmony/desktop` | **~20%** | 设置/槽位壳 + Caps；无注入/捕获/布局同步 |
| 统一桌面 UI（Avalonia） | W6 | `App.Desktop` | **~60%** | Mac/Linux 共用壳；侧栏 Path 彩标 + 七页导航已与 Win 对齐 |
| **MAUI 跨平台 UI** | **W7** | `App.Maui` | **~80%** | Win DesktopFull + Android 触控板/键盘/剪贴板/直传 + 无障碍被控；UI 与 Win 对齐 |

---

## 2. 能力对照（Desktop）

以 Win 已交付能力为列「Win」= 完整。

| 能力 | Win | macOS | Linux X11 | Linux Wayland | 鸿蒙电脑 |
|------|-----|-------|-----------|---------------|----------|
| 工程可编译 / 独立包 | 完整 | 完整 | 完整 | 完整 | 骨架（DevEco） |
| UDP 发现 + TCP 握手 | 完整 | 骨架 | 骨架 | 骨架 | 骨架 |
| Protocol v2 Caps 宣告 | 部分¹ | 骨架 | 骨架 | 骨架 | 骨架 |
| EdgeMouse 边缘切换 | **完整** | 占位 | 占位 | — / ❌ | 占位 |
| 本机捕获键鼠（主机） | **完整** | 占位 | 占位 | ❌ | 占位 |
| 被控注入键鼠 | **完整** | 骨架² | 骨架² | 占位 | 占位 |
| 1×4 / 2×2 布局 + 同步 | **完整** | 占位 | 占位 | ❌ | 占位 |
| 文本/图片剪贴板 | **完整** | 骨架⁴ | 骨架⁴ | 骨架⁴ | — |
| 文件剪贴板按需拉取 | **完整** | 骨架⁵ | 骨架⁵ | 骨架⁵ | — |
| 直接传输 | **完整** | 骨架 | 骨架 | 骨架 | — |
| 托盘 / 单实例 / 自启 | **完整** | — | —³ | — | — |
| 主题 / 多语言 / 更新 | **完整** | — | — | — | — |
| 连接诊断 | **完整** | — | — | — | — |

¹ Win 已发握手扩展字段；与旧端兼容冒烟仍建议补强。  
² macOS CGEvent / Linux XTest：代码与权限入口有，真机联调与键码映射未齐。  
³ Linux 支持 `--daemon` + systemd 示例，无桌面托盘。  
⁴ Avalonia：文本轮询宣告 + 对端拉取写入；富文本/PNG 降级为文本或 Inbox。传输核心已抽到 `FlyScreen.Core`。  
⁵ 桌面拉取到 Inbox/PasteStaging；不做 Win 式 CF_HDROP 延迟粘贴。无头 CLI 仅 `send`/`inbox`/`clip set`。

---

## 3. 能力对照（Phone / Tablet → 控桌面）

对标目标见 [device-matrix.md](device-matrix.md)；「Win」列表示**桌面作为被控端是否已具备接收能力**（基准侧）。

| 能力 | 桌面被控（Win / MAUI Win / Linux / Mac） | Android（MAUI） | 鸿蒙手机/平板 |
|------|------------------------------------------|-----------------|----------------|
| AbsPointer / 触控板相对移动 | **完整**（`EnsureAcceptsRemoteInput`：AbsPointer 可自动进入被控） | **完整**（相对移动 + SwitchFocus） | **骨架** |
| 触控区轻点 / 左右键 / 中键 | 完整 | **完整**（Pad=左键；中区轻点=中键；上下滑=滚轮） | 占位 |
| 系统输入法 → 对端 | 完整（KeyEvent / 剪贴板粘贴） | **完整**（本地不留稿，停顿后直发） | — |
| 文本剪贴板 | 完整 | ✅ | — |
| 文件接收（DirectTransfer） | 完整 | ✅ | — |
| Desktop → Phone（Easy Mouse） | 桌面发送 | ⚠️ 须开「玄鸟键鼠注入」无障碍；点击/滑动为主 | ❌ |
| LayoutSync | 桌面能力 | ❌ 不宣告 | ❌ |

---

## 4. 基础设施（跨端）

| 项 | 状态 |
|----|------|
| `FlyScreen.Core` 协议 / 连接 / 发现 / 设置 | ✅ 桌面三端共用 |
| 握手 `Platform` / `DeviceClass` / `Caps` | ✅ 宣告 + 对端存储 + `IntersectCaps` 门控（EdgeMouse/Inject/Layout） |
| Win 工程拆分 Core / Windows / App.Windows | ✅ |
| Caps / 握手单元测试 | ✅ `tests/FlyScreen.Core.Tests` |
| Win stable 回归门禁文档 | ✅ 含 Caps / 旧新交叉项（H 节） |
| Mac/Linux/Android/鸿蒙/MAUI 构建 runbook | ✅ |
| Android TV（W4） | ❌ 未开始 |
| Avalonia 统一桌面（W6） | 推进中（`App.Desktop`：七页导航 + Path 彩标侧栏；键鼠主路径仍骨架） |
| MAUI 跨平台 UI（W7） | 推进中（`App.Maui` + `MauiAppHost`；UI 与 Win 同构，能力接线见上表） |
| 三端 UI 信息架构 | ✅ Win / Desktop / MAUI：基础·网络·共享·安全·高级·关于·帮助；密钥在安全页；关于/帮助为页非对话框 |

---

## 5. 阶段完成度（按 ROADMAP）

| 阶段 | 主题 | 完成度 | 备注 |
|------|------|--------|------|
| W0 | Core 拆分 + Caps 地基 | **100%** | 拆分 + 握手 Caps 协商门控 + 单测；Adapter 深迁非阻塞 |
| W1 | macOS | **~45%** | 剪贴板文本 + 直传/Inbox；键鼠主路径未齐 |
| W2 | Linux | **~45%** | GUI/CLI 传输路径；键鼠主路径未齐 |
| W3 | Android 手机/平板 | **~55%** | MAUI 主路径可用；Kotlin 独立工程仍骨架 |
| W4 | Android TV | **0%** | — |
| W5a | 鸿蒙手机/平板 | **~30%** | 对标 W3 骨架级 |
| W5b | 鸿蒙电脑 | **~20%** | 壳 + 降级 Caps；无桌面键鼠 |
| W6 | Avalonia 统一桌面 UI | **~60%** | 共用壳 + 直传/传输列表；侧栏与七页已对齐 Win；键鼠主路径未齐 |
| W7 | MAUI 跨平台 UI | **~80%** | AbsPointer 四端对齐；Android 无障碍被控 + 签名 APK 兜底；OLE/真机打磨仍待 |

---

## 6. 建议优先级（相对 Win 补齐）

1. **Win**：保持回归门禁；握手 v2 与旧端交叉冒烟写进发版检查。  
2. **Android W3**：剪贴板文本 + 联调稳定性（最接近「可用的第二端」）。  
3. **macOS / Linux X11**：EventTap / 边缘捕获 + 被控注入打磨（桌面互通）。  
4. **鸿蒙 5a**：对齐 Android 触控板联调；**5b** 待系统注入 API 再投入。  
5. **W7 MAUI**：与 WPF 真机交叉联调（边缘鼠标 / 直传）；Android 触控板联调。  
6. **W4**：在移动一端稳定后再开。

---

## 7. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-10-14 | 首版：以 Win 为基准汇总 W0–W5 骨架进度 |
| 2026-10-14 | W0 收口 100%：Caps 协商门控 + Core.Tests |
| 2026-10-14 | 增补 W7 MAUI 跨平台 UI 骨架与文档 |
| 2026-10-14 | 传输核心抽到 Core；Mac/Linux 桌面文本剪贴板+直传；Linux CLI send/inbox/clip |
| 2026-10-15 | W7：MAUI 双轨对齐（Win DesktopFull 适配 + Android 触控板） |
| 2026-10-15 | UI 同构：安全密钥独立页；关于/帮助改页内；侧栏彩色 Path 图标；MAUI 窄屏抽屉+Android 可点；Desktop/MAUI 对齐 WPF |
| 2026-10-16 | AbsPointer 门控对齐 Win/MAUI/Linux/Mac；触控板左键/中键；键盘直发；Android EdgeMouse+无障碍注入；Signed APK 补齐 |
