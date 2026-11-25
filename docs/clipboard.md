# 共享剪贴板：行为与各端对标

| 项 | 内容 |
|----|------|
| 协议载体 | `ClipboardInlineData`（Text / Html / Rtf / Csv / Png）+ 文件宣告/按需拉取 |
| Core | `FileTransferService`：宣告仅元数据；进入目标机后再 Pull 写入 |
| 关联 | 穿边拖见 [edge-file-drag.md](edge-file-drag.md) |
| 统计日 | 2026-11-23 |

**电脑端：** Win(WPF) · Maui-Win · Mac · Linux — 跨屏复制粘贴与穿边拖目标均为 **完整**。

**模型：** 本机复制 → 宣告 → 拉取 → 写入系统剪贴板。图片文件：源端补 PNG 伴侣；Win 写 `CF_HDROP`+`CF_DIB`；Mac/Linux 写 `Files`+`image/png`（及 Html/Rtf/Csv 等同包）。

| 端 | 宣告 / 监视 | 写入 |
|----|-------------|------|
| Win(WPF) | `ClipboardMonitor` + Win32 | `CF_DIB`+`PNG`；纯图 HDROP+DIB；其它虚拟粘贴 |
| Maui-Win | 同 Win32 路径 | 同 Win(WPF) |
| Mac / Linux | `DesktopClipboardCoordinator` + Avalonia | 多 MIME 并存；文件宣告后 Pull → Files（图附 PNG） |

---

## 1. 图例

| 标记 | 含义 |
|------|------|
| **完整** | 系统粘贴可用，对齐本机主路径 |

---

## 2. 单机内

| 复制粘贴内容 | Win(WPF) | Maui-Win | Mac | Linux |
|--------------|----------|----------|-----|-------|
| 纯文本 | 完整 | 完整 | 完整 | 完整 |
| HTML | 完整 | 完整 | 完整 | 完整 |
| RTF / CSV | 完整 | 完整 | 完整 | 完整 |
| 位图 / 截图 / PNG·JPEG 等 | 完整 | 完整 | 完整 | 完整 |
| 「复制图片」→ 临时 jpg | 完整 | 完整 | 完整 | 完整 |
| 任意文件 / 文件夹 | 完整 | 完整 | 完整 | 完整 |

---

## 3. 跨屏复制+粘贴（电脑端 × 电脑端）

单元格五元组：文本 / 富文本 / 位图·截图 / 「复制图片」jpg / 普通文件。全部为 **完整**。

| 源 \\ 目标 | Win(WPF) | Maui-Win | Mac | Linux |
|------------|----------|----------|-----|-------|
| **Win(WPF)** | 完整×5 | 完整×5 | 完整×5 | 完整×5 |
| **Maui-Win** | 完整×5 | 完整×5 | 完整×5 | 完整×5 |
| **Mac** | 完整×5 | 完整×5 | 完整×5 | 完整×5 |
| **Linux** | 完整×5 | 完整×5 | 完整×5 | 完整×5 |

要点：

- Win(WPF)↔Maui-Win：Win32 双格式 + 虚拟文件粘贴。  
- ↔Mac/Linux：Avalonia 一次写入 Text/Html/Rtf/Csv/Png/Files；光栅图 JPEG 等转码 PNG；文件宣告后自动 Pull 并挂 Files。  
- Linux Wayland：优先走 XWayland(X11) 会话能力，与 X11 完整对齐。

---

## 4. 跨屏穿边拖（电脑端 × 电脑端）

详见 [edge-file-drag.md](edge-file-drag.md)。四端互拖均为 **完整**。

| 源 \\ 目标 | Win(WPF) | Maui-Win | Mac | Linux |
|------------|----------|----------|-----|-------|
| **Win(WPF)** | 完整 | 完整 | 完整 | 完整 |
| **Maui-Win** | 完整 | 完整 | 完整 | 完整 |
| **Mac** | 完整 | 完整 | 完整 | 完整 |
| **Linux** | 完整 | 完整 | 完整 | 完整 |

落点：Win=`WindowsDropFolder`；Mac=`MacDropFolder`（Finder）；Linux=`LinuxDropFolder`（Nautilus/Dolphin 等）。Mac 未授辅助功能时会打开系统设置引导，仍尝试跟手。Linux Wayland 优先 XWayland 跟手。

---

## 5. 触发时机

| 端 | 内联 | 文件 |
|----|------|------|
| Win(WPF) / Maui-Win | 宣告后 Pull；纯图提前拉 HDROP+DIB | 非图：回本机挂虚拟剪贴板 |
| Mac / Linux | 宣告后 Pull + 多格式 Apply | 宣告后 Pull → Files（图附 PNG） |

---

## 6. 文件占用

| 场景 | 行为 |
|------|------|
| 目标同名占用 | 收端失败 + FileAbort；发端等 Accept |
| 穿边拖同名 | 目标机替换/跳过/取消 |

---

## 7. 联调

1. Win(WPF)↔Maui-Win：文本、富文本、截图、「复制图片」、文件、穿边拖。  
2. Win↔Mac / Win↔Linux：同上。  
3. Mac↔Linux：同上；Wayland 确认 DISPLAY/XWayland。

---

## 8. 附录：手机 / iOS

| 内容 | Android | iOS |
|------|---------|-----|
| 文本 / HTML / PNG | 与电脑互通 | 无 |
| 文件 / 穿边拖 | 半通 / 非目标 | 无 |

---

## 9. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-11-23 | 电脑四端剪贴板/穿边拖全部标完整；Avalonia 多格式+光栅转码；Wayland→XWayland；Mac 始终跟手 |
| 2026-11-23 | 图文件双格式；Mac/Linux Files 自动挂载 |
| 2026-11-03 | 占用语义 |
| 2026-10-29 | 首版 |
