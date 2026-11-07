using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools.SharedDevices;

namespace FlyScreen.Services.Tools;

public static class ToolVisualStates
{
    public const string NotInstalled = "NotInstalled";
    public const string Installed = "Installed";
    public const string Running = "Running";
    public const string Installing = "Installing";
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
    /// <summary>本端可作为共享设备提供方（三点菜单「管理」）。</summary>
    public bool CanProvide { get; init; }
    /// <summary>本端可作为申请端（三点菜单「测试」）。</summary>
    public bool CanConsume { get; init; }
    public bool IsRunning { get; init; }
    public bool IsInstalled { get; init; }
    public bool IsInstalling { get; init; }
    public int InstallPercent { get; init; }
    /// <summary>0..1，卡片背景进度条水平缩放。</summary>
    public double InstallProgressRatio { get; init; }
    /// <summary><see cref="ToolVisualStates"/>。</summary>
    public required string VisualState { get; init; }
    /// <summary>浅色主题卡片底色（Maui 等无 DynamicResource 触发器时用）。</summary>
    public string CardBgHex { get; init; } = "#F3F4F6";
    public string Tooltip { get; init; } = "";
}

public static class ToolCardRows
{
    public static IReadOnlyList<ToolCardRow> Build(
        bool sharedDevicesRunning,
        ToolInstallProgress? progress = null)
    {
        progress ??= ToolService.Instance.InstallProgress;
        var rows = new List<ToolCardRow>();
        foreach (var card in ToolService.Instance.Catalog.GetCardsForLocalDevice())
        {
            var installed = card.Status == ToolInstallStatus.Installed;
            var isShared = string.Equals(card.Id, ToolIds.SharedDevices, StringComparison.OrdinalIgnoreCase);
            var isRunning = isShared && sharedDevicesRunning;
            var isInstalling = progress.IsInstalling(card.Id);
            var percent = progress.GetPercent(card.Id);

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
            var canStart = false;
            var primaryEnabled = !isInstalling;
            if (!installed)
            {
                primary = Loc.T("Tool.Install");
                secondary = Loc.T("Tool.Uninstall");
                secondaryEnabled = false;
            }
            else if (isShared && card.CanConsume)
            {
                if (isRunning)
                {
                    // 运行中不设卡片单击停止，避免误触；停止仅在 ⋯ 菜单
                    primary = Loc.T("Tool.StatusRunning");
                    secondary = Loc.T("Tool.Uninstall");
                    primaryEnabled = false;
                }
                else
                {
                    primary = Loc.T("Tool.Start");
                    secondary = Loc.T("Tool.Uninstall");
                    canStart = true;
                }
            }
            else
            {
                primary = Loc.T("Tool.Uninstall");
                secondary = Loc.T("Tool.Start");
                secondaryEnabled = false;
            }

            var tip = ToolCatalog.DisplayName(card.Manifest) + " · " + status;
            if (isInstalling)
                tip += " · " + Loc.T("Tool.TipInstalling");
            else if (!installed)
                tip += " · " + Loc.T("Tool.TipClickInstall");
            else if (canStart)
                tip += " · " + Loc.T("Tool.TipClickStart");
            else if (isRunning)
                tip += " · " + Loc.T("Tool.TipMenuStop");
            tip += " · " + Loc.T("Tool.TipRightUninstall");

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
                InstallPercent = percent,
                InstallProgressRatio = Math.Clamp(percent / 100.0, 0, 1),
                VisualState = visualState,
                CardBgHex = visualState switch
                {
                    ToolVisualStates.Running => "#DBEAFE",
                    ToolVisualStates.Installed => "#ECFDF5",
                    ToolVisualStates.Installing => "#EFF6FF",
                    _ => "#F3F4F6"
                },
                Tooltip = tip
            });
        }

        return rows;
    }

    public static string LocalizeKind(string kind) => kind.ToLowerInvariant() switch
    {
        SharedDeviceKinds.Speaker => Loc.T("Tool.SharedDevices.Speaker"),
        SharedDeviceKinds.Microphone => Loc.T("Tool.SharedDevices.Microphone"),
        SharedDeviceKinds.Camera => Loc.T("Tool.SharedDevices.Camera"),
        _ => kind
    };
}
