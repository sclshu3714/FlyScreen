# 工具系统需求（实现规格）

| 项 | 内容 |
|----|------|
| 文档版本 | 1.12 |
| 状态 | 需求规格 + **真包骨架已编码**：`Tools/` 落在程序目录；Bundled/Cache/Import/GitHub 索引；ALC 加载；快捷栏；`build/Pack-FlyTools.ps1` |
| 读者 | 实现本功能的开发者 / AI 代理 |
| 统计日 | 2026-11-10 |

**关联文档（业务细节，冲突时以本文「工具宿主 / 清单 / 标题栏 / 通用协议 / 第一期工具清单」为准）：**

- [shared-devices.md](shared-devices.md) — 共享设备媒体格式、虚拟设备、与投影声音冲突等技术细节（**会话建立流程以本文 §6.1 为准**；该文档旧「装完即开」叙述待对齐）
- [color-picker.md](color-picker.md) — 本机颜色拾取器（滴管、格式、内嵌工作区）；跨屏取色仍见 §8.6
- [extended-display.md](extended-display.md) — 投影会话、采集、互斥、声音同步（**本期仍内置于主程序**，行为仍适用）
- [protocol.md](protocol.md) — 现有消息与 Caps；工具信封落地后须回写
- [mtt_vdd/README.md](mtt_vdd/README.md) — VDD 离线包（投影迁工具时再用）

**用词：**

| 词 | 含义 |
|----|------|
| MUST | 必须实现；缺了不算完成 |
| MUST NOT | 禁止 |
| SHOULD | 默认要做；有明确理由可延期并在 PR 说明 |
| 主机 / 宿主 | 玄鸟主程序（连接、侧栏、标题栏、工具加载） |
| 工具 | 可安装的能力单元，有唯一 `ToolId` |
| 跨屏工具 | 需要至少两端参与会话的工具（共享设备、跨屏取色等；投影迁工具后亦属此类） |
| 发送端 / 接收端 | 同一跨屏工具内的两种角色；**不是**两个 ToolId，而是同一安装包在会话中的分工 |
| 申请端 | 需要使用共享扬声器 / 麦克风 / 摄像头的一端；本端操作含安装、启动、停止、卸载 |
| 接收端 | 被申请使用其真实扬声器 / 麦克风 / 摄像头的一端；本端操作只需**安装**（收到申请后同意或拒绝） |
| `.flytool` | 单文件工具包（见 §5.3）；安装/分享/导入的唯一交付物 |
| 已知摘要 | 发布时公开的 `contentSha256`（随 Release / 仓库校验文件）；开源项目以此防篡改，**不用**私有「工具根密钥」（见 §5.5） |
| 电脑 | `DeviceClass=Desktop`（含鸿蒙电脑目标态） |
| 手机 | `DeviceClass=Phone` 或 `Tablet` |

**范围声明：** 键鼠穿边、布局、剪贴板、文件传输、用户「程序」页 **不是** 工具，MUST NOT 迁入工具页。**本期（v1.7）暂不把投影抽成独立工具**——投影继续走主程序内置路径（见 [extended-display.md](extended-display.md)）。工具页**优先交付**合并后的共享扬声器 / 麦克风 / 摄像头（`shared-devices`）。

---

## 1. 目标与非目标

### 1.1 目标

1. 侧栏增加「工具」页：发现、安装、卸载、（部分）修复本机工具。
2. 工具可按设备形态（电脑 / 手机）提供不同清单项与不同标题栏操作。
3. 每个已启用且需要入口的工具，向标题栏注入 **恰好一个** 菜单项；多操作用下拉；按钮支持多视觉状态（含运行中与悬停）。
4. 需要跨机通信的工具走 **通用工具信封**（见 §8）；不靠为每个工具新增一串 `MessageType` 枚举值（过渡期除外，见 §8.5）。
5. **本期优先交付 `shared-devices`：** 扬声器 / 麦克风 / 摄像头合并为一个工具；申请端与接收端都装后，申请端双击启动 → 选布局内**任意已装工具且具备扬声器/麦克风/摄像头之一的设备**（电脑、手机、平板均可作外设）→ 确认后向接收端申请，同意后即可使用（见 §6.1）。
6. Win / Maui / Desktop 同一信息架构，同一任务内改齐，禁止只改一端。
7. **跨屏工具两端都装：** 申请端与接收端 MUST 均已安装同一 `ToolId`，否则 MUST NOT 建立该工具会话（见 §2.1）。
8. **工具可分享到布局内已连接设备**（全部或勾选），分享时 MUST 按接收端 DeviceClass/Platform 过滤（见 §4.1）。
9. **单文件交付：** 每个工具 MUST 打成一个 `.flytool`；支持工具页安装、导入安装、外部打开安装、对端分享安装（见 §5.3）。
10. **工具安全（开源友好）：** 公开哈希 + 确认 + 加载复验；**MUST NOT** 依赖私有根密钥（见 §5.5）。
11. **无障碍隔离（致命级）：** 无障碍服务 **MUST NOT** 交给工具代码；将来投影迁工具后若需无障碍回控，仍由**宿主内置路径**代注（见 §5.5.9 / §5.5.10）。

### 1.2 非目标（本期 MUST NOT）

- **把投影（复制+扩展）抽成独立工具 `projection`**（后期再迁；本期投影保持主程序内置，见 §6.2）。
- 把扬声器 / 麦克风 / 摄像头拆成三个独立 ToolId 或三个安装包（MUST 合并为 `shared-devices`）。
- 装完共享设备工具后**无需申请、随系统打开虚拟设备即自动建会话**的旧流程（MUST 改为 §6.1 申请 / 授权）。
- 给**所有**工具在工具页统一放通用「启动 / 停止」（`shared-devices` 例外：申请端在工具卡片上提供启动/停止，见 §3.2 / §6.1；其它工具仍按各自入口）。
- 用户程序（侧栏「程序」）合并进工具页。
- 工具自行操作 HWND / 原生标题栏控件绕过宿主（MUST 经 §7 注入 API）。
- 卸载 ToDesk / Oray 等第三方虚拟显示驱动。
- 用浮动 `master` 分支目录作为唯一安装源而不做版本与校验（见 §5）。
- 把键鼠、心跳、布局主路径塞进工具信封。
- 将发送端与接收端拆成两个 ToolId 或两个安装包（跨屏角色 MUST 合在同一工具内）。
- 分享时静默强制安装且无接收端确认（MUST 经用户确认，见 §4.1）。
- 用「散落多文件目录」作为对外交付形态（开发目录可以，发布/分享 MUST 为单一 `.flytool`）。
- 宣称「工具在 OS 级沙箱中绝对无法作恶」（宿主做哈希门禁与权限明示；驱动类仍受系统 UAC，见 §5.5）。
- 设计依赖「只有官方知道的工具根密钥 / 闭源信任根」——本仓库为开源，人人可编译宿主；信任材料 MUST 可公开审计。
- 默认静默安装**摘要未知**的包（导入/分享时 MUST 展示哈希并经用户确认，见 §5.5.3）。
- **把无障碍、Fly 设备控制、键鼠注入 API 开放给工具模块**（即使用户点了同意、即使哈希正确也不行，见 §5.5.9）。
- 用「安装工具」替代或绕过系统无障碍授权流程。

---

## 2. 工具分类（实现时必须区分）

| 类型 | 定义 | 跨机协议 | 例子 |
|------|------|----------|------|
| A. 独立增强工具 | 不装不影响键鼠；装了才有该能力 | 若需跨机：通用信封 `Tool:<id>` | `shared-devices`；本机 `color-picker`（跨屏取色后期） |
| B. 独立会话工具 | 不装无该会话能力；装后有完整业务 UI/协议处理 | 可继续使用已有专用消息并由工具模块注册，或迁信封（见 §6） | `projection`（**后期**迁入；本期非交付） |
| C. 本机组件依赖 | 不单独占「跨机能力」；被某工具依赖 | 无独立跨机协议 | VDD：归属将来的 `projection`；本期不随工具交付 |

