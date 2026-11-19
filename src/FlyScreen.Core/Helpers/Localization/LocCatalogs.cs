namespace FlyScreen.Helpers.Localization;

/// <summary>各语言完整词条（与 Loc 中文键一致）。</summary>
internal static partial class LocCatalogs
{
    public static Dictionary<string, string> Clone(Dictionary<string, string> src)
        => new(src, StringComparer.Ordinal);

    public static void Overlay(Dictionary<string, string> target, params (string Key, string Value)[] pairs)
    {
        foreach (var (k, v) in pairs)
            target[k] = v;
    }

    /// <summary>Clone English (or base) then overlay a full translation set.</summary>
    public static Dictionary<string, string> Complete(
        Dictionary<string, string> en,
        params (string Key, string Value)[] pairs)
    {
        var t = Clone(en);
        Overlay(t, pairs);
        return t;
    }

    public static Dictionary<string, string> GenericFromEn(Dictionary<string, string> en, params (string, string)[] pairs)
        => Complete(en, pairs);
}
