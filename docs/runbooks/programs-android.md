# 用户程序（Android / Fly API）

Win / Avalonia Desktop / MAUI 均可编辑与共享「用户程序」；**设备控制（读界面、点击、滑动、OCR）仅在手机玄鸟 + 无障碍就绪时可用**。电脑端调用 `Fly.*` 会写警告并返回失败值。

## 1. 前置

| 项 | 说明 |
|----|------|
| 运行端 | **MAUI Android**（玄鸟 APK） |
| 无障碍 | 系统设置 → 无障碍 → **「玄鸟键鼠注入」** 开启；`Fly.Ready == true` |
| OCR | Android **11+**（API 30+）；部分全屏/安全层视频可能截屏失败 |
| 调试红框 | 建议同时开启「显示在其他应用上层」，否则 `Fly.DrawRect` 可能加窗失败 |

存储目录：与设置同级的 `Programs/{id}/`（含 `program.json` + `main.cs` 等）。**各端本地一份**，互不同步磁盘；跨端靠 **程序共享**。

## 2. 语言与执行

| 语言 | 执行方式 |
|------|----------|
| C# | 进程内编译运行（手机）；模板见 `ProgramCatalog` |
| JavaScript | Jint；API 为 camelCase（如 `Fly.hasText`） |
| 其它 | 脚手架为主，设备控制以 C#/JS 为准 |

编辑器：三端「程序」页 → 添加 / 编辑 / 运行 / 调试（断点）。日志页可查看脚本 `Console.WriteLine` 输出。

## 3. Fly API 摘要

完整 XML 注释见 `FlyScreen.Core/Services/Programs/Fly.cs`。常用：

| API | 作用 |
|-----|------|
| `Ready` / `OpenSettings()` | 无障碍是否就绪；引导打开设置 |
| `App` | 前台包名 |
| `ScreenWidth` / `ScreenHeight` | 屏幕像素（随旋转交换） |
| `HasText` / `HasId` / `HasDesc` / `Texts` | 无障碍树文本（多窗口） |
| `Ocr(l,t,r,b)` | 局部截屏 + ML Kit 中英识别；失败返回 `""` |
| `DrawRect(...)` | 调试红框；返回是否加窗成功 |
| `Tap` / `TapText` / `Swipe` / `SwipeUp` 等 | 手势；`SwipeUp` 偏左，减轻误触右下广告区 |
| `Back` / `Home` / … | 系统全局动作 |

**坐标：** 归一化 `0..1`（左上→右下）；OCR/DrawRect 用像素，与 `ScreenWidth/Height` 同源。

### OCR 注意

- 宿主对截屏有约 **900ms** 缓存，避免脚本连调多次 `Ocr` 触发系统截屏限流（否则横屏常恒空）。
- 横屏时截图缓冲可能与屏幕方向不一致，宿主会旋转/角点映射；仍空时日志可能出现 `OCR截屏失败 code=3`（限流）或 `code=6`（安全窗口）。
- 无障碍读不到的图画字（如短剧顶栏胶囊）需靠 OCR；建议区域覆盖顶栏中右，并用 `DrawRect` 核对。

### 脚本建议（短剧/广告上滑类）

- **禁止左滑补救**（易点右下「查看详情」进广告）。
- 只匹配含「上滑… / 继续观看剧集」等语义，勿单独匹配「广告」。
- 上滑走屏幕**左侧**中轴；有「查看详情」时更靠左、起点略抬高。
- 电脑改了脚本必须 **分享到手机或在手机编辑器保存**，否则手机仍跑旧源码。

## 4. 程序共享（`MessageType.ProgramShare`）

| 项 | 行为 |
|----|------|
| 发送 | 已连接对端；载荷含 Id / Name / Language / Source 等 |
| 同 Id | **覆盖**对端源码与元数据（用于电脑改完推到手机） |
| 仅同名不同 Id | 跳过，避免误覆盖 |

UI：程序列表「共享」；接收端日志可见「已更新共享程序…」。

## 5. 相关工程

| 路径 | 说明 |
|------|------|
| `FlyScreen.Core/Services/Programs/` | `Fly`、目录、编译、运行、共享、调试插桩 |
| `App.Maui/Platforms/Android/*Program*` / `FlyScreenRegionOcr` / `FlyScreenDebugRect` / `FlyScreenAccessibilityAutomation` | 设备实现 |
| `App.Windows` / `App.Desktop` / `App.Maui` 程序编辑器 | 三端 UI |

跨端改协议或剪贴板约定时仍遵守 [.cursor/rules/multi-platform-sync.mdc](../../.cursor/rules/multi-platform-sync.mdc)。

## 6. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-10-27 | 首版：与 Fly / OCR / DrawRect / 程序共享（同 Id 覆盖）实现同步 |
