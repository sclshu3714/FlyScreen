using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace FlyScreen.Helpers;

/// <summary>
/// 通过当前用户 Run 注册表项控制开机自启。
/// </summary>
public static class AutoStartHelper
{
    public const string AutostartArg = "--autostart";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FlyScreen";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key == null) return false;

            if (enabled)
            {
                var exe = ResolveExecutablePath();
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
                    return false;
                key.SetValue(ValueName, $"\"{exe}\" {AutostartArg}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsAutostartLaunch(IEnumerable<string>? args)
        => args != null && args.Any(a =>
            string.Equals(a, AutostartArg, StringComparison.OrdinalIgnoreCase));

    private static string? ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return path;

        try
        {
            return Process.GetCurrentProcess().MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }
}
