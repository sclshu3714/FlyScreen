using FlyScreen.Services.Network;

namespace FlyScreen.Services.Programs;

/// <summary>玄鸟宿主产品形态（与 OS 名无关；靠握手 Cap 区分）。</summary>
public enum ProgramHostKind
{
    Unknown = 0,
    WinWpf = 1,
    MauiWindows = 2,
    MauiMobile = 3,
    MauiMacCatalyst = 4,
    AvaloniaDesktop = 5
}

/// <summary>子程序 UI 框架；<see cref="None"/> 表示纯脚本。</summary>
public enum ProgramUiKind
{
    None = 0,
    Wpf = 1,
    Maui = 2,
    Avalonia = 3
}

/// <summary>本机 HostKind / 默认 UiKind / 能否跑某 UiKind / 从 Caps 解析对端。</summary>
public static class ProgramUiCompatibility
{
    private static ProgramHostKind? _override;

    /// <summary>宿主启动时注入；未注入时从本机 Caps / TFM 启发式推断。</summary>
    public static void ConfigureLocalHost(ProgramHostKind kind)
        => _override = kind;

    public static ProgramHostKind LocalHostKind
        => _override ?? InferLocalHostKind();

    public static ProgramUiKind DefaultCreateUiKind(ProgramHostKind host) => host switch
    {
        ProgramHostKind.WinWpf => ProgramUiKind.Wpf,
        ProgramHostKind.MauiWindows => ProgramUiKind.Maui,
        ProgramHostKind.MauiMobile => ProgramUiKind.Maui,
        ProgramHostKind.MauiMacCatalyst => ProgramUiKind.Maui,
        ProgramHostKind.AvaloniaDesktop => ProgramUiKind.Avalonia,
        _ => ProgramUiKind.None
    };

    public static ProgramUiKind DefaultCreateUiKindForLocal()
        => DefaultCreateUiKind(LocalHostKind);

    public static bool CanRun(ProgramHostKind host, ProgramUiKind ui) => ui switch
    {
        ProgramUiKind.None => true,
        ProgramUiKind.Wpf => host is ProgramHostKind.WinWpf or ProgramHostKind.MauiWindows,
        ProgramUiKind.Maui => host is ProgramHostKind.MauiWindows
            or ProgramHostKind.MauiMobile
            or ProgramHostKind.MauiMacCatalyst,
        ProgramUiKind.Avalonia => host == ProgramHostKind.AvaloniaDesktop,
        _ => false
    };

    public static bool CanRunLocal(ProgramUiKind ui) => CanRun(LocalHostKind, ui);

    public static bool PeerCanRun(IEnumerable<string>? caps, ProgramUiKind ui)
        => CanRun(FromCaps(caps), ui);

    public static ProgramHostKind FromCaps(IEnumerable<string>? caps)
    {
        if (caps == null) return ProgramHostKind.Unknown;
        var set = caps as ISet<string> ?? new HashSet<string>(caps, StringComparer.OrdinalIgnoreCase);
        if (Contains(set, PeerCaps.ProgHostWinWpf)) return ProgramHostKind.WinWpf;
        if (Contains(set, PeerCaps.ProgHostMauiWin)) return ProgramHostKind.MauiWindows;
        if (Contains(set, PeerCaps.ProgHostMauiMobile)) return ProgramHostKind.MauiMobile;
        if (Contains(set, PeerCaps.ProgHostMauiMac)) return ProgramHostKind.MauiMacCatalyst;
        if (Contains(set, PeerCaps.ProgHostAvalonia)) return ProgramHostKind.AvaloniaDesktop;
        return ProgramHostKind.Unknown;
    }

    public static string CapFor(ProgramHostKind host) => host switch
    {
        ProgramHostKind.WinWpf => PeerCaps.ProgHostWinWpf,
        ProgramHostKind.MauiWindows => PeerCaps.ProgHostMauiWin,
        ProgramHostKind.MauiMobile => PeerCaps.ProgHostMauiMobile,
        ProgramHostKind.MauiMacCatalyst => PeerCaps.ProgHostMauiMac,
        ProgramHostKind.AvaloniaDesktop => PeerCaps.ProgHostAvalonia,
        _ => ""
    };

    /// <summary>把 ProgHost Cap 并入握手 Caps（宿主 Configure 时用）。</summary>
    public static string[] WithProgHost(IEnumerable<string> baseline, ProgramHostKind host)
    {
        var cap = CapFor(host);
        var list = baseline
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .ToList();
        if (!string.IsNullOrEmpty(cap)
            && !list.Any(c => string.Equals(c, cap, StringComparison.OrdinalIgnoreCase)))
            list.Add(cap);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string DisplayName(ProgramUiKind ui) => ui switch
    {
        ProgramUiKind.Wpf => "WPF",
        ProgramUiKind.Maui => "MAUI",
        ProgramUiKind.Avalonia => "Avalonia",
        _ => "None"
    };

    public static string ShareHint(ProgramUiKind ui) => ui switch
    {
        ProgramUiKind.Wpf => "Win WPF、Maui-Win",
        ProgramUiKind.Maui => "Maui-Win、手机/平板、Maui Mac",
        ProgramUiKind.Avalonia => "Mac / Linux Avalonia 桌面",
        _ => ""
    };

    public static ProgramUiKind ParseUiKind(int value)
        => Enum.IsDefined(typeof(ProgramUiKind), value) ? (ProgramUiKind)value : ProgramUiKind.None;

    private static bool Contains(ISet<string> set, string cap)
        => set.Contains(cap) || set.Any(c => string.Equals(c, cap, StringComparison.OrdinalIgnoreCase));

    private static ProgramHostKind InferLocalHostKind()
    {
        var fromCaps = FromCaps(LocalPeerIdentity.Caps);
        if (fromCaps != ProgramHostKind.Unknown)
            return fromCaps;

        if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            return ProgramHostKind.MauiMobile;
        if (OperatingSystem.IsMacOS())
            return ProgramHostKind.AvaloniaDesktop;
        if (OperatingSystem.IsLinux())
            return ProgramHostKind.AvaloniaDesktop;
        if (OperatingSystem.IsWindows())
            return ProgramHostKind.WinWpf; // 未宣告 Cap 时保守按 WPF；Maui-Win 须 ConfigureLocalHost
        return ProgramHostKind.Unknown;
    }
}
