using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace FlyScreen.Services;

/// <summary>
/// 松开拖放时，在放下文件的那台电脑弹出「替换或跳过」。
/// 用 WPF 自绘，避免 TaskDialog 在管理员进程里静默失败（看起来像没弹窗、直接取消）。
/// </summary>
internal static class EdgeDragConflictPrompt
{
    public static Task<EdgeDragConflictAction> AskAsync(EdgeDragConflictRequest request)
    {
        if (request.Conflicts.Count == 0)
            return Task.FromResult(EdgeDragConflictAction.Proceed);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return Task.FromResult(Show(request));
        if (dispatcher.CheckAccess())
            return Task.FromResult(Show(request));
        return dispatcher.InvokeAsync(() => Show(request)).Task;
    }

    private static EdgeDragConflictAction Show(EdgeDragConflictRequest request)
    {
        var result = EdgeDragConflictAction.Cancel;
        var one = request.Conflicts.Count == 1;
        var dest = FolderLabel(request.Directory);
        var source = string.IsNullOrWhiteSpace(request.SourceFolder) ? "对端" : request.SourceFolder.Trim();
        var detailsShown = false;

        var window = new Window
        {
            Title = "替换或跳过文件",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.SingleBorderWindow,
            Topmost = true,
            ShowInTaskbar = false,
            Background = Brushes.White,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI")
        };

        var root = new DockPanel { Margin = new Thickness(18, 14, 18, 12) };
        var body = new StackPanel();

        body.Children.Add(new TextBlock
        {
            Text = $"正在将 {Math.Max(request.TotalCount, request.Conflicts.Count)} 个项目从 {source} 复制到 {dest}",
            FontSize = 13,
            Foreground = Brush(0x33, 0x41, 0x55),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        body.Children.Add(new TextBlock
        {
            Text = one
                ? $"目标已包含一个名为“{request.Conflicts[0]}”的文件"
                : $"目标已包含 {request.Conflicts.Count} 个同名项目",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(0x0F, 0x17, 0x2A),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        body.Children.Add(CommandLink(
            "✓",
            "替换目标中的文件",
            null,
            () =>
            {
                result = EdgeDragConflictAction.Replace;
                window.Close();
            }));
        body.Children.Add(CommandLink(
            "↻",
            one ? "跳过该文件" : "跳过这些文件",
            null,
            () =>
            {
                result = EdgeDragConflictAction.Skip;
                window.Close();
            }));

        var detailsBox = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = Brush(0xF8, 0xFA, 0xFC),
            BorderBrush = Brush(0xE2, 0xE8, 0xF0),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 8, 0, 0),
            Child = new TextBlock
            {
                Text = BuildCompare(request),
                FontSize = 12,
                Foreground = Brush(0x47, 0x55, 0x69),
                TextWrapping = TextWrapping.Wrap
            }
        };

        body.Children.Add(CommandLink(
            "⇄",
            "比较两个文件的信息",
            null,
            () =>
            {
                detailsShown = true;
                detailsBox.Visibility = Visibility.Visible;
                if (detailsBox.Child is TextBlock block)
                    block.Text = BuildCompare(request);
            }));
        body.Children.Add(detailsBox);

        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        var more = new System.Windows.Controls.Button
        {
            Content = "更多详细信息",
            Padding = new Thickness(8, 4, 8, 4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Foreground = Brush(0x25, 0x63, 0xEB),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        more.Click += (_, _) =>
        {
            detailsShown = !detailsShown;
            detailsBox.Visibility = detailsShown ? Visibility.Visible : Visibility.Collapsed;
            if (detailsBox.Child is TextBlock block)
                block.Text = detailsShown ? BuildDetails(request) : BuildCompare(request);
            more.Content = detailsShown ? "简略信息" : "更多详细信息";
        };
        DockPanel.SetDock(more, Dock.Left);
        footer.Children.Add(more);

        root.Children.Add(body);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        // Dock 顺序：先 Bottom，再填满区
        root.Children.Clear();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(body);

        window.Content = root;
        window.Activated += (_, _) => window.Topmost = true;
        try
        {
            window.ShowDialog();
        }
        catch
        {
            result = EdgeDragConflictAction.Cancel;
        }

        return result;
    }

    private static Border CommandLink(string glyph, string title, string? subtitle, Action click)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontSize = 18,
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(0x16, 0xA3, 0x4A)
        });
        var texts = new StackPanel();
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            Foreground = Brush(0x0F, 0x17, 0x2A)
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            texts.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = Brush(0x64, 0x74, 0x8B),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }

        panel.Children.Add(texts);

        var border = new Border
        {
            Child = panel,
            Padding = new Thickness(10, 10, 10, 10),
            Margin = new Thickness(0, 0, 0, 4),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand
        };
        border.MouseEnter += (_, _) => border.Background = Brush(0xEF, 0xF6, 0xFF);
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        border.MouseLeftButtonUp += (_, _) => click();
        return border;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
        => new(Color.FromRgb(r, g, b));

    private static string FolderLabel(string directory)
    {
        try
        {
            var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch
        {
            // 用原路径
        }

        return string.IsNullOrWhiteSpace(directory) ? "目标文件夹" : directory;
    }

    private static string BuildDetails(EdgeDragConflictRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"目标文件夹：{request.Directory}");
        if (!string.IsNullOrWhiteSpace(request.SourceFolder))
            sb.AppendLine($"源文件夹：{request.SourceFolder}");
        foreach (var name in request.Conflicts.Take(12))
        {
            sb.AppendLine();
            sb.AppendLine(name);
            sb.AppendLine("  正在复制：" + DescribeSource(request.SourceItems, name));
            sb.AppendLine("  已经存在：" + DescribeDest(request.Directory, name));
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildCompare(EdgeDragConflictRequest request)
    {
        var sb = new StringBuilder();
        foreach (var name in request.Conflicts.Take(8))
        {
            sb.AppendLine($"文件：{name}");
            sb.AppendLine("  正在复制的文件  " + DescribeSource(request.SourceItems, name));
            sb.AppendLine("  已有的文件      " + DescribeDest(request.Directory, name));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string DescribeSource(IReadOnlyList<EdgeDragItemInfo> items, string name)
    {
        foreach (var item in items)
        {
            if (!string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            return Format(item.Size, item.LastWriteUtcTicks, item.IsDirectory);
        }

        return "未知";
    }

    private static string DescribeDest(string directory, string name)
    {
        try
        {
            var path = Path.Combine(directory, name);
            if (Directory.Exists(path))
                return Format(-1, Directory.GetLastWriteTimeUtc(path).Ticks, true);
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                return Format(fi.Length, fi.LastWriteTimeUtc.Ticks, false);
            }
        }
        catch
        {
            // 读不到
        }

        return "未知";
    }

    private static string Format(long size, long utcTicks, bool folder)
    {
        var time = utcTicks > 0
            ? new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime()
                .ToString("yyyy/M/d HH:mm", CultureInfo.InvariantCulture)
            : "未知时间";
        if (folder) return $"文件夹，{time}";
        if (size < 0) return time;
        if (size < 1024) return $"{size} 字节，{time}";
        double n = size;
        string[] units = ["KB", "MB", "GB", "TB"];
        var u = -1;
        do
        {
            n /= 1024;
            u++;
        } while (n >= 1024 && u < units.Length - 1);

        return $"{n:0.##} {units[u]}，{time}";
    }
}
