# 设备形态 × 功能矩阵

与 [ROADMAP.md](ROADMAP.md) 配套。实现与测试均以本表为准；表中「—」表示非目标，「⚠️」表示降级或二期。

## 1. DeviceClass

| 枚举 | 含义 | 典型设备 |
|------|------|----------|
| `Desktop` | 桌面形态，支持边缘布局 | Windows PC、Mac、Linux PC、**鸿蒙电脑** |
| `Phone` | 手机 | Android 手机、鸿蒙手机 |
| `Tablet` | 平板 | Android 平板、鸿蒙平板 |
| `Tv` | 电视 / 大屏 | Android TV、部分鸿蒙大屏 |
| `Unknown` | 未识别 | 按最低能力处理 |

## 2. Capability 定义

| Cap | 含义 |
|-----|------|
| `EdgeMouse` | 鼠标移出屏幕边缘切换到布局中相邻设备 |
| `AbsPointer` | 绝对指针或触控板相对移动（移动端主路径） |
| `ClipboardText` | 文本 / 富文本 / 常见图片剪贴板 |
| `ClipboardFiles` | 文件类剪贴板按需拉取 |
| `DirectTransfer` | 直接推送到对端路径或应用收件箱 |
| `LayoutSync` | 1×4 / 2×2 槽位布局同步 |
| `InjectInput` | 允许对端注入本机键鼠 |
| `CaptureInput` | 本机可作为主机捕获键鼠并转发 |

## 3. 功能矩阵（目标态）

图例：✅ 必须支持 · ⚠️ 降级 / 二期 · ❌ 不支持 · — 不适用

### 3.1 桌面 Desktop

| 能力 | Windows | macOS | Linux X11 | Linux Wayland | 鸿蒙电脑 |
|------|---------|-------|-----------|---------------|----------|
| EdgeMouse | ✅ | ✅（W1） | ✅（W2） | ❌ / ⚠️ | ✅（W5b）或 ⚠️ |
| AbsPointer | ✅ | ✅ | ✅ | ⚠️ | ✅ |
| ClipboardText | ✅ | ✅ | ✅ | ✅ | ✅ |
| ClipboardFiles | ✅ | ✅¹ | ✅¹ | ✅¹ | ⚠️→✅ |
| DirectTransfer | ✅ | ✅ | ✅ | ✅ | ⚠️→✅ |
| LayoutSync | ✅ | ✅ | ✅ | ❌ | ✅ |
| InjectInput | ✅ | ✅ | ✅ | ⚠️ | ✅ / ⚠️ |
| CaptureInput | ✅ | ✅ | ✅ | ⚠️ | ✅ / ⚠️ |

### 3.2 移动与 TV

| 能力 | Android Phone | Android Tablet | Android TV | 鸿蒙手机 | 鸿蒙平板 |
|------|---------------|----------------|------------|----------|----------|
| EdgeMouse | ⚠️ 可选² | ⚠️ 可选² | ❌ | ❌ | ❌ |
| AbsPointer（控桌面） | ✅ | ✅ | ✅ 遥控映射 | ✅ | ✅ |
| ClipboardText | ✅ | ✅ | ⚠️ | ✅ | ✅ |
| ClipboardFiles | ⚠️ | ⚠️ | ❌ | ⚠️ | ⚠️ |
| DirectTransfer | ✅ 收件箱 | ✅ | ⚠️ | ✅ | ✅ |
| LayoutSync | ❌ | ❌ | ❌ | ❌ | ❌ |
| InjectInput（被桌面控） | ⚠️→✅² | ⚠️→✅² | ⚠️ | ⚠️ | ⚠️ |
| CaptureInput（当主机） | ❌→触控板充当 | 同左 | 遥控主机 | 同左 | 同左 |

² **MAUI Android（`App.Maui`）**：握手宣告 `EdgeMouse` + `InjectInput`；桌面边缘切入手机后，经**无障碍服务「玄鸟键鼠注入」**派发点击/滑动（须用户授权）。完整键码注入仍受限。Kotlin 独立工程（`mobile/android`）仍为旧 Caps，不含此项。

### 3.3 一期主路径（产品默认）

1. **Desktop ↔ Desktop**：完整键鼠 + 布局（Wayland 除外）。  
2. **Phone/Tablet → Desktop**：触控板控键鼠 + 文本剪贴板 + 文件接收（MAUI：系统输入法直发对端）。  
3. **Tv → Desktop**：方向键 / 简易指针遥控。  
4. **Desktop → Phone**：MAUI Android 在开启无障碍后支持 Easy Mouse 切入与点击/滑动；完整键盘注入仍为降级。  
5. **本机用户程序（非 Caps）**：MAUI Android 可在无障碍就绪后运行脚本（`Fly.HasText` / `Ocr` / 手势等）；不参与握手能力位。见 [programs-android.md](runbooks/programs-android.md)。

## 4. 布局与槽位

- 具备 `LayoutSync` 的 `Desktop` 进入 1×4 / 2×2 布局槽并参与布局同步。  
- Phone / Tablet **不宣告** `LayoutSync`，但可出现在布局槽中以便边缘邻接（MAUI/WPF 当前行为）；不参与布局广播主路径。  
- 若未来允许多于 4 台桌面，另开版本与协议变更。

## 5. 形态差异验收要点

| 形态 | 必测 |
|------|------|
| Phone | 竖屏触控板、通知栏前台服务、杀后台恢复 |
| Tablet | 横屏触控板、分屏下可用性 |
| Tv | 仅遥控器无触控、焦点模式、无障碍说明 |
| 鸿蒙电脑 | 按 Desktop 清单；与 Win 交叉连接 |
| 鸿蒙手机 | 与 Android Phone 对标，权限文案按鸿蒙体系 |

### 3.4 Linux 无头 / CLI

| 能力 | 行为 |
|------|------|
| ClipboardText | CLI `clip set` 宣告；**无**系统粘贴体验 |
| ClipboardFiles / DirectTransfer | `send` 直传；收到写入 Inbox（`inbox` 命令） |
| EdgeMouse / Inject / Capture / LayoutSync | ❌ 不宣告 |

¹ 桌面：拉取到 Inbox/PasteStaging；不做 Win CF_HDROP 延迟系统粘贴。富文本/图片以文本降级或落盘为主。

## 6. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-10-13 | 首版 |
| 2026-10-14 | Mac/Linux 桌面 ClipboardFiles+DirectTransfer 落地；无头仅直传+文本通道 |
| 2026-10-16 | MAUI Android：`AndroidMobile` 含 EdgeMouse/InjectInput；无障碍注入；触控板 AbsPointer 主路径完善 |
| 2026-10-27 | 补充本机用户程序（Fly/OCR）说明；非 Caps |
