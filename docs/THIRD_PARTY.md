# 第三方依赖与授权说明

本项目源码以 **Apache License 2.0**（见仓库根目录 [`LICENSE.txt`](../LICENSE.txt)）开源。  
下文区分：**开源依赖**、**可合法用于开源应用的专有依赖**，以及 **不应再分发的专有内容**。

完整清单会随依赖变更更新；若遗漏请提 Issue。

---

## 1. 本项目开源范围

| 内容 | 许可 |
|------|------|
| `src/`、`mobile/`、`tests/`、`docs/`、`build/` 等自研代码与文档 | Apache-2.0 |
| 鸿蒙工程 `oh-package.json5` 的 `license` 字段 | Apache-2.0（与根许可证一致） |

使用者可按 Apache-2.0 复制、修改、再分发；保留版权与许可声明即可。  
**自研代码开源 ≠ 所有依赖都必须是 OSI 开源**：应用可以合法依赖专有 SDK，只要遵守对方条款并在文档中如实说明。

---

## 2. 开源第三方

| 组件 | 许可 | 用途 | 位置 / 引用 |
|------|------|------|-------------|
| .NET / .NET MAUI | MIT | 运行时与跨平台 UI | NuGet |
| Avalonia / Avalonia.Desktop / Themes.Fluent | MIT | Mac / Linux 桌面 UI | NuGet |
| Avalonia.Fonts.Inter | SIL OFL | 嵌入 UI 字体 | NuGet |
| AvalonEdit / AvaloniaEdit | MIT | 用户程序源码编辑器 | NuGet |
| Jint | BSD-3-Clause | 用户程序 JavaScript | NuGet |
| Roslyn（`Microsoft.CodeAnalysis.CSharp.Scripting`） | MIT | 用户程序 C# 脚本 | NuGet |
| Monaco Editor | MIT | MAUI 程序编辑器（Web） | `src/FlyScreen.App.Maui/Resources/Raw/wwwroot/vs/` |
| AndroidX / Material / Gson / Kotlin 协程等 | Apache-2.0 | Android Kotlin / MAUI Android | Gradle / NuGet |
| System.Drawing.Common | MIT | Windows 图像 / GDI | NuGet |
| xUnit / 测试 SDK | Apache-2.0 | 单元测试 | NuGet |
| Virtual Display Driver（MTT）离线安装包 | MIT | Windows 虚拟副屏 | `tools/VddOffline/*.exe`（进发布包） |

再分发含 **Monaco** 或 **VDD** 二进制时，请保留其版权与 MIT 许可文本。仓库已附：

| 文本 | 路径 |
|------|------|
| Monaco MIT | [`third_party/licenses/MIT-Monaco-Editor.txt`](../third_party/licenses/MIT-Monaco-Editor.txt)、`Maui/.../wwwroot/LICENSE-Monaco.txt` |
| VDD MIT | [`third_party/licenses/MIT-Virtual-Display-Driver.txt`](../third_party/licenses/MIT-Virtual-Display-Driver.txt)、`tools/VddOffline/LICENSE.txt` |
| Noto Sans SC OFL | [`tools/linux-fonts/OFL.txt`](../tools/linux-fonts/OFL.txt) |

Linux 正式包（含 `Fonts/`）会打成 **zip**，内含可执行文件、`Fonts/`、`Licenses/`。

Mouse without Borders 仅为产品灵感，**未纳入其代码**。

---

## 3. 专有依赖（开源项目可以使用）

### Google ML Kit Text Recognition（含中文）— 正式功能，默认启用

| | |
|--|--|
| 用途 | Android 用户程序 `Fly.Ocr` / 区域 OCR（重要功能） |
| 性质 | **专有** SDK + 端侧模型，**不是** OSI 开源库 |
| 能否用在开源 App | **可以**。Google 允许将 ML Kit 集成进免费或商业应用并随 APK 分发，须遵守 [ML Kit Terms](https://developers.google.com/ml-kit/terms) |
| 不能做什么 | 不能把 ML Kit / 模型当成 Apache-2.0「再开源」；禁止逆向提取模型源码等（见条款） |
| 本仓库态度 | **保留并默认启用**；自研对接代码仍按 Apache-2.0；构建时从 Google Maven / NuGet 拉取专有二进制 |

说明：许多开源 App 都采用「自研代码开源 + 专有 OCR/地图/推送 SDK」模式。本项目开源的是 **玄鸟自己的代码**；用户/二次分发者使用 OCR 功能时，同时受本项目 Apache-2.0 与 Google ML Kit 条款约束。

可选关闭（仅当你明确需要「不捆绑任何 Google ML 专有包」的构建时）：

```powershell
dotnet build src\FlyScreen.App.Maui\FlyScreen.App.Maui.csproj -c Release -f net10.0-android -p:FlyEnableMlKit=false
```

默认 `FlyEnableMlKit=true`。关闭后 `Fly.Ocr` 不可用，无障碍其它能力不受影响。

---

## 4. Linux 中文字体

| 做法 | 是否允许 | 说明 |
|------|----------|------|
| 安装系统包 `fonts-noto-cjk` / `noto-fonts-cjk` 等 | ✅ | 推荐；由发行版提供 |
| 随包附带 **Noto Sans SC**（`tools/linux-fonts/NotoSansSC-Regular.otf`，SIL OFL）到 `Fonts/` | ✅ | **默认已实施**；许可见同目录 `OFL.txt`；重拉：`docs/linux_fonts/fetch-noto-sc.ps1` |
| 复制 Windows `msyh` / `simhei` 到 Linux | ❌ | 专有字体 EULA 禁止再分发；**已屏蔽** |

仅引用本机**已安装**字体族名一般不构成再分发；本项目不再主动复制微软字体文件。

---

## 5. 与 README 鸣谢的关系

- [README.md](../README.md)「特别鸣谢」区分开源组件与专有依赖。
- ML Kit 作为正式 OCR 能力列出，并注明专有条款（不以「开源项目」表述）。
- 本文件为授权与合规的权威说明。
