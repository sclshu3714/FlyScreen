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
| 1 | 现网 | 现有 Win 消息集（键鼠、剪贴板、文件、布局等） |
| 2 | 规划（W0） | 在 Hello/握手中增加 `Platform`、`DeviceClass`、`Caps`；旧端忽略 |
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
if EdgeMouse in effectiveCaps && both Desktop → 启用边缘切换
else if AbsPointer in effectiveCaps → 启用触控板/指针模式
Clipboard* / DirectTransfer → 按位启用
```

无任何有效输入能力时：保持连接，UI 提示「对端不支持键鼠控制」，仍可尝试剪贴板 / 文件（若有 Caps）。

## 4. 现有消息类别（Version 1，勿随意改义）

| 类别 | 用途 | 多端注意 |
|------|------|----------|
| 连接 / 心跳 | 保活与身份 | 移动端后台可能更长心跳间隔，可配置但默认兼容 |
| 键鼠事件 | 相对/按键/滚轮 | 移动端映射为 AbsPointer 事件类型（新增消息或扩展字段） |
| 焦点 / 布局 | 切换与同步 | 仅 Desktop+LayoutSync |
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
