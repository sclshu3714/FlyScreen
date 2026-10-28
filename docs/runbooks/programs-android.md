# 用户程序（Android / Fly API）

Win / Avalonia Desktop / MAUI 均可编辑与共享「用户程序」；**设备控制（读界面、点击、滑动、OCR）仅在手机玄鸟 + 无障碍就绪时可用**。电脑端调用 `Fly.*` 会写警告并返回失败值。

## 1. 前置

| 项 | 说明 |
|----|------|
| 运行端 | **MAUI Android**（玄鸟 APK） |
| 无障碍 | 系统设置 → 无障碍 → **「玄鸟键鼠注入」** 开启；`Fly.Ready == true` |
| OCR | Android **11+**（API 30+）；部分全屏/安全层视频可能截屏失败 |
| 调试红框 | `SetDebugRect` 可多框常驻；OCR 截屏前撤框。调试OCR：非目标红 / 目标蓝 / 命中绿；操作中操作区绿 |
| 区域标定 | 设置页表格：竖/横各一行识别区+操作区；建议在**竖正/横正**下标定；竖反/横反共用对应键（见下） |
| 设置持久化 | `Programs/{id}/settings.json`；重启玄鸟 / 再跑程序仍生效；升级内置源码**不覆盖**该文件 |
| 内置源码 | 可打开编辑器阅读、复制、查找；**不可改字/保存**（改参数请用设置） |
| 调试日志 | 设置 `debugLog`；运行时屏幕**左中**浮层显示最近 **5** 行程序日志（不挡触摸；需悬浮窗权限） |

存储目录：与设置同级的 `Programs/{id}/`（含 `program.json`、`main.cs`、`settings.json`）。**各端本地一份**；跨端靠 **程序共享**。

## 2. 内置程序与设置

| 项 | 说明 |
|----|------|
| 内置「通用识别操作」 | Id=`builtin-generic-swipe`；安装时 EnsureBuiltin；**不可删除**；源码只读 |
| 程序列表 | 更多菜单：… → 共享 → **设置** → 删除（内置隐藏删除） |
| `@fly-settings` | 脚本注释块声明可配项；设置页自动生成控件 |
| `settings.json` | 覆盖标注默认值；运行前 `Fly.BindSettings`；**不回写** main.cs |

标注示例见嵌入源码 `Services/Programs/Builtin/generic_swipe_main.cs`。

运行流水线：**HasText（swipeHints）→（当前朝向已标定识别区才）OCR → 若文案含倒计时（如 `7s后可继续上滑观看` / `N秒`）则先等待 N 秒 → 操作区执行**（未标定操作区则竖/横屏均用屏幕中央）。宽&gt;高为横屏用 `*Landscape`，否则 `*Portrait`；横/竖互不借用对方标定。

**触发后再识别：** 执行成功后 `coolUntil` **2.5s** + `Delay(800ms)`≈**3.3s** 内不会再次执行；且须提示文案先消失（`tipArmed`）后再次出现才会再滑。未命中时约每 **1s** 轮询一次 OCR/HasText。

| 设置键 | 含义 |
|--------|------|
| `ocrRegionPortrait` / `ocrRegionLandscape` | 竖/横屏识别区 |
| `actionRegionPortrait` / `actionRegionLandscape` | 竖/横屏操作区 |

**四向放置与两套键**：归一化矩形相对**当前显示**左上原点；内容随系统旋转的 App，正放标定在反放下通常仍对齐。

| 放置 | 宽高 | 选用 |
|------|------|------|
| 竖正 / 竖反 | 高≥宽 | `*Portrait` |
| 横正 / 横反 | 宽&gt;高 | `*Landscape` |

请在竖正、横正下标定。界面锁定方向或不随旋转重排的 App，反放可能不准（不为此再存四套键）。日志可看 `Fly.DisplayRotation`（0/90/180/270）排查。

## 3. 语言与执行

| 语言 | 执行方式 |
|------|----------|
| C# | 进程内编译运行（手机）；模板见 `ProgramCatalog` |
| JavaScript | Jint；API 为 camelCase（如 `Fly.hasText`） |
| 其它 | 脚手架为主，设备控制以 C#/JS 为准 |

## 4. Fly API 摘要

| API | 作用 |
|-----|------|
| `Ready` / `OpenSettings()` | 无障碍是否就绪；引导打开设置 |
| `GetBool` / `GetStringList` / `GetAction` / `GetRect` / … | 读本程序设置 |
| `IsLandscape` / `DisplayRotation` / `CurrentOcrRegionKey` / `CurrentActionRegionKey` | 横竖选键；Rotation 仅诊断；当前区域键 |
| `OcrSettingRegion` | 对识别区 OCR（可省略键，按朝向） |
| `PerformActionInRegion` | 在操作区内执行（可省略键，按朝向） |
| `HasText` / `Ocr` / `DrawRect` / `Swipe*` / … | 同前 |

**坐标：** 归一化 `0..1`；OCR/DrawRect 像素与 `ScreenWidth/Height` 同源。矩形设置存归一化 `x,y,w,h`。

### OCR 注意

- 截屏约 **900ms** 缓存；截屏前撤 DrawRect。
- 识别区：正交 **0/90/180/270°**，仍空则 **±15/±30°** 倾斜；日志 `OCR hit rotation=/tilt=`。
- 安全窗口 `code=6`、限流 `code=3` 见宿主警告。

## 5. 程序共享

同 Id **覆盖**对端源码；内置 Id 共享时对方也会得到同 Id（仍为内置元数据视接收端 EnsureBuiltin）。

## 6. 相关工程

| 路径 | 说明 |
|------|------|
| Core `ProgramSettings*` / `Builtin/` / `Fly` | Schema、settings.json、内置脚本、API |
| Maui `ProgramSettingsPage` / Android `FlyScreenRegionCalibrator` | 设置 UI、触屏标定 |
| Win/Desktop `ProgramSettingsWindow` | 设置 UI（标定提示去手机） |

## 7. 修订记录

| 日期 | 说明 |
|------|------|
| 2026-10-28 | 内置源 v5：OCR/文案含 `Ns`/`N秒` 时等满再操作；说明触发后再识别间隔 |
| 2026-10-27 | 调试框按归一化存储，旋转后 Relayout；内置源 v6 |
| 2026-10-27 | 四向放置说明：竖反/横反共用竖/横两套键；日志带 DisplayRotation；内置源 v5 |
| 2026-10-27 | 旋转后刷新屏尺寸/调试框与日志浮层位置；内置源 v4 |
| 2026-10-27 | 调试OCR 双框变色；启用OCR 置前；内置源 v3 |
| 2026-10-27 | 竖/横屏四区域表格标定；运行按朝向选用 |
| 2026-10-27 | 识别区 / 操作区分按钮独立标定 |
| 2026-10-27 | 程序设置 / 区域标定 / 内置通用脚本 / OCR 多角度 |
| 2026-10-27 | OCR：截屏前撤 DrawRect；空结果试旋转 |
| 2026-10-27 | DrawRect：双路径加窗、仅描边 |
| 2026-10-27 | 首版 Fly / OCR / 程序共享 |
