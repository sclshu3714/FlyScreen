using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
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

        var win = new Window
        {
            Title = string.IsNullOrWhiteSpace(schema.Title) ? "卸载决策" : schema.Title,
            Width = 480,
            Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var fieldHost = new StackPanel();
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

        void Rebuild()
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
                            var rb = new RadioButton
                            {
                                Content = opt.Label,
                                GroupName = "f_" + field.Id + "_" + toolId,
                                IsChecked = values.TryGetValue(field.Id, out var cur)
                                    && string.Equals(cur, opt.Value, StringComparison.OrdinalIgnoreCase),
                                Margin = new Thickness(0, 2)
                            };
                            var captured = opt.Value;
                            rb.IsCheckedChanged += (_, _) =>
                            {
                                if (rb.IsChecked == true)
                                {
                                    values[field.Id] = captured;
                                    Rebuild();
                                }
                            };
                            choicePanel.Children.Add(rb);
                        }
                        if (!values.ContainsKey(field.Id) && field.Options is { Count: > 0 })
                            values[field.Id] = field.Options[0].Value;
                        fieldHost.Children.Add(choicePanel);
                        break;

                    case "password":
                        var pwd = new TextBox
                        {
                            PasswordChar = '•',
                            Text = values.TryGetValue(field.Id, out var pv) ? pv : ""
                        };
                        pwd.TextChanged += (_, _) => values[field.Id] = pwd.Text ?? "";
                        fieldHost.Children.Add(pwd);
                        break;

                    case "folder":
                        var row = new DockPanel();
                        var tb = new TextBox
                        {
                            IsReadOnly = true,
                            Text = values.TryGetValue(field.Id, out var fv) ? fv : "",
                            [DockPanel.DockProperty] = Dock.Left
                        };
                        var browse = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0) };
                        DockPanel.SetDock(browse, Dock.Right);
                        browse.Click += async (_, _) =>
                        {
                            var folders = await owner.StorageProvider.OpenFolderPickerAsync(
                                new Avalonia.Platform.Storage.FolderPickerOpenOptions
                                {
                                    Title = field.Label,
                                    AllowMultiple = false
                                });
                            if (folders.Count == 0) return;
                            var path = folders[0].Path.LocalPath;
                            values[field.Id] = path;
                            tb.Text = path;
                        };
                        row.Children.Add(browse);
                        row.Children.Add(tb);
                        fieldHost.Children.Add(row);
                        break;

                    case "confirm":
                        var box = new TextBox { Text = values.TryGetValue(field.Id, out var cv) ? cv : "" };
                        box.TextChanged += (_, _) => values[field.Id] = box.Text ?? "";
                        fieldHost.Children.Add(box);
                        break;
                }
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
        var cancel = new Button { Content = "取消", Width = 80 };
        var ok = new Button { Content = "确定", Width = 80 };
        cancel.Click += (_, _) =>
        {
            result = ToolUninstallAnswers.Cancel();
            win.Close();
        };
        ok.Click += (_, _) =>
        {
            var missing = ToolUninstallUiEvaluator.ValidateRequired(schema, values);
            if (missing != null)
                return;
            result = ToolUninstallAnswers.From(values);
            win.Close();
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);
        win.Content = new ScrollViewer { Content = root };

        await win.ShowDialog(owner);
        return result;
    }
}