实现 MUST：用清单字段 `kind`: `enhancement` | `session` | `component` 区分。  
`component` MUST NOT 单独向标题栏注入项（由所属会话工具注入）。  
清单 MUST 含 `crossScreen: true | false`（见 §2.1）。

### 2.1 跨屏工具：发送端与接收端（同一工具）

下列规则适用于所有 `crossScreen=true` 的工具（含共享设备、跨屏取色等；投影迁工具后亦适用）。

| 规则 | MUST |
|------|------|
| 单 Id | 发送端能力与接收端能力 MUST 打在 **同一个** `ToolId` / 同一个 `.flytool` 内，按本机 `DeviceClass` 与会话角色启用不同 feature |
| 两端安装 | 跨屏会话建立前，**参与双方** MUST 均已安装该 `ToolId`（版本兼容见下） |
| 能力探测 | 以握手 Caps 为准：增强类看 `Tool:<id>`。缺一侧 → MUST NOT 开始会话，UI MUST 提示「对端未安装 xxx」 |
| 角色 | 会话内再区分申请端/接收端、取色请求方/采样方等；**不是**安装两个工具 |
| 版本 | `schemaVersion` / 包 `version`：次版本 SHOULD 兼容；主版本不兼容时 MUST 拒绝会话并提示升级。分享时 SHOULD 带完整包，避免对端缺依赖 |

**共享设备（本期）：** 申请端与接收端均为同一 `shared-devices` ToolId；双方 MUST 均已安装。仅安装不等于可传媒体——MUST 由申请端启动并经接收端同意（§6.1）。

**投影（本期例外）：** 仍内置于主程序，**不**要求 `projection` 工具安装；迁工具后的两端都装规则见 §6.2（后期）。

---

## 3. 侧栏与工具页 UI

### 3.1 导航

- 文案键：`Nav.Tools` = 中文「工具」。
- 顺序 MUST：`… · 程序 · 工具 ·`（若有日志则工具在程序后、日志前）。
- 工程：`App.Windows`、`App.Maui`、`App.Desktop` 同步。

### 3.2 工具页行为

| 需求 | 规格 |
|------|------|
| 分组 | 按清单 `group` 显示。本期预置：`shared-devices`（共享设备）、`utilities`（含 `color-picker`）。`display`（投影）留给后期迁工具 |
| 筛选 | SHOULD：`全部` \| `未安装` \| `已安装` \| `需修复`（可后置；网格态用透明度/提示区分） |
| **展示形态** | **独立卡片**，左→右、上→下 **自适应换行**（Wrap）：每项 = 图标 + 标题 + 状态文案 + 一句话说明。MUST NOT 再做成右侧大按钮条的列表卡 |
| 卡片底色 | MUST 区分：**未安装** / **已安装** / **已启动** 三种背景；**安装中**另用安装底色，并在卡片背景上显示 **自左向右进度条** |
| 卡片字段 | MUST：图标、名称、状态文案、一句话说明；操作经单击 / 右键，不靠常驻按钮 |
| 操作（通用） | **单击** → 主操作（未安装则安装；已装且可启动则启动；运行中则停止）。**右键菜单** → 卸载（及分享/修复等）。导入 `.flytool` 可用页级入口（后期） |
| 操作（`shared-devices`） | 见下表；MUST NOT 给其它无关工具套用同一套「单击启动」 |
| 操作（`color-picker`） | 已装单击 → 打开**内嵌工作区**（非弹窗）；运行中停止仅 ⋯ / 关页签 |
| 使用中 | `shared-devices` / 有工作区工具运行中 MUST NOT 单击卡片停止（防误触），停止仅在 ⋯ 菜单；卸载前 MUST 结束该工具会话 |
| **工作区宿主** | 凡 `hasWorkspace=true`（或约定有主界面）的工具，主界面 MUST 嵌入工具页，MUST NOT 独立弹出为主窗。有已打开工具页时 MUST 显示导航条：首项「工具」（目录）+ 各打开页签（可关）。点「工具」回目录且**保留页签** |
| 禁止 | MUST NOT 在网格项上常驻并列「安装/启动/停止/卸载」按钮条 |

**`shared-devices` 网格项操作（MUST）：**

| 本端角色 | 已安装后 |
|----------|----------|
| 申请端（需要使用共享设备） | 单击 → 启动申请面板；运行中停止改走 ⋯ 菜单（避免误触）；⋯ / 右键 → 卸载 |
| 接收端（被申请提供真实设备） | 安装后待命；右键 → 卸载；**无**启动。收到申请弹窗（同意 / 拒绝，可「记住适配」） |
| 未安装（任一角色） | 单击 → 安装 |

同一台机器若既可申请又可被申请：单击仍走启动（作申请端）；接收申请与是否本机已启动无关，只要已安装即可应答。

### 3.3 按设备过滤可见工具

安装与列表展示前 MUST 用本机身份过滤：

```text
本机 DeviceClass ∈ 清单 deviceClasses
且 本机 Platform ∈ 清单 platforms（若清单省略 platforms 则仅按 DeviceClass）
```

不满足 MUST NOT 显示「安装」成功路径；可显示灰卡「本设备不可用」或直接隐藏（实现二选一，同一产品内 MUST 统一；推荐 Desktop 显示灰卡，Phone 隐藏不适用项）。

---

## 4. 安装 / 卸载 / 修复 / 分享（语义）

| 动作 | MUST |
|------|------|
| 安装 | 取得 `.flytool` → **§5.5 安全校验全部通过** → 按本机 rid 解压 → 写入工具目录与安装标记 → 状态=已安装 → 注册标题栏（若有）→ 更新握手 Caps。任一步失败 MUST 回滚，MUST NOT 留半安装 |
| 卸载 | 停止该工具会话 → 注销标题栏贡献 → 清除 Caps 中本工具相关项 → 卸组件 → 状态=未安装 |
| 修复 | 仅当 `supportsRepair=true` 且状态=需修复；执行与安装相同的静默修复管线 + 就绪检测；成功→已安装，失败保持需修复 |
| 分享 | 见 §4.1 |

安装状态 MUST NOT 进入 `BehaviorSync`。

检测 VDD / 驱动是否异常时 MUST 只读（禁止在检测阶段 `DisplaySwitch`、禁止建副屏）。

### 4.1 分享到布局内已连接设备

适用：本机已安装且 `crossScreen=true` 的工具。

| 步骤 | 规格 |
|------|------|
| 入口 | 工具卡片「分享到设备…」 |
| 可选目标 | 当前**电脑布局槽位中且已双向连接**的对端；可「全选可用」或多选 |
| 过滤（MUST） | 仅列出：`对端 DeviceClass ∈ 工具 deviceClasses` 且 `对端 Platform ∈ platforms` 的设备。手机不会出现在「仅 Desktop」工具的分享列表中 |
| 已安装跳过 | 对端已装 **相同 id 且 version ≥ 本机包版本** → 默认勾选「跳过」或列表标注「已是最新」；MUST NOT 强制降级覆盖，除非用户显式选「覆盖安装」 |
| 传输 | MUST 发送完整 `.flytool` 单文件（Bulk 或文件直传）；MUST NOT 只发 manifest；MUST NOT 发「解压目录重打无校验信息的 zip」（§5.5.5） |
| 接收端确认 | 对端 MUST 弹窗：工具名、版本、**完整 contentSha256**、是否命中已知发布摘要、来源机器、权限列表、设备是否匹配 → 用户确认后才安装 |
| 接收端安装 | 与本地导入相同管线（§5.5）；按**接收端** rid 解压；装完更新 Caps |
| 失败 | 不匹配的 DeviceClass、校验失败、用户拒绝、传输中断 → 明确错误；MUST NOT 半安装残留为「已安装」 |

分享 MUST NOT 发给：未连接、不在布局、DeviceClass/Platform 不支持、或无法解析身份的对端。

### 4.2 三种取得安装包的方式（无歧义）

