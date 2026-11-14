using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;

namespace FlyScreen.Helpers;

/// <summary>托盘「切换机器」菜单项：按布局 4 格生成（空槽跳过）。</summary>
public static class TraySwitchMenu
{
    public static IReadOnlyList<(string MachineName, string Label, bool IsLocal)> BuildItems(AppSettings settings)
    {
        settings.EnsureLocalMachineSlot();
        var items = new List<(string, string, bool)>(4);
        for (var i = 0; i < 4; i++)
        {
            var name = i < settings.MachineSlots.Count ? settings.MachineSlots[i]?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            var local = IsLocalMachine(name);
            var label = local ? name + Loc.T("Tray.LocalSuffix") : name;
            items.Add((name, label, local));
        }

        return items;
    }

    public static string DisableLabel(bool disabled)
        => disabled ? "✓ " + Loc.T("Hot.Off") : Loc.T("Hot.Off");

    /// <summary>设置页/托盘写入切换热键模式，并维护禁用前记忆。</summary>
    public static void ApplyMode(AppSettings settings, int mode)
    {
        mode = mode is >= 0 and <= 2 ? mode : 0;
        if (mode == 2)
        {
            if (settings.SwitchHotkeyMode is 0 or 1)
                settings.SwitchHotkeyModeBeforeOff = settings.SwitchHotkeyMode;
            else if (settings.SwitchHotkeyModeBeforeOff is not (0 or 1))
                settings.SwitchHotkeyModeBeforeOff = 0;
        }
        else
            settings.SwitchHotkeyModeBeforeOff = mode;

        settings.SwitchHotkeyMode = mode;
    }

    /// <summary>切换「切换机器」快捷键禁用：2 ↔ 恢复到禁用前的 0/1。</summary>
    public static bool ToggleSwitchHotkeyDisabled(AppSettings settings)
    {
        if (settings.SwitchHotkeyMode == 2)
            return EnsureSwitchHotkeyEnabled(settings);

        SetSwitchHotkeyDisabled(settings);
        return true;
    }

    /// <summary>设为禁用并记住当前 F1–F4 / 1–4。</summary>
    public static void SetSwitchHotkeyDisabled(AppSettings settings)
    {
        if (settings.SwitchHotkeyMode is 0 or 1)
            settings.SwitchHotkeyModeBeforeOff = settings.SwitchHotkeyMode;
        else if (settings.SwitchHotkeyModeBeforeOff is not (0 or 1))
            settings.SwitchHotkeyModeBeforeOff = 0;
        settings.SwitchHotkeyMode = 2;
        settings.Save();
    }

    /// <summary>取消禁用，恢复到 F1–F4 或 1–4。返回是否发生了变更。</summary>
    public static bool EnsureSwitchHotkeyEnabled(AppSettings settings)
    {
        if (settings.SwitchHotkeyMode != 2) return false;
        settings.SwitchHotkeyMode = settings.SwitchHotkeyModeBeforeOff is 0 or 1
            ? settings.SwitchHotkeyModeBeforeOff
            : 0;
        settings.Save();
        return true;
    }

    private static bool IsLocalMachine(string name)
    {
        if (string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        var env = Environment.MachineName;
        return !LocalPeerIdentity.IsBogusMachineName(env)
               && string.Equals(name, env, StringComparison.OrdinalIgnoreCase);
    }
}
