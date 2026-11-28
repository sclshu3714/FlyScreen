using FlyScreen.Helpers.Localization;
using FlyScreen.Ui.Theme;
using FlyScreen.Ui.Theme.Icons;
using FlyScreen.Ui.Theme.Maui.Icons;
using Microsoft.Maui.Controls.Shapes;
using ShapePath = Microsoft.Maui.Controls.Shapes.Path;

namespace FlyScreen.Tool.FileVault.Ui.Maui;

/// <summary>仿 Win11 资源管理器：Tab / 命令栏 / 右键顶栏图标。</summary>
internal static class VaultExplorerChrome
{
    /// <summary>
    /// 自定义标题栏 = Tab 行（左 Tab / 中拖拽 / 右自绘窗控）。无系统标题栏。
    /// </summary>
    /// <param name="embedInCaption">放进系统标题栏：只保留 Tab，窗控用系统按钮，中间空白由系统拖窗。</param>
    public static (View Bar, View DragRegion) BuildTitleTabBar(
        Action onCloseWindow,
        Action onMinimize,
        Action onToggleMaximize,
        Func<bool> isMaximized,
        bool embedInCaption = false)
    {
        var tabClose = MakeIconButton("✕", Loc.T("Tool.FileVault.CloseTab"), onCloseWindow, 22);
        tabClose.FontSize = 11;
        var tab = new Border
        {
            HeightRequest = 32,
            MinimumWidthRequest = 148,
            Padding = new Thickness(10, 0, 4, 0),
            Margin = new Thickness(8, 0, 0, 0),
            StrokeThickness = 1,
            Stroke = ColorOf("Line"),
            BackgroundColor = ColorOf("CardBg"),
            VerticalOptions = LayoutOptions.End,
            StrokeShape = new RoundRectangle
            {
                CornerRadius = new CornerRadius(8, 8, 0, 0)
            },
            Content = new HorizontalStackLayout
            {
                Spacing = 8,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label
                    {
                        Text = "🔐",
                        FontSize = 13,
                        VerticalOptions = LayoutOptions.Center
                    },
                    new Label
                    {
                        Text = Loc.T("Tool.FileVault.Name"),
                        FontSize = 12,
                        TextColor = ColorOf("Text"),
                        VerticalOptions = LayoutOptions.Center,
                        LineBreakMode = LineBreakMode.TailTruncation
                    },
                    tabClose
                }
            }
        };

        var plus = MakeIconButton("＋", Loc.T("Tool.FileVault.NewTab"), () => { }, 28);
        plus.Opacity = 0.45;
        plus.InputTransparent = true;
        plus.VerticalOptions = LayoutOptions.End;
        plus.Margin = new Thickness(2, 0, 0, 2);

