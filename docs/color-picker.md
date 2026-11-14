# 颜色拾取器（本机）

| 项 | 内容 |
|----|------|
| 文档版本 | 1.1 |
| 状态 | 已编码（本机取色 + 工具页内嵌工作区；逻辑在独立工具包） |
| ToolId | `color-picker` |
| 统计日 | 2026-11-10 |

相关：[tools.md](tools.md)（工具宿主 / 工作区导航权威规格）。跨屏取色仍见 tools.md §8.6，**本期不做**。

---

## 1. 行为

1. 工具页安装 `color-picker`（Bundled 标记安装，与共享设备同一安装器）。
2. 单击启动 → **不弹窗**；工具页出现导航条：`工具` | `颜色拾取器`，内容区嵌入拾色面板。
3. 点「工具」回目录，页签保留；关页签或 ⋯「停止」结束会话。
4. 面板内「颜色拾取器」：进入滴管；移动实时采样并刷新全部格式；单击锁定；再点按钮 / 右键 / Esc 取消。
5. 格式行单击复制到剪贴板。

## 2. 颜色格式

HEX、HEX AARRGGBB、RGB/RGBA、HSL/HSLA、HSV/HSB、HWB、CMYK、LAB、LCH、XYZ、CSS `rgb()`/`hsl()`、Android Color、Flutter Color、Swift UIColor、Decimal RGB。

## 3. 平台

| 端 | 卡片 | 滴管取色 |
|----|------|----------|
| Win WPF / Maui-Win | 是 | Win32 钩 + GDI 采样 |
| Avalonia Mac/Linux | 是 | 全屏透明层 + `CopyFromScreen`（依赖系统图形库） |
| Android / 手机平板 | **不显示**（清单仅 Desktop） | — |

## 4. 代码落点

| 层 | 路径 |
|----|------|
| ToolSdk | `IColorPickerService` / `IScreenColorSampler` / `RgbaColor` |
| 工具包 | `FlyScreen.Tool.ColorPicker`（`Module`、`ColorPickerService`、格式转换、`ColorPickerLocalization`、`manifest.json` `icon=tool-color-picker`） |
| Core | `ToolWorkspace`、`ToolIds.ColorPicker`、按 `EntryAssembly`/`EntryType` 加载 |
| Win UI | `FlyScreen.Tool.ColorPicker.Ui.Win`（`ColorPickerPanel` + `ColorPickerWinUiContributor`）；滴管 `FlyScreen.Windows/Tools/Win*` |
| Desktop UI | `FlyScreen.Tool.ColorPicker.Ui.Desktop` |
| Maui UI | `FlyScreen.Tool.ColorPicker.Ui.Maui`（Windows 滴管；其它 TFM 提示不支持） |
| 宿主 | 仅 `ToolUiDiscovery` + 注入采样器；无面板源码 |