| 方式 | 触发 | 行为 |
|------|------|------|
| A. 工具页安装 | 点「安装」 | 使用内置 `.flytool` 或按清单 URL 下载后走同一安装器 |
| B. 导入安装 | 点「导入…」或工具页空白处说明 | 用户选择本地 `.flytool` → 同一安装器 |
| C. 外部直接安装 | 双击 /「打开方式」关联 `.flytool`；或命令行参数传入路径 | 宿主启动或激活后走同一安装器；安装完成 SHOULD 打开工具页并选中该工具 |

对端分享到达后的安装 = 方式 B 的变体（文件来自网络临时目录），确认框多「来源对端」字段。

三种方式 MUST 共用 `ToolInstaller.InstallFromFlyToolFile(path)`，禁止分叉三套解压逻辑。

---

## 5. 工具清单与单文件包（`.flytool`）

### 5.0 设计目标

- **一个工具 = 一个文件**，避免分享/拷贝时漏 DLL、漏驱动、漏清单。
- 包内可含多 RID / 多 DeviceClass 实现；安装时只启用本机所需切片。
- 发送端与接收端逻辑同包，不拆成 `projection-host.flytool` / `projection-sink.flytool`。

### 5.1 清单字段（manifest，位于包内）

编码时模型字段名 MUST 与下表一致。示例：

本期主交付示例（`shared-devices`）：

```json
{
  "id": "shared-devices",
  "kind": "enhancement",
  "crossScreen": true,
  "group": "shared-devices",
  "nameKey": "Tool.SharedDevices.Name",
  "summaryKey": "Tool.SharedDevices.Summary",
  "schemaVersion": 1,
  "minAppVersion": "1.0.0",
  "deviceClasses": ["Desktop", "Phone", "Tablet"],
  "platforms": ["Windows", "Android", "macOS", "Linux"],
  "featuresByDeviceClass": {
    "Desktop": ["speaker", "microphone", "camera", "provide", "consume"],
    "Phone": ["speaker", "microphone", "camera", "provide"],
    "Tablet": ["speaker", "microphone", "camera", "provide"]
  },
  "roles": ["request", "authorize"],
  "supportsRepair": false,
  "capNames": ["Tool:shared-devices"],
  "titleBar": false,
  "permissions": ["camera", "microphone", "speaker", "virtualAudio", "peerNetwork"],
  "package": {
    "version": "1.0.0",
    "format": "flytool",
    "formatVersion": 1
  }
}
```

后期 `projection` 清单仍可含 `dependencies`（如 `virtual-display`）与 `featuresByDeviceClass` 的 `copy`/`extend`/`sink`；格式约束同下表。

### 5.2 字段约束

| 字段 | 约束 |
|------|------|
| `id` | 全局唯一；小写 kebab-case；与 Caps `Tool:<id>`（若使用）一致 |
| `kind` | `enhancement` \| `session` \| `component` |
| `crossScreen` | true：适用 §2.1 / §4.1；false：纯本机工具，无分享跨屏会话要求 |
| `roles` | 跨屏工具 MUST 含语义上的收发（`shared-devices`：`request`=申请端、`authorize`=接收端；其它可用 `send`/`receive`）；禁止拆包 |
| `deviceClasses` | 非空；取值同协议 `DeviceClass` |
| `featuresByDeviceClass` | 可选；缺省表示该端清单声明的能力全开。`shared-devices` 手机第一期 SHOULD 不含 `consume`。后期投影手机 MUST 不含 `extend` |
| `supportsRepair` | 默认 false；VDD 依赖为 true（后期） |
| `titleBar` | true 时 MUST 实现 §7；每个 `id` 最多一个标题栏贡献 |
| `icon` | 卡片图标键（如 `tool-color-picker` / `tool-shared-devices`）；宿主按键解析 BrandAssets 或默认矢量 |
| `entryAssembly` / `entryType` | `.flytool` 与内置清单共用；宿主按此进程内/ALC 加载 `IToolModule` |
| `capNames` | 安装且对本端启用后，握手 MUST 并入这些 Cap；增强类跨机工具用 `Tool:<id>` 见 §8 |
| `package.format` | MUST 为 `flytool` |
| `dependencies[].embeddedPath` | 组件 MUST 打进同一 `.flytool`，路径相对包根；MUST NOT 依赖用户再下第二份散文件 |

### 5.2b 本机安装目录（已实现）

桌面默认：`{AppContext.BaseDirectory}/Tools/`（**不**写入 `%AppData%\Roaming\FlyScreen\Tools`）。

| 子路径 | 用途 |
|--------|------|
| `Bundled/*.flytool` | 源包目录：内置 + 导入/远程下载（不再单独设 Cache） |
| `Installed/<id>/<ver>/` | 解压运行目录 |
| `tools-install.json` | 安装记录 |
| `tools-shortcuts.json` | 工具页快捷栏（4 槽） |
| `tools-index.cache.json` | 远程索引缓存 |

组包：`build/Pack-FlyTools.ps1` → `artifacts/flytools/` 并复制到各 App 输出 `Tools/Bundled`。亦可在 `FlyScreen.Setup` 勾选「插件工具 (.flytool)」一键产出 `FlyScreen-{ver}-flytools.zip`。契约工程：`FlyScreen.ToolSdk`；入口工程：`FlyScreen.Tool.ColorPicker` / `FlyScreen.Tool.SharedDevices`。

**卡片与 UI 解耦（已实现）：**

| 项 | 规格 |
|----|------|
| 卡片壳 | 宿主只渲染底色 / 进度 / 三点 / 安装卸载通用项；内容来自清单 + `IToolModule` |
| `icon` | manifest 字段（如 `tool-color-picker`）；`ToolCardRow.IconKey` 驱动三端图标，禁止按 `ToolId` DataTrigger |
| 三点菜单 | 通用 Install/Start/Uninstall + `GetMenuContributions`；工具特有项不得写在 MainWindow 分支 |
| 平台 UI | 工具卫星工程 `FlyScreen.Tool.*.Ui.{Win\|Desktop\|Maui}` 实现面板/弹窗与 `IToolUiContributor`；宿主 `ToolUiDiscovery` 扫描注册，**主程序不含工具业务 UI** |
| 加载 | `ToolManifest.EntryAssembly` / `EntryType` 驱动进程内注册与 Bundled 保底加载 |
| 多语言 | 工具自带 `IToolLocalization`（至少 zh-CN/en-US）；`Loc.RegisterPlugin` 接入；**MUST NOT** 再向主程序 `Loc.cs` 加工具专用键 |

### 5.3 `.flytool` 文件格式（实现规格）

| 项 | 规格 |
|----|------|
| 扩展名 | `.flytool`（MUST） |
| 容器 | ZIP（可无 Store 或 Deflate）；文件头可用魔数注释或首文件 `flytool.json` 标识 |
| 根文件 | MUST 含 `manifest.json`（即 §5.1 内容） |
| 入口 | `entryAssembly` + `entryType`（实现 `IToolModule`） |
| 目录约定 | `runtimes/<rid>/…` 存放该 RID 的托管/原生实现；`components/<dep-id>/…` 存放依赖（如 VDD 离线安装器）；`assets/` 可选图标与文案 |
| RID | 与发布一致，如 `win-x64`、`android-arm64`、`osx-arm64`、`linux-x64` |
| 完整性 | MUST 含包裹级 `contentSha256`；安装前 MUST 校验（§5.5） |
| 可选分离签名 | SHOULD 支持维护者 **公开** 的 minisign/GPG 附加签名（公钥在仓库内可审计）；**不是**私有产品根密钥 |
| 单文件 | 对外复制、分享、导入、外部分发 MUST 只给这一个文件 |

安装算法（伪代码，实现 MUST 等价；**安全步骤不可删减或后置**）：

