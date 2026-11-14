using FlyScreen.Helpers.Localization;

namespace FlyScreen.Services.Tools;

public static class ToolVisualStates
{
    public const string NotInstalled = "NotInstalled";
    public const string Installed = "Installed";
    public const string Running = "Running";
    public const string Installing = "Installing";
    public const string LocalProgram = "LocalProgram";
}

/// <summary>三端工具页共用的卡片数据（独立卡片 + 状态底色 + 安装进度）。</summary>
public sealed class ToolCardRow
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Summary { get; init; }
    public required string StatusText { get; init; }
    public required string PrimaryAction { get; init; }
    public required string SecondaryAction { get; init; }
    public bool PrimaryEnabled { get; init; }
    public bool SecondaryEnabled { get; init; }
    public bool CanStart { get; init; }
    /// <summary>清单 feature「provide」能力位（菜单显隐由模块决定）。</summary>
    public bool CanProvide { get; init; }
    /// <summary>清单 feature「consume」能力位（菜单显隐由模块决定）。</summary>
    public bool CanConsume { get; init; }
    public bool IsRunning { get; init; }
    public bool IsInstalled { get; init; }
    public bool IsInstalling { get; init; }
    /// <summary>本地运行程序（无安装/卸载，状态文案为「本地程序」，次操作为移除）。</summary>
    public bool IsLocalProgram { get; init; }
    public int InstallPercent { get; init; }
    /// <summary>0..1，卡片背景进度条水平缩放。</summary>
    public double InstallProgressRatio { get; init; }
    /// <summary><see cref="ToolVisualStates"/>。</summary>
    public required string VisualState { get; init; }
    /// <summary>浅色主题卡片底色（Maui 等无 DynamicResource 触发器时用）。</summary>
    public string CardBgHex { get; init; } = "#F3F4F6";
    public string Tooltip { get; init; } = "";
    /// <summary>启动后嵌入工具页工作区。</summary>
    public bool HasWorkspace { get; init; }
    /// <summary>卡片图标键（manifest.icon）。</summary>
    public string IconKey { get; init; } = "";
}

public static class ToolCardRows
{
    public static IReadOnlyList<ToolCardRow> Build(ToolInstallProgress? progress = null)
    {
        ToolBundledCatalogAssets.Ensure();
        Loc.EnsureToolLocalizationsFromLoadedAssemblies();
        progress ??= ToolService.Instance.InstallProgress;
        var host = ToolService.Instance.Host;
        var rows = new List<ToolCardRow>();
        foreach (var card in ToolService.Instance.Catalog.GetCardsForLocalDevice())
        {
            var installed = card.Status == ToolInstallStatus.Installed;
            var module = host.Get(card.Id);
            var isRunning = module?.IsRunning == true;
            var isInstalling = progress.IsInstalling(card.Id);
            var percent = progress.GetPercent(card.Id);
            var canStart = installed && !isRunning && (
                card.Manifest.HasWorkspace
                || card.CanConsume);

            string visualState;
            string status;
            if (isInstalling)
            {
                visualState = ToolVisualStates.Installing;
                status = string.Format(Loc.T("Tool.StatusInstalling"), percent);
            }
            else if (isRunning)
            {
                visualState = ToolVisualStates.Running;
                status = Loc.T("Tool.StatusRunning");
            }
            else if (installed)
            {
                visualState = ToolVisualStates.Installed;
                status = Loc.T("Tool.StatusInstalled");
            }
            else
            {
                visualState = ToolVisualStates.NotInstalled;
                status = Loc.T("Tool.StatusNotInstalled");
            }

            string primary;
            string secondary;
            var secondaryEnabled = true;
            var primaryEnabled = !isInstalling;
            if (!installed)
            {
                primary = Loc.T("Tool.Install");
                secondary = Loc.T("Tool.Uninstall");
                secondaryEnabled = false;
            }
            else if (canStart || isRunning)
            {
                if (isRunning)
                {
                    primary = Loc.T("Tool.StatusRunning");
                    secondary = Loc.T("Tool.Uninstall");
                    primaryEnabled = false;
                }
                else
                {
                    primary = Loc.T("Tool.Start");
                    secondary = Loc.T("Tool.Uninstall");
                }
            }
            else
            {
                primary = Loc.T("Tool.Uninstall");
                secondary = Loc.T("Tool.Start");
                secondaryEnabled = false;
            }

            rows.Add(new ToolCardRow
            {
                Id = card.Id,
                Name = ToolCatalog.DisplayName(card.Manifest),
                Summary = ToolCatalog.DisplaySummary(card.Manifest),
                StatusText = status,
                PrimaryAction = primary,
                SecondaryAction = secondary,
                PrimaryEnabled = primaryEnabled,
                SecondaryEnabled = secondaryEnabled,
                CanStart = canStart,
                CanProvide = card.CanProvide,
                CanConsume = card.CanConsume,
                IsRunning = isRunning,
                IsInstalled = installed,
                IsInstalling = isInstalling,
                IsLocalProgram = false,
                InstallPercent = percent,
                InstallProgressRatio = Math.Clamp(percent / 100.0, 0, 1),
                VisualState = visualState,
                HasWorkspace = card.Manifest.HasWorkspace,
                IconKey = card.Manifest.Icon,
                CardBgHex = visualState switch
                {
                    ToolVisualStates.Running => "#DBEAFE",
                    ToolVisualStates.Installed => "#ECFDF5",
                    ToolVisualStates.Installing => "#EFF6FF",
                    _ => "#F3F4F6"
                }
            });
        }

        foreach (var prog in ToolService.Instance.LocalPrograms.Items)
        {
            var pathOk = !string.IsNullOrWhiteSpace(prog.ExecutablePath) && File.Exists(prog.ExecutablePath);
            var summary = pathOk
                ? prog.ExecutablePath
                : Loc.T("Tool.LocalProgramMissing");
            rows.Add(new ToolCardRow
            {
                Id = prog.Id,
                Name = string.IsNullOrWhiteSpace(prog.DisplayName)
                    ? Path.GetFileName(prog.ExecutablePath)
                    : prog.DisplayName,
                Summary = summary,
                StatusText = Loc.T("Tool.StatusLocalProgram"),
                PrimaryAction = Loc.T("Tool.Start"),
                SecondaryAction = Loc.T("Tool.Remove"),
                PrimaryEnabled = pathOk,
                SecondaryEnabled = true,
                CanStart = pathOk,
                CanProvide = false,
                CanConsume = false,
                IsRunning = false,
                IsInstalled = true,
                IsInstalling = false,
                IsLocalProgram = true,
                InstallPercent = 0,
                InstallProgressRatio = 0,
                VisualState = ToolVisualStates.LocalProgram,
                HasWorkspace = false,
                IconKey = prog.IconKey,
                CardBgHex = "#ECFDF5",
                Tooltip = prog.ExecutablePath
            });
        }

        return rows;
    }
}
