using System.Collections.ObjectModel;
using FlyScreen.AppMaui.Helpers;
using FlyScreen.AppMaui.Services;
using FlyScreen.Helpers.Localization;
using FlyScreen.Models;
using FlyScreen.Services.Network;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.AppMaui;

public partial class MainPage : ContentPage
{
    private readonly MauiAppHost _host;
    private ObservableCollection<DiscoveredPeer> _discovery = [];
    private readonly ObservableCollection<MachineSlot> _slots = [];
    private readonly List<Button> _tabButtons = [];
    private int _pageIndex;
    private bool _twoRow = true;
    private MachineSlot? _selected;
    private IDispatcherTimer? _statusTimer;
    private const string DragSlotKey = "FlyScreen.MachineSlot";

    public MainPage()
    {
        InitializeComponent();
        _host = MauiAppHost.EnsureCreated();
        DiscoveryList.ItemsSource = _discovery;
        TransferList.ItemsSource = _host.Transfers.Items;
        _tabButtons.AddRange([TabBasic, TabOptions, TabNetwork, TabTransfer]);
        TrayBtn.IsVisible = true;
        AutoStartCheck.IsEnabled = _host.Platform.SupportsAutoStart;
        Loaded += OnLoaded;
        Unloaded += (_, _) => _statusTimer?.Stop();
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        Loc.SetCulture(_host.Settings.UiLanguage);
        MauiThemeService.Apply(_host.Settings.UiTheme, this);
        LoadUi();
        ApplyLocalizedUi();
        RebuildMatrix();
        PlatformLabel.Text = _host.PlatformBanner;
        _host.StatusMessage += msg => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = msg);
        _host.LayoutSyncedFromPeer += _ => MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadUi();
            ApplyLocalizedUi();
            RebuildMatrix();
        });
        _host.Connections.Discovery.PeersUpdated += _ => MainThread.BeginInvokeOnMainThread(RefreshDiscovery);
        _host.Connections.PeerLinkChanged += (_, _, _) => MainThread.BeginInvokeOnMainThread(RefreshSlotStatuses);

        await _host.StartAsync();
        StatusLabel.Text = $"监听 {_host.Connections.BindIp}:{_host.Settings.TcpPort}";
        RefreshNics();
        RefreshDiscovery();
        RefreshSlotStatuses();
        ShowPage(0);

        _statusTimer = Dispatcher.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2);
        _statusTimer.Tick += (_, _) => RefreshSlotStatuses();
        _statusTimer.Start();
    }

    private void LoadUi()
    {
        var s = _host.Settings;
        s.EnsureLocalMachineSlot();
        KeyEntry.Text = s.SecurityKey;
        KeyEntry.IsPassword = true;
        ShowKeyCheck.IsChecked = false;
        _twoRow = s.TwoRow;
        ApplyLayoutModeButtons();

        WrapMouseCheck.IsChecked = s.WrapMouse;
        ShareClipboardCheck.IsChecked = s.ShareClipboard;
        TransferFileCheck.IsChecked = s.TransferFile;
        HideMouseCheck.IsChecked = s.HideMouseAtEdge;
        DrawCursorCheck.IsChecked = s.DrawMouseCursor;
        EasyMouseCheck.IsChecked = s.EasyMouse;
        SameSubnetCheck.IsChecked = s.SameSubnetOnly;
        ShowStatusCheck.IsChecked = s.ShowStatusMessages;
        AutoStartCheck.IsChecked = _host.Platform.SupportsAutoStart
            ? _host.Platform.GetAutoStartEnabled()
            : s.AutoStart;
        BlockSaverCheck.IsChecked = s.BlockScreenSaver;
        RelativeMouseCheck.IsChecked = s.MoveMouseRelatively;
        BlockCornersCheck.IsChecked = s.BlockMouseAtCorners;
        HotkeySettingsEntry.Text = s.HotkeySettings;
        HotkeyReconnectEntry.Text = s.HotkeyReconnect;
        HotkeyExitEntry.Text = s.HotkeyExit;
        HotkeyLockEntry.Text = s.HotkeyLock;
        HotF.IsChecked = s.SwitchHotkeyMode == 0;
        HotNum.IsChecked = s.SwitchHotkeyMode == 1;
        HotOff.IsChecked = s.SwitchHotkeyMode == 2;
        ThemeDark.IsChecked = string.Equals(s.UiTheme, "Dark", StringComparison.OrdinalIgnoreCase);
        ThemeLight.IsChecked = !ThemeDark.IsChecked;

        LanguagePicker.ItemsSource = Loc.Languages.ToList();
        LanguagePicker.SelectedItem = Loc.Languages.FirstOrDefault(l =>
            string.Equals(l.Code, s.UiLanguage, StringComparison.OrdinalIgnoreCase)) ?? Loc.Languages[0];

        TcpPortEntry.Text = s.TcpPort.ToString();
        UdpPortEntry.Text = s.UdpPort.ToString();

        SyncSlotsFromSettings();
    }

    private void ApplyLocalizedUi()
    {
        if (Application.Current?.Resources is { } rd)
        {
            rd["Str.Add"] = Loc.T("Btn.Add");
            rd["Str.Delete"] = Loc.T("Btn.Delete");
        }

        Title = Loc.T("App.Name");
        SearchEntry.Placeholder = Loc.T("Search.Placeholder") + "…";

        NavBasic.Text = Loc.T("Nav.Basic");
        NavNetwork.Text = Loc.T("Nav.Network");
        NavShare.Text = Loc.T("Nav.Share");
        NavSecurity.Text = Loc.T("Nav.Security");
        NavAdvanced.Text = Loc.T("Nav.Advanced");

        TabBasic.Text = Loc.T("Tab.Basic");
        TabOptions.Text = Loc.T("Tab.Options");
        TabNetwork.Text = Loc.T("Tab.Network");
        TabTransfer.Text = Loc.T("Tab.Transfer");
        AboutBtn.Text = Loc.T("Btn.About");
        HelpBtn.Text = Loc.T("Btn.Help");
        ApplyBtn.Text = Loc.T("Btn.Apply");
        ExitBtn.Text = Loc.T("Btn.Exit");
        TrayBtn.Text = _host.Platform.SupportsTray ? Loc.T("Btn.HideTray") : "后台运行";

        SecurityTitleText.Text = Loc.T("Sec.Title");
        ShowKeyLabel.Text = Loc.T("Sec.Show");
        NewKeyBtn.Text = Loc.T("Sec.NewKey");
        SecurityHintText.Text = Loc.T("Sec.Hint");
        LayoutTitleText.Text = Loc.T("Layout.Title");
        LayoutHintText.Text = Loc.T("Layout.Hint");
        LayoutDragTipText.Text = Loc.T("Layout.DragTip");
        AddMachineBtn.Text = Loc.T("Layout.Add");
        RemoveMachineBtn.Text = Loc.T("Layout.Remove");
        ReconnectBtn.Text = Loc.T("Layout.Reconnect");

        FeatTitleText.Text = Loc.T("Feat.Title");
        BehTitleText.Text = Loc.T("Beh.Title");
        WrapMouseLabel.Text = Loc.T("Opt.WrapMouse");
        ShareClipboardLabel.Text = Loc.T("Opt.ShareClipboard");
        TransferFileLabel.Text = Loc.T("Opt.TransferFile");
        HideMouseLabel.Text = Loc.T("Opt.HideMouse");
        DrawCursorLabel.Text = Loc.T("Opt.DrawCursor");
        SameSubnetLabel.Text = Loc.T("Opt.SameSubnet");
        ShowStatusLabel.Text = Loc.T("Opt.ShowStatus");
        AutoStartLabel.Text = Loc.T("Opt.AutoStart");
        BlockSaverLabel.Text = Loc.T("Opt.BlockSaver");
        RelativeMouseLabel.Text = Loc.T("Opt.MoveRel");
        BlockCornersLabel.Text = Loc.T("Opt.BlockCorner");
        EasyMouseLabel.Text = Loc.T("Opt.EasyMouse");

        HotTitleText.Text = Loc.T("Hot.Title");
        HotSwitchText.Text = Loc.T("Hot.Switch");
        HotF.Content = Loc.T("Hot.F14");
        HotNum.Content = Loc.T("Hot.Num");
        HotOff.Content = Loc.T("Hot.Off");
        HotSettingsLabel.Text = Loc.T("Hot.Settings");
        HotReconnectLabel.Text = Loc.T("Hot.Reconnect");
        HotExitLabel.Text = Loc.T("Hot.Exit");
        HotLockLabel.Text = Loc.T("Hot.Lock");

        SysTitleText.Text = Loc.T("Sys.Title");
        SysStyleLabel.Text = Loc.T("Sys.Style");
        SysLangLabel.Text = Loc.T("Sys.Language");
        ThemeLight.Content = Loc.T("Sys.StyleLight");
        ThemeDark.Content = Loc.T("Sys.StyleDark");

        NicTitleText.Text = Loc.T("Net.NicTitle");
        NicHintText.Text = Loc.T("Net.NicHint");
        RefreshNicsBtn.Text = Loc.T("Net.RefreshNics");
        TcpPortLabel.Text = Loc.T("Net.TcpPort");
        UdpPortLabel.Text = Loc.T("Net.UdpPort");
        DiscoveryTitleText.Text = Loc.T("Net.DiscoveryTitle");
        DiscoveryHintText.Text = Loc.T("Net.DiscoveryHint");
        RefreshDiscoveryBtn.Text = Loc.T("Net.RefreshDiscovery");

        XferTitleText.Text = Loc.T("Xfer.Title");
        XferHintText.Text = Loc.T("Xfer.Hint");
        DirectTransferBtn.Text = Loc.T("Xfer.Direct");
        OpenInboxBtn.Text = Loc.T("Xfer.OpenInbox");
        ClearTransfersBtn.Text = Loc.T("Xfer.ClearDone");
        ColDir.Text = Loc.T("Xfer.Col.Dir");
        ColName.Text = Loc.T("Xfer.Col.Name");
        ColPeer.Text = Loc.T("Xfer.Col.Peer");
        ColSize.Text = Loc.T("Xfer.Col.Size");
        ColStatus.Text = Loc.T("Xfer.Col.Status");
        XferEmptyText.Text = Loc.Culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? "暂无传输任务。"
            : "No transfers yet.";

        foreach (var item in _host.Transfers.Items)
            item.RefreshLocalized();

        foreach (var slot in _slots)
            slot.RefreshStatus();

        if (Window != null)
            Window.Title = Loc.T("App.Name");
    }

    private string ReadSelectedLanguage()
    {
        if (LanguagePicker.SelectedItem is LanguageOption opt)
            return opt.Code;
        if (LanguagePicker.ItemsSource is System.Collections.IList list
            && LanguagePicker.SelectedIndex >= 0
            && LanguagePicker.SelectedIndex < list.Count
            && list[LanguagePicker.SelectedIndex] is LanguageOption byIndex)
            return byIndex.Code;
        return string.IsNullOrWhiteSpace(_host.Settings.UiLanguage)
            ? Loc.DefaultCulture
            : _host.Settings.UiLanguage;
    }

    private void SyncSlotsFromSettings()
    {
        var s = _host.Settings;
        s.EnsureLocalMachineSlot();
        var local = LocalPeerIdentity.MachineName;
        while (s.MachineSlots.Count < 4) s.MachineSlots.Add("");

        _slots.Clear();
        for (var i = 0; i < 4; i++)
        {
            var name = s.MachineSlots[i]?.Trim() ?? "";
            var slot = new MachineSlot
            {
                Index = i,
                Name = name,
                IsLocal = string.Equals(name, local, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(name, Environment.MachineName, StringComparison.OrdinalIgnoreCase),
                Enabled = !string.IsNullOrWhiteSpace(name)
            };
            slot.SetLayoutHint(_twoRow);
            slot.RefreshStatus();
            _slots.Add(slot);
        }

        _selected = null;
    }

    private void RebuildMatrix()
    {
        // 与 Win UniformGrid 一致：1×4 或 2×2，卡片拉伸填满
        MachineMatrix.Children.Clear();
        MachineMatrix.RowDefinitions.Clear();
        MachineMatrix.ColumnDefinitions.Clear();

        var rows = _twoRow ? 2 : 1;
        var cols = _twoRow ? 2 : 4;
        for (var r = 0; r < rows; r++)
            MachineMatrix.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        for (var c = 0; c < cols; c++)
            MachineMatrix.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            var border = CreateSlotCard(slot);
            Grid.SetRow(border, i / cols);
            Grid.SetColumn(border, i % cols);
            MachineMatrix.Children.Add(border);
        }
    }

    private Border CreateSlotCard(MachineSlot slot)
    {
        var selected = slot.IsLocal || slot.IsSelected;
        // 与 Win 一致：默认 Margin=4；2×2 时 Margin=3；内边距由内容模板自身 Margin 承担
        var margin = _twoRow ? new Thickness(3) : new Thickness(4);
        var border = new Border
        {
            Margin = margin,
            Padding = 0,
            StrokeThickness = selected ? 2 : 1,
            Stroke = selected
                ? (Color)Application.Current!.Resources["Blue"]
                : (Color)Application.Current!.Resources["Line"],
            BackgroundColor = selected
                ? (Color)Application.Current!.Resources["BlueHover"]
                : slot.Enabled
                    ? (Color)Application.Current!.Resources["CardBg"]
                    : (Color)Application.Current!.Resources["MatrixBg"],
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill,
            Opacity = slot.Enabled || selected ? 1 : 0.9,
            BindingContext = slot
        };

        var statusColor = slot.IsLocal || slot.OutboundOk || slot.InboundOk
            ? (Color)Application.Current!.Resources["Success"]
            : (Color)Application.Current!.Resources["Muted"];

        View content;
        if (_twoRow)
        {
            // 对齐 Win MachineCard2x2：图标左 + 文字右
            var grid = new Grid
            {
                Margin = new Thickness(6, 4),
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(GridLength.Auto),
                    new(GridLength.Star)
                },
                ColumnSpacing = 10,
                VerticalOptions = LayoutOptions.Center
            };
            grid.Add(CreatePcIcon(), 0, 0);
            var text = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 3 };
            text.Children.Add(new Label
            {
                Text = slot.PositionLabel,
                FontSize = 11,
                TextColor = (Color)Application.Current!.Resources["Muted"],
                Margin = new Thickness(0, 0, 0, 1)
            });
            text.Children.Add(new Label
            {
                Text = slot.DisplayName,
                FontSize = 13,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.TailTruncation,
                HorizontalTextAlignment = TextAlignment.Start
            });
            text.Children.Add(new Label
            {
                Text = slot.DisplaySubtitle,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = statusColor,
                HorizontalTextAlignment = TextAlignment.Start,
                Margin = new Thickness(0, 3, 0, 0)
            });
            grid.Add(text, 1, 0);
            content = grid;
        }
        else
        {
            // 对齐 Win MachineCard1x4：位置 / 图标 / 名称 / 状态 纵向居中
            var stack = new VerticalStackLayout
            {
                Margin = new Thickness(4),
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Center,
                Spacing = 0
            };
            stack.Children.Add(new Label
            {
                Text = slot.PositionLabel,
                FontSize = 11,
                TextColor = (Color)Application.Current!.Resources["Muted"],
                HorizontalTextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var icon = CreatePcIcon();
            icon.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(icon);
            stack.Children.Add(new Label
            {
                Text = slot.DisplayName,
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.TailTruncation,
                HorizontalTextAlignment = TextAlignment.Center
            });
            stack.Children.Add(new Label
            {
                Text = slot.DisplaySubtitle,
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = statusColor,
                HorizontalTextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            });
            content = stack;
        }

        border.Content = content;
        AttachSlotGestures(border, slot);
        return border;
    }

    /// <summary>单击选中、双击移除、拖拽互换（对齐 Win）。</summary>
    private void AttachSlotGestures(Border border, MachineSlot slot)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => OnSlotTapped(slot);
        border.GestureRecognizers.Add(tap);

        var drag = new DragGestureRecognizer();
        drag.DragStarting += (_, e) =>
        {
            e.Data.Properties[DragSlotKey] = slot;
            SelectSlotOnly(slot);
        };
        border.GestureRecognizers.Add(drag);

        var drop = new DropGestureRecognizer { AllowDrop = true };
        drop.DragOver += (_, e) => e.AcceptedOperation = DataPackageOperation.Copy;
        drop.Drop += (_, e) =>
        {
            e.Handled = true;
            if (!e.Data.Properties.TryGetValue(DragSlotKey, out var obj) || obj is not MachineSlot src)
                return;
            if (ReferenceEquals(src, slot)) return;

            SwapSlotData(src, slot);
            SyncSlotLocalFlags();
            SelectSlotOnly(slot);
            RebuildMatrix();
            StatusLabel.Text = "已互换位置（请点「应用」同步）";
        };
        border.GestureRecognizers.Add(drop);
    }

    private static void SwapSlotData(MachineSlot a, MachineSlot b)
    {
        (a.Name, b.Name) = (b.Name, a.Name);
        (a.Enabled, b.Enabled) = (b.Enabled, a.Enabled);
        (a.IsLocal, b.IsLocal) = (b.IsLocal, a.IsLocal);
        (a.ResolvedIp, b.ResolvedIp) = (b.ResolvedIp, a.ResolvedIp);
        (a.OutboundOk, b.OutboundOk) = (b.OutboundOk, a.OutboundOk);
        (a.InboundOk, b.InboundOk) = (b.InboundOk, a.InboundOk);
        a.RefreshStatus();
        b.RefreshStatus();
    }

    private void SyncSlotLocalFlags()
    {
        var local = LocalPeerIdentity.MachineName;
        foreach (var s in _slots)
        {
            s.IsLocal = string.Equals(s.Name, local, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(s.Name, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
            s.Enabled = !s.IsEmpty;
            s.RefreshStatus();
        }
    }

    private void SelectSlotOnly(MachineSlot slot)
    {
        foreach (var s in _slots)
            s.IsSelected = ReferenceEquals(s, slot);
        _selected = slot;
    }

    /// <summary>简易 PC 图标，尺寸对齐 Win PcIconTemplate 40×34。</summary>
    private static View CreatePcIcon()
    {
        var screen = new Border
        {
            WidthRequest = 36,
            HeightRequest = 24,
            BackgroundColor = Color.FromArgb("#1F2937"),
            Stroke = Color.FromArgb("#111827"),
            StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 3 },
            HorizontalOptions = LayoutOptions.Center,
            Content = new Border
            {
                Margin = 3,
                BackgroundColor = Color.FromArgb("#60A5FA"),
                Opacity = 0.4,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 2 }
            }
        };
        var stand = new BoxView
        {
            WidthRequest = 12,
            HeightRequest = 3,
            Color = Color.FromArgb("#6B7280"),
            HorizontalOptions = LayoutOptions.Center,
            CornerRadius = 1
        };
        return new VerticalStackLayout
        {
            WidthRequest = 40,
            HeightRequest = 34,
            Spacing = 0,
            HorizontalOptions = LayoutOptions.Center,
            Children = { screen, stand }
        };
    }

    private void OnSlotTapped(MachineSlot slot)
    {
        // 双击移除（对齐 Win）；互换改为拖拽
        if (_selected == slot && slot.CanRemove
            && Environment.TickCount64 - _lastTapTicks < 400)
        {
            RemoveSlot(slot);
            _lastTapTicks = 0;
            return;
        }

        _lastTapTicks = Environment.TickCount64;
        SelectSlotOnly(slot);
        RebuildMatrix();
        StatusLabel.Text = slot.IsEmpty
            ? "已选空位，可添加电脑"
            : $"已选 {slot.DisplayName}（拖拽到其他格子互换）";
    }

    private long _lastTapTicks;

    private void RefreshSlotStatuses()
    {
        foreach (var st in _host.Connections.GetConfiguredPeerStatuses())
        {
            var slot = _slots.FirstOrDefault(s =>
                string.Equals(s.Name, st.Name, StringComparison.OrdinalIgnoreCase));
            if (slot == null) continue;
            slot.OutboundOk = st.Outbound;
            slot.InboundOk = st.Inbound;
            if (!string.IsNullOrEmpty(st.DiscoveredIp))
                slot.ResolvedIp = st.DiscoveredIp;
            slot.RefreshStatus();
        }

        RebuildMatrix();
    }

    private void ApplyLayoutModeButtons()
    {
        try
        {
            var active = (Style)Application.Current!.Resources["SegBtnActive"];
            var normal = (Style)Application.Current!.Resources["SegBtn"];
            Layout1x4Btn.Style = _twoRow ? normal : active;
            Layout2x2Btn.Style = _twoRow ? active : normal;
        }
        catch { /* ignore */ }
    }

    private void ShowPage(int index)
    {
        _pageIndex = index;
        PageBasic.IsVisible = index == 0;
        PageOptions.IsVisible = index == 1;
        PageNetwork.IsVisible = index == 2;
        PageTransfer.IsVisible = index == 3;

        try
        {
            var primary = (Style)Application.Current!.Resources["PrimaryBtn"];
            var normal = (Style)Application.Current!.Resources["NormalBtn"];
            var nav = (Style)Application.Current!.Resources["NavBtn"];
            var navActive = (Style)Application.Current!.Resources["NavBtnActive"];
            for (var i = 0; i < _tabButtons.Count; i++)
                _tabButtons[i].Style = i == index ? primary : normal;
            NavBasic.Style = index == 0 ? navActive : nav;
            NavSecurity.Style = nav;
            NavAdvanced.Style = index == 1 ? navActive : nav;
            NavNetwork.Style = index == 2 ? navActive : nav;
            NavShare.Style = index == 3 ? navActive : nav;
        }
        catch { /* ignore */ }
    }

    private void Search_Changed(object? sender, TextChangedEventArgs e)
    {
        var q = (e.NewTextValue ?? "").Trim();
        if (string.IsNullOrEmpty(q)) return;
        if (q.Contains("传输") || q.Contains("文件") || q.Contains("共享")) ShowPage(3);
        else if (q.Contains("网络") || q.Contains("发现") || q.Contains("网卡") || q.Contains("端口")) ShowPage(2);
        else if (q.Contains("高级") || q.Contains("热键") || q.Contains("选项") || q.Contains("主题")) ShowPage(1);
        else if (q.Contains("密钥") || q.Contains("布局") || q.Contains("安全") || q.Contains("基础")) ShowPage(0);
    }

    private void Nav_Clicked(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, NavBasic) || ReferenceEquals(sender, NavSecurity)) ShowPage(0);
        else if (ReferenceEquals(sender, NavAdvanced)) ShowPage(1);
        else if (ReferenceEquals(sender, NavNetwork)) ShowPage(2);
        else if (ReferenceEquals(sender, NavShare)) ShowPage(3);
    }

    private void Tab_Clicked(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, TabBasic)) ShowPage(0);
        else if (ReferenceEquals(sender, TabOptions)) ShowPage(1);
        else if (ReferenceEquals(sender, TabNetwork)) ShowPage(2);
        else if (ReferenceEquals(sender, TabTransfer)) ShowPage(3);
    }

    private void LayoutMode_Clicked(object? sender, EventArgs e)
    {
        _twoRow = ReferenceEquals(sender, Layout2x2Btn);
        foreach (var s in _slots) s.SetLayoutHint(_twoRow);
        ApplyLayoutModeButtons();
        RebuildMatrix();
    }

    private void ShowKey_Changed(object? sender, CheckedChangedEventArgs e)
        => KeyEntry.IsPassword = !e.Value;

    private void NewKey_Clicked(object? sender, EventArgs e)
    {
        KeyEntry.Text = AppSettings.GenerateKey();
        KeyEntry.IsPassword = ShowKeyCheck.IsChecked != true;
    }

    private async void Apply_Clicked(object? sender, EventArgs e)
    {
        _ = int.TryParse(TcpPortEntry.Text, out var tcp);
        _ = int.TryParse(UdpPortEntry.Text, out var udp);
        if (tcp <= 0) tcp = 15155;
        if (udp <= 0) udp = 15156;

        if (NicPicker.SelectedItem is NicInfo nic)
        {
            _host.Settings.PreferredNicId = nic.Id;
            _host.Settings.PreferredLocalIp = nic.IPv4;
        }

        var switchMode = HotF.IsChecked == true ? 0 : HotNum.IsChecked == true ? 1 : 2;
        var theme = ThemeDark.IsChecked == true ? MauiThemeService.Dark : MauiThemeService.Light;
        var lang = ReadSelectedLanguage();

        _host.ApplyFromUi(
            KeyEntry.Text ?? "",
            _slots.Select(s => s.Name).ToList(),
            _twoRow,
            tcp,
            udp,
            WrapMouseCheck.IsChecked == true,
            ShareClipboardCheck.IsChecked == true,
            TransferFileCheck.IsChecked == true,
            EasyMouseCheck.IsChecked == true,
            SameSubnetCheck.IsChecked == true,
            HideMouseCheck.IsChecked == true,
            DrawCursorCheck.IsChecked == true,
            ShowStatusCheck.IsChecked == true,
            AutoStartCheck.IsChecked == true,
            BlockSaverCheck.IsChecked == true,
            RelativeMouseCheck.IsChecked == true,
            BlockCornersCheck.IsChecked == true,
            switchMode,
            HotkeySettingsEntry.Text,
            HotkeyLockEntry.Text,
            HotkeyReconnectEntry.Text,
            HotkeyExitEntry.Text,
            theme,
            lang);

        Loc.SetCulture(lang);
        MauiThemeService.Apply(theme, this);
        LoadUi();
        ApplyLocalizedUi();
        RebuildMatrix();
        await _host.BroadcastLayoutSyncAsync();
        await _host.RestartNetworkingAsync();
        StatusLabel.Text = Loc.T("Sys.Applied");
    }

    private async void Reconnect_Clicked(object? sender, EventArgs e)
    {
        await _host.Connections.ReconnectNowAsync();
        StatusLabel.Text = "正在重新连接…";
    }

    private void RefreshNics_Clicked(object? sender, EventArgs e) => RefreshNics();

    private void RefreshNics()
    {
        var nics = NicService.ListUsableIpv4Nics().ToList();
        NicPicker.ItemsSource = nics;
        NicPicker.ItemDisplayBinding = new Binding(nameof(NicInfo.ShortDisplay));
        var preferred = NicService.ResolvePreferred(_host.Settings.PreferredNicId, _host.Settings.PreferredLocalIp);
        NicPicker.SelectedItem = preferred != null
            ? nics.FirstOrDefault(n => n.Id == preferred.Id && n.IPv4 == preferred.IPv4) ?? nics.FirstOrDefault()
            : nics.FirstOrDefault();
    }

    private void RefreshDiscovery_Clicked(object? sender, EventArgs e) => RefreshDiscovery();

    private void RefreshDiscovery()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            LocalPeerIdentity.MachineName,
            Environment.MachineName
        };
        foreach (var s in _slots)
            if (!string.IsNullOrWhiteSpace(s.Name)) used.Add(s.Name);

        var peers = _host.Connections.Discovery.GetPeers()
            .Where(p => !string.IsNullOrWhiteSpace(p.MachineName) && !used.Contains(p.MachineName))
            .ToList();

        // WinUI：对已绑定 CollectionView 的 ObservableCollection 调用 Clear() 会发 Reset，
        // 进而在 NotifyCollectionChanged 封送时 COM 崩溃；改为整体替换 ItemsSource。
        _discovery = new ObservableCollection<DiscoveredPeer>(peers);
        DiscoveryList.ItemsSource = _discovery;
    }

    private void AddDiscovered_Clicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: DiscoveredPeer peer }) return;
        TryAddPeer(peer.MachineName);
    }

    private void AddPeer_Clicked(object? sender, EventArgs e)
    {
        RefreshDiscovery();
        if (_discovery.Count == 0)
        {
            ShowPage(2);
            StatusLabel.Text = "暂无新发现的电脑，请先刷新发现";
            return;
        }

        TryAddPeer(_discovery[0].MachineName);
    }

    private void RemovePeer_Clicked(object? sender, EventArgs e)
    {
        if (_selected == null || !_selected.CanRemove)
        {
            StatusLabel.Text = "请先选中一台可移除的对端电脑";
            return;
        }

        RemoveSlot(_selected);
    }

    private void RemoveSlot(MachineSlot slot)
    {
        slot.Name = "";
        slot.IsLocal = false;
        slot.Enabled = false;
        slot.OutboundOk = false;
        slot.InboundOk = false;
        slot.RefreshStatus();
        _selected = null;
        RebuildMatrix();
        RefreshDiscovery();
        StatusLabel.Text = "已移除（请点「应用」同步）";
    }

    private void TryAddPeer(string name)
    {
        if (_slots.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusLabel.Text = $"{name} 已在布局中";
            return;
        }

        var empty = _slots.FirstOrDefault(s => s.IsEmpty);
        if (empty == null)
        {
            StatusLabel.Text = "没有空的机器槽位，请先移除一台电脑。";
            return;
        }

        empty.Name = name;
        empty.Enabled = true;
        empty.IsLocal = false;
        empty.RefreshStatus();
        RebuildMatrix();
        StatusLabel.Text = $"已添加 {name}";
        RefreshDiscovery();
        ShowPage(0);
    }

    private async void DirectTransfer_Clicked(object? sender, EventArgs e)
    {
        var peer = _host.Connections.GetConfiguredPeerStatuses().FirstOrDefault(s => s.AnyAlive)?.Name;
        if (string.IsNullOrEmpty(peer))
        {
            StatusLabel.Text = "没有已连接的对端";
            return;
        }

        var picked = await FilePicker.Default.PickMultipleAsync();
        var files = picked?.ToList() ?? [];
        if (files.Count == 0) return;
        var paths = files.Select(f => f.FullPath).Where(p => !string.IsNullOrEmpty(p) && File.Exists(p!)).Cast<string>().ToList();
        if (paths.Count == 0) return;

        await _host.Transfers.StartDirectTransferAsync(peer, _host.Transfers.InboxDirectory, paths);
        StatusLabel.Text = $"正在传输 {paths.Count} 个文件 → {peer}";
        ShowPage(3);
    }

    private async void OpenInbox_Clicked(object? sender, EventArgs e)
    {
        var dir = _host.Transfers.InboxDirectory;
        try
        {
            Directory.CreateDirectory(dir);
#if WINDOWS
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
#else
            await Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(dir) });