```text
Open .flytool
Compute contentSha256 (§5.5.1)
ResolveTrust (§5.5.2)：KnownRelease | Bundled | Unknown
If KnownRelease/Bundled：hash 必须等于已公布/内置期望值，否则中止（包被篡改）
If Unknown：不得静默安装；同意框强制展示完整哈希与警告
Optional VerifyMaintainerDetachedSignature (§5.5.1) 若包附带且用户/策略要求
Read manifest → permissions、id、version…
ShowInstallConsent (§5.5.3) — 拒绝则中止
DeviceClass/Platform 检查
Extract runtimes/<localRid>/ → Tools/<id>/<version>/
Re-hash extracted == 记录值
Component installers（VDD 等）— OS UAC
Write install record (id, version, path, contentHash, trustLevel)
Load only from that path
```

### 5.4 发布与安装源

- SHOULD 使用 GitHub **Release** 上的单个 `.flytool`，**同一 Release** 附 `SHA256SUMS` 或 `*.flytool.sha256`（纯文本，可审计）。
- MUST NOT 仅依赖 `tree/master/tools` 网页目录作为程序下载地址。
- SHOULD 随主程序内置第一期 `.flytool`，并在宿主内嵌这些包的 **期望 contentSha256**（与开源仓库中校验文件一致，人人可核对）。
- 仓库 `tools/` 保留组包源；组包脚本 MUST 打印/写出 sha256；发版 MUST 把校验文件与包一并上传。
- 本项目为 **开源**：任何 fork 可自行组包；用户信任的是「自己核对过的哈希 / 自己编译的宿主内嵌哈希」，不是无法公开的密钥。

### 5.5 工具安全（开源模型：公开哈希 + 确认 + 复验）

本项目是开源软件，**MUST NOT** 设计「玄鸟工具根私钥 / 闭源信任根」——私钥一旦进 CI 或文档，对开源仓库没有保密意义，且与「谁都能编译宿主」矛盾。

安全目标改为：

1. **发现篡改**（哈希不一致则拒装/拒载）；  
2. **用户知情**（未知来源必须看到完整哈希并确认）；  
3. **缩小权限面**（manifest 权限 + 宿主 API 门控）；  
4. **可选**维护者分离签名（公钥放在仓库里，任何人可验证，任何人也可 fork 换自己的公钥——这是开源常态）。

手机与电脑同一套逻辑。

#### 5.5.1 内容哈希（主防线）

| 规则 | MUST |
|------|------|
| 算法 | SHA-256；对包内受保护条目按**稳定路径序**规范化后计算，算法与路径序写死在代码与本文，便于复现 |
| 包内声明 | `manifest.contentSha256` 或并列 `SHA256SUMS`；安装时重算，必须自洽 |
| 篡改 | 改 DLL/脚本/驱动任一字节 → 哈希变 → 与期望值不符则 **拒绝安装** |
| 已装后 | 每次**加载**前对安装目录复算，与安装记录不一致 → MUST NOT 加载，标「已损坏」，提示重新获取发布包 |
| 传输后 | 下载或分享落盘后、安装前再算一次 |

#### 5.5.2 信任等级（无根密钥）

| trustLevel | 含义 | 安装策略 |
|------------|------|----------|
| `Bundled` | 宿主安装包内自带的工具，期望哈希**编译进宿主**（源码中公开） | 哈希匹配 → 可装；不匹配 → 拒装（安装包被改） |
| `KnownRelease` | 哈希出现在：本机缓存的 Release `SHA256SUMS`、或仓库发布的校验文件、或用户已保存的「信任摘要列表」 | 匹配 → 按正常同意框安装 |
| `Unknown` | 哈希未出现在任何已知列表（自行组包、来路不明、对端分享的新包等） | MUST 展示完整 sha256 + 强警告；用户明确确认才可装；MUST NOT 默认勾选确认 |

**如何得到 KnownRelease（开源可审计）：**

- 发版时上传 `.flytool` + `SHA256SUMS`；宿主更新检查 MAY 拉取同 tag 的校验文件（HTTPS）。  
- 或文档/仓库路径如 `tools/checksums/<id>-<version>.sha256` 随源码公开。  
- 用户可手动「添加信任摘要」（粘贴 sha256），写入本机，不上传。

**可选分离签名（非必须）：**

- 维护者可用 **minisign / GPG** 对 `contentSha256` 做分离签名；**公钥文件放在仓库**（如 `build/flytool-pubkeys/`），私钥由维护者自行保管，**MUST NOT** 提交进 git。  
- 宿主 MAY 校验该签名作为附加提示（「维护者密钥 A 已确认」）。  
- Fork 可替换公钥；用户编译谁的源码，就信任谁写入的公钥/内嵌哈希——这是开源预期行为。  
- **禁止**把「有签名」写成闭源产品式根密钥神话。

**玄鸟连接安全密钥：** 只保护设备配对与信道，**MUST NOT** 用来证明 `.flytool` 未被篡改。

#### 5.5.3 安装同意与权限明示

manifest MUST 声明 `permissions`（可空）。

**允许声明（同意书 MUST 展示含义）：**

| permission | 含义 |
|------------|------|
| `titleBar` | 可在标题栏显示入口 |
| `camera` | 经系统 Camera API 使用摄像头（须系统授权） |
| `microphone` | 经系统录音 API（须系统授权） |
| `speaker` | 播放音频 |
| `screenCapture` | 经 **MediaProjection** 等系统录屏授权采集（须每次/系统同意）；**不是**无障碍截屏 |
| `virtualDisplay` | 电脑端安装/操作虚拟显示驱动 |
| `virtualAudio` | 电脑端虚拟声卡 |
| `peerNetwork` | 经玄鸟已有连接发工具信封 |
| `fileSystem.inbox` | 读写玄鸟收件箱类目录 |
| `networkStream` | 可连接你添加的视频地址与局域网摄像头/NVR；不会扫描网段 |
| `localFile.open` | 经系统文件选择器读取你选中的视频文件 |
| `localMediaListen` | 仅媒体入流（RTP/SIP）可在授权下绑定本机端口；不得承载工具信封 |

**禁止出现在工具 manifest 中的权限（出现则 MUST 拒绝安装整包）：**

| 禁止名（示例） | 原因 |
|----------------|------|
| `accessibility` / `a11y` / `injectInput` / `flyDeviceControl` | 手机无障碍可点击、读界面、滑动；一旦交给工具，篡改或恶意工具危害不可接受 |
| `readAllUi` / `globalGesture` | 同上 |
| 任何暗示驱动「玄鸟键鼠注入」服务的声明 | 无障碍仅服务主程序内置键鼠与「用户程序」 |

同意框 MUST 显示：工具名、版本、**contentSha256**、trustLevel、权限列表、来源；并固定提示：**「工具包本身不能使用无障碍。投影若开启远程控制，由玄鸟主程序在已授权无障碍时代为注入，工具代码拿不到无障碍。」**  
`Unknown` 时额外警告哈希未核对。  
用户取消 → 不安装。  
Android：摄像头/麦克风/录屏仍走系统运行时权限。

#### 5.5.4 各安装来源

| 来源 | 策略 |
|------|------|
| 宿主内置 | `Bundled`：与内嵌期望哈希比对 |
| 工具页下载 Release | HTTPS 下载包 + 同 Release 校验文件 → `KnownRelease` 或失败 |
| 用户导入 / 外部打开 | 计算哈希；命中已知列表则为 Known；否则 Unknown + 强制确认 |
| 对端分享 | 同导入；**配对不能跳过哈希展示与确认** |

| 开发者选项 | 规格 |
|-------------|------|
| 「跳过已知摘要检查」 | 默认 **关闭**；仅调试；开启时每次红色警告。正式场景 SHOULD 保持关闭 |

#### 5.5.5 分享路径

- 发送端 MUST 分享安装时保存的**原始 `.flytool` 字节**（或带相同 contentSha256 的发布原件），MUST NOT 把解压目录随意重打成新包（除非重新计算并如实变成 Unknown）。  
- 接收端 MUST 走 §5.5 全流程。  
- 若发送端包为 Unknown 且接收端未确认，不得安装。

#### 5.5.6 加载与权限面

