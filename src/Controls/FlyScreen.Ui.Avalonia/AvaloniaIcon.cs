using Avalonia.Controls;
using Avalonia.Media;
using FlyScreen.Ui.Theme.Icons;

namespace FlyScreen.Ui.Avalonia;

/// <summary>将 <see cref="IconCatalog"/> 注册为 Avalonia StreamGeometry（含 Geo* 兼容键）。</summary>
public static class AvaloniaIcon
{
    private static readonly object Gate = new();
    private static WeakReference? _registeredAppResources;

    public static StreamGeometry Parse(string iconCode)
        => StreamGeometry.Parse(IconCatalog.GetPath(iconCode));

    public static void RegisterTo(IResourceDictionary resources)
    {
        foreach (var (code, path) in IconCatalog.AllPaths)
            resources[code] = StreamGeometry.Parse(path);

        foreach (var (legacy, code) in IconCatalog.AllLegacyAliases)
        {
            if (IconCatalog.TryGetPath(code, out var path))
                resources[legacy] = StreamGeometry.Parse(path);
        }
    }

    /// <summary>注册到 Application.Current.Resources（幂等）；无 Application 时忽略。</summary>
    public static void RegisterToApplication()
    {
        if (global::Avalonia.Application.Current?.Resources is not { } rd)
            return;

        lock (Gate)
        {
            if (_registeredAppResources?.Target as IResourceDictionary == rd)
                return;
            RegisterTo(rd);
            _registeredAppResources = new WeakReference(rd);
        }
    }
}