        var tabs = new HorizontalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.End,
            Children = { tab, plus }
        };

        if (embedInCaption)
            return (tabs, tabs);

        // 纯透明拖拽区（勿用近透明色，WinUI 会画成一条可见把手）
        var drag = new Border
        {
            BackgroundColor = Colors.Transparent,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };

        ShapePath? maxPath = null;
        var minBtn = MakeChromeIconBtn(IconCodes.WindowMinimize, "最小化", onMinimize);
        var maxBtn = MakeChromeIconBtn(
            isMaximized() ? IconCodes.WindowRestore : IconCodes.WindowMaximize,
            "最大化",
            () =>
            {
                onToggleMaximize();
                if (maxPath != null)
                    maxPath.Data = ParseGeo(isMaximized() ? IconCodes.WindowRestore : IconCodes.WindowMaximize);
            },
            out maxPath);
        var closeBtn = MakeChromeIconBtn(IconCodes.Close, Loc.T("Tool.FileVault.CloseTab"), onCloseWindow, isClose: true);

        var chrome = new HorizontalStackLayout
        {
            Spacing = 0,
            VerticalOptions = LayoutOptions.Fill,
            Children = { minBtn, maxBtn, closeBtn }
        };

        var bar = new Border
        {
            HeightRequest = 36,
            StrokeThickness = 0,
            BackgroundColor = ColorOf("WinBg"),
            Padding = new Thickness(0),
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Auto),
                    new(GridLength.Star),
                    new(GridLength.Auto)
                },
                Children =
                {
                    tabs,
                    PlaceCol(drag, 1),
                    PlaceCol(chrome, 2)
                }
            }
        };
        return (bar, drag);
    }

    private static Border MakeChromeIconBtn(
        string iconCode,
        string tip,
        Action click,
        bool isClose = false)
        => MakeChromeIconBtn(iconCode, tip, click, out _, isClose);

    private static Border MakeChromeIconBtn(
        string iconCode,
        string tip,
        Action click,
        out ShapePath path,
        bool isClose = false)
    {
        var glyph = new ShapePath
        {
            Data = ParseGeo(iconCode),
            Aspect = Stretch.Uniform,
            Fill = ColorOf("Muted"),
            WidthRequest = 12,
            HeightRequest = 12,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            InputTransparent = true
        };
        path = glyph;
        var host = new Border
        {
            WidthRequest = 46,
            HeightRequest = 36,
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Content = glyph
        };
        ToolTipProperties.SetText(host, tip);
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => click();
        host.GestureRecognizers.Add(tap);
        if (isClose)
        {
            // 按下近似悬停红底
            var press = new PointerGestureRecognizer();
            press.PointerPressed += (_, _) =>
            {
                host.BackgroundColor = Color.FromArgb("#E81123");
                glyph.Fill = Colors.White;
            };
            press.PointerReleased += (_, _) =>
            {
                host.BackgroundColor = Colors.Transparent;
                glyph.Fill = ColorOf("Muted");
            };
            host.GestureRecognizers.Add(press);
        }
        return host;
    }

    private static Geometry? ParseGeo(string iconCode)
    {
        var converter = new PathGeometryConverter();
        return converter.ConvertFromInvariantString(IconCatalog.GetPath(iconCode)) as Geometry;
    }

    private static View PlaceCol(View view, int col)
    {
        Grid.SetColumn(view, col);
        return view;
    }

    public static View BuildCommandBar(
        Func<VaultNewEntry, Task> onCreateNew,
        Func<Task> onAddFile,
        Func<Task> onAddFolder,
        Func<Task> onDelete,
        Func<Task> onMore,
        out Border newBtn,
        out Border addFileBtn,
        out Border addFolderBtn,
        out Border deleteBtn)
    {
        newBtn = BuildNewButton(onCreateNew);
        addFileBtn = BuildCmdIcon("📄", Loc.T("Tool.FileVault.AddFile"), onAddFile);
        addFolderBtn = BuildCmdIcon("📁", Loc.T("Tool.FileVault.AddFolder"), onAddFolder);
        deleteBtn = BuildCmdIcon("🗑", Loc.T("Tool.FileVault.Delete"), onDelete);
        var sortBtn = BuildCmdLabeled("⇅", Loc.T("Tool.FileVault.Sort"), null);
        sortBtn.Opacity = 0.45;
        sortBtn.InputTransparent = true;
        var viewBtn = BuildCmdLabeled("☰", Loc.T("Tool.FileVault.View"), null);
        viewBtn.Opacity = 0.45;
        viewBtn.InputTransparent = true;
        var moreBtn = BuildCmdIcon("⋯", Loc.T("Tool.FileVault.Settings"), onMore);

        return new Border
        {
            Padding = new Thickness(8, 4, 8, 6),
            StrokeThickness = 0,
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Star),
                    new(GridLength.Auto)
                },
                Children =
                {
                    new ScrollView
                    {
                        Orientation = ScrollOrientation.Horizontal,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Never,
                        Content = new HorizontalStackLayout
                        {
                            Spacing = 2,
                            VerticalOptions = LayoutOptions.Center,
                            Children =
                            {
                                newBtn,
                                VSep(),
                                addFileBtn,
                                addFolderBtn,
                                deleteBtn,
                                VSep(),
                                sortBtn,
                                viewBtn,
                                moreBtn
                            }
                        }
                    }
                }
            }
        };
    }

    public static Border BuildWin11ContextMenu(
        bool hasSelection,
        bool unlocked,
        Func<Task> onOpen,
        Func<Task> onDelete,
        Func<Task> onImportFile,
        Func<Task> onImportFolder,
        Func<Task> onImportBackup,
        Func<Task> onExportFile,
        Func<Task> onExportFolder,
        Func<Task> onExportBackup,
        Func<Task> onIdleLock,
        Func<Task> onCut,
        Func<Task> onCopy,
        Func<Task> onRename,
        Func<Task> onPaste,
        bool canPaste,
        Action dismiss)
    {
        var top = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(GridLength.Star),
                new(GridLength.Star),
                new(GridLength.Star)
            },
            Padding = new Thickness(8, 8, 8, 6)
        };
        var canEdit = unlocked && hasSelection;
        top.Add(TopAction("✂", Loc.T("Tool.FileVault.Cut"), canEdit, onCut, dismiss), 0);
        top.Add(TopAction("⧉", Loc.T("Tool.FileVault.Copy"), canEdit, onCopy, dismiss), 1);
        top.Add(TopAction("✎", Loc.T("Tool.FileVault.Rename"), canEdit, onRename, dismiss), 2);
        top.Add(TopAction("🗑", Loc.T("Tool.FileVault.Delete"),
            enabled: unlocked && hasSelection, onDelete, dismiss), 3);

        var list = new VerticalStackLayout { Spacing = 0, Padding = new Thickness(4, 0, 4, 4) };
        void Row(string icon, string title, bool enabled, Func<Task>? action)
        {
            var iconLb = new Label
            {
                Text = icon,
                WidthRequest = 22,
                FontSize = 14,
                VerticalOptions = LayoutOptions.Center
            };
            var titleLb = new Label
            {
                Text = title,
                FontSize = 13,
                TextColor = ColorOf("Text"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(titleLb, 1);
            var row = new Border
            {
                StrokeThickness = 0,
                Padding = new Thickness(10, 8),
                Opacity = enabled ? 1 : 0.38,
                InputTransparent = !enabled,
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
                Content = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitionCollection
                    {
                        new(GridLength.Auto),
                        new(GridLength.Star)
                    },
                    Children = { iconLb, titleLb }
                }
            };
            if (enabled && action != null)
            {
                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) =>
                {
                    dismiss();
                    try { await action(); } catch { /* panel catches */ }
                };
                row.GestureRecognizers.Add(tap);
            }
            list.Children.Add(row);
        }

        Row("📂", Loc.T("Tool.FileVault.Open"), unlocked && hasSelection, onOpen);
        Row("📋", Loc.T("Tool.FileVault.Paste"), unlocked && canPaste, onPaste);
        list.Children.Add(HSep());
        Row("📄", Loc.T("Tool.FileVault.ImportFile"), unlocked, onImportFile);
        Row("📁", Loc.T("Tool.FileVault.ImportFolder"), unlocked, onImportFolder);
        Row("📦", Loc.T("Tool.FileVault.ImportBackup"), true, onImportBackup);
        list.Children.Add(HSep());
        Row("📤", Loc.T("Tool.FileVault.ExportFile"), unlocked && hasSelection, onExportFile);
        Row("📤", Loc.T("Tool.FileVault.ExportFolder"), unlocked && hasSelection, onExportFolder);
        Row("💾", Loc.T("Tool.FileVault.ExportBackup"), true, onExportBackup);
        list.Children.Add(HSep());
        Row("⏱", Loc.T("Tool.FileVault.IdleLock"), true, onIdleLock);

        return new Border
        {
            WidthRequest = 280,
            StrokeThickness = 1,
            Stroke = ColorOf("Line"),
            BackgroundColor = ColorOf("CardBg"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(0, 2, 0, 4),
            Shadow = new Shadow
            {
                Brush = Colors.Black,
                Offset = new Point(0, 4),
                Radius = 16,
                Opacity = 0.28f
            },
            Content = new VerticalStackLayout
            {
                Children =
                {
                    top,
                    HSep(),
                    list
                }
            }
        };
    }

    private static Border BuildNewButton(Func<VaultNewEntry, Task> onCreateNew)
    {
        var btn = new Border
        {
            StrokeThickness = 0,
            Padding = new Thickness(10, 6),
            MinimumHeightRequest = 34,
            BackgroundColor = Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
            Content = new HorizontalStackLayout
            {
                Spacing = 6,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label
                    {
                        Text = "＋",
                        FontSize = 16,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = ColorOf("Blue"),
                        VerticalOptions = LayoutOptions.Center
                    },
                    new Label
                    {
                        Text = Loc.T("Tool.FileVault.New"),
                        FontSize = 13,
                        TextColor = ColorOf("Text"),
                        VerticalOptions = LayoutOptions.Center
                    },
                    new Label
                    {
                        Text = "▾",
                        FontSize = 10,
                        TextColor = ColorOf("Muted"),
                        VerticalOptions = LayoutOptions.Center
                    }
                }
            }
        };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) =>
        {
            if (btn.Opacity < 0.9) return;
            ShowNewDropdown(btn, onCreateNew);
        };
        btn.GestureRecognizers.Add(tap);
        return btn;
    }

    private static void ShowNewDropdown(View anchor, Func<VaultNewEntry, Task> onCreate)
    {
        Grid? host = null;
        for (Element? cur = anchor; cur != null; cur = cur.Parent)
        {
            if (cur is Grid grid)
                host = grid;
        }
        if (host == null)
            return;

        for (var i = host.Children.Count - 1; i >= 0; i--)
        {
            if (host.Children[i] is View old && (old.ClassId == "vault-new-menu" || old.ClassId == "vault-new-scrim"))
                host.Children.RemoveAt(i);
        }

        var scrim = new BoxView { Color = Colors.Transparent, ZIndex = 60, ClassId = "vault-new-scrim" };
        var list = new VerticalStackLayout { Spacing = 0 };
        var menu = new Border
        {
            ClassId = "vault-new-menu",
            WidthRequest = 280,
            MaximumHeightRequest = 420,
            StrokeThickness = 1,
            Stroke = ColorOf("Line"),
            BackgroundColor = ColorOf("CardBg"),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(0, 4),
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Start,
            ZIndex = 61,
            Content = list
        };

        void Close()
        {
            host.Children.Remove(scrim);
            host.Children.Remove(menu);
        }

        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => Close();
        scrim.GestureRecognizers.Add(scrimTap);

        foreach (var entry in VaultNewCatalog.Load())
        {
            var captured = entry;
            var icon = new ShapePath
            {
                Data = MauiIcon.Parse(captured.IconCode),
                Fill = Color.FromArgb(captured.IconColor),
                Aspect = Stretch.Uniform,
                WidthRequest = 16,
                HeightRequest = 16,
                VerticalOptions = LayoutOptions.Center
            };
            var title = new Label
            {
                Text = captured.MenuTitle,
                FontSize = 13,
                TextColor = ColorOf("Text"),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(title, 1);
            var row = new Grid
            {
                Padding = new Thickness(12, 7),
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Auto),
                    new(GridLength.Star)
                },
                Children = { icon, title }
            };
            var rowTap = new TapGestureRecognizer();
            rowTap.Tapped += async (_, _) =>
            {
                Close();
                try { await onCreate(captured); } catch { /* panel alerts */ }
            };
            row.GestureRecognizers.Add(rowTap);
            list.Children.Add(row);
        }

        double x = 0, y = 0;
        for (VisualElement? node = anchor; node != null && !ReferenceEquals(node, host); node = node.Parent as VisualElement)
        {
            x += node.X;
            y += node.Y;
        }
        menu.Margin = new Thickness(Math.Max(8, x), Math.Max(8, y + anchor.Height), 0, 0);

        var span = Math.Max(1, host.RowDefinitions.Count);
        Grid.SetRowSpan(scrim, span);
        Grid.SetRowSpan(menu, span);
        host.Children.Add(scrim);
        host.Children.Add(menu);
    }

    private static Border BuildCmdIcon(string glyph, string tip, Func<Task> onClick)
    {
        var btn = new Border
        {
            StrokeThickness = 0,
            WidthRequest = 36,
            HeightRequest = 34,
            BackgroundColor = Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
            Content = new Label
            {
                Text = glyph,
                FontSize = 15,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center
            }
        };
        ToolTipProperties.SetText(btn, tip);
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (btn.Opacity < 0.9) return;
            try { await onClick(); } catch { /* ignore */ }
        };
        btn.GestureRecognizers.Add(tap);
        return btn;
    }

    private static Border BuildCmdLabeled(string glyph, string label, Func<Task>? onClick)
    {
        var btn = new Border
        {
            StrokeThickness = 0,
            Padding = new Thickness(8, 6),
            HeightRequest = 34,
            BackgroundColor = Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
            Content = new HorizontalStackLayout
            {
                Spacing = 4,
                Children =
                {
                    new Label { Text = glyph, FontSize = 13, VerticalOptions = LayoutOptions.Center },
                    new Label
                    {
                        Text = label,
                        FontSize = 12,
                        TextColor = ColorOf("Text"),
                        VerticalOptions = LayoutOptions.Center
                    },
                    new Label
                    {
                        Text = "▾",
                        FontSize = 10,
                        TextColor = ColorOf("Muted"),
                        VerticalOptions = LayoutOptions.Center
                    }
                }
            }
        };
        if (onClick != null)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                try { await onClick(); } catch { /* ignore */ }
            };
            btn.GestureRecognizers.Add(tap);
        }
        return btn;
    }

    private static View TopAction(string icon, string label, bool enabled, Func<Task>? action, Action dismiss)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 4,
            HorizontalOptions = LayoutOptions.Center,
            Children =
            {
                new Label
                {
                    Text = icon,
                    FontSize = 16,
                    HorizontalTextAlignment = TextAlignment.Center,
                    TextColor = ColorOf("Blue")
                },
                new Label
                {
                    Text = label,
                    FontSize = 11,
                    HorizontalTextAlignment = TextAlignment.Center,
                    TextColor = ColorOf("Text")
                }
            }
        };
        var host = new Border
        {
            StrokeThickness = 0,
            Padding = new Thickness(4, 6),
            Opacity = enabled ? 1 : 0.35,
            InputTransparent = !enabled,
            BackgroundColor = Colors.Transparent,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 4 },
            Content = stack
        };
        if (enabled && action != null)
        {
            var tap = new TapGestureRecognizer();
            tap.Tapped += async (_, _) =>
            {
                dismiss();
                try { await action(); } catch { /* ignore */ }
            };
            host.GestureRecognizers.Add(tap);
        }
        return host;
    }

    private static Button MakeIconButton(string text, string tip, Action click, double size)
    {
        var b = new Button
        {
            Text = text,
            FontSize = 14,
            WidthRequest = size,
            HeightRequest = size,
            Padding = 0,
            Margin = 0,
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            TextColor = ColorOf("Muted"),
            VerticalOptions = LayoutOptions.Center
        };
        ToolTipProperties.SetText(b, tip);
        b.Clicked += (_, _) => click();
        return b;
    }

    private static View VSep() => new BoxView
    {
        WidthRequest = 1,
        HeightRequest = 22,
        Color = ColorOf("Line"),
        VerticalOptions = LayoutOptions.Center,
        Margin = new Thickness(6, 0)
    };

    private static View HSep() => new BoxView
    {
        HeightRequest = 1,
        Color = ColorOf("Line"),
        Margin = new Thickness(8, 4)
    };

    private static Color ColorOf(string key)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Color c)
            return c;
        var map = ThemePalette.ToResourceMap(ThemePalette.ForDialog(ThemePalette.LightName, maui: true));
        return map.TryGetValue(key, out var hex) ? Color.FromArgb(hex) : Colors.Gray;
    }
}
