using System.Windows;
using System.Windows.Media;
using FlyScreen.Ui.Theme.Icons;

namespace FlyScreen.Ui.Theme.Wpf;

/// <summary>WPF 主题入口：合并 Shared 令牌 + 控件样式，并注册图标码 Geometry。</summary>
public partial class WpfTheme : ResourceDictionary
{
    public WpfTheme()
    {
        InitializeComponent();
        RegisterIcons();
    }

    private void RegisterIcons()
    {
        foreach (var (code, path) in IconCatalog.AllPaths)
        {
            var g = Geometry.Parse(path);
            g.Freeze();
            this[code] = g;
        }

        // 兼容旧 Geo* 键（各自 Freeze 副本，避免共享可变 Freezable）
        foreach (var (legacy, code) in IconCatalog.AllLegacyAliases)
        {
            if (!IconCatalog.TryGetPath(code, out var path))
                continue;
            var g = Geometry.Parse(path);
            g.Freeze();
            this[legacy] = g;
        }
    }
}