#endif
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("收件箱", $"{dir}\n{ex.Message}", "OK");
        }
    }

    private void ClearTransfers_Clicked(object? sender, EventArgs e)
        => _host.Transfers.ClearCompleted();

    private void DeleteTransfer_Clicked(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: TransferItem item })
            _host.Transfers.RemoveItem(item);
    }

    private void Tray_Clicked(object? sender, EventArgs e)
    {
        _host.Platform.HideToBackground(
            showMain: () => { },
            reconnect: () => _ = _host.Connections.ReconnectNowAsync(),
            about: () => MainThread.BeginInvokeOnMainThread(() => _ = ShowAboutAsync()),
            exit: () => Application.Current?.Quit());
    }

    private async void About_Clicked(object? sender, EventArgs e)
        => await ShowAboutAsync();

    private async Task ShowAboutAsync()
    {
        var ver = typeof(MainPage).Assembly.GetName().Version;
        var verText = ver == null ? "1.0" : $"{ver.Major}.{ver.Minor}.{ver.Build}";
        await DisplayAlertAsync(Loc.T("About.Title"), string.Format(Loc.T("About.Body"), verText), "OK");
    }

    private async void Help_Clicked(object? sender, EventArgs e)
    {
        var page = new HelpPage(_host);
        if (Shell.Current != null)
            await Shell.Current.Navigation.PushModalAsync(page);
        else
            await Navigation.PushModalAsync(page);
    }

    private void Exit_Clicked(object? sender, EventArgs e)
        => Application.Current?.Quit();
}