| 规则 | MUST |
|------|------|
| 加载路径 | 仅 `%AppData%/FlyScreen/Tools/<id>/<version>/`（或各端等价目录） |
| 复验 | 加载前 contentHash 一致 |
| API | 仅 `IToolHostServices` 中的**白名单**方法；未声明权限 MUST 拒绝；§5.5.9 所列能力 MUST 永不挂到该接口 |
| 卸载 | 删目录与记录；停会话；清 Caps/标题栏 |

VDD 等系统驱动：同意书 MUST 写明将安装系统组件；仍受 OS UAC；哈希只保证与发布声明一致。

#### 5.5.7 威胁与对策

| 威胁 | 对策 |
|------|------|
| HTTPS 下载被替换 | 与公布 sha256 比对失败 → 拒装 |
| 有人改包再发给你 | 哈希变化 → 拒装或 Unknown 警告 |
| 局域网配对推送恶意工具 | 哈希 + 强制确认；配对不证明包内容 |
| 安装后改磁盘文件 | 加载复验 → 拒载 |
| 开源无根密钥 | 公开校验和 + 用户确认 |
| **恶意/被改工具想借无障碍操作用户手机** | **§5.5.9：工具拿不到无障碍**；哈希正确也一样拿不到 |
| 用户误以为装工具会「增强无障碍脚本」 | 同意框写明工具不能用无障碍；自动化仍只在「用户程序」 |

#### 5.5.8 工程落点

| 项 | 规格 |
|----|------|
| 校验文件 | 发版 `*.sha256` / `SHA256SUMS` |
| 内嵌期望哈希 | Bundled 列表来自开源树 |
| 可选公钥 | `build/flytool-pubkeys/` 仅公钥 |
| `ToolInstaller` | 哈希 → 信任等级 → **拒绝禁止权限** → 同意 → 解压 → 复哈希 |
| `ToolShareService` | 传原包；接收端完整 §5.5 |
| `IToolHostServices` | 接口物理上不含 A11y/Fly 注入；代码审查 MUST 守住 |
| 日志 | 可记 hash 前缀；MUST NOT 记整包 |

#### 5.5.9 无障碍与高危设备控制隔离（手机，致命）

**问题：** 玄鸟 Android 为键鼠注入与用户程序开启「玄鸟键鼠注入」无障碍后，权限极高（点击、滑动、读界面节点、部分场景下接近操控整机）。若 `.flytool` 工具代码能调用到这层，**仅靠哈希不够**——用户一点确认、或内网分享一个「哈希看起来正常」的包，后果远比普通 App 权限严重。

**硬性拆分（实现 MUST 遵守，无例外）：**

| 能力 | 谁可以有 | `.flytool` 工具代码 |
|------|----------|----------------------|
| 无障碍服务实例 / `AccessibilityService` | 仅主程序内置服务 | **MUST NOT** 获得引用、回调、或代理 |
| `Fly.*` 设备控制 | 仅侧栏「用户程序」 | **MUST NOT** |
| 桌面 Easy Mouse 控手机（穿边注入） | 仅主程序输入路径 | **MUST NOT** |
| 投影 **远程控制** 注入（见 §5.5.10） | 仅主程序在收到 `ScreenExtendInput` 后走内置注入/无障碍 | 工具 **MUST NOT** 自己调无障碍；只可经宿主声明「本会话需要远程控制」 |
| MediaProjection 投屏采集 | 宿主代申请；工具可消费帧管线由宿主接好的流 | 允许；与无障碍无关 |
| Camera / Mic | 系统权限 | 允许 |
| 工具信封 / 投影信令编解码 | 工具或宿主模块 | 允许（注入仍只在宿主） |

**架构约束：**

1. `IToolHostServices` MUST NOT 暴露：无障碍节点、任意坐标点击/手势、`Fly`/`ProgramDevice`。  
2. 允许的窄接口（投影专用，由宿主实现）：例如 `RequestProjectionSession(..., remoteControl: bool)` / 会话状态回调——**宿主**在 `remoteControl==true` 且本机无障碍已开时，自行把对端 `ScreenExtendInput` 送进现有注入路径；工具 DLL 收不到无障碍对象。  
3. Bundled 投影同样遵守：回控逻辑放在 `FlyScreen.App` / 平台适配层，不放在可热替换的工具任意代码里若无法审计；若投影实现打在 `.flytool` 内，回控注入仍 MUST 回调宿主 `IProjectionInputSink`（宿主实现），工具只解析协议坐标。  
4. 禁止权限名单见 §5.5.3；命中则拒装。  
5. 无障碍设置说明 SHOULD 写清：用于键鼠、用户程序，以及**主程序执行的投影远程控制**；不是为了让第三方工具包操控手机。

**与「用户程序」边界：**

- 用户程序：本机脚本主动控机。  
- 工具包：禁止持有无障碍；投影回控 = 宿主代注，不是工具持权。

#### 5.5.10 投影远程控制 vs 无障碍（答疑，无歧义）

现网事实（[extended-display.md](extended-display.md)）：手机作投影 Host 且开启「远程控制」时，从机键鼠经 `ScreenExtendInput` 到达手机后，**由主程序用无障碍手势注入**。电脑 Host 用 Win32 注入，也不把注入器交给工具脚本。

因此：

| 误解 | 正确规格 |
|------|----------|
| 「投影是工具 ⇒ 工具必须能调无障碍」 | 否。需要无障碍的是**宿主内置回控管道**，与工具是否安装投影包解耦 |
| 「禁止工具用无障碍 ⇒ 手机不能远程控制」 | 否。远程控制仍可用：Offer 带 `RemoteControl` → 宿主注册回控 → 宿主调无障碍；工具只负责采集/编码/会话 UI |
| 「装了投影工具就扩大了无障碍能力」 | 否。无障碍仍是用户原已授予主程序的同一服务；工具不能新增「随便点屏」API |

实现检查清单：

- [ ] 搜索工具程序集：无 `AccessibilityService`、无障碍手势 dispatch 的直接调用。  
- [ ] `ScreenExtendInput` 处理函数位于宿主/平台工程，不位于工具可替换入口的「任意插件导出」。  
- [ ] 投影工具若需「回控开关」，只设布尔会话参数，不接收「注入这些坐标」的通用回调给工具反过来调系统。

---

## 6. 第一期工具清单（必须交付的 Id）

### 6.1 `shared-devices`（共享扬声器 / 麦克风 / 摄像头）

扬声器、麦克风、摄像头 **合并为一个工具**（一个 `ToolId`、一个 `.flytool`）。三路能力是工具内的可选申请项，不是三个安装包。

| 项 | 值 |
|----|-----|
| id | `shared-devices` |
| kind | `enhancement` |
| crossScreen | **true** |
| group | `shared-devices` |
| titleBar | **false**（本期；启动/停止在工具页，见 §3.2 / §6.1.2。运行中徽章在工具卡片即可；后期若需标题栏状态再开） |
| supportsRepair | false（虚拟音频组件若缺失，可在安装流程内提示安装；不单列修复卡片） |
| 协议 | MUST 走 §8 通用信封；Caps 宣告 **`Tool:shared-devices`**（单 Cap，不再分三 Id） |
| 两端 | 申请端与接收端 MUST 均已安装 `shared-devices` |
| 媒体 / 虚拟设备 / 与投影声音冲突 | 技术细节仍见 [shared-devices.md](shared-devices.md)；**会话如何建立以本节为准** |
| 包 | 一条 `.flytool`，自包含申请端与接收端逻辑 |

**两端需要的操作（MUST，同一工具包内按角色启用）：**

| 角色 | 本端需要 |
|------|----------|
| 申请端 | **安装**、**启动**（双击工具）、**停止**、**卸载** |
| 接收端 | **安装**（以及卸载；无启动/停止）。安装后即可接收申请并同意/拒绝 |

**features（同一包内）：**

| feature | 含义 |
|---------|------|
| `speaker` | 可申请 / 可提供扬声器 |
| `microphone` | 可申请 / 可提供麦克风 |
| `camera` | 可申请 / 可提供摄像头 |
| `provide` | 本端可作为接收端（有真实硬件可被申请） |
| `consume` | 本端可作为申请端（使用方：注册虚拟设备等） |

