using System.Reflection;

namespace FlyScreen.Helpers;

/// <summary>关于页 / 握手等展示用版本号（与 AssemblyInformationalVersion / Version 对齐）。</summary>
public static class AppVersionDisplay
{
    public static string Current
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // 去掉可能附带的 +commit 元数据
                var plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info.Trim();
            }

            var v = asm.GetName().Version;
            if (v == null) return "0.0.0";
            return $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }
}
