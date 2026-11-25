using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;

namespace FlyScreen.Helpers;

/// <summary>托盘「工具 → 锁屏」：布局内电脑（排除手机/平板）+「全部」。</summary>
public static class TrayLockMenu
{
    public static string LockLabel => Loc.T("Tray.Lock");
    public static string AllLabel => Loc.T("Tray.LockAll");

    /// <summary>布局槽位中可锁屏的电脑（空槽 / 手机 / 平板跳过）。</summary>
    public static IReadOnlyList<(string MachineName, string Label, bool IsLocal)> BuildItems(
        AppSettings settings,
        ConnectionManager? connections = null)
    {
        settings.EnsureLocalMachineSlot();
        var items = new List<(string, string, bool)>(4);
        for (var i = 0; i < 4; i++)
        {
            var name = i < settings.MachineSlots.Count ? settings.MachineSlots[i]?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!IsLockableComputer(name, connections)) continue;
            var local = IsLocalMachine(name);
            var label = local ? name + Loc.T("Tray.LocalSuffix") : name;
            items.Add((name, label, local));
        }

        return items;
    }

    /// <summary>向指定机器发锁屏：本机直接锁，对端发 <see cref="MessageType.LockScreen"/>。</summary>
    public static void LockMachine(
        ConnectionManager connections,
        string machineName,
        Action lockLocal)
    {
        if (string.IsNullOrWhiteSpace(machineName)) return;
        if (IsLocalMachine(machineName))
        {
            try { lockLocal(); } catch { /* ignore */ }
            return;
        }

        _ = connections.SendToAsync(machineName.Trim(), MessageType.LockScreen);
    }

    /// <summary>锁定布局内全部可锁屏电脑（本机 + 已连接对端）。</summary>
    public static void LockAll(
        AppSettings settings,
        ConnectionManager connections,
        Action lockLocal)
    {
        foreach (var (name, _, isLocal) in BuildItems(settings, connections))
        {
            if (isLocal)
            {
                try { lockLocal(); } catch { /* ignore */ }
                continue;
            }

            _ = connections.SendToAsync(name, MessageType.LockScreen);
        }
    }

    /// <summary>手机/平板不可锁屏；未知/旧端/桌面/大屏可锁。</summary>
    public static bool IsLockableComputer(string machineName, ConnectionManager? connections)
    {
        if (IsLocalMachine(machineName))
        {
            var localDc = LocalPeerIdentity.DeviceClass;
            return !IsPhoneOrTablet(localDc);
        }

        var info = connections?.GetPeerRemoteInfo(machineName);
        return !IsPhoneOrTablet(info?.DeviceClass);
    }

    private static bool IsPhoneOrTablet(string? deviceClass)
        => string.Equals(deviceClass, PeerDeviceClass.Phone, StringComparison.OrdinalIgnoreCase)
           || string.Equals(deviceClass, PeerDeviceClass.Tablet, StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalMachine(string name)
    {
        if (string.Equals(name, LocalPeerIdentity.MachineName, StringComparison.OrdinalIgnoreCase))
            return true;
        var env = Environment.MachineName;
        return !LocalPeerIdentity.IsBogusMachineName(env)
               && string.Equals(name, env, StringComparison.OrdinalIgnoreCase);
    }
}
