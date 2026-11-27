using System.Windows;
using System.Windows.Controls;
using FlyScreen.ToolSdk;
using Microsoft.Win32;

namespace FlyScreen.Uninstaller.Controls;

/// <summary>按 schema 嵌入卸载器窗口的通用决策表单。</summary>
public sealed class ToolUninstallSchemaPanel : StackPanel
{
    private readonly ToolUninstallUiSchema _schema;
    private readonly Dictionary<string, string> _values;
    private readonly StackPanel _fields = new();

    public ToolUninstallSchemaPanel(ToolUninstallUiSchema schema)
    {
        _schema = schema;
        _values = new Dictionary<string, string>(schema.Defaults, StringComparer.OrdinalIgnoreCase);
        Margin = new Thickness(0, 8, 0, 0);
        if (!string.IsNullOrWhiteSpace(schema.Description))
        {
            Children.Add(new TextBlock
            {
                Text = schema.Description,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Foreground = System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 6)
            });
        }
        Children.Add(_fields);
        Rebuild();
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public string? Validate() => ToolUninstallUiEvaluator.ValidateRequired(_schema, _values);

    private void Rebuild()
    {
        _fields.Children.Clear();
        foreach (var field in _schema.Fields)
        {
            if (!ToolUninstallUiEvaluator.IsVisible(field, _values))
                continue;

            _fields.Children.Add(new TextBlock
            {
                Text = field.Label,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 2)
            });

            switch (field.Type.ToLowerInvariant())
            {
                case "choice":
                    var panel = new StackPanel();
                    foreach (var opt in field.Options ?? [])
                    {
                        var rb = new RadioButton
                        {
                            Content = opt.Label,
                            Tag = opt.Value,
                            GroupName = "schema_" + _schema.Title + "_" + field.Id,
                            Margin = new Thickness(0, 2, 0, 2),
                            IsChecked = _values.TryGetValue(field.Id, out var cur)
                                && string.Equals(cur, opt.Value, StringComparison.OrdinalIgnoreCase)
                        };
                        rb.Checked += (_, _) =>
                        {
                            if (rb.Tag is string v)
                            {
                                _values[field.Id] = v;
                                Rebuild();
                            }
                        };
                        panel.Children.Add(rb);
                    }
                    if (!_values.ContainsKey(field.Id) && field.Options is { Count: > 0 })
                        _values[field.Id] = field.Options[0].Value;
                    _fields.Children.Add(panel);
                    break;

                case "password":
                    var pwd = new PasswordBox { Height = 28 };
                    if (_values.TryGetValue(field.Id, out var pv))
                        pwd.Password = pv;
                    pwd.PasswordChanged += (_, _) => _values[field.Id] = pwd.Password;
                    _fields.Children.Add(pwd);
                    break;

                case "folder":
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var tb = new TextBox
                    {
                        IsReadOnly = true,
                        Height = 28,
                        VerticalContentAlignment = VerticalAlignment.Center,
                        Text = _values.TryGetValue(field.Id, out var fv) ? fv : ""
                    };
                    var btn = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
                    btn.Click += (_, _) =>
                    {
                        var dlg = new OpenFolderDialog { Title = field.Label };
                        if (dlg.ShowDialog() == true)
                        {
                            _values[field.Id] = dlg.FolderName;
                            tb.Text = dlg.FolderName;
                        }
                    };
                    Grid.SetColumn(tb, 0);
                    Grid.SetColumn(btn, 1);
                    row.Children.Add(tb);
                    row.Children.Add(btn);
                    _fields.Children.Add(row);
                    break;

                case "confirm":
                    var box = new TextBox { Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
                    if (_values.TryGetValue(field.Id, out var cv))
                        box.Text = cv;
                    box.TextChanged += (_, _) => _values[field.Id] = box.Text;
                    _fields.Children.Add(box);
                    break;
            }
        }
    }
}
