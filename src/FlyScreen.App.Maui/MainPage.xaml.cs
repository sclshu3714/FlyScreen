using System.Collections.ObjectModel;
using FlyScreen.Helpers.Localization;
using FlyScreen.AppMaui.Services;
using FlyScreen.Models;
using FlyScreen.Services.Network;

namespace FlyScreen.AppMaui;

public partial class MainPage : ContentPage
{
    private readonly MauiAppHost _host;
    private readonly ObservableCollection<DiscoveredPeer> _discovery = [];
    private int _pageIndex;
    private bool _twoRow = true;
    private readonly List<Button> _navButtons = [];
    private readonly List<Button> _tabButtons = [];

    public MainPage()
    {
        InitializeComponent();
        _host = MauiAppHost.EnsureCreated();
        DiscoveryList.ItemsSource = _discovery;
        _navButtons.AddRange([NavBasic, NavNetwork, NavShare, NavSecurity, NavAdvanced]);
        _tabButtons.AddRange([TabBasic, TabOptions, TabNetwork, TabTransfer]);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        LoadUi();
        PlatformLabel.Text = _host.PlatformBanner;
        _host.StatusMessage += msg => MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = msg);
        _host.LayoutSyncedFromPeer += from => MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadUi();
            StatusLabel.Text = $"已同步布局 ← {from}";
        });
        _host.Connections.Discovery.PeersUpdated += _ => MainThread.BeginInvokeOnMainThread(RefreshDiscovery);

        await _host.StartAsync();
        StatusLabel.Text = $"监听 {_host.Connections.BindIp}:{_host.Settings.TcpPort}";
        RefreshNics();
        RefreshDiscovery();
        ShowPage(0);
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

        var slots = s.MachineSlots;
        while (slots.Count < 4) slots.Add("");
        Slot0.Text = slots[0];
        Slot1.Text = slots[1];
        Slot2.Text = slots[2];
        Slot3.Text = slots[3];

        WrapMouseCheck.IsChecked = s.WrapMouse;
        ShareClipboardCheck.IsChecked = s.ShareClipboard;
        TransferFileCheck.IsChecked = s.TransferFile;
        EasyMouseCheck.IsChecked = s.EasyMouse;
        SameSubnetCheck.IsChecked = s.SameSubnetOnly;
        TcpPortEntry.Text = s.TcpPort.ToString();
        UdpPortEntry.Text = s.UdpPort.ToString();
    }

    private void ApplyLayoutModeButtons()
    {
        try
        {
            var primary = (Style)Application.Current!.Resources["PrimaryBtn"];
            var normal = (Style)Application.Current!.Resources["NormalBtn"];
            Layout1x4Btn.Style = _twoRow ? normal : primary;
            Layout2x2Btn.Style = _twoRow ? primary : normal;
        }
        catch
        {
            // ignore style swap failures
        }
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

            // 侧栏映射：基础/安全→0，高级→1，网络→2，共享→3
            NavBasic.Style = index == 0 ? navActive : nav;
            NavSecurity.Style = index == 0 ? nav : nav;
            NavAdvanced.Style = index == 1 ? navActive : nav;
            NavNetwork.Style = index == 2 ? navActive : nav;
            NavShare.Style = index == 3 ? navActive : nav;
        }
        catch
        {
            // ignore
        }
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
        ApplyLayoutModeButtons();
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

        _host.ApplyFromUi(
            KeyEntry.Text ?? "",
            [Slot0.Text ?? "", Slot1.Text ?? "", Slot2.Text ?? "", Slot3.Text ?? ""],
            _twoRow,
            tcp,
            udp,
            WrapMouseCheck.IsChecked == true,
            ShareClipboardCheck.IsChecked == true,
            TransferFileCheck.IsChecked == true,
            EasyMouseCheck.IsChecked == true,
            SameSubnetCheck.IsChecked == true);

        LoadUi();
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
        NicPicker.ItemDisplayBinding = new Binding(nameof(NicInfo.Display));
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
            Environment.MachineName,
            Slot0.Text?.Trim() ?? "",
            Slot1.Text?.Trim() ?? "",
            Slot2.Text?.Trim() ?? "",
            Slot3.Text?.Trim() ?? ""
        };
        used.Remove("");

        _discovery.Clear();
        foreach (var p in _host.Connections.Discovery.GetPeers()
                     .Where(p => !string.IsNullOrWhiteSpace(p.MachineName) && !used.Contains(p.MachineName)))
            _discovery.Add(p);
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

    private void TryAddPeer(string name)
    {
        var boxes = new[] { Slot0, Slot1, Slot2, Slot3 };
        if (boxes.Any(b => string.Equals(b.Text?.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusLabel.Text = $"{name} 已在布局中";
            return;
        }

        var empty = boxes.FirstOrDefault(b => string.IsNullOrWhiteSpace(b.Text));
        if (empty == null)
        {
            StatusLabel.Text = "没有空的机器槽位";
            return;
        }

        empty.Text = name;
        StatusLabel.Text = $"已添加 {name}";
        RefreshDiscovery();
    }

    private async void About_Clicked(object? sender, EventArgs e)
    {
        var ver = typeof(MainPage).Assembly.GetName().Version;
        await DisplayAlert(Loc.T("Btn.About"), $"{Loc.T("App.Name")}\nVersion {ver}\n{_host.PlatformBanner}", "OK");
    }

    private async void Help_Clicked(object? sender, EventArgs e)
    {
        await DisplayAlert(Loc.T("Btn.Help"),
            Loc.T("Help.Doc.Q2") + "\n\n" + Loc.T("Help.Doc.Q4") + "\n\nMAUI 界面分区与 Windows 端保持一致。",
            "OK");
    }

    private void Exit_Clicked(object? sender, EventArgs e)
        => Application.Current?.Quit();
}
