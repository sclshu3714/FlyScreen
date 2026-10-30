# MTT / Virtual Display Driver 离线修复包

供开发人员维护清单与拉取脚本。本目录**不存放**安装包二进制。

## 共用目录

**唯一二进制源**：`tools/VddOffline\`（工具资源；`src` 保持纯代码）。

| 端 | 如何引用 | 进发布产物 |
|----|----------|------------|
| WPF Win | `..\..\tools\VddOffline\**\*` | setup + VDDControl + README |
| Maui-Win | `..\..\tools\VddOffline\**\*`（Windows TFM） | 同上 |
| Mac | 仅 `..\..\tools\VddOffline\README.txt` | 仅 README |
| Linux | 仅 `..\..\tools\VddOffline\README.txt` | 仅 README |
| Maui Android | — | 不复制 |

`docs/mtt_vdd` 不进任何端的发布包。

## 基本离线包（写入 tools\VddOffline）

| 文件 | 来源 | 用途 |
|------|------|------|
| `Virtual.Display.Driver-v25.05.03-setup-x64.exe` | 25.5.2 | **静默安装**（`/VERYSILENT`） |
| `VDDControl-v25.05.03.exe` | 25.5.2 | 安装后拉起管道 |

不随包：`Driver.Only` / Signed-Driver / 音频 / `VDD.Control` 大包（setup 或 winget 失败时，程序可按需在线下载 Driver.Only）。

可选：将 `nefconw.exe` 放到 `tools\VddOffline\nefcon\`；缺失时回退 `pnputil`。

## 程序静默修复顺序（仅 Windows 主机）

1. 离线 `*setup*.exe` → `/VERYSILENT`
2. `winget install … --silent --disable-interactivity`
3. 按需下载 `Driver.Only` → `nefcon` 或 `pnputil`
4. 启动 `VDDControl*.exe`（必要时在线准备 `VDD.Control`）
5. 管道就绪后 `SETDISPLAYCOUNT` + `DisplaySwitch /extend`
6. **仅环境真正通过**才写入本程序安装标记

## 拉取

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File docs\mtt_vdd\fetch-offline-pack.ps1
```

写入 `tools\VddOffline\`。说明：25.7 起官方改为便携 Control、不再发 setup；静默因此保留 25.5.2 的 setup-x64。
