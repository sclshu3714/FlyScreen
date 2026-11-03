using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace FlyScreen.LinuxApp.Gui;

/// <summary>
/// 默认嵌入 Inter 保证可启动；CJK 使用开源字体：
/// 1) 程序目录 <c>Fonts/</c>（可随包附带 SIL OFL 的 Noto Sans SC 等）；
/// 2) 系统已安装的 Noto CJK / 文泉驿等。
/// 不复制、不捆绑 Windows 专有字体（微软雅黑 / 黑体）。
/// </summary>
internal static class LinuxFontSetup
{
    public const string InterFamilyKey = "fonts:Inter#Inter";
    public const string CjkCollectionKey = "fonts:FlyScreenCjk";

    public static string UiFontFamily { get; private set; } =
        $"{InterFamilyKey}, Inter, sans-serif";

    public static bool HasCjkFont { get; private set; }

    public static AppBuilder WithCjkFontSupport(this AppBuilder builder)
    {
        var cjkPaths = CollectCjkFontPaths();
        HasCjkFont = cjkPaths.Count > 0;
        string? loadedPath = null;
        string loadedFamily = "Noto Sans SC";

        return builder
            .WithInterFont()
            .ConfigureFonts(fm =>
            {
                foreach (var path in cjkPaths)
                {
                    if (!TryAddFileFontCollection(fm, path))
                        continue;
                    loadedPath = path;
                    loadedFamily = GuessFamilyName(path);
                    Console.WriteLine($"已加载 CJK 字体文件: {path}（族名约 {loadedFamily}）");
                    break;
                }
            })
            .AfterSetup(_ =>
            {
                if (loadedPath != null)
                {
                    UiFontFamily = string.Join(", ",
                    [
                        $"{CjkCollectionKey}#{loadedFamily}",
                        loadedFamily,
                        "Noto Sans CJK SC",
                        "Noto Sans SC",
                        "WenQuanYi Micro Hei",
                        "WenQuanYi Zen Hei",
                        InterFamilyKey,
                        "Inter",
                        "sans-serif"
                    ]);
                }
                else
                {
                    // 未找到字体文件时仍挂系统族名：用户 apt 安装后 fontconfig 可能已注册
                    UiFontFamily = string.Join(", ",
                    [
                        "Noto Sans CJK SC",
                        "Noto Sans SC",
                        "WenQuanYi Micro Hei",
                        "WenQuanYi Zen Hei",
                        "Droid Sans Fallback",
                        InterFamilyKey,
                        "Inter",
                        "sans-serif"
                    ]);
                    Console.WriteLine(
                        "未找到 CJK 字体文件，汉字可能显示为方框。请任选其一：\n" +
                        "  • Debian/Ubuntu: sudo apt install -y fonts-noto-cjk && fc-cache -fv\n" +
                        "  • Fedora:        sudo dnf install -y google-noto-sans-cjk-fonts\n" +
                        "  • Arch:          sudo pacman -S noto-fonts-cjk\n" +
                        "  • 或将 OFL 字体放入程序旁 Fonts/（见 docs/runbooks/build-linux.md）");
                }

                Console.WriteLine($"字体 UI: {UiFontFamily}");
            })
            .With(new FontManagerOptions
            {
                DefaultFamilyName = InterFamilyKey,
                FontFallbacks =
                [
                    new FontFallback { FontFamily = new FontFamily($"{CjkCollectionKey}#Noto Sans SC") },
                    new FontFallback { FontFamily = new FontFamily($"{CjkCollectionKey}#Noto Sans CJK SC") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans CJK SC") },
                    new FontFallback { FontFamily = new FontFamily("Noto Sans SC") },
                    new FontFallback { FontFamily = new FontFamily("WenQuanYi Micro Hei") },
                    new FontFallback { FontFamily = new FontFamily("WenQuanYi Zen Hei") },
                    new FontFallback { FontFamily = new FontFamily("Droid Sans Fallback") },
                    new FontFallback { FontFamily = new FontFamily("sans-serif") }
                ]
            });
    }

    private static bool TryAddFileFontCollection(FontManager fm, string path)
    {
        try
        {
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

    private static string GuessFamilyName(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Contains("wqy-microhei", StringComparison.OrdinalIgnoreCase))
            return "WenQuanYi Micro Hei";
        if (name.Contains("wqy-zenhei", StringComparison.OrdinalIgnoreCase))
            return "WenQuanYi Zen Hei";
        if (name.Contains("CJK", StringComparison.OrdinalIgnoreCase))
            return "Noto Sans CJK SC";
        if (name.Contains("NotoSansSC", StringComparison.OrdinalIgnoreCase)
            || name.Contains("NotoSans-SC", StringComparison.OrdinalIgnoreCase))
            return "Noto Sans SC";
        return "Noto Sans SC";
    }

    /// <summary>程序旁 Fonts/ → 系统开源字体目录；不复制专有字体。</summary>
    private static List<string> CollectCjkFontPaths()
    {
        var list = new List<string>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return list;

        try
        {
            foreach (var path in CandidateOpenCjkFontFiles())
            {
                if (File.Exists(path))
                    list.Add(path);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"探测 CJK 字体失败: {ex.Message}");
        }

        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> CandidateOpenCjkFontFiles()
    {
        foreach (var dir in CandidateFontDirs())
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                continue;

            string[] patterns =
            [
                "NotoSansSC-Regular.otf",
                "NotoSansSC-Regular.ttf",
                "NotoSansSC-VF.ttf",
                "NotoSansCJK-Regular.ttc",
                "NotoSansCJKsc-Regular.otf",
                "NotoSansCJKsc-Regular.ttf",
                "wqy-microhei.ttc",
                "wqy-zenhei.ttc",
                "NotoSansSC*.otf",
                "NotoSansSC*.ttf",
                "NotoSansCJK*.ttc",
                "NotoSansCJKsc*",
                "wqy-*.ttc"
            ];

            foreach (var pattern in patterns)
            {
                IEnumerable<string> hits;
                try
                {
                    hits = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (var hit in hits)
                    yield return hit;
            }
        }
    }

    private static IEnumerable<string> CandidateFontDirs()
    {
        var dirs = new List<string>();

        // 1) 随包 / 旁路：可再分发的 OFL 字体放这里
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
            dirs.Add(Path.Combine(baseDir, "Fonts"));

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var exeDir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(exeDir))
                    dirs.Add(Path.Combine(exeDir, "Fonts"));
            }
        }
        catch
        {
            // ignore
        }

        // 2) 系统包
        dirs.Add("/usr/share/fonts");
        dirs.Add("/usr/local/share/fonts");
        dirs.Add("/usr/share/fonts/opentype");
        dirs.Add("/usr/share/fonts/truetype");
        dirs.Add("/usr/share/fonts/google-noto-cjk");
        dirs.Add("/usr/share/fonts/noto-cjk");

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            dirs.Add(Path.Combine(profile, ".local", "share", "fonts"));

        return dirs;
    }
}
