using System.Reflection;
using FlyScreen.ToolSdk;

namespace FlyScreen.Services.Tools;

/// <summary>已加载工具程序集贡献的图标（按 IconKey）。</summary>
public static class ToolIconRegistry
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IToolIconContribution> ByKey =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, byte[]> BitmapCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(IToolIconContribution contribution)
    {
        if (contribution == null || string.IsNullOrWhiteSpace(contribution.IconKey))
            return;
        var key = contribution.IconKey.Trim();
        lock (Gate)
        {
            ByKey[key] = contribution;
            BitmapCache.Remove(key);
        }
    }

    /// <summary>卸载工具时移除贡献实例（保留位图缓存亦可；此处一并清掉以免陈旧）。</summary>
    public static void Unregister(string? iconKey)
    {
        if (string.IsNullOrWhiteSpace(iconKey)) return;
        var key = iconKey.Trim();
        lock (Gate)
        {
            ByKey.Remove(key);
            BitmapCache.Remove(key);
        }
    }

    public static bool TryGet(string? iconKey, out IToolIconContribution contribution)
    {
        contribution = null!;
        if (string.IsNullOrWhiteSpace(iconKey)) return false;
        lock (Gate)
            return ByKey.TryGetValue(iconKey.Trim(), out contribution!);
    }

    /// <summary>读取并缓存位图字节；未注册或无流时返回 null。</summary>
    public static byte[]? TryReadBitmapBytes(string? iconKey)
    {
        if (string.IsNullOrWhiteSpace(iconKey)) return null;
        var key = iconKey.Trim();
        lock (Gate)
        {
            if (BitmapCache.TryGetValue(key, out var cached))
                return cached;
            if (!ByKey.TryGetValue(key, out var c))
                return null;
            try
            {
                using var s = c.OpenBitmap();
                if (s == null) return null;
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                BitmapCache[key] = bytes;
                return bytes;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"读取工具图标失败 {key}: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>扫描已加载的 <c>FlyScreen.Tool.*</c> 程序集中的 <see cref="IToolIconContribution"/>。</summary>
    public static void EnsureFromLoadedAssemblies()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = asm.GetName().Name;
            if (name == null || !name.StartsWith("FlyScreen.Tool.", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Contains(".Ui.", StringComparison.OrdinalIgnoreCase))
                continue;

            Type[] types;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var type in types)
            {
                if (type.IsAbstract || !typeof(IToolIconContribution).IsAssignableFrom(type))
                    continue;
                try
                {
                    IToolIconContribution? icon = null;
                    var instProp = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    if (instProp?.GetValue(null) is IToolIconContribution fromProp)
                        icon = fromProp;
                    else if (Activator.CreateInstance(type) is IToolIconContribution created)
                        icon = created;
                    if (icon != null)
                        Register(icon);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }
}
