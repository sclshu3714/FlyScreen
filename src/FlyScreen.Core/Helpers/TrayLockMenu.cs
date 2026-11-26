using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services;
using FlyScreen.Services.Network;

namespace FlyScreen.Helpers;

/// <summary>
/// 托盘「锁屏」子菜单：布局内电脑（排除手机/平板）+ 全部锁屏 / 全部解锁 / 全部关机。
/// </summary>
public static class TrayLockMenu
{
    public static string LockLabel => Loc.T("Tray.Lock");
    public static string AllLabel => Loc.T("Tray.LockAll");
    public static string UnlockAllLabel => Loc.T("Tray.UnlockAll");
    public static string ShutdownAllLabel => Loc.T("Tray.ShutdownAll");

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

    /// <summary>
    /// 准备全部解锁：仅<strong>对端电脑</strong>（跳过本机）。
    /// <paramref name="items"/> = 待发解锁的对端（已知锁屏或状态未知；已解锁跳过）；
    /// <paramref name="missing"/> = 布局内对端尚无本机密码记录（含已解锁），用于弹出维护表。
    /// </summary>
    public static void PrepareUnlockAll(
        AppSettings settings,
        ConnectionManager? connections,
        MachineUnlockCredentialStore store,
        out IReadOnlyList<(string MachineName, string Label, bool IsLocal)> items,
        out Dictionary<string, string> passwords,
        out IReadOnlyList<(string MachineName, string Label)> missing)
    {
        var remotes = BuildItems(settings, connections)
            .Where(x => !x.IsLocal)
            .ToList();
        items = remotes
            .Where(x => NeedsUnlock(connections, x.MachineName))
            .ToList();
        passwords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var miss = new List<(string, string)>();
        foreach (var (name, label, _) in remotes)
        {
            var saved = store.Get(name);
            if (!string.IsNullOrEmpty(saved))
                passwords[name] = saved!;
            else
                miss.Add((name, label));
        }

        missing = miss;
    }

    /// <summary>把弹窗填入的密码写入内存字典与本机库。</summary>
    public static void ApplyMissingPasswords(
        MachineUnlockCredentialStore store,
        Dictionary<string, string> passwords,
        IReadOnlyDictionary<string, string> filled)
    {
        foreach (var (name, password) in filled)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(password)) continue;
            passwords[name.Trim()] = password;
            store.Set(name, password);
        }

        try { store.Save(); } catch { /* ignore */ }
    }

    /// <summary>仅对端发 <see cref="MessageType.UnlockScreen"/>（本机不参与全部解锁）。</summary>
    public static void ExecuteUnlock(
        IReadOnlyList<(string MachineName, string Label, bool IsLocal)> items,
        IReadOnlyDictionary<string, string> passwords,
        ConnectionManager connections)
    {
        foreach (var (name, _, isLocal) in items)
        {
            if (isLocal) continue;
            if (!passwords.TryGetValue(name, out var password) || string.IsNullOrEmpty(password))
                continue;

            var payload = MessageCodec.JsonBytes(new UnlockScreenPayload { Password = password });
            _ = connections.SendToAsync(name, MessageType.UnlockScreen, payload);
        }
    }

    /// <summary>
    /// 同步全部解锁（适合 WPF ShowDialog）。本机不参与；已解锁对端不发解锁。
    /// 布局对端缺密码时仍弹维护表（即使当前无需解锁），便于安装后首次录入。
    /// </summary>
    public static void UnlockAll(
        AppSettings settings,
        ConnectionManager connections,
        MachineUnlockCredentialStore store,
        Func<IReadOnlyList<(string MachineName, string Label)>, IReadOnlyDictionary<string, string>?> promptMissingPasswords)
    {
        PrepareUnlockAll(settings, connections, store, out var items, out var passwords, out var missing);
        if (items.Count == 0 && missing.Count == 0) return;

        if (missing.Count > 0)
        {
            var filled = promptMissingPasswords(missing);
            if (filled == null) return;
            ApplyMissingPasswords(store, passwords, filled);
        }

        if (items.Count == 0) return;
        ExecuteUnlock(items, passwords, connections);
    }

    /// <summary>布局内全部可关机电脑（本机 + 已连接对端）。</summary>
    public static void ShutdownAll(
        AppSettings settings,
        ConnectionManager connections,
        Action shutdownLocal)
    {
        foreach (var (name, _, isLocal) in BuildItems(settings, connections))
        {
            if (isLocal)
            {
                try { shutdownLocal(); } catch { /* ignore */ }
                continue;
            }

            _ = connections.SendToAsync(name, MessageType.ShutdownComputer);
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

    /// <summary>已知已解锁则 false；未知或锁屏则 true。</summary>
    private static bool NeedsUnlock(ConnectionManager? connections, string machineName)
    {
        var locked = connections?.GetPeerScreenLocked(machineName);
        return locked != false;
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
