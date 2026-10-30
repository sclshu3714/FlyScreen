# 电脑扩展屏（迭代说明）

| 项 | 内容 |
|----|------|
| 文档版本 | 1.1 |
| 状态 | E1：标题栏投影 UX + MTT 按需环境修复 |
| 范围 | **电脑↔电脑**真扩展屏；手机作系统扩展屏 / 手机投屏另议 |

## 1. 产品逻辑

1. **标题栏**（最小化左侧）投影按钮 → 弹窗：先选 **投影方式**（复制 / 扩展），再选 **目标**（本机真副屏 + 双向已连接电脑）。扩展且对端目标缺 MTT 时显示 **环境修复**（离线 `VddOffline` 优先，其次 winget）。
2. 投影中标题栏变为「投影中」，再点即 **停止**。
3. A **采集**（复制=主屏；扩展=副屏/VDD）经局域网推到 B；B **全屏显示**。
4. 会话期内，B 从 **穿边拓扑摘除**。**手机**不作投影目标；Mac/Linux 本期可 **接收** 投影，发起请用 Windows。

> 真扩展 ≠ 把所有电脑当成扩展屏。无第二输出时无法「凭空」扩展；须虚拟 IDD 或第二物理显示器。复制模式不强制 MTT。

## 2. 分期

| 阶段 | 目标 | 验收 |
|------|------|------|
| **E0** | 协议 + 会话信令 + 文档 | Offer/Accept/Reject/Stop；Caps；未知消息忽略 |
| **E1**（本期） | Win ↔ Win / Maui-Win：第二输出 + JPEG 推流 + 从机全屏 + 穿边排除 | 有 VDD 或第二物理屏时可扩展；停止后恢复穿边 |
| **E2** | 更低延迟（可选 H.264）、分辨率/帧率协商、失败提示完善 | 延迟与带宽可配置 |
| **E3** | Mac/Linux 从机或主机（按平台采集能力） | 能力位降级，不拖垮 Win |

## 3. 协议（`Protocol.cs`）

| MessageType | 值 | 通道 | 说明 |
|-------------|----|------|------|
| `ScreenExtendOffer` | 80 | Control | A→B 发起 |
| `ScreenExtendAccept` | 81 | Control | B→A 接受（可带从机分辨率） |
| `ScreenExtendReject` | 82 | Control | B→A 拒绝 |
| `ScreenExtendStop` | 83 | Control | 任一方结束 |
| `ScreenExtendFrame` | 84 | **Bulk** | 二进制帧：16B SessionId + w/h/seq + JPEG |

能力位（桌面 `DesktopFull` 等宣告）：

- `ScreenExtendHost`：可作为主机（创建/占用扩展输出并推流）
- `ScreenExtendSink`：可作为从机（全屏收流）

手机 Caps **不**宣告上述两位。

## 4. 主机第二输出策略（Windows）

按优先级：

1. 若本机已有 **非主显示器**（真副屏），扩展时可直接采集，不强制 MTT。
2. 否则 **MTT Virtual Display Driver** 管道 `\\.\pipe\MTTVirtualDisplayPipe`：`PING` → `SETDISPLAYCOUNT N`。安装：**静默优先**——`VddOffline\*setup*.exe`（`/VERYSILENT`）→ `winget --silent` →（按需）Driver.Only + pnputil/nefcon → 启动 `VDDControl` 拉管道。发布进包仅 **setup + VDDControl**（共用 `tools/VddOffline/`；清单在 `docs/mtt_vdd/`）。**仅管道真正就绪**才写入 `%AppData%/FlyScreen/mtt-vdd-installed-by-app.flag`。
3. 仅一块物理屏且管道不可用 → 返回 `NeedEnvironmentRepair`。25.7 起官方改为便携 Control、不再发 setup；静默离线包因此保留 25.5.2 setup-x64（详见 `docs/mtt_vdd/README.md`）。
4. 「卸载并清理」仅在**本程序安装标记且环境检查已通过（管道就绪）**时显示；无内容时不显示空卡片。

### 如何在本机确认 VDD

| 位置 | 看什么 |
|------|--------|
| 设置 → 应用 → 已安装的应用 | `Virtual Display Driver` |
| 设备管理器 → 显示适配器 | `MttVDD` / Virtual Display 相关项 |
| 设置 → 系统 → 显示 | 扩展后应出现额外显示器 |
| 开始菜单 | 打开 Virtual Display Driver / VDD.Control（管道常由此进程提供） |

> 说明：官方 `VirtualDisplayDriver-x86.Driver.Only.zip` 文件名带 x86，但是 **x64 Windows（Intel/AMD）的正确包**；ARM64 用 `…-ARM64.Driver.Only.zip`。控制台用离线 `VDDControl*.exe` 即可（大包 `VDD.Control.*.zip` 不进发布）。

虚拟副屏创建后会：写入 `vdd_settings.xml`（主屏分辨率置顶、`HardwareCursor=false`）→ `SETDISPLAYCOUNT` → 必要时 `DisplaySwitch /extend` → **仅临时**调整副屏分辨率/方位（不写注册表、不改主屏 DPI）。

**严禁影响主屏：** 投影前快照主屏模式。停止虚拟扩展时：释放 VDD → `DisplaySwitch /internal` 收回副屏窗口 → **再用快照恢复主屏**（单独 /internal 曾改坏缩放，故必须「先收拢、后恢复」）。

