namespace FlyScreen.Tool.DeveloperTools.Scaffold;

/// <summary>生成 README / manifest 说明文档。</summary>
internal static class ToolScaffoldDocs
{
    public static string BuildReadme(ToolScaffoldRequest req, ToolScaffoldIdentity id, bool win, bool maui, bool desktop)
    {
        var ui = req.UiMode switch
        {
            ToolUiMode.Workspace => "嵌入工作区（hasWorkspace=true）",
            ToolUiMode.Dialog => "启动弹窗（hasWorkspace=false + RegisterHandler）",
            _ => "纯后台（无 UI 工程，Start/Stop 控生命周期）"
        };

        return $"""
            # {req.DisplayNameZh}（{id.FolderName}）

            由玄鸟「开发者工具」生成的独立插件脚手架。

            | 项 | 值 |
            |----|----|
            | ToolId | `{id.ToolId}` |
            | 界面形态 | {ui} |
            | 电脑 | {(req.SupportDesktop ? "是" : "否")} |
            | 手机 | {(req.SupportPhone ? "是" : "否")} |
            | 工程 | `{id.AssemblyName}`{(win ? " + Ui.Win" : "")}{(desktop ? " + Ui.Desktop" : "")}{(maui ? " + Ui.Maui" : "")} |
            | 解决方案 | **`{id.AssemblyName}.sln`**（请打开此文件，不要单独打开某一个 .csproj） |

            {req.SummaryZh}

            ---

            ## 1. 怎么打开工程

            1. 用 **Visual Studio** 打开本目录下的 **`{id.AssemblyName}.sln`**（含全部子项目）。
            2. 或用 **VS Code**：在本目录执行 `code .`（会看到所有文件夹/工程）。

            > 若只双击某个 `.csproj`，VS 会生成临时解决方案，**只显示 1 个项目**——这是正常现象，请改开 `.sln`。

            ---

            ## 2. 目录与文件说明

            | 路径 | 作用 | 你通常要改什么 |
            |------|------|----------------|
            | `{id.AssemblyName}.sln` | 聚合全部子工程的解决方案 | 一般不用改 |
            | `{id.AssemblyName}/` | **逻辑层**：清单、模块入口、业务服务、文案、图标 | `Module.cs`、`*Service.cs`、业务代码 |
            | `{id.AssemblyName}/manifest.json` | 工具清单（安装/卡片/能力声明） | 见 `docs/manifest.md` |
            | `{id.AssemblyName}/Module.cs` | `IToolModule` 入口：加载/启动/停止/菜单 | 启动停止流程、是否开工作区/弹窗 |
            | `{id.AssemblyName}/*Service.cs` | 运行时状态与业务逻辑 | **主要业务写这里** |
            | `{id.AssemblyName}/*Localization.cs` | 工具独立多语言（勿写入主程序 Loc） | 增删文案键 |
            | `{id.AssemblyName}/*Icons.cs` + `Icons/` | 卡片图标嵌入资源 | 替换 png 并保持 LogicalName |
            {(win ? $"| `{id.AssemblyName}.Ui.Win/` | Windows WPF UI（工作区面板或弹窗） | Panel/Dialog + `*WinUiContributor` |\n" : "")}{(desktop ? $"| `{id.AssemblyName}.Ui.Desktop/` | macOS/Linux Avalonia UI | Panel/Dialog + `*DesktopUiContributor` |\n" : "")}{(maui ? $"| `{id.AssemblyName}.Ui.Maui/` | Android / Maui-Win UI | Panel/Dialog + `*MauiUiContributor` |\n" : "")}| `docs/manifest.md` | **manifest 每个字段的详细说明** | 查阅 |
            | `docs/ui-modes.md` | 工作区 / 弹窗 / 后台三种形态怎么实现 | 查阅 |
            | `README.md` | 本文件 | — |

            ---

            ## 3. 推荐开发顺序

            1. 读 `docs/manifest.md`、`docs/ui-modes.md`，确认界面形态与清单字段。
            2. 在 `*Service.cs` 实现业务；`Module.cs` 只做宿主对接（启动/停止/开 UI）。
            3. 在对应 `Ui.*` 工程完善界面；`*UiContributor` 负责向宿主注册工厂/Handler。
            4. 本地：打开 `{id.AssemblyName}.sln` 编译通过。
            5. 接入玄鸟：写入 `ToolIds` / `ToolCatalog` / `Pack-FlyTools.ps1`（若生成时未自动写入），执行 `pwsh build/Pack-FlyTools.ps1`，在工具页安装 `{id.ToolId}.flytool` 验证。

            ---

            ## 4. 接入宿主清单（源码仓库开发时）

            1. `FlyScreen.sln`：已含或 `dotnet sln add` 各 csproj  
            2. `src/FlyScreen.Core/Services/Tools/ToolIds.cs`：`public const string {id.FolderName} = "{id.ToolId}";`  
            3. `ToolCatalog.BuildFallbackBuiltin()`：增加与 `manifest.json` 一致的条目  
            4. `build/Pack-FlyTools.ps1`：增加打包项  
            5. 编译并打包后，工具页安装验证

            安装包用户：可在本目录开发；引用指向本机玄鸟源码/宿主 DLL（见各 csproj）。打 `.flytool` 后用工具页「导入」安装。
            """;
    }

