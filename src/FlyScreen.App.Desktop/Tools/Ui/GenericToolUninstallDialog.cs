using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FlyScreen.ToolSdk;

namespace FlyScreen.DesktopUi.Tools.Ui;

internal static class GenericToolUninstallDialog
{
    public static async Task<ToolUninstallAnswers> ShowAsync(Window? owner, string toolId, ToolUninstallUiSchema schema)
    {
        if (owner == null)
            return ToolUninstallAnswers.Cancel();

        var values = new Dictionary<string, string>(schema.Defaults, StringComparer.OrdinalIgnoreCase);
        var result = ToolUninstallAnswers.Cancel();
        var rebuilding = false;

        var win = new Window
        {
            Title = string.IsNullOrWhiteSpace(schema.Title) ? "卸载决策" : schema.Title,
            Width = 520,
            MinHeight = 240,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };

        var fieldHost = new StackPanel();
        var hint = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.IndianRed,
            IsVisible = false,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var root = new StackPanel { Margin = new Thickness(16), Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(schema.Description))
        {
            root.Children.Add(new TextBlock
            {
                Text = schema.Description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Opacity = 0.85
            });
        }
        root.Children.Add(fieldHost);
        root.Children.Add(hint);

        void Rebuild()
        {
            if (rebuilding) return;
            rebuilding = true;
            try
            {
                fieldHost.Children.Clear();
                foreach (var field in schema.Fields)
                {
                    if (!ToolUninstallUiEvaluator.IsVisible(field, values))
                        continue;

                    fieldHost.Children.Add(new TextBlock
                    {
                        Text = field.Label,
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = FontWeight.SemiBold,
                        Margin = new Thickness(0, 8, 0, 4)
                    });

                    switch (field.Type.ToLowerInvariant())
                    {
                        case "choice":
                            var choicePanel = new StackPanel();
                            foreach (var opt in field.Options ?? [])
                            {
                                var captured = opt.Value;
                                var rb = new RadioButton
                                {
                                    Content = opt.Label,
                                    GroupName = "f_" + field.Id + "_" + toolId,
                                    Margin = new Thickness(0, 2)
                                };
                                rb.IsCheckedChanged += (_, _) =>
                                {
                                    if (rebuilding || rb.IsChecked != true) return;
                                    values[field.Id] = captured;
                                    hint.IsVisible = false;
                                    Rebuild();
                                };
                                rb.IsChecked = values.TryGetValue(field.Id, out var cur)
                                    && string.Equals(cur, opt.Value, StringComparison.OrdinalIgnoreCase);
                                choicePanel.Children.Add(rb);
                            }
                            if (!values.ContainsKey(field.Id) && field.Options is { Count: > 0 })
                            {
                                values[field.Id] = field.Options[0].Value;
                                if (choicePanel.Children[0] is RadioButton first)
                                    first.IsChecked = true;
                            }
                            fieldHost.Children.Add(choicePanel);
                            break;

                        case "password":
                            var pwd = new TextBox
                            {
                                PasswordChar = '•',
                                Watermark = field.Label,
                                Text = values.TryGetValue(field.Id, out var pv) ? pv : ""
                            };
                            pwd.TextChanged += (_, _) => values[field.Id] = pwd.Text ?? "";
                            fieldHost.Children.Add(pwd);
                            break;

                        case "folder":
                        {
                            // 手输 + 框内嵌选夹图标；嵌套 ShowDialog 下系统选夹常失败，失败时提示手输
                            var tb = new TextBox
                            {
                                Watermark = "粘贴或输入输出目录路径，例如 D:\\VaultExport",
                                Text = values.TryGetValue(field.Id, out var fv) ? fv : "",
                                Padding = new Thickness(8, 6, 36, 6)
                            };
                            tb.TextChanged += (_, _) => values[field.Id] = tb.Text?.Trim() ?? "";
                            var browse = new Button
                            {
                                Content = "📁",
                                Width = 32,
                                Height = 28,
                                Padding = new Thickness(0),
                                HorizontalAlignment = HorizontalAlignment.Right,
                                VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(0, 0, 4, 0),
                                Background = Brushes.Transparent,
                                BorderThickness = new Thickness(0)
                            };
                            ToolTip.SetTip(browse, "选择文件夹");
                            browse.Click += async (_, _) =>
                            {
                                hint.IsVisible = false;
                                var path = await TryPickFolderAsync(owner, win, field.Label).ConfigureAwait(true);
                                if (string.IsNullOrWhiteSpace(path))
                                {
                                    hint.Text = "未能打开系统文件夹对话框。请直接在上方输入框粘贴输出目录完整路径。";
                                    hint.IsVisible = true;
                                    return;
                                }
                                values[field.Id] = path;
                                tb.Text = path;
                            };
                            var chrome = new Grid();
                            chrome.Children.Add(tb);
                            chrome.Children.Add(browse);
                            fieldHost.Children.Add(chrome);
                            fieldHost.Children.Add(new TextBlock
                            {
                                Text = "提示：可直接输入路径；空保险箱也可选「仅卸载工具」或「永久销毁」。",
                                FontSize = 11,
                                Opacity = 0.7,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 4, 0, 0)
                            });
                            break;
                        }

                        case "confirm":
                            var box = new TextBox
                            {
                                Watermark = field.Label,
                                Text = values.TryGetValue(field.Id, out var cv) ? cv : ""
                            };
                            box.TextChanged += (_, _) => values[field.Id] = box.Text ?? "";
                            fieldHost.Children.Add(box);
                            break;
                    }
                }
            }
            finally
            {
                rebuilding = false;
            }
        }

        Rebuild();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Spacing = 8
        };
        var cancel = new Button { Content = "取消", MinWidth = 80 };
        var ok = new Button { Content = "确定", MinWidth = 80 };
        cancel.Click += (_, _) =>
        {
            result = ToolUninstallAnswers.Cancel();
            win.Close();
        };
        ok.Click += (_, _) =>
        {
            var missing = ToolUninstallUiEvaluator.ValidateRequired(schema, values);
            if (missing != null)
            {
                hint.Text = "请填写：" + missing;
                hint.IsVisible = true;
                return;
            }
            result = ToolUninstallAnswers.From(values);
            win.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        win.Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        await win.ShowDialog(owner).ConfigureAwait(true);
        return result;
    }

    /// <summary>
    /// 嵌套模态窗上调系统文件夹选择器在 Win/Avalonia 上常抛空消息异常；
    /// 依次尝试 owner / dialog / 主窗 StorageProvider，失败返回 null（由调用方改用手输路径）。
    /// </summary>
    private static async Task<string?> TryPickFolderAsync(Window owner, Window dialog, string title)
    {
        foreach (var sp in EnumerateStorageProviders(owner, dialog))
        {
            try
            {
                var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false
                }).ConfigureAwait(true);
                if (folders.Count == 0)
                    return null; // 用户取消
                var path = folders[0].TryGetLocalPath() ?? folders[0].Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(path))
                    return path;
            }
            catch
            {
                // 试下一个 provider
            }
        }

        return null;
    }

    private static IEnumerable<IStorageProvider> EnumerateStorageProviders(Window owner, Window dialog)
    {
        if (dialog.StorageProvider is { } d)
            yield return d;
        if (owner.StorageProvider is { } o && !ReferenceEquals(o, dialog.StorageProvider))
            yield return o;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desk
            && desk.MainWindow?.StorageProvider is { } main
            && !ReferenceEquals(main, dialog.StorageProvider)
            && !ReferenceEquals(main, owner.StorageProvider))
            yield return main;
    }
}
