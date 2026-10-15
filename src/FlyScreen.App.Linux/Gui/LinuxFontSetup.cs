using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace FlyScreen.LinuxApp.Gui;

/// <summary>
/// 默认嵌入 Inter 保证可启动；把 Windows 雅黑 / 系统 CJK 挂进 FontManager 回退。
/// </summary>
internal static class LinuxFontSetup
{
    public const string InterFamilyKey = "fonts:Inter#Inter";
    public const string CjkCollectionKey = "fonts:FlyScreenCjk";

    public static string UiFontFamily { get; private set; } =
        $"{InterFamilyKey}, Inter, sans-serif";

    public static AppBuilder WithCjkFontSupport(this AppBuilder builder)
    {
        var cjkPaths = CollectCjkFontPaths();

        return builder
            .WithInterFont()
            .ConfigureFonts(fm =>
            {
                foreach (var path in cjkPaths)
                {
                    if (!TryAddFileFontCollection(fm, path))
                        continue;
                    Console.WriteLine($"已加载 CJK 字体文件: {path}");
                    break;
                }
            })
            .AfterSetup(_ =>
            {
                UiFontFamily = string.Join(", ",
                [
                    $"{CjkCollectionKey}#Microsoft YaHei",
                    $"{CjkCollectionKey}#Microsoft YaHei UI",
                    "Microsoft YaHei",
                    "Microsoft YaHei UI",
                    "Noto Sans CJK SC",
                    "Noto Sans SC",
                    "WenQuanYi Micro Hei",
                    InterFamilyKey,
                    "Inter",
                    "sans-serif"
                ]);
                Console.WriteLine($"字体 UI: {UiFontFamily}");
            })
            .With(new FontManagerOptions
            {
                DefaultFamilyName = InterFamilyKey,
                FontFallbacks =
                [
                    new FontFallback { FontFamily = new FontFamily($"{CjkCollectionKey}#Microsoft YaHei") },
                    new FontFallback { FontFamily = new FontFamily($"{CjkCollectionKey}#Microsoft YaHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei") },
                    new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans CJK SC") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans SC") },
                    new FontFallback { FontFamily = new FontFamily("WenQuanYi Micro Hei") },
                    new FontFallback { FontFamily = new FontFamily("WenQuanYi Zen Hei") },
                    new FontFallback { FontFamily = new FontFamily("Droid Sans Fallback") },
                    new FontFallback { FontFamily = new FontFamily("SimHei") },
                    new FontFallback { FontFamily = new FontFamily("sans-serif") }
                ]
            });
    }

    private static bool TryAddFileFontCollection(FontManager fm, string path)
    {
        try
        {
            // file:///abs/path/font.ttc
            var full = Path.GetFullPath(path).Replace('\\', '/');
            if (!full.StartsWith('/'))
                full = "/" + full;
            var fileUri = new Uri("file://" + full, UriKind.Absolute);
            fm.AddFontCollection(new EmbeddedFontCollection(
                new Uri(CjkCollectionKey, UriKind.Absolute),
                fileUri));
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载字体失败 {path}: {ex.Message}");
            return false;
        }
    }

    private static List<string> CollectCjkFontPaths()
    {
        var list = new List<string>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return list;

        try
        {
            foreach (var home in CandidateHomeDirs())
            {
                var destDir = Path.Combine(home, ".local", "share", "fonts", "flyscreen");
                Directory.CreateDirectory(destDir);

                foreach (var (src, fileName) in CandidateWindowsFonts())
                {
                    if (!File.Exists(src)) continue;
                    var dest = Path.Combine(destDir, fileName);
                    try
                    {
                        if (!File.Exists(dest) || new FileInfo(src).Length != new FileInfo(dest).Length)
                        {
                            File.Copy(src, dest, overwrite: true);
                            Console.WriteLine($"已复制中文字体: {src} → {dest}");
                        }

                        list.Add(dest);
                        list.Add(src);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"复制字体失败: {ex.Message}");
                        if (File.Exists(src))
                            list.Add(src);
                    }
                }

                if (list.Count > 0)
                    break;
            }

            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "fc-cache",
                    Arguments = "-f",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                p?.WaitForExit(20000);
            }
            catch
            {
                // ignore
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"注册中文字体失败: {ex.Message}");
        }

        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> CandidateHomeDirs()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            yield return profile;

        var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
        if (!string.IsNullOrWhiteSpace(sudoUser) && sudoUser != "root")
            yield return Path.Combine("/home", sudoUser);

        yield return "/root";
    }

    private static IEnumerable<(string Path, string FileName)> CandidateWindowsFonts()
    {
        foreach (var root in new[] { "/mnt/c/Windows/Fonts", "/mnt/c/windows/Fonts" })
        {
            yield return (Path.Combine(root, "msyh.ttc"), "msyh.ttc");
            yield return (Path.Combine(root, "msyh.ttf"), "msyh.ttf");
            yield return (Path.Combine(root, "simhei.ttf"), "simhei.ttf");
        }
    }
}
