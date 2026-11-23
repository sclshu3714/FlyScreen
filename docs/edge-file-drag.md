# 穿边拖文件（跨屏拖放）

| 项 | 内容 |
|----|------|
| 文档版本 | 1.2 |
| 状态 | 电脑四端完整：Win(WPF) / Maui-Win / Mac / Linux |
| Core | `EdgeFileDragService` + `MessageType.EdgeDrag*` |
| 统计日 | 2026-11-23 |

**模型：** 源机拖文件 → 穿边 → 跟手提示 → 松开 → 目标目录直传。扩展屏对端不参与。关联 [clipboard.md](clipboard.md)。

---

## 1. 协议

| MessageType | 值 | 说明 |
|-------------|----|------|
| `EdgeDragHover` | 88 | 跟手提示 |
| `EdgeDragQuery` | 85 | 询放下目录 + 同名元数据 |
| `EdgeDragPlace` | 86 | 目录 + Action |

---

## 2. 各端落点

| 端 | 捕获 | 跟手 | 冲突 | 放下目录 |
|----|------|------|------|----------|
| Win(WPF) | `EdgeFileDragCapture` | `EdgeFileDragCue` | 冲突对话框 | `WindowsDropFolder` |
| Maui-Win | 同 Win | 同左 | 同语义 | `WindowsDropFolder` |
| Mac | `EdgeFileDragOverlay` | `MacEdgeDragPointer` | 冲突对话框 | `MacDropFolder`（Finder） |
| Linux | Overlay | `X11EdgeDragPointer`（含 XWayland） | 同左 | `LinuxDropFolder` |

---

## 3. 电脑端 × 电脑端（全部完整）

| 源 \\ 目标 | Win(WPF) | Maui-Win | Mac | Linux |
|------------|----------|----------|-----|-------|
| **Win(WPF)** | 完整 | 完整 | 完整 | 完整 |
| **Maui-Win** | 完整 | 完整 | 完整 | 完整 |
| **Mac** | 完整 | 完整 | 完整 | 完整 |
| **Linux** | 完整 | 完整 | 完整 | 完整 |

实现要点：

- Mac：始终挂 `MacEdgeDragPointer`；未授辅助功能时打开系统设置引导，仍跟手。  
- Linux Wayland：工厂优先 `X11InputInjector`（XWayland），跟手与 X11 同等完整。  
- 落点：Win 资源管理器；Mac Finder 前窗；Linux Nautilus/Dolphin/Thunar 尽力解析后回落桌面。

---

## 4. 行为

1. 松开才传文件体。  
2. 同名冲突在目标机。  
3. 占用：FileAbort / 等待 Accept。  
4. 投影会话对端拒绝拖入。

---

## 5. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-11-23 | 四端互拖全部完整；Wayland→XWayland；Mac 始终跟手 |
| 2026-11-03 | 首版 |
