using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlyScreen.Setup.Services;

/// <summary>
/// 缺少轮播图、许可、安装清单时自动生成默认资源。
/// </summary>
public static class AssetGenerator
{
    // 对齐安装包原型：青绿渐变轮播 + 左文案右示意
    private static readonly (string File, string Title, string Subtitle, Color From, Color To)[] DefaultSlides =
    [
        ("01-welcome.png", "无缝协同", "多电脑像一台在用",
            Color.FromRgb(0x0B, 0x3D, 0x3A), Color.FromRgb(0x0F, 0x7A, 0x5A)),
        ("02-sync.png", "键鼠共享", "光标自然跨屏移动",
            Color.FromRgb(0x0A, 0x3A, 0x4A), Color.FromRgb(0x12, 0x7A, 0x8A)),
        ("03-secure.png", "剪贴板同步", "文本与文件随手即达",
            Color.FromRgb(0x1A, 0x3A, 0x2A), Color.FromRgb(0x2A, 0x8A, 0x55))
    ];

    public static void EnsurePublisherAssets(string publisherAssetsDir, Action<string>? log = null)
    {
        Directory.CreateDirectory(publisherAssetsDir);
        var carousel = Path.Combine(publisherAssetsDir, "carousel");
        Directory.CreateDirectory(carousel);
        EnsureCarousel(carousel, log);

        var license = Path.Combine(publisherAssetsDir, "license.txt");
        if (!File.Exists(license))
        {
            File.WriteAllText(license, DefaultLicenseText(), Encoding.UTF8);
            log?.Invoke($"已生成默认许可协议: {license}");
        }
    }

    public static void EnsureInstallerAssets(
        string installerDir,
        string? sourceCarouselDir,
        string? sourceLicenseFile,
        Action<string>? log = null)
    {
        var assets = Path.Combine(installerDir, "Assets");
        var carousel = Path.Combine(assets, "carousel");
        Directory.CreateDirectory(carousel);

        if (!string.IsNullOrWhiteSpace(sourceCarouselDir) && Directory.Exists(sourceCarouselDir))
        {
            foreach (var file in Directory.EnumerateFiles(sourceCarouselDir, "*.png"))
            {
                var dest = Path.Combine(carousel, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }
            log?.Invoke($"已同步轮播图: {sourceCarouselDir} → {carousel}");
        }

        EnsureCarousel(carousel, log);

        var licenseDest = Path.Combine(assets, "license.txt");
        if (!string.IsNullOrWhiteSpace(sourceLicenseFile) && File.Exists(sourceLicenseFile))
        {
            File.Copy(sourceLicenseFile, licenseDest, overwrite: true);
            log?.Invoke($"已同步许可协议: {sourceLicenseFile}");
        }
        else if (!File.Exists(licenseDest))
        {
            File.WriteAllText(licenseDest, DefaultLicenseText(), Encoding.UTF8);
            log?.Invoke($"已生成默认许可协议: {licenseDest}");
        }

        // 复制应用图标（若有）
        var iconSrc = Path.GetFullPath(Path.Combine(installerDir, "..", "..", "src", "FlyScreen.Assets", "Icons", "app.ico"));
        var iconDest = Path.Combine(assets, "app.ico");
        if (File.Exists(iconSrc) && !File.Exists(iconDest))
        {
            File.Copy(iconSrc, iconDest, overwrite: true);
            log?.Invoke($"已复制应用图标: {iconDest}");
        }
    }

    public static void EnsureCarousel(string carouselDir, Action<string>? log = null)
    {
        Directory.CreateDirectory(carouselDir);
        var existing = Directory.GetFiles(carouselDir, "*.png");
        if (existing.Length > 0)
            return;
        RegenerateCarousel(carouselDir, log);
    }

    /// <summary>按原型风格强制重绘三张默认轮播图。</summary>
    public static void RegenerateCarousel(string carouselDir, Action<string>? log = null)
    {
        Directory.CreateDirectory(carouselDir);
        foreach (var slide in DefaultSlides)
        {
            var path = Path.Combine(carouselDir, slide.File);
            WriteSlidePng(path, slide.Title, slide.Subtitle, slide.From, slide.To);
            log?.Invoke($"已生成轮播图: {path}");
        }
    }

    public static void EnsureInstallerProject(string installerDir, Action<string>? log = null)
    {
        Directory.CreateDirectory(installerDir);
        var csproj = Path.Combine(installerDir, "FlyScreen.Installer.csproj");
        if (!File.Exists(csproj))
        {
            throw new InvalidOperationException(
                $"安装工程不存在: {csproj}。请确认 setup/FlyScreen.Installer 已纳入仓库。");
        }

        // 保证关键代码文件存在（防止被误删）
        var required = new[]
        {
            "App.xaml", "App.xaml.cs",
            "MainWindow.xaml", "MainWindow.xaml.cs",
            "Controls/CarouselView.xaml", "Controls/CarouselView.xaml.cs",
            "Services/InstallService.cs", "Services/DiskSpace.cs", "Services/ManifestLoader.cs"
        };

        foreach (var rel in required)
        {
            var full = Path.Combine(installerDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full))
                log?.Invoke($"警告: 安装工程缺少文件 {rel}，请从仓库恢复。");
        }
    }