申请目标：**布局内任意其它设备**（`Desktop` / `Phone` / `Tablet`），只要已装本工具且至少具备扬声器 / 麦克风 / 摄像头之一即可作接收端外设（手机当然可以）。有真实可共享硬件的端宣告 `provide`；需要使用对端设备的端宣告 `consume`（判定见 shared-devices.md §5）。手机第一期以 `provide` 为主，亦可在具备 `consume` 时作申请端。

#### 6.1.1 安装（两端共同前提）

- **要使用共享设备功能的端（申请端）MUST 先安装本工具**；**被申请的设备（接收端）也 MUST 已安装**，否则不能出现在可选目标里、也不能应答。
- 未安装 → MUST NOT 出现在申请端的目标列表中；MUST NOT 建立媒体会话。
- 安装本身 **不等于** 开始采集或传流；申请端 MUST 再「启动」并发起申请，接收端同意后才可用。

#### 6.1.2 操作流程（本期 MUST）

旧流程（作废）：装完 → 选定提供方 → 本机软件打开虚拟设备即自动 `Open`。

新流程：

```text
【申请端】
1. 安装 shared-devices。
2. 双击工具（或点「启动」）打开启动面板。
3. 在布局中选择其它设备作为目标（电脑 / 手机 / 平板等）：
   - 目标 MUST 已连接、在布局内、已安装 shared-devices；
   - 目标 MUST 至少具备扬声器 / 麦克风 / 摄像头中的一种可共享能力（无任何可共享种类 MUST NOT 出现在列表，或置灰并说明原因）。
4. 勾选本次要使用的可共享设备（扬声器 / 麦克风 / 摄像头，可多选；仅列出目标实际可提供的种类）。
5. 用户确认启动 → 向所选设备发出申请。
6. 等待接收端同意或拒绝。
7. 同意 → 申请端即可使用该设备上已选中的扬声器 / 麦克风 / 摄像头（注册/启用本机虚拟设备并建媒体会话）。
8. 需要结束时：申请端点「停止」或卸载 / 断连 → 结束会话。

【接收端】
1. 安装 shared-devices（只需安装，不必启动工具）。
2. 收到申请弹窗：申请端机器名、申请的种类；可同意 / 拒绝；可选「记住适配」。
3. 同意后按种类提供采集或播放通道；拒绝则回拒绝原因，不建会话。
```

| 步骤细节 | MUST |
|----------|------|
| 启动入口 | **申请端**工具页：**双击**已安装的工具卡片（或等价「启动」按钮）打开启动面板 |
| 停止入口 | **申请端**运行中经工具卡片 ⋯ 菜单点「停止」（MUST NOT 单击卡片停止）；停止 MUST 结束进行中的申请与媒体会话 |
| 接收端 UI | 无启动面板；仅安装后待命 + 申请弹窗 |
| 目标范围 | 布局内**其它已装本工具的设备**（含手机）；须至少有一种可共享设备 |
| 多选种类 | 确认启动前勾选 1～N 种（N≤目标可提供数）；未勾选的 MUST NOT 进入本次申请 |
| 确认启动 | 用户明确确认后才发 `request`；关闭面板不发申请 |
| 通过后即用 | 接收端同意后申请端 MUST 直接进入可用状态，不必再点第二次开始；会议等软件随后选用虚拟设备即可 |
| 一路占用 | 每一路硬件同时仍只服务一台申请端（冲突见 shared-devices.md） |

#### 6.1.3 记住适配

接收端在**接收申请**时 MAY/SHOULD 提供勾选：**记住适配**。

| 规则 | MUST |
|------|------|
| 含义 | 记住「某申请端机器 + 已通过的种类」；下次该申请端再次启动并对本机发起**相同种类**申请时，可直接通过，不再弹确认框 |
| 粒度 | 按（申请端设备身份，种类）记忆；三种类可分别记住 |
| 范围 | 仅本机接收侧持久化；MUST NOT 进 `BehaviorSync` |
| 取消 | 用户可在工具设置 / 授权记录中清除某条或全部；清除后下次须再确认 |
| 变更 | 申请种类超出已记住集合时，对**新增种类**仍 MUST 弹确认（可再次勾选记住） |
| 安全 | 记住适配 **不能** 跳过工具安装确认或 `.flytool` 哈希；仅跳过「媒体共享申请」确认 |

无「记住适配」或未勾选：每次申请都须接收端手动同意。

#### 6.1.4 信封 Op（共享设备，实现时对齐命名）

在 `ToolId=shared-devices` 下建议 Op（宿主不解释 Body 业务字段，工具模块处理）：

| Op | 方向 | 用途 |
|----|------|------|
| `request` | 申请端 → 接收端 | Body：`kinds[]`（`speaker`/`microphone`/`camera`）、`SessionId` |
| `request-accept` | 接收端 → 申请端 | 同意；可含采样参数 |
| `request-reject` | 接收端 → 申请端 | 拒绝；`Reason`：`busy` / `denied` / `no-device` / `no-bulk` / `not-installed` 等 |
| `session-close` | 任一方 | 结束一路或多路 |
| `media-state` | 接收端 → 申请端（可选） | 硬件插拔 / 可提供状态变化 |

媒体帧走 `ToolData`（或过渡期见 §8.5）；音视频格式与优先级仍以 shared-devices.md 为准。

#### 6.1.5 与旧三工具文档的关系

- 旧 `shared-camera` / `shared-microphone` / `shared-speaker` **三 Id 作废**，统一为 `shared-devices`。
- Caps 旧名 `SharedCamera*` 等若曾预留，实现时改用 `Tool:shared-devices`，并回写 protocol / shared-devices。

---

### 6.2 `projection`（投影：复制 + 扩展）— **本期不做**

| 项 | 值 |
|----|-----|
| 本期状态 | **暂不抽成独立工具**；继续主程序内置，行为以 [extended-display.md](extended-display.md) 为准 |
| 工具页 | MUST NOT 出现「投影」工具卡片（本期） |
| 标题栏 | 投影入口仍由主程序提供，**不**经 `TitleBarToolSlot` |
| Caps | 仍由主程序宣告 `ScreenExtendHost` / `ScreenExtendSink`（与是否安装工具无关） |

**后期迁入时（非本期验收）预留规格摘要：**

| 项 | 值 |
|----|-----|
| kind | `session` |
| group | `display` |
| titleBar | **true** |
| crossScreen | **true** |
| 两端 | 发起端与目标端均已安装 `projection` |
| 依赖 | Desktop+Windows `extend` → 组件 `virtual-display` 打进同一 `.flytool` |
| 远程控制 | Android Host 无障碍注入走宿主（§5.5.10） |

迁入完成前：现网投影行为不变。迁入后：内置入口拆除，仅工具提供；并补两端都装与分享规则。

### 6.3 组件 `virtual-display`（VDD）— **随投影后期**

本期不随工具页交付。投影扩展屏安装/修复继续走主程序现有路径（[extended-display.md](extended-display.md) / [mtt_vdd/README.md](mtt_vdd/README.md)）。

后期归属 `projection.dependencies`；`supportsRepair=true`；MUST NOT 卸载 ToDesk / Oray 等非本程序驱动。

---

## 7. 标题栏注入（宿主 API 规格）

### 7.1 硬规则

1. 每个 `ToolId` MUST 最多注册 **一个** `TitleBarContribution`。
2. 若可执行操作数 ≥ 2，Kind MUST 为 `menu`（下拉）；MUST NOT 为同一工具注册多个并列标题栏按钮。
3. 若可执行操作数 = 1，Kind 可为 `button`。
4. 工具 MUST NOT 直接操作原生标题栏；MUST 调用宿主注册/更新 API。
5. 卸载或禁用工具 MUST 注销贡献并刷新标题栏。

### 7.2 贡献模型

