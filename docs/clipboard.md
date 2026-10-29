# 共享剪贴板：行为与各端对标

| 项 | 内容 |
|----|------|
| 协议载体 | `ClipboardInlineData`（Text / Html / Rtf / Csv / Png）+ 文件宣告/按需拉取 |
| Core | `FileTransferService`：宣告仅元数据；进入目标机后再 Pull 写入 |
| 统计日 | 2026-10-29 |

**模型：** 本机复制 → **宣告** → 鼠标/焦点进入对端 → **拉取** → **写入该端系统剪贴板**（文件可为虚拟剪贴板，粘贴时再传）。目标体验：像在同一台电脑内复制粘贴。

相关代码：

| 端 | 宣告 / 监视 | 写入 |
|----|-------------|------|
| Win 桌面 | `ClipboardMonitor` + `ClipboardBridge.TryCaptureInline` | `ClipboardBridge.ApplyInlineSuppressing`；文件 `OfferVirtualFilePaste` |
| Maui-Win | `MauiTextClipboardMonitor` + `MauiWinClipboardInline` | `MauiClipboardApplySink` → WinRT `DataPackage`；文件虚拟粘贴 |
| Android | `AndroidMauiPlatformServices` 轮询 | `MauiClipboardApplySink`（FileProvider / HtmlText） |
| Mac/Linux | `DesktopClipboardCoordinator` + `AvaloniaClipboardApplySink` | 同左；文件靠 UI「接收剪贴板文件」 |

---

## 1. 图例

| 标记 | 含义 |
|------|------|
| **完整** | 原生格式读写，可 Ctrl+V / 系统粘贴 |
| **尽力** | 已实现，效果依赖系统 MIME / 桌面环境 |
| **降级** | 以纯文本（或其它次优格式）承托 |
| **半通** | 能传输，但不能像本机一样挂系统剪贴板粘贴 |
| **无** | 未实现 |

---

## 2. 复制（宣告）对标

| 格式 | Win 桌面 | Maui-Win | Android | Mac/Linux Desktop |
|------|----------|----------|---------|-------------------|
| 文本 Text | 完整 | 完整 | 完整 | 完整 |
| HTML | 完整 | 完整（WinRT HtmlFormat） | 完整（HtmlText） | 尽力（text/html 等） |
| RTF | 完整 | 完整（SetRtf / Rich Text Format） | 无 | 尽力 |
| CSV | 完整 | 完整（Csv） | 无 | 尽力 |
| 图片 PNG | 完整 | 完整（Bitmap→PNG） | 完整（URI→Bitmap） | 尽力（image/png 等） |
| 文件 / 文件夹 | 完整（HDROP） | 完整（HDROP + StorageItems） | 半通（非图 URI 物化；需开启传文件） | 完整（DataFormats.Files） |

---

## 3. 粘贴（写入本机）对标

| 格式 | Win 桌面 | Maui-Win | Android | Mac/Linux Desktop |
|------|----------|----------|---------|-------------------|
| 文本 Text | 完整 | 完整 | 完整 | 完整 |
| HTML | 完整（CF_HTML） | 完整（SetHtmlFormat） | 完整（NewHtmlText） | 尽力 |
| RTF | 完整 | 完整（SetRtf） | 降级纯文本 | 尽力 |
| CSV | 完整 | 完整 | 降级纯文本 | 尽力 |
| 图片 PNG | 完整（SetImage） | 完整（SetBitmap + Flush） | 完整（FileProvider URI） | 尽力（失败则落成文件） |
| 文件 / 文件夹 | 完整（虚拟剪贴板，Ctrl+V 再拉） | 完整（同上） | 半通（UI 接收；不挂系统剪贴板） | 半通（「接收剪贴板文件」） |

---

## 4. 触发时机

| 端 | 内联粘贴 | 文件粘贴 |
|----|----------|----------|
| Win / Maui-Win | 焦点回本机 `ArmRemoteClipboardForPaste` → Pull → 写入 | 挂虚拟剪贴板；真正粘贴时再拉 |
| Android | 焦点回本机 Arm → Pull → `ClipboardManager` | 不 Arm；需产品侧接收路径 |
| Mac/Linux Desktop | 对端宣告后立即 Pull + Apply | 按钮「接收剪贴板文件」显式拉取 |

---

## 5. 主场景结论

| 场景 | 状态 |
|------|------|
| **Win ↔ Maui-Win** 文本 / HTML / RTF / CSV / PNG / 文件 | **主路径已对齐**（系统粘贴可用） |
| Win ↔ Android 文本 / HTML / PNG | 可用 |
| Win ↔ Android RTF/CSV | 粘贴降级文本；宣告侧 Android 无 RTF/CSV |
| Win ↔ Android / Desktop 文件 | 半通（拉得到，不一定 Ctrl+V） |
| Mac/Linux 本机复制图片/富文本 | 尽力；视系统剪贴板 MIME 而定 |

---

## 6. 联调建议

1. 两台 PC：一端 Win、一端 Maui-Win；双向复制纯文本、Word 富文本、Excel 单元格、截图、资源管理器文件。  
2. 手机：复制文本/截图 → 电脑粘贴；电脑复制 HTML/图 → 手机粘贴。  
3. Mac/Linux：文本必测；PNG/HTML 按桌面环境抽测。

更细的 Win stable 门禁见 [runbooks/regression-windows.md](runbooks/regression-windows.md) D 节。

---

## 7. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-10-29 | 首版：各端复制/粘贴对标表；Maui-Win 全格式内联；Android Html/图/文件物化；Desktop 多格式尽力 |
