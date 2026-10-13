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
| ClipboardFiles | ✅ | ⚠️→✅ | ⚠️→✅ | ⚠️ | ⚠️→✅ |
| DirectTransfer | ✅ | ⚠️→✅ | ⚠️→✅ | ⚠️ | ⚠️→✅ |
| LayoutSync | ✅ | ✅ | ✅ | ❌ | ✅ |
| InjectInput | ✅ | ✅ | ✅ | ⚠️ | ✅ / ⚠️ |
| CaptureInput | ✅ | ✅ | ✅ | ⚠️ | ✅ / ⚠️ |

### 3.2 移动与 TV

| 能力 | Android Phone | Android Tablet | Android TV | 鸿蒙手机 | 鸿蒙平板 |
|------|---------------|----------------|------------|----------|----------|
| EdgeMouse | ❌ | ❌ | ❌ | ❌ | ❌ |
| AbsPointer（控桌面） | ✅ | ✅ | ✅ 遥控映射 | ✅ | ✅ |
| ClipboardText | ✅ | ✅ | ⚠️ | ✅ | ✅ |
| ClipboardFiles | ⚠️ | ⚠️ | ❌ | ⚠️ | ⚠️ |
| DirectTransfer | ✅ 收件箱 | ✅ | ⚠️ | ✅ | ✅ |
| LayoutSync | ❌ | ❌ | ❌ | ❌ | ❌ |
| InjectInput（被桌面控） | ⚠️ 后期 | ⚠️ | ⚠️ | ⚠️ | ⚠️ |
| CaptureInput（当主机） | ❌→触控板充当 | 同左 | 遥控主机 | 同左 | 同左 |

### 3.3 一期主路径（产品默认）

1. **Desktop ↔ Desktop**：完整键鼠 + 布局（Wayland 除外）。  
2. **Phone/Tablet → Desktop**：触控板控键鼠 + 文本剪贴板 + 文件接收。  
3. **Tv → Desktop**：方向键 / 简易指针遥控。  
4. **Desktop → Phone**：一期不承诺。

## 4. 布局与槽位

- 仅 `DeviceClass=Desktop` 且具备 `LayoutSync` 的设备进入 1×4 / 2×2 布局槽。  
- Phone / Tablet / Tv 显示在「移动设备 / 其他设备」列表，**不占用**四槽布局（避免破坏现有最多 4 台桌面逻辑）。  
- 若未来允许多于 4 台桌面，另开版本与协议变更，不在 W0–W5 默认范围。

## 5. 形态差异验收要点

| 形态 | 必测 |
|------|------|
| Phone | 竖屏触控板、通知栏前台服务、杀后台恢复 |
| Tablet | 横屏触控板、分屏下可用性 |
| Tv | 仅遥控器无触控、焦点模式、无障碍说明 |
| 鸿蒙电脑 | 按 Desktop 清单；与 Win 交叉连接 |
| 鸿蒙手机 | 与 Android Phone 对标，权限文案按鸿蒙体系 |

## 6. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-10-13 | 首版 |