```text
TitleBarContribution {
  ToolId: string
  Kind: "button" | "menu"
  States: TitleBarState[]          // 至少 1 个
  CurrentStateId: string
  Actions: TitleBarAction[]        // Kind=menu 时 ≥1；Kind=button 时 0 或 1
}
TitleBarState {
  Id: string
  Icon: …                          // 宿主可接受的资源键或几何
  Label: string                    // 短文案，如「投影」「投影中」
  Tooltip: string
  HoverIcon: optional
  HoverLabel: optional
  IsActive: bool                   // 运行中视觉（如投影中）
  IsEnabled: bool
  PrimaryActionId: optional        // 左键：button 必填动作；menu 可表示「打开菜单」或某默认动作
}
TitleBarAction {
  Id: string
  Label: string
  IsEnabled: bool
  IsDestructive: bool
  VisibleInStates: string[] | null // null=全部状态可见
  RequiredFeature: string | null   // 如 "extend"；本端 features 不含则 MUST NOT 出现在菜单
}
```

### 7.3 宿主回调

| 回调 | 时机 |
|------|------|
| `OnPrimaryClick(toolId)` | Kind=button 左键；或 menu 且 Primary 定义为非开菜单时 |
| `OnOpenMenu(toolId)` | Kind=menu 打开下拉前；工具可刷新 Actions |
| `OnAction(toolId, actionId)` | 用户点下拉项 |

宿主 MUST 根据 `CurrentStateId` 绘制对应 State；悬停时若存在 `Hover*` 则使用，否则统一悬停样式 + `Tooltip`。

### 7.4 投影工具标题栏（后期迁工具时适用）

本期投影不经工具槽位。下列为后期迁入后的规范示例：

| StateId | Label | IsActive | 左键建议 |
|---------|-------|----------|----------|
| `idle` | 投影 | false | 打开下拉 |
| `projecting` | 投影中 | true | 执行 `stop` |
| `error` | 投影异常 | false | 打开下拉 |

| ActionId | Label | Desktop | Phone |
|----------|-------|---------|-------|
| `copy` | 复制投影… | 有 | 有 |
| `extend` | 扩展投影… | 有（VDD/真副屏未就绪则禁用或隐藏） | **MUST NOT 出现** |
| `stop` | 停止投影 | 仅 `projecting` | 同左 |
| `repair-vdd` | 修复虚拟屏 | 仅需修复时 | MUST NOT |

### 7.5 `shared-devices` 与标题栏（本期）

本期 `titleBar=false`：**不**向标题栏注入入口。申请端启动 / 停止在工具页完成（§3.2 / §6.1.2）；接收端仅安装 + 申请弹窗。

后期若需要「共享中」常驻标题栏状态，再开 `titleBar=true` 并补贡献模型；不得与工具页双击启动语义冲突。

---

## 8. 通用工具跨机协议

### 8.1 何时使用

- `kind=enhancement` 且需要跨机传信令/媒体：MUST 用本节（本期核心：`shared-devices`）。
- `projection`：本期仍用主程序专用消息；后期迁工具见 §6.2。
- 将来如 `color-picker` 跨屏取色：MUST 用本节。

### 8.2 宿主新增消息（落地时写入 Protocol.cs 与 protocol.md）

| MessageType 名 | 通道 | 用途 |
|----------------|------|------|
| `ToolSignal` | Control | JSON 信封，小信令 |
| `ToolData` | Bulk | 二进制媒体；信封头 + 载荷 |

旧端不认识类型：MUST 忽略并不断连（现有规则）。

### 8.3 信封

```text
ToolEnvelope {
  ToolId: string           // 如 "shared-devices" / "color-picker"
  Op: string               // 工具自定义；宿主不解释业务
  SchemaVersion: int
  SessionId: string        // GUID 字符串
  Body: object | null      // ToolSignal 下为 JSON；ToolData 可为定长头+裸字节
}
```

宿主收包后 MUST：

1. 解析 `ToolId`  
2. 若本机未安装该工具 → 可选回 `Op=reject` Reason=`unsupported`，或忽略  
3. 否则调用该工具的 `Handle(peerName, envelope)`  
4. MUST NOT 在宿主内硬编码某工具的 Body 业务字段  

### 8.4 Caps 与「两端都装」

- 增强工具安装后宣告：`Tool:<id>`（例：`Tool:shared-devices`）。  
- 跨屏会话：双方 Caps 交集 MUST 含该工具所需 Cap；否则 MUST NOT 开会话，UI MUST 提示对端未安装。  
- 申请目标列表：MUST 用 Caps（及布局连接状态）过滤未装本工具的对端。  
- 投影本期：主程序宣告 `ScreenExtendHost` / `ScreenExtendSink`，与工具 Caps 无关。  
- 探测对端是否已装：优先 Caps；分享 UI 可另用「工具安装查询」信令（可选 Op，如 `ToolSignal` `Op=query-installed`），有则展示更友好，无则以 Caps 为准。

### 8.5 共享设备与 90–96

[shared-devices.md](shared-devices.md) 曾预留 MessageType 90–96，且旧稿按三路独立 Open。实现时：

- **优先**直接用 §8 信封 + §6.1.4 Op（`request` / `request-accept` 等），不再占用 90–96；并回写 shared-devices / protocol。  
- 若短期保留 90–96，MUST 在代码与文档标明 Deprecated，下一协议整理删除；语义上仍应对齐「先申请后建会话」，不得回退为无确认自动 Open。

### 8.6 颜色拾取

**本机取色（已交付）：** 见 [color-picker.md](color-picker.md)。`crossScreen=false`；桌面端内嵌工作区 + 滴管；不占用 Caps / 工具信封。

**跨屏取色（后期样板，本期 MUST NOT 实现）：**

1. 两边安装同一 `color-picker`（或后续升为 `crossScreen=true`），Caps 含 `Tool:color-picker`。  
2. 甲发 `ToolSignal` Op=`sample` Body=`{x,y}` 或归一化坐标。  
3. 乙工具读像素，回 Op=`sample-result` Body=`{r,g,b,hex}`。  
4. 标题栏：若需要入口，仅一项；下拉可含「取色」等。  
5. 一端可「分享到设备」把 `.flytool` 发给布局内对端（§4.1）。

### 8.7 工具包传输信令（分享）

分享 `.flytool` 时 SHOULD 复用现有文件直传 / Bulk；若用工具信封，仅允许元数据 Op（如 `tool-offer` / `tool-offer-accept`），**文件本体走 Bulk 文件通道**，避免把百兆驱动塞进 Control。

---

## 9. 宿主必须提供的服务（实现清单）

编码时 Core / App 分层 MUST 覆盖：

| 服务 | 职责 |
|------|------|
| `ToolCatalog` | 读清单、按 DeviceClass/Platform 过滤、安装状态持久化、对端是否已装（Caps） |
| `ToolInstaller` | **唯一**入口：`InstallFromFlyToolFile`；**先算哈希 → 定 trustLevel → 同意 → 解压 → 复哈希 → 记录**；加载前复验 |
| `ToolPackager`（构建期） | 打成单个 `.flytool` 并写出 sha256 校验文件（CI/发版） |
| `ToolTrust` | Bundled 期望哈希、KnownRelease 校验文件缓存、用户信任摘要列表；可选校验仓库内维护者**公钥**的分离签名；**无产品根私钥** |
| `ToolShareService` | 选对端、过滤设备、传原包字节、对端确认（含完整哈希） |
| `ToolHost` | 加载已装工具、Caps 聚合、消息分发 `ToolSignal`/`ToolData`；跨屏前检查双方已装 |
| `TitleBarToolSlot` | 注册贡献、状态切换、下拉、多端 UI（WPF/Maui/Avalonia） |
| `IToolModule` | 工具实现接口：`Install/Uninstall/Repair?`、`HandleMessage?`、`GetTitleBarContribution?`；内含发送与接收角色 |

主程序保留：TCP 连接、密钥、Bulk、穿边拓扑暂停/摘除 API、键鼠注入 API。工具 MUST 通过这些 API 协作，MUST NOT 自开监听端口做跨机通信（**例外**：声明 `localMediaListen` 时，仅允许绑定本机端口接收 RTP/SIP 等媒体入流，且不得承载 FlyScreen 工具信封或跨机业务）。

