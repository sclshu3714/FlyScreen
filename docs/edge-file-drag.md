# 穿边拖文件（跨屏拖放）

| 项 | 内容 |
|----|------|
| 文档版本 | 1.0 |
| 状态 | 已落地（Win / Maui-Win / Mac / Linux Desktop） |
| Core | `EdgeFileDragService` + `MessageType.EdgeDrag*` |
| 统计日 | 2026-11-03 |

**模型：** 在源机拖着文件/文件夹 → 光标按布局穿边到目标机 → 目标机显示跟手提示 → 松开左键 → 若目标目录可写则开始复制（走现有 `FileTransferService` 直传通道）。扩展屏占用的对端不参与穿边拖。

相关文档：[clipboard.md](clipboard.md)（粘贴/直传占用）、[extended-display.md](extended-display.md)（投影会话摘除穿边）、[protocol.md](protocol.md)。

---

## 1. 协议

| MessageType | 值 | 说明 |
|-------------|----|------|
| `EdgeDragHover` | 88 | 光标所在机显示/收起跟手提示（文件名列表） |
| `EdgeDragQuery` | 85 | 源端请目标机回报放下目录，并带顶层名称/大小/日期供同名比对 |
| `EdgeDragPlace` | 86 | 目标机应答：目录 + `Action`（0 直传 / 1 替换 / 2 跳过同名 / 3 取消）+ `SkipNames` |

旧端忽略未知类型，不断连。

---

## 2. 各端落点

| 端 | 捕获拖路径 | 跟手提示 | 同名冲突弹窗 | 放下目录 |
|----|------------|----------|--------------|----------|
| Win 桌面 | `EdgeFileDragCapture` + `WindowsDragPaths` | `EdgeFileDragCue` | `EdgeDragConflictDialog` / `EdgeDragConflictPrompt` | `WindowsDropFolder` |
| Maui-Win | 同 Win 适配层路径 | 同左 | 同左 | 同左 |
| Mac / Linux | `MacEdgeDragPointer` / `X11EdgeDragPointer` | `EdgeFileDragCueWindow` + Overlay | `EdgeDragConflictPrompt` | Desktop 解析目录 |
| Android / 鸿蒙 | — | — | — | 非本期（手机不作穿边拖目标） |

Core 侧 `EdgeFileDragService.RejectPeer`：扩展屏会话中的对端拒绝拖入。

---

## 3. 行为要点

1. **仅松开时传输**：穿边过程只传 Hover/Query，真正复制在 Place 确认后启动。  
2. **同名冲突**：目标目录已有同名项时，在**目标机**弹「替换 / 跳过 / 取消」；可展示源端大小与日期。未接线冲突回调时旧行为为直接覆盖。  
3. **占用处理**：落盘失败（目标文件被占用）时接收端标失败并 `FileAbort`；发送端等待 `FileAccept`，避免「发端完成、收端 0%」。详见 [clipboard.md](clipboard.md) §占用。  
4. **与投影互斥**：正在扩展屏会话的对端不接收穿边拖；会话期从机本就从穿边拓扑摘除。  
5. **取消**：源端 Esc / 中途松在不可放位置 / Place.Action=Cancel → 不传文件。

---

## 4. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-11-03 | 首版：对齐 EdgeDrag Hover/Query/Place、同名冲突、占用与投影互斥 |