    private static void WriteSlidePng(string path, string title, string subtitle, Color from, Color to)
    {
        const int w = 720;
        const int h = 320;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new LinearGradientBrush(from, to, new Point(0, 0.5), new Point(1, 0.5));
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));

            dc.DrawEllipse(
                new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF)),
                null,
                new Point(w * 0.78, h * 0.42),
                130,
                130);

            var uiFont = new FontFamily("Microsoft YaHei UI");
            var titleText = new FormattedText(
                title,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(uiFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                40,
                Brushes.White,
                96);
            dc.DrawText(titleText, new Point(40, 108));

            var subText = new FormattedText(
                subtitle,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(uiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                18,
                new SolidColorBrush(Color.FromRgb(0xD7, 0xF5, 0xE8)),
                96);
            dc.DrawText(subText, new Point(42, 168));

            var stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xD0, 0xE8, 0xFF, 0xF5)), 2.2);
            stroke.Freeze();
            DrawDeviceGlyphs(dc, stroke, w * 0.62, h * 0.22);
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void DrawDeviceGlyphs(DrawingContext dc, Pen stroke, double x, double y)
    {
        dc.DrawRoundedRectangle(null, stroke, new Rect(x, y + 36, 110, 72), 6, 6);
        dc.DrawLine(stroke, new Point(x + 40, y + 118), new Point(x + 70, y + 118));
        dc.DrawLine(stroke, new Point(x + 30, y + 128), new Point(x + 80, y + 128));

        dc.DrawRoundedRectangle(null, stroke, new Rect(x + 140, y + 48, 36, 68), 6, 6);
        dc.DrawEllipse(null, stroke, new Point(x + 158, y + 106), 2.5, 2.5);

        var cloud = new StreamGeometry();
        using (var ctx = cloud.Open())
        {
            ctx.BeginFigure(new Point(x + 70, y + 28), false, false);
            ctx.ArcTo(new Point(x + 100, y + 18), new Size(18, 14), 0, false, SweepDirection.Clockwise, true, false);
            ctx.ArcTo(new Point(x + 130, y + 28), new Size(18, 14), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(x + 130, y + 42), true, false);
            ctx.LineTo(new Point(x + 70, y + 42), true, false);
            ctx.Close();
        }
        cloud.Freeze();
        dc.DrawGeometry(null, stroke, cloud);

        dc.DrawLine(stroke, new Point(x + 55, y + 70), new Point(x + 85, y + 48));
        dc.DrawLine(stroke, new Point(x + 158, y + 70), new Point(x + 125, y + 48));
    }

    /// <summary>缺失 license.txt 时写入的默认免责声明。</summary>
    public static string DefaultLicenseText() =>
        """
        玄鸟（FlyScreen）软件免责声明与使用须知

        重要提示：安装、复制或使用本软件前，请仔细阅读本声明。
        勾选「我已阅读并同意」即表示您已充分理解并接受以下全部内容。

        一、软件性质
        本软件按「现状（AS IS）」提供，不附带任何明示或暗示的保证，包括但不限于
        适销性、特定用途适用性、不侵权及持续可用的保证。

        二、免责声明
        在适用法律允许的最大范围内，作者及贡献者不对因安装、使用、无法使用或
        依赖本软件而产生的任何直接、间接、附带、特殊、惩罚性或后果性损害承担责任，
        包括但不限于数据丢失、业务中断、设备故障、隐私泄露、安全风险或利润损失，
        无论该等主张基于合同、侵权（含过失）或其他理论，即使事先已被告知可能发生损害。

        三、使用风险自担
        您自行承担使用本软件的全部风险。跨设备键鼠、剪贴板、文件传输等功能可能
        涉及本机与对端设备的系统权限与数据访问；请仅在您信任的设备与网络环境中使用，
        并自行做好备份与安全防护。

        四、合法使用
        您承诺仅将本软件用于合法目的，不得用于未经授权访问他人设备、窃取数据或
        其他违法违规行为。因您违规使用所产生的一切责任由您自行承担。

        五、第三方组件
        本软件可能包含或依赖第三方库、驱动与工具；其权利与责任以各自许可为准。
        作者不对第三方组件的缺陷或变更承担责任。

        六、更新与变更
        本软件可能检查或提供更新。作者有权随时修改、中止或终止软件功能，且无义务
        提供支持、维护或新版本。本声明内容可能随版本更新，以安装包内文本为准。

        七、其他
        若本声明任何条款被认定无效，不影响其余条款效力。
        项目信息与开源说明可通过产品内许可链接访问。

        若您不同意本声明，请立即停止安装并删除本软件。
        """;
}
