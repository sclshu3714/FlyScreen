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
        Action<View> onSort,
        Action<View> onView,
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
        var sortTap = new TapGestureRecognizer();
        sortTap.Tapped += (_, _) => onSort(sortBtn);
        sortBtn.GestureRecognizers.Add(sortTap);
        var viewBtn = BuildCmdLabeled("☰", Loc.T("Tool.FileVault.View"), null);
        var viewTap = new TapGestureRecognizer();
        viewTap.Tapped += (_, _) => onView(viewBtn);
        viewBtn.GestureRecognizers.Add(viewTap);
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

    public static void ShowSortMenu(View anchor, VaultSortState state, Action refresh)
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
            if (host.Children[i] is View old && old.ClassId is "vault-sort-menu" or "vault-sort-sub" or "vault-sort-scrim")
                host.Children.RemoveAt(i);
        }

        var scrim = new BoxView { Color = Colors.Transparent, ZIndex = 60, ClassId = "vault-sort-scrim" };
        Border? sub = null;

        void Close()
        {
            host.Children.Remove(scrim);
            if (host.Children.FirstOrDefault(v => v is View view && view.ClassId == "vault-sort-menu") is View menuView)
                host.Children.Remove(menuView);
            if (sub != null)
                host.Children.Remove(sub);
        }

        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => Close();
        scrim.GestureRecognizers.Add(scrimTap);

        void Apply(Action change)
        {
            change();
            Close();
            refresh();
        }

        View Row(string title, bool radio, bool on, bool chevron, Action? tap)
        {
            var mark = new Label
            {
                Text = on ? "●" : "",
                WidthRequest = 16,
                FontSize = 10,
                TextColor = ColorOf("Blue"),
                VerticalOptions = LayoutOptions.Center
            };
            var label = new Label
            {
                Text = title,
                FontSize = 13,
                TextColor = ColorOf("Text"),
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(label, 1);
            var grid = new Grid
            {
                Padding = new Thickness(12, 7),
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Auto),
                    new(GridLength.Star),
                    new(GridLength.Auto)
                },
                Children = { mark, label }
            };
            if (chevron)
            {
                var arrow = new Label { Text = "›", FontSize = 16, TextColor = ColorOf("Muted"), VerticalOptions = LayoutOptions.Center };
                Grid.SetColumn(arrow, 2);
                grid.Children.Add(arrow);
            }
            if (tap != null)
            {
                var gesture = new TapGestureRecognizer();
                gesture.Tapped += (_, _) => tap();
                grid.GestureRecognizers.Add(gesture);
            }
            return grid;
        }

        Border? menu = null;
        var list = new VerticalStackLayout { Spacing = 0 };
        void AddKey(VaultSortKey key) =>
            list.Children.Add(Row(VaultSort.Title(key), true, state.Key == key, false, () => Apply(() => state.Key = key)));
        foreach (var key in VaultSort.Primary)
            AddKey(key);

        list.Children.Add(Row(Loc.T("Tool.FileVault.SortMore"), false, false, true, () => OpenSub(VaultSort.More, false)));
        list.Children.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(8, 4), Color = ColorOf("Line") });
        list.Children.Add(Row(Loc.T("Tool.FileVault.SortAsc"), true, state.Order == VaultSortOrder.Ascending, false,
            () => Apply(() => state.Order = VaultSortOrder.Ascending)));
        list.Children.Add(Row(Loc.T("Tool.FileVault.SortDesc"), true, state.Order == VaultSortOrder.Descending, false,
            () => Apply(() => state.Order = VaultSortOrder.Descending)));
        list.Children.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(8, 4), Color = ColorOf("Line") });
        list.Children.Add(Row(Loc.T("Tool.FileVault.SortGroup"), false, false, true, () => OpenSub(VaultSort.GroupKeys, true)));

        menu = new Border
        {
            ClassId = "vault-sort-menu",
            WidthRequest = 220,
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

        void OpenSub(IReadOnlyList<VaultSortKey> keys, bool grouping)
        {
            if (sub != null)
                host.Children.Remove(sub);
            var subList = new VerticalStackLayout { Spacing = 0 };
            foreach (var key in keys)
            {
                var captured = key;
                var selected = grouping ? state.Group == captured : state.Key == captured;
                subList.Children.Add(Row(VaultSort.Title(captured), true, selected, false, () => Apply(() =>
                {
                    if (grouping)
                        state.Group = state.Group == captured ? null : captured;
                    else
                        state.Key = captured;
                })));
            }
            sub = new Border
            {
                ClassId = "vault-sort-sub",
                WidthRequest = 200,
                StrokeThickness = 1,
                Stroke = ColorOf("Line"),
                BackgroundColor = ColorOf("CardBg"),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(0, 4),
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                ZIndex = 62,
                Content = subList
            };
            var left = menu?.Margin.Left ?? 8;
            var top = menu?.Margin.Top ?? 8;
            sub.Margin = new Thickness(left + 208, top + (grouping ? 168 : 96), 0, 0);
            var span = Math.Max(1, host.RowDefinitions.Count);
            Grid.SetRowSpan(sub, span);
            host.Children.Add(sub);
        }

        double x = 0, y = 0;
        for (VisualElement? node = anchor; node != null && !ReferenceEquals(node, host); node = node.Parent as VisualElement)
        {
            x += node.X;
            y += node.Y;
        }
        menu.Margin = new Thickness(Math.Max(8, x), Math.Max(8, y + anchor.Height), 0, 0);
        var hostSpan = Math.Max(1, host.RowDefinitions.Count);
        Grid.SetRowSpan(scrim, hostSpan);
        Grid.SetRowSpan(menu, hostSpan);
        host.Children.Add(scrim);
        host.Children.Add(menu);
    }

    public static void ShowViewMenu(View anchor, VaultViewState state, Action refresh)
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
            if (host.Children[i] is View old && old.ClassId is "vault-view-menu" or "vault-view-sub" or "vault-view-scrim")
                host.Children.RemoveAt(i);
        }

        var scrim = new BoxView { Color = Colors.Transparent, ZIndex = 60, ClassId = "vault-view-scrim" };
        Border? menu = null;
        Border? sub = null;

        void Close()
        {
            host.Children.Remove(scrim);
            if (menu != null) host.Children.Remove(menu);
            if (sub != null) host.Children.Remove(sub);
        }

        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => Close();
        scrim.GestureRecognizers.Add(scrimTap);

        void Apply(Action change)
        {
            change();
            Close();
            refresh();
        }

        View Row(string iconCode, string title, bool radio, bool on, bool chevron, Action? tap)
        {
            var mark = new Label
            {
                Text = on ? (radio ? "●" : "✓") : "",
                WidthRequest = 16,
                FontSize = 11,
                TextColor = ColorOf("Blue"),
                VerticalOptions = LayoutOptions.Center
            };
            var icon = new ShapePath
            {
                Data = MauiIcon.Parse(iconCode),
                Fill = ColorOf("Muted"),
                Aspect = Stretch.Uniform,
                WidthRequest = 16,
                HeightRequest = 16,
                VerticalOptions = LayoutOptions.Center
            };
            Grid.SetColumn(icon, 1);
            var label = new Label
            {
                Text = title,
                FontSize = 13,
                TextColor = ColorOf("Text"),
                VerticalOptions = LayoutOptions.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(label, 2);
            var grid = new Grid
            {
                Padding = new Thickness(10, 7),
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Auto),
                    new(GridLength.Auto),
                    new(GridLength.Star),
                    new(GridLength.Auto)
                },
                Children = { mark, icon, label }
            };
            if (chevron)
            {
                var arrow = new Label { Text = "›", FontSize = 16, TextColor = ColorOf("Muted"), VerticalOptions = LayoutOptions.Center };
                Grid.SetColumn(arrow, 3);
                grid.Children.Add(arrow);
            }
            if (tap != null)
            {
                var gesture = new TapGestureRecognizer();
                gesture.Tapped += (_, _) => tap();
                grid.GestureRecognizers.Add(gesture);
            }
            return grid;
        }

        var list = new VerticalStackLayout { Spacing = 0 };
        foreach (var mode in VaultView.Layouts)
        {
            var captured = mode;
            list.Children.Add(Row(
                VaultView.IconCode(captured),
                VaultView.Title(captured),
                true,
                state.Layout == captured,
                false,
                () => Apply(() => state.Layout = captured)));
        }
        list.Children.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(8, 4), Color = ColorOf("Line") });
        list.Children.Add(Row(VaultView.DetailsPaneIcon, Loc.T("Tool.FileVault.ViewDetailsPane"), true, state.SidePane == VaultSidePane.Details, false,
            () => Apply(() => VaultView.ToggleSidePane(state, VaultSidePane.Details))));
        list.Children.Add(Row(VaultView.PreviewPaneIcon, Loc.T("Tool.FileVault.ViewPreviewPane"), true, state.SidePane == VaultSidePane.Preview, false,
            () => Apply(() => VaultView.ToggleSidePane(state, VaultSidePane.Preview))));
        list.Children.Add(new BoxView { HeightRequest = 1, Margin = new Thickness(8, 4), Color = ColorOf("Line") });
        list.Children.Add(Row(VaultView.ShowIcon, Loc.T("Tool.FileVault.ViewShow"), false, false, true, OpenShow));

        menu = new Border
        {
            ClassId = "vault-view-menu",
            WidthRequest = 240,
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

        void OpenShow()
        {
            if (sub != null)
                host.Children.Remove(sub);
            var subList = new VerticalStackLayout { Spacing = 0 };
            void Toggle(string iconCode, string title, bool on, Action flip) =>
                subList.Children.Add(Row(iconCode, title, false, on, false, () => Apply(flip)));
            Toggle(VaultView.NavPaneIcon, Loc.T("Tool.FileVault.ViewNavPane"), state.NavigationPane, () => state.NavigationPane = !state.NavigationPane);
            Toggle(VaultView.CompactIcon, Loc.T("Tool.FileVault.ViewCompact"), state.CompactView, () => state.CompactView = !state.CompactView);
            Toggle(VaultView.CheckBoxesIcon, Loc.T("Tool.FileVault.ViewCheckBoxes"), state.ItemCheckBoxes, () => state.ItemCheckBoxes = !state.ItemCheckBoxes);
            Toggle(VaultView.ExtensionsIcon, Loc.T("Tool.FileVault.ViewExtensions"), state.ShowExtensions, () => state.ShowExtensions = !state.ShowExtensions);
            Toggle(VaultView.HiddenIcon, Loc.T("Tool.FileVault.ViewHidden"), state.ShowHidden, () => state.ShowHidden = !state.ShowHidden);
            sub = new Border
            {
                ClassId = "vault-view-sub",
                WidthRequest = 220,
                StrokeThickness = 1,
                Stroke = ColorOf("Line"),
                BackgroundColor = ColorOf("CardBg"),
                StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 8 },
                Padding = new Thickness(0, 4),
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Start,
                ZIndex = 62,
                Content = subList
            };
            var left = menu?.Margin.Left ?? 8;
            var top = menu?.Margin.Top ?? 8;
            sub.Margin = new Thickness(left + 228, top + 280, 0, 0);
            var span = Math.Max(1, host.RowDefinitions.Count);
            Grid.SetRowSpan(sub, span);
            host.Children.Add(sub);
        }

        double x = 0, y = 0;
        for (VisualElement? node = anchor; node != null && !ReferenceEquals(node, host); node = node.Parent as VisualElement)
        {
            x += node.X;
            y += node.Y;
        }
        menu.Margin = new Thickness(Math.Max(8, x), Math.Max(8, y + anchor.Height), 0, 0);
        var hostSpan = Math.Max(1, host.RowDefinitions.Count);
        Grid.SetRowSpan(scrim, hostSpan);
        Grid.SetRowSpan(menu, hostSpan);
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