文件关联：Windows/macOS/Linux SHOULD 注册 `.flytool` → 玄鸟（外装方式 C）。

---

## 10. 多端矩阵（第一期）

| 端 | 工具页 | `shared-devices` | 本机 `color-picker` | 投影（主程序内置） |
|----|--------|------------------|---------------------|--------------------|
| Win WPF / Maui-Win | 完整+工作区 | 是（申请端+接收端；申请端启动） | 是（内嵌+滴管） | 现有内置，不经工具页 |
| Maui Android | 完整+工作区骨架 | 可作接收端外设（提供） | **否**（清单过滤） | 现有内置 copy Host |
| Mac / Linux Desktop | 壳+工作区 | 硬件视实现 | 是（内嵌；采样视 GDI/系统库） | 与 extended-display 一致 |
| 鸿蒙 | 随 UI | 后期 | 后期 | 后期 |

---

## 11. 实施顺序（给 AI / 排期）

必须按序，避免半套入口。**本期到步骤 7 为止；投影迁工具不排进本期。**

1. **`.flytool` 格式 + 公开哈希信任（§5.5）+ `ToolInstaller`/`ToolTrust` + 组包与 SHA256SUMS 脚本。**  
2. **宿主骨架：** 侧栏工具页 + `ToolCatalog` + 安装/导入/卸载（全部走哈希门禁）。  
3. **标题栏槽位 API** + mock 工具验证多状态与下拉。  
4. **`ToolShareService`：** 分享到布局设备 + 接收确认。  
5. **`ToolSignal`/`ToolData` + Caps `Tool:`。**  
6. **`shared-devices` 一条 `.flytool`：** 两端都装；申请端工具页双击启动 / 停止；选布局内已装工具且具备音视频硬件的设备（含手机）+ 可共享种类；接收端同意/拒绝 +「记住适配」；通过后建媒体会话。  
7. **媒体与虚拟设备：** 对齐 shared-devices.md 中格式 / 虚拟摄像头音频 / 与投影声音冲突规则（会话触发改为申请通过后，不再「装完即随 Open」）。  

**后期（非本期）：**

8. **`projection` 迁出为 `.flytool`：** 未安装无入口；开会话前检查对端已装。  
9. **`virtual-display` 打进投影包**；环境修复 → 同一修复 API。  

每步 MUST：三端 UI 同构改动；Win 回归门禁不因工具页失败而跳过键鼠用例。

---

## 12. 验收标准（完成定义）

### 12.1 工具页与包

- [ ] 三端侧栏有「工具」，位于「程序」下。  
- [ ] 筛选四态可用；未安装/已安装/需修复外观可区分。  
- [ ] `shared-devices` 仅需一个 `.flytool` 即可完成安装（扬声器/麦克风/摄像头同包）。  
- [ ] 工具页**无**投影工具卡片（本期）。  
- [ ] 导入本地 `.flytool` 与工具页「安装」结果一致。  
- [ ] 外部打开 `.flytool` 能进入同一安装器（至少 Windows）。  

### 12.1b 安全（开源）

- [ ] Bundled/KnownRelease：哈希匹配可装；改包内文件后哈希失败且**拒绝安装**。  
- [ ] Unknown 来源：必须展示完整 sha256 并确认，不得静默安装。  
- [ ] 同意框含权限列表与哈希。  
- [ ] 对端分享同样校验/确认；**不能**因已配对而跳过。  
- [ ] 篡改已安装目录后加载失败。  
- [ ] 实现与文档均**不依赖**私有工具根密钥；校验材料可公开审计。  
- [ ] 分享传输单个原包；接收端装后入口正确。  
- [ ] 工具宿主 API **无**通用无障碍/Fly 注入方法；含禁止权限的包无法安装。  
- [ ] 同意框含工具不持有无障碍的说明（投影回控仍在主程序，§5.5.10）。  

### 12.2 两端都装与分享（`shared-devices`）

- [ ] 仅本机安装、对端未装时，申请目标列表不含该对端或选中后明确失败。  
- [ ] 两边都装后，可完成申请 → 授权 → 使用。  
- [ ] 「分享到设备」只列出 DeviceClass/Platform 匹配的已连接布局设备。  
- [ ] 接收端必须确认后才安装；拒绝不影响连接。  

### 12.3 共享设备操作流程

- [ ] 申请端已安装后：可单击工具启动；运行中经 ⋯ 菜单停止（单击卡片不停止）；可卸载。  
- [ ] 接收端已安装后：无启动/停止；能弹出申请并同意/拒绝。  
- [ ] 启动面板：列出布局中已装本工具、且至少有一种可共享设备的其它设备（电脑/手机/平板均可）。  
- [ ] 可勾选扬声器 / 麦克风 / 摄像头中目标实际可提供的一个或多个，确认后才发申请。  
- [ ] 接收端同意后，申请端即可使用所选电脑上对应设备；拒绝有明确提示。  
- [ ] 接收端可勾选「记住适配」；下次相同申请端 + 已记种类可直接通过。  
- [ ] 申请端停止 / 任一方卸载 / 断连会结束会话；未装工具无法传媒体。  
- [ ] 本期标题栏无共享设备入口（`titleBar=false`）。  
- [ ] MUST NOT 再出现「只安装、不经申请就自动建共享会话」的旧路径。  

### 12.4 通用协议

- [ ] 未知 `ToolSignal` 不断连。  
- [ ] 未装对应工具时不分发进业务导致崩溃。  
- [ ] 对端无 `Tool:shared-devices` 时有明确 UI 提示。  

### 12.5 回归

- [ ] 未装任何新工具时，键鼠 / 剪贴板 / 文件与迁移前一致。  
- [ ] **投影仍走主程序：** E1 投影用例（复制、扩展、声音、回控、互斥）不因工具页引入而回退。  

---

## 13. 修订记录

| 日期 | 版本 | 说明 |
|------|------|------|
| 2026-11-03 | 0.1–0.2 | 初稿：工具页、共享设备、VDD |
| 2026-11-03 | 1.0 | 完整规格：分类、清单、投影独立工具、标题栏、通用信封、验收 |
| 2026-11-03 | 1.1 | 跨屏两端都装；收发同包；分享到布局设备并按接收端过滤；`.flytool` 单文件与导入/外装/分享三路同一安装器 |
| 2026-11-03 | 1.2 | 曾写发布方根密钥模型（不适配开源） |
| 2026-11-03 | 1.3 | **开源安全模型**：去掉工具根密钥；公开 contentSha256 + Bundled/KnownRelease/Unknown；可选仓库内维护者公钥分离签名；配对密钥不证明包完整性 |
| 2026-11-03 | 1.4 | **无障碍隔离 §5.5.9**：工具永不获得 A11y/Fly 控机；禁止权限名单；与用户程序拆分；同意框明示；哈希正确也不开放无障碍 |
| 2026-11-03 | 1.5 | **§5.5.10**：投影远程控制需要无障碍时由宿主代注；澄清「工具禁 A11y」≠「禁止投影回控」 |
| 2026-11-07 | 1.6 | **本期不抽投影**；工具优先 `shared-devices`（扬声器/麦克风/摄像头合并）；流程改为布局内申请→授权方可「记住适配」→通过即用；废除三独立 ToolId 与「装完即自动建会话」 |
| 2026-11-07 | 1.7 | 明确申请端=安装/双击启动/停止/卸载，接收端=安装后应答；启动选布局内其它电脑与可共享种类再确认申请；本期 `titleBar=false` |
| 2026-11-07 | 1.8 | 工具页改为**图标网格**（图标+标题+说明，约 3 列）；单击主操作、右键卸载，去掉项内按钮条 |
| 2026-11-07 | 1.9 | 独立卡片 + 未安装/已安装/已启动三态底色；安装耗时在卡片背景显示进度；左→右上→下自适应换行 |
| 2026-11-07 | 1.10 | 申请目标放开为布局内任意已装工具且具备扬声器/麦克风/摄像头之一的设备（手机可作外设）；去掉「仅电脑」限制 |
