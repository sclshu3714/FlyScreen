# 玄鸟文档索引

| 文档 | 说明 |
|------|------|
| [ROADMAP.md](ROADMAP.md) | **多端迭代总规划**（周期、阶段、Win 零回退） |
| [status.md](status.md) | **各端完成情况表**（以 Windows 为基准） |
| [clipboard.md](clipboard.md) | **共享剪贴板**：宣告/粘贴行为与各端格式对标表 |
| [device-matrix.md](device-matrix.md) | 设备形态（手机/平板/电视/桌面/鸿蒙电脑）× 功能矩阵 |
| [protocol.md](protocol.md) | 协议版本与能力位协商概要 |
| [compatibility.md](compatibility.md) | 产品版本、协议兼容、分发包命名 |
| [runbooks/regression-windows.md](runbooks/regression-windows.md) | Windows stable 回归门禁清单 |
| [runbooks/release-windows.md](runbooks/release-windows.md) | Windows 发版步骤 |
| [runbooks/build-macos.md](runbooks/build-macos.md) | macOS（W1）工程与构建 |
| [runbooks/build-linux.md](runbooks/build-linux.md) | Linux（W2）GUI / CLI / Daemon |
| [runbooks/build-android.md](runbooks/build-android.md) | Android Kotlin 骨架（W3）；日常请用 MAUI |
| [runbooks/build-harmony.md](runbooks/build-harmony.md) | 鸿蒙（W5）手机/平板 + 电脑 |
| [runbooks/build-maui.md](runbooks/build-maui.md) | **MAUI 跨平台 UI（W7）**：Win/Android 触控板、无障碍、APK 签名、发布 |
| [runbooks/programs-android.md](runbooks/programs-android.md) | **用户程序 / Fly API**：无障碍读写界面、OCR、DrawRect、程序共享 |
| [../build/README.md](../build/README.md) | **VS 发布项目**：`build/FlyScreen.Publish.*` 右键生成即发布各端 |

当前产品仍以 **Windows 桌面（WPF）** 为唯一 stable 交付；Mac / Linux / Android / 鸿蒙 / MAUI 按 ROADMAP 分期，独立工程与独立安装包。

**UI 同构（Win / Avalonia Desktop / MAUI）：** 侧栏七页（基础·网络·共享·安全·高级·关于·帮助）、彩色 Path 图标、安全密钥在安全页（生成按钮在标题工具栏）、高级顶栏功能/行为/穿边三列（窄屏堆叠）、共享传输列表列对齐、关于/帮助为页内内容；三端均有**程序**编辑器。Linux GUI 细节见 [runbooks/build-linux.md](runbooks/build-linux.md)。进度见 [status.md](status.md)。共享剪贴板格式对标见 [clipboard.md](clipboard.md)。
