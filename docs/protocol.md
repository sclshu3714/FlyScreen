# 协议与能力位（概要）

本文描述多端扩展所需的协议约定。**实现细节以代码 `Services/Network/Protocol.cs` 为准**；本文约束演进规则，避免破坏现有 Windows 客户端。

## 1. 兼容原则

1. **新增字段必须可选**；缺省时行为与 ProtocolVersion 1（当前 Win）一致。  
2. **不得复用旧字段含义**；改义须升主协议版本。  
3. 对端发送未知 `MessageType`：记录日志并忽略，不断开。  
4. 能力以握手 `Caps` 交集为准，禁止假定对端「一定是 Win 桌面」。

## 2. ProtocolVersion

| 版本 | 状态 | 说明 |
|------|------|------|
| 1 | 现网 / 兼容 | 现有 Win 消息集（键鼠、剪贴板、文件、布局等） |
| 2 | 已落地字段 | 握手 JSON 可选：`Platform`、`DeviceClass`、`Caps`、`AppVersion`、`Channel`；旧端忽略多余字段 |
| 3+ | 未来 | 破坏性变更时使用 |

## 3. 握手扩展（Version ≥ 2 逻辑字段）

建议在现有握手 / Hello 载荷中增加（名称以实现为准）：

```text
PeerHello {
  ProtocolVersion: int
  MachineName: string
  Platform: "Windows" | "macOS" | "Linux" | "Android" | "HarmonyOS" | ...
  DeviceClass: "Desktop" | "Phone" | "Tablet" | "Tv" | "Unknown"
  Caps: string[]          // 见 device-matrix.md
  Screens: { Width, Height, DpiScale }[]
  AppVersion: string      // 如 1.2.0，便于诊断
}
```

### 3.1 协商算法

```text
localCaps ∩ remoteCaps → effectiveCaps
if remoteCaps 缺失或空（Protocol v1 旧端）→ effectiveCaps = localCaps（兼容）
if EdgeMouse in effectiveCaps → 允许边缘切换到该对端
if InjectInput in effectiveCaps → 允许本机 SwitchToPeer 控该对端
if LayoutSync in effectiveCaps → 允许向该对端推送布局
Clipboard* / DirectTransfer → 按位启用（后续路径继续收紧）
```

实现：`LocalPeerIdentity.IntersectCaps` / `ConnectionManager.PeerSupports` / `IsPeerEdgeMouseCapable`。

### 3.2 独立大块传输通道（Bulk）

双方 Caps 均含 `BulkTransfer` 时，在**同一 TCP 端口**再建第二条连接，握手 `Channel=1`（`PeerChannel.Bulk`）：

| 通道 | 承载 |
| --- | --- |
| Control（`Channel=0`/缺省） | 键鼠、心跳、布局/行为同步、剪贴板宣告与拉取信令 |
| Bulk（`Channel=1`） | `File*`、`ClipboardInlineChunk/Complete`、`ClipboardImageChunk/Complete` |

旧端无 `BulkTransfer` / 忽略 `Channel`：大块消息仍走控制面（兼容）。建立 bulk 须对端 **Caps 显式含** `BulkTransfer`（不可用「空 Caps=legacy 全开」逻辑）。

无任何有效输入能力时：保持连接；边缘切换不会选中该 peer。

## 4. 现有消息类别（Version 1，勿随意改义）

| 类别 | 用途 | 多端注意 |
|------|------|----------|
| 连接 / 心跳 | 保活与身份 | 移动端后台可能更长心跳间隔，可配置但默认兼容 |
| 键鼠事件 | 相对/按键/滚轮 | Phone 触控板发相对 `MouseMove` + `MouseButton`/`MouseWheel`/`KeyEvent`；桌面以 `SwitchFocus` 或 AbsPointer 自动进入被控后注入 |
| 焦点 / 布局 | 切换与同步 | `SwitchFocus`：EdgeMouse 切入与 AbsPointer 会话；`LayoutSync` 仅 Desktop 交集 |
| 剪贴板宣告 / 拉取 | 文本图片文件 | 电视端可能缺省 |
| 直接传输 | 推路径 | 移动端目标为应用收件箱语义 |
| 发现 UDP | 局域网 | 移动端注意省电与权限 |

## 5. 新增消息（规划，Version 2+）

仅在 Caps 需要时引入，示例：

| 消息 | 用途 |
|------|------|
| `PointerAbsolute` | 归一化坐标 0–1 或像素 + 屏索引（平板/触控板） |
| `TouchpadDelta` | 相对位移 + 按钮状态 |
| `CapsUpdate` | 运行时权限变化（如用户刚授予辅助功能） |

具体 opcode / JSON 字段在实现 PR 中更新本文与 `Protocol.cs` 注释。

## 6. 安全

- 继续共享安全密钥校验。  
- 后续可选：配对码、一次性 token（新版本协议，不影响 v1）。  
- 不在日志中输出密钥明文。

## 7. 测试要求

任何协议变更 PR 必须包含：

- [ ] 旧 Win（无新字段）↔ 新 Win  
- [ ] 新 Win ↔ 新 Win（带 Caps）  
- [ ] 若已有第二平台：Win ↔ 该平台抽样  

## 8. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-10-13 | 首版概要 |
| 2026-10-13 | W1：HandshakePayload 扩展 + Mac 端发送 ProtocolVersion=2 |
| 2026-10-14 | W2：Linux Caps（X11 / Wayland / Headless）按会话宣告 |
| 2026-10-14 | W3：Android 握手 Platform=Android、DeviceClass=Phone/Tablet、Caps=AbsPointer+… |
| 2026-10-14 | W5：HarmonyOS Phone/Tablet/Desktop Caps；Platform=HarmonyOS |
| 2026-10-14 | W0 收口：Caps 交集门控 + Core.Tests |
| 2026-10-16 | MAUI `AndroidMobile` / `HarmonyMobile`：AbsPointer+ClipboardText+DirectTransfer，另含 **EdgeMouse+InjectInput**（无 LayoutSync）；桌面 AbsPointer 接收侧 `EnsureAcceptsRemoteInput`（Win/MAUI/Linux/Mac） |