**环境检测只读：** 打开投影弹窗时的检查不得调用 `DisplaySwitch` / `EnsureVirtualMonitorVisible`；副屏仅在用户点「开始」时创建。检测阶段反复改拓扑会导致 DWM/显示驱动卡死至需重启。

**扩展方位：** 以连接布局中「投影目标电脑」相对本机的槽位为准（`LayoutPeerDirection`）。虚拟副屏临时摆到主屏左/右/上/下。

采集：GDI 按**副屏**边界抓取（扩展模式禁止回退主屏）→ **叠加软件光标**（BitBlt 本身不含指针）→ JPEG（质量约 80）。

投影弹窗在目标下提供**分辨率与缩放**：复制默认本机主屏；扩展默认目标电脑（`DisplayInfoQuery` / Accept 上报）。扩展时仅改虚拟副屏模式，**不改主屏**分辨率/DPI。手机端仅「复制」，无扩展/环境修复。

## 5. 从机行为（接收投影）

- 桌面端（Win / Maui-Win / Mac / Linux）默认 **自动 Accept** Offer；手机拒绝。
- Accept 时上报本机主屏分辨率；打开 **置顶全屏** 窗，首帧前显示「等待投影画面…」。
- 帧走 Bulk（无 bulk 时回退控制通道）；Esc / Alt+F4 / 关窗发送 `ScreenExtendStop`；进程退出尽力通知，杀进程靠断连。
- 主机收到 Stop/断连：停采集、释放虚拟副屏并恢复主屏（与本机点「停止」同等）。
- 会话中：**禁用本机 EasyMouse 穿边**；从机从穿边拓扑摘除。
- 断连视为 Stop。验证收流可不装 VDD：主机选「复制」推主屏即可。

## 6. 工程落点

| 层 | 路径 |
|----|------|
| 文档 | `docs/extended-display.md`（本文） |
| 协议 / 会话 / 目标模型 | `FlyScreen.Core`：`MessageType`、`ScreenExtendService`、`ProjectionTarget`、`DisplayProjectionMode` |
| Win32 采集 / VDD / 环境 | `FlyScreen.Windows`：`ScreenExtend/*`（含 `MttVddEnvironment`、`MttVddInstaller`） |
| 离线包（共用） | `tools/VddOffline\`（WPF/Maui-Win 全量；Mac/Linux 仅 README） |
| WPF 主机/从机 UI | `FlyScreen.App.Windows`（弹窗 + 标题栏） |
| Maui 电脑端 | `FlyScreen.App.Maui` + `Platforms/Windows` |
| Mac / Linux UI | `FlyScreen.App.Desktop` + `FlyScreen.App.Mac` / `FlyScreen.App.Linux`（可收流；本期不发起） |

## 7. 非目标（本期）

- 手机当扩展屏、有线 USB 扩展协议、HDR、多路扩展、从机键鼠回控扩展屏内容（键鼠仍在主机扩展桌面上本地操作）。
- 设置页勾选扩展；「仅电脑屏幕 / 仅第二屏幕」作为发起方式（Internal 仅停止时系统侧使用）。

## 8. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-10-29 | 首版；对齐产品「真扩展 + 会话摘除穿边」；启动 E1 Win/Maui-Win |
| 2026-10-29 | UI：行为勾选 + 目标下拉 + 四种系统投影；点应用生效 |
| 2026-10-30 | 应用后立刻 Ensure 本机第二输出；缺 MTT 时明确提示 winget 安装；不依赖对端 Accept |
| 2026-10-30 | 取消设置页扩展屏；标题栏投影弹窗（目标+四种方式+开始/取消）；Win/Maui-Win/Desktop 同步 |
| 2026-10-30 | UX 收敛：方式仅复制/扩展；目标=真副屏+双向电脑；扩展按需环境修复（VddOffline→winget）；标题栏投影中可停 |
| 2026-10-30 | 记录本程序是否安装 MTT；仅本程序安装时弹窗可卸载清理；投影弹窗加窗体图标 |
| 2026-10-30 | 扩展到对端电脑时，虚拟副屏方位对齐连接布局（Win/Maui-Win） |
| 2026-10-30 | VddOffline 置于 `tools/VddOffline`；各端独立引用，`src` 保持纯代码 |
| 2026-10-30 | 修复投影破坏主屏：停用 /internal 与 DPI API；主屏快照恢复（Win/Maui-Win） |
| 2026-10-30 | 从机 Esc/关窗/退出/断连时主机同等停止投影并释放副屏 |
| 2026-10-30 | 环境检测改为只读：禁止在弹窗检测时 DisplaySwitch/建副屏（曾导致整机卡死） |
| 2026-10-30 | 扩展采集叠光标；副屏对齐主机主屏分辨率；禁止回退抓主屏 |
| 2026-10-30 | 投影弹窗增加分辨率/缩放；扩展副屏可独立于主屏；手机仅复制 |
| 2026-10-30 | 停投：VDD 释放后 /internal 收回窗口再恢复主屏快照；帧内光标按系统尺寸绘制 |
| 2026-10-30 | Maui-Win 关窗=隐藏托盘；手机投影入口在触控板顶栏（设置页窄屏顶栏亦有） |
