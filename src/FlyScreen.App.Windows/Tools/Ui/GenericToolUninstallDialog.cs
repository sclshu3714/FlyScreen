using System.Windows;
using System.Windows.Controls;
using FlyScreen.ToolSdk;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FlyScreen.Tools.Ui;

/// <summary>按 <see cref="ToolUninstallUiSchema"/> 动态生成的通用卸载决策框。</summary>
internal static class GenericToolUninstallDialog
{
    public static ToolUninstallAnswers Show(Window? owner, string toolId, ToolUninstallUiSchema schema)
    {
        var values = new Dictionary<string, string>(schema.Defaults, StringComparer.OrdinalIgnoreCase);

        var win = new Window
        {
            Title = string.IsNullOrWhiteSpace(schema.Title) ? "卸载决策" : schema.Title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 560,
            WindowStartupLocation = owner == null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner,
            ShowInTaskbar = false
        };

        var root = new StackPanel { Margin = new Thickness(16) };
        if (!string.IsNullOrWhiteSpace(schema.Description))
        {
            root.Children.Add(new TextBlock
            {
                Text = schema.Description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Opacity = 0.85
            });
        }

        var fieldHost = new StackPanel();
        root.Children.Add(fieldHost);

        var controls = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        var rebuilding = false;
        void Rebuild()
        {
            if (rebuilding) return;
            rebuilding = true;
            try
            {
            fieldHost.Children.Clear();
            controls.Clear();
            foreach (var field in schema.Fields)
            {
                if (!ToolUninstallUiEvaluator.IsVisible(field, values))
                    continue;

                fieldHost.Children.Add(new TextBlock
                {
                    Text = field.Label,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 4),
                    FontWeight = FontWeights.SemiBold
                });

                switch (field.Type.ToLowerInvariant())
                {
                    case "choice":
                    {
                        var panel = new StackPanel();
                        foreach (var opt in field.Options ?? [])
                        {
                            var rb = new RadioButton
                            {
                                Content = opt.Label,
                                Tag = opt.Value,
                                GroupName = "f_" + field.Id + "_" + toolId,
                                Margin = new Thickness(0, 2, 0, 2)
                            };
                            rb.Checked += (_, _) =>
                            {
                                if (rebuilding) return;
                                if (rb.Tag is string v)
                                {
                                    values[field.Id] = v;
                                    Rebuild();
                                }
                            };
                            rb.IsChecked = values.TryGetValue(field.Id, out var cur)
                                && string.Equals(cur, opt.Value, StringComparison.OrdinalIgnoreCase);
                            panel.Children.Add(rb);
                        }
                        if (!values.ContainsKey(field.Id) && field.Options is { Count: > 0 })
                        {
                            values[field.Id] = field.Options[0].Value;
                            if (panel.Children[0] is RadioButton first)
                                first.IsChecked = true;
                        }
                        controls[field.Id] = panel;
                        fieldHost.Children.Add(panel);
                        break;
                    }
                    case "password":
                    {
                        var box = new PasswordBox { Margin = new Thickness(0, 0, 0, 4) };
                        if (values.TryGetValue(field.Id, out var pv))
                            box.Password = pv;
                        box.PasswordChanged += (_, _) => values[field.Id] = box.Password;
                        controls[field.Id] = box;
                        fieldHost.Children.Add(box);
                        break;
                    }
                    case "folder":
                    {
                        // 手输 + 框内嵌选夹图标按钮
                        var tb = new TextBox
                        {
                            Text = values.TryGetValue(field.Id, out var fv) ? fv : "",
                            Padding = new Thickness(6, 4, 34, 4),
                            VerticalContentAlignment = VerticalAlignment.Center
                        };
                        tb.TextChanged += (_, _) => values[field.Id] = tb.Text?.Trim() ?? "";
                        var btn = new Button
                        {
                            Content = "📁",
                            Width = 30,
                            Height = 26,
                            Padding = new Thickness(0),
                            Margin = new Thickness(0, 0, 3, 0),
                            HorizontalAlignment = HorizontalAlignment.Right,
                            VerticalAlignment = VerticalAlignment.Center,
                            Background = System.Windows.Media.Brushes.Transparent,
                            BorderThickness = new Thickness(0),
                            Cursor = System.Windows.Input.Cursors.Hand,
                            ToolTip = "选择文件夹"
                        };
                        btn.Click += (_, _) =>
                        {
                            try
                            {
                                var dlg = new OpenFolderDialog { Title = field.Label };
                                if (dlg.ShowDialog(win) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
                                {
                                    values[field.Id] = dlg.FolderName;
                                    tb.Text = dlg.FolderName;
                                }
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(win,
                                    "未能打开文件夹对话框，请直接在输入框粘贴路径。\n" + ex.Message,
                                    win.Title, MessageBoxButton.OK, MessageBoxImage.Information);
                            }
                        };
                        var chrome = new Grid();
                        chrome.Children.Add(tb);
                        chrome.Children.Add(btn);
                        controls[field.Id] = chrome;
                        fieldHost.Children.Add(chrome);
                        break;
                    }
                    case "confirm":
                    {
                        var box = new TextBox { Margin = new Thickness(0, 0, 0, 4) };
                        if (values.TryGetValue(field.Id, out var cv))
                            box.Text = cv;
                        box.TextChanged += (_, _) => values[field.Id] = box.Text;
                        controls[field.Id] = box;
                        fieldHost.Children.Add(box);
                        break;
                    }
                    default:
                        fieldHost.Children.Add(new TextBlock { Text = $"不支持的字段类型: {field.Type}" });
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
            Margin = new Thickness(0, 16, 0, 0)
        };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(14, 6, 14, 6), IsCancel = true };
        var ok = new Button { Content = "确定", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
        ToolUninstallAnswers result = ToolUninstallAnswers.Cancel();
        cancel.Click += (_, _) =>
        {
            result = ToolUninstallAnswers.Cancel();
            win.DialogResult = false;
        };
        ok.Click += (_, _) =>
        {
            // 同步 password 控件
            foreach (var field in schema.Fields)
            {
                if (controls.TryGetValue(field.Id, out var el) && el is PasswordBox pb)
                    values[field.Id] = pb.Password;
            }
            var missing = ToolUninstallUiEvaluator.ValidateRequired(schema, values);
            if (missing != null)
            {
                MessageBox.Show(win, $"请填写：{missing}", win.Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            result = ToolUninstallAnswers.From(values);
            win.DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        win.Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        win.ShowDialog();
        return result;
    }
}
