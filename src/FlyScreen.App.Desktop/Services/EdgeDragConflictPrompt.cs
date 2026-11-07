using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using FlyScreen.Services.Clipboard;
using FlyScreen.Services.Network;

namespace FlyScreen.DesktopUi.Services;

/// <summary>Mac / Linux：仿资源管理器「替换或跳过文件」布局。</summary>
public static class EdgeDragConflictPrompt
{
    public static Task<EdgeDragConflictAction> AskAsync(EdgeDragConflictRequest request)
    {
        if (request.Conflicts.Count == 0)
            return Task.FromResult(EdgeDragConflictAction.Proceed);
        return Dispatcher.UIThread.InvokeAsync(() => Show(request));
    }

    private static async Task<EdgeDragConflictAction> Show(EdgeDragConflictRequest request)
    {
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
            ?.MainWindow;
        var result = EdgeDragConflictAction.Cancel;
        var one = request.Conflicts.Count == 1;
        var dest = FolderLabel(request.Directory);
        var source = string.IsNullOrWhiteSpace(request.SourceFolder) ? "对端" : request.SourceFolder.Trim();
        var detailsVisible = false;

        var window = new Window
        {
            Title = "替换或跳过文件",
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            CanResize = false,
            Topmost = true,
            ShowInTaskbar = false,
            Background = Brushes.White
        };

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16), Spacing = 10 };
        root.Children.Add(new TextBlock
        {
            Text = $"正在将 {Math.Max(request.TotalCount, request.Conflicts.Count)} 个项目从 {source} 复制到 {dest}",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(new TextBlock
        {
            Text = one
                ? $"目标已包含一个名为“{request.Conflicts[0]}”的文件"
                : $"目标已包含 {request.Conflicts.Count} 个同名项目",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x0F, 0x17, 0x2A)),
            TextWrapping = TextWrapping.Wrap
        });

        var links = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
        links.Children.Add(CommandLink("✓  替换目标中的文件", () =>
        {
            result = EdgeDragConflictAction.Replace;
            window.Close();
        }));
        links.Children.Add(CommandLink(one ? "↻  跳过该文件" : "↻  跳过这些文件", () =>
        {
            result = EdgeDragConflictAction.Skip;
            window.Close();
        }));

        var details = new TextBlock
        {
            IsVisible = false,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
            TextWrapping = TextWrapping.Wrap,
            Text = BuildDetails(request)
        };

        links.Children.Add(CommandLink("⇄  比较两个文件的信息", () =>
        {
            detailsVisible = !detailsVisible;
            details.IsVisible = detailsVisible;
            details.Text = detailsVisible ? BuildCompare(request) : BuildDetails(request);
            if (detailsVisible)
                details.IsVisible = true;
        }));
        root.Children.Add(links);
        root.Children.Add(details);

        var more = new Button
        {
            Content = "更多详细信息",
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        more.Click += (_, _) =>
        {
            detailsVisible = !detailsVisible;
            details.IsVisible = detailsVisible;
            details.Text = BuildDetails(request);
            more.Content = detailsVisible ? "简略信息" : "更多详细信息";
        };
        var cancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(16, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        cancel.Click += (_, _) => window.Close();
        var footer = new Grid
        {
            Margin = new Thickness(0, 12, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        Grid.SetColumn(more, 0);
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(more);
        footer.Children.Add(cancel);
        root.Children.Add(footer);

        window.Content = root;
        if (owner != null)
            await window.ShowDialog(owner).ConfigureAwait(true);
        else
        {
            window.Show();
            var tcs = new TaskCompletionSource();
            window.Closed += (_, _) => tcs.TrySetResult();
            await tcs.Task.ConfigureAwait(true);
        }

        return result;
    }

    private static Button CommandLink(string text, Action click)
    {
        var button = new Button
        {
            Content = new TextBlock
            {
                Text = text,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static string FolderLabel(string directory)
    {
        var folder = directory.TrimEnd('/', '\\');
        var slash = folder.LastIndexOfAny(['/', '\\']);
        if (slash >= 0 && slash < folder.Length - 1)
            folder = folder[(slash + 1)..];
        return string.IsNullOrWhiteSpace(folder) ? "目标文件夹" : folder;
    }

    private static string BuildDetails(EdgeDragConflictRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"目标文件夹：{request.Directory}");
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
            ? new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy/M/d HH:mm", CultureInfo.InvariantCulture)
            : "未知时间";
        if (folder) return $"文件夹，{time}";
        if (size < 0) return time;
        if (size < 1024) return $"{size} 字节，{time}";
        double n = size;
        string[] units = ["KB", "MB", "GB", "TB"];
        var u = -1;
        do { n /= 1024; u++; } while (n >= 1024 && u < units.Length - 1);
        return $"{n:0.##} {units[u]}，{time}";
    }
}