    public static string BuildManifestDoc(ToolScaffoldIdentity id)
        => $$"""
            # manifest.json 字段说明

            文件位置：`{{id.AssemblyName}}/manifest.json`。  
            宿主安装 `.flytool`、工具页卡片展示、ALC 加载入口都依赖本清单。  
            编码模型见宿主 `ToolManifest` / `ToolPackageManifest`（`docs/tools.md` §5）。

            ## 必填 / 常用字段

            | 字段 | 类型 | 可设值 / 示例 | 作用与意义 |
            |------|------|---------------|------------|
            | `id` | string | 小写 kebab-case，如 `{{id.ToolId}}` | **全局唯一** ToolId；与 Caps `Tool:<id>`、安装目录名一致。改名后视为新工具。 |
            | `kind` | string | `enhancement` \| `session` \| `component` | 工具类别。增强/会话可上工具页；`component` 不单独注入标题栏，一般作依赖组件。 |
            | `crossScreen` | bool | `true` / `false` | `true`：需要两端都装、可走分享安装；`false`：纯本机工具。 |
            | `group` | string | 如 `utilities` / `shared-devices` / `media` | 工具页分组键。 |
            | `nameKey` | string | 如 `{{id.NameKey}}` | 显示名的本地化键；文案在 `*Localization`（`IToolLocalization`），**不要**写入主程序 `Loc.cs`。 |
            | `summaryKey` | string | 如 `{{id.SummaryKey}}` | 卡片一句话说明的本地化键。 |
            | `schemaVersion` | int | 通常 `1` | 清单结构版本；主版本不兼容时应拒绝会话/安装。 |
            | `minAppVersion` | string | 如 `1.0.0` | 要求宿主最低版本。 |
            | `deviceClasses` | string[] | `Desktop` / `Phone` / `Tablet` | 哪些设备形态可见/可安装；不匹配则灰卡或隐藏。 |
            | `platforms` | string[] | `Windows` / `Android` / `macOS` / `Linux` | OS 过滤；可与 deviceClasses 组合。 |
            | `featuresByDeviceClass` | object | `"Desktop": ["local"]` 等 | 按设备类声明能力切片；缺省表示该端全开。 |
            | `roles` | string[] | 本机常用 `["local"]`；跨屏如 `request`/`authorize` | 会话内角色语义；**禁止**拆成两个 ToolId。 |
            | `supportsRepair` | bool | 默认 `false` | 是否支持「修复」管线（驱动类工具常用）。 |
            | `capNames` | string[] | 如 `["Tool:{{id.ToolId}}"]` 或空 | 安装启用后并入握手 Caps；跨屏增强类常用 `Tool:<id>`。 |
            | `titleBar` | bool | 默认 `false` | `true` 时向宿主标题栏注入恰好一个入口（见 tools.md §7）。 |
            | `hasWorkspace` | bool | 工作区=`true`；弹窗/后台=`false` | `true`：主界面**必须**嵌入工具页工作区，禁止独立主窗；启动后 `Workspace.OpenOrFocus`。 |
            | `icon` | string | 如 `{{id.IconKey}}` | 卡片图标键；由 `IToolIconContribution` 提供位图。 |
            | `permissions` | string[] | 如 `screenSample` / `camera` / … | 安装确认时展示；声明工具申请的能力（开源友好明示）。 |
            | `package.version` | string | 如 `1.0.0` | 包版本；分享/升级比较用。 |
            | `package.format` | string | 必须 `flytool` | 交付容器格式。 |
            | `package.formatVersion` | int | 通常 `1` | 包格式版本。 |
            | `entryAssembly` | string | `{{id.AssemblyName}}.dll` | ALC/进程内加载的入口程序集（可带或不带 `.dll`）。 |
            | `entryType` | string | `{{id.Namespace}}.Module` | 实现 `IToolModule` 的类型全名。 |

            ## 与界面形态的对应关系

            | 形态 | `hasWorkspace` | UI 工程 | Module 行为 |
            |------|----------------|---------|-------------|
            | 嵌入工作区 | `true` | 有 Panel + `RegisterWorkspaceFactory` | `UsesWorkspace=true`，`OpenOrFocus` |
            | 启动弹窗 | `false` | 有 Dialog + `RegisterHandler` | `UsesWorkspace=false`，`Ui.RequestAsync` |
            | 纯后台 | `false` | **无** Ui.* 工程 | 仅 `Start`/`Stop`；无工厂无 Handler |

            更细的代码路径见同目录 `ui-modes.md`。
            """;

    public static string BuildUiModesDoc(ToolScaffoldRequest req, ToolScaffoldIdentity id)
        => $$"""
            # 三种界面形态：如何实现

            当前脚手架选择：**{{req.UiMode}}**。

            ---

            ## A. 嵌入工作区（Workspace）

            **适用**：工具有较完整主界面，应留在工具页页签内（规格要求：`hasWorkspace=true` 禁止独立主窗）。

            ### 链路

            1. `manifest.json` → `"hasWorkspace": true`
            2. `Module.UsesWorkspace => true`
            3. 启动：`Module.PrimaryAsync` → `_service.Start()` → `_host.Workspace.OpenOrFocus(Id, 标题)`
            4. UI：`*UiContributor.Register` → `ui.RegisterWorkspaceFactory(toolId, () => new XxxPanel())`
            5. 宿主把工厂返回的控件嵌进工具工作区页签

            ### 你要改的地方

            - `*Service.cs`：业务状态
            - `Ui.*/XxxPanel`：界面
            - 停止：`StopAsync` 里 `_service.Stop()` + `Workspace.Close(Id)`

            ---

            ## B. 启动弹窗（Dialog）

            **适用**：点卡片后弹对话框完成一次操作（对照共享设备启动面板），**不**嵌入工作区。

            ### 链路

            1. `manifest.json` → `"hasWorkspace": false`
            2. `Module.UsesWorkspace => false`
            3. UI：`ui.RegisterHandler(toolId, Module.UiMain, handler)` 里 `ShowDialog` / `PushModalAsync`
            4. 启动：`Module.PrimaryAsync` → `_host.Ui.RequestAsync(Id, Module.UiMain, …)`

            ### 注意

            - Handler 的 `kind` 字符串必须与 `RequestAsync` 一致（模板里是 `Module.UiMain`）。
            - 弹窗关闭后是否保持 `IsRunning`，由你在 Service 里定义。

            ---

            ## C. 纯后台（Background）

            **适用**：无界面；点启动后在后台跑，直到用户点停止或卸载。

            ### 链路

            1. `hasWorkspace: false`，且**不生成** Ui.* 工程
            2. `PrimaryAsync`：若未运行则 `_service.Start()`；运行中单击可选择忽略或仅菜单停止
            3. `StopAsync` / 菜单 `stop` → `_service.Stop()`

            ### 长时间运行怎么写

            在 `*Service.cs` 中：

            ```csharp
            private CancellationTokenSource? _cts;
            private Task? _loop;

            public void Start()
            {
                if (IsRunning) return;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _loop = Task.Run(async () =>
                {
                    while (!ct.IsCancellationRequested)
                    {
                        // 你的周期工作…
                        await Task.Delay(1000, ct).ConfigureAwait(false);
                    }
                }, ct);
                // 置 IsRunning + SessionChanged
            }

            public void Stop()
            {
                _cts?.Cancel();
                try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
                _cts?.Dispose();
                _cts = null;
                _loop = null;
                // 清 IsRunning + SessionChanged
            }
            ```

            **要点**：用 `CancellationToken` 协作取消；不要 `Thread.Abort`；卸载时宿主会调 `OnUnloadedAsync` → 务必 `Stop()`。

            ---

            ## 对照本工具 Id

            - ToolId: `{{id.ToolId}}`
            - 入口类型: `{{id.Namespace}}.Module`
            """;
}
