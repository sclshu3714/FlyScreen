using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.DesktopUi;

public partial class ScreenExtendSinkWindow : Window
{
    private readonly Action<string> _onStopTab;
    private readonly Action _onCloseWindow;
    private readonly Func<IReadOnlyList<string>> _listAddablePeers;
    private readonly Func<string, Task> _startProjection;
    private readonly SinkLocalCursorIdleHide _cursorIdle = new();
    private readonly ScreenExtendReverseMoveGate _reverseGate = new();
    private readonly List<SinkTabState> _tabs = [];
    private string? _activePeer;
    private ScreenExtendService? _session;
    private bool _closingFromSession;
    private bool _userCloseRaised;
    private bool _captureBound;
    private bool _chromeFullscreen;
    private bool _fsToolbarExpanded = true;
    private bool _fsToolbarDragging;
    private bool _fsToolbarMoved;
    private Point _fsDragStart;
    private Point _fsToolbarOrigin;

    private static readonly IBrush TabActiveBg = Brush.Parse("#2C2C30");
    private static readonly IBrush TabInactiveBg = Brush.Parse("#18181B");
    private static readonly IBrush SignalGreen = Brush.Parse("#22C55E");
    private static readonly IBrush SignalYellow = Brush.Parse("#EAB308");
    private static readonly IBrush SignalRed = Brush.Parse("#EF4444");
    private static readonly IBrush SignalMuted = Brush.Parse("#6B7280");

    private sealed class SinkTabState
    {
        public required string Peer { get; init; }
        public required Border TabBorder { get; init; }
        public required Canvas SignalCanvas { get; init; }
        public required TextBlock NameText { get; init; }
        public Bitmap? FrameBitmap;
        public int ContentW;
        public int ContentH;
        public double LatencyEma;
        public long LatencySkewBaseline = long.MinValue;
        public ProjectionLatency.Signal LastSignal;
        public string LatencyTip = "";
    }

    public ScreenExtendSinkWindow()
        : this(_ => { }, () => { }, () => [], _ => Task.CompletedTask) { }

    public ScreenExtendSinkWindow(
        Action<string> onStopTab,
        Action onCloseWindow,
        Func<IReadOnlyList<string>> listAddablePeers,
        Func<string, Task> startProjection)
    {
        _onStopTab = onStopTab;
        _onCloseWindow = onCloseWindow;
        _listAddablePeers = listAddablePeers;
        _startProjection = startProjection;
        InitializeComponent();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Activated += (_, _) =>
        {
            Focus();
            TryAttachCapture();
        };
        Deactivated += (_, _) => DetachCapture();
        Closing += (_, _) => NotifyWindowClose();
        Closed += (_, _) =>
        {
            DetachCapture();
            _cursorIdle.EndSession();
            _cursorIdle.Dispose();
            DisposeAllTabBitmaps();
            NotifyWindowClose();
        };
        _cursorIdle.ShowLocalCursorChanged += show =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_chromeFullscreen)
                    Cursor = show
                        ? new Cursor(StandardCursorType.Arrow)
                        : new Cursor(StandardCursorType.None);
            });
        };
    }

    public bool HasTabs => _tabs.Count > 0;

    public void BindSession(ScreenExtendService service)
    {
        _session = service;
        var peer = service.Peer?.Trim();
        if (!string.IsNullOrWhiteSpace(peer))
            AddOrActivateTab(peer, service);
    }

    private SinkTabState CreateTabUi(string peer)
    {
        var signalCanvas = new Canvas { Width = 14, Height = 12, Margin = new Thickness(0, 0, 8, 0) };
        var nameText = new TextBlock
        {
            Text = peer,
            Foreground = Brush.Parse("#F3F4F6"),
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var closeBtn = new Button
        {
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Content = "×",
            FontSize = 13,
            Foreground = Brush.Parse("#9CA3AF"),
            Tag = peer,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(closeBtn, "结束投影");
        closeBtn.Click += TabClose_Click;

        var tabBody = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Spacing = 0,
            Children = { signalCanvas, nameText, closeBtn }
        };

        var tabBorder = new Border
        {
            Background = TabInactiveBg,
            CornerRadius = new CornerRadius(6, 6, 0, 0),
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(10, 0, 6, 0),
            MinWidth = 120,
            Height = 28,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Child = tabBody,
            Tag = peer,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        tabBorder.PointerPressed += TabBorder_PointerPressed;

        var state = new SinkTabState
        {
            Peer = peer,
            TabBorder = tabBorder,
            SignalCanvas = signalCanvas,
            NameText = nameText,
            LatencyTip = Loc.T("Proj.LatencyTipWait")
        };
        ToolTip.SetTip(tabBorder, state.LatencyTip);
        DrawSignalOn(signalCanvas, ref state.LastSignal, new ProjectionLatency.Signal(0, 0));
        return state;
    }

    public void EnsureTab(string peer, ScreenExtendService? service = null)
    {
        if (service != null)
            _session = service;
        peer = peer.Trim();
        if (string.IsNullOrEmpty(peer)) return;
        if (FindTab(peer) != null) return;

        var created = CreateTabUi(peer);
        _tabs.Add(created);
        TabsHost.Children.Add(created.TabBorder);
        if (string.IsNullOrEmpty(_activePeer))
            SetActiveTab(peer);
        else
            RefreshTabChrome();
    }

    public void AddOrActivateTab(string peer, ScreenExtendService? service = null)
    {
        EnsureTab(peer, service);
        if (HasTab(peer))
            SetActiveTab(peer);
    }

    public void RemoveTab(string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return;
        peer = peer.Trim();
        var idx = _tabs.FindIndex(t => string.Equals(t.Peer, peer, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;

        var removed = _tabs[idx];
        TabsHost.Children.Remove(removed.TabBorder);
        try { removed.FrameBitmap?.Dispose(); } catch { /* ignore */ }
        _tabs.RemoveAt(idx);

        if (_tabs.Count == 0)
        {
            _activePeer = null;
            FrameImage.Source = null;
            WaitingText.IsVisible = true;
            return;
        }

        if (string.Equals(_activePeer, peer, StringComparison.OrdinalIgnoreCase))
            SetActiveTab(_tabs[Math.Min(idx, _tabs.Count - 1)].Peer);
        else
            RefreshTabChrome();
    }

    private void SetActiveTab(string peer)
    {
        peer = peer.Trim();
        if (!HasTab(peer)) return;
        var same = string.Equals(_activePeer, peer, StringComparison.OrdinalIgnoreCase);
        _activePeer = peer;
        _session?.SetActiveSinkPeer(peer);
        RefreshTabChrome();
        RefreshTitle();
        ShowActiveTabFrame();
        ApplyActiveLatencyTip();
        if (same) return;
        DetachCapture();
        TryAttachCapture();
    }

    private void SelectNextTab()
    {
        if (_tabs.Count <= 1) return;
        var idx = _tabs.FindIndex(t => string.Equals(t.Peer, _activePeer, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) idx = 0;
        var next = _tabs[(idx + 1) % _tabs.Count];
        SetActiveTab(next.Peer);
    }

    private void RefreshTabChrome()
    {
        foreach (var t in _tabs)
        {
            var active = string.Equals(t.Peer, _activePeer, StringComparison.OrdinalIgnoreCase);
            t.TabBorder.Background = active ? TabActiveBg : TabInactiveBg;
        }
    }

    private void RefreshTitle()
    {
        var peer = _activePeer?.Trim();
        Title = string.IsNullOrWhiteSpace(peer) ? "玄鸟投影" : $"玄鸟投影 · {peer}";
    }

    private void ShowActiveTabFrame()
    {
        var tab = FindTab(_activePeer);
        if (tab?.FrameBitmap != null)
        {
            FrameImage.Source = tab.FrameBitmap;
            WaitingText.IsVisible = false;
        }
        else
        {
            FrameImage.Source = null;
            WaitingText.IsVisible = true;
        }
    }

    private SinkTabState? FindTab(string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return null;
        return _tabs.Find(t => string.Equals(t.Peer, peer.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private bool HasTab(string peer) => FindTab(peer) != null;

    private int ActiveContentW => FindTab(_activePeer)?.ContentW ?? 0;
    private int ActiveContentH => FindTab(_activePeer)?.ContentH ?? 0;

    private void TabBorder_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;
        if (e.Source is Visual src)
        {
            for (Visual? cur = src; cur != null; cur = cur.GetVisualParent())
            {
                if (cur is Button) return; // 关闭钮留给 Click
                if (cur == sender) break;
            }
        }

        var peer = (sender as Control)?.Tag as string;
        if (string.IsNullOrWhiteSpace(peer)) return;
        SetActiveTab(peer);
        e.Handled = true;
    }

    private void TabClose_Click(object? sender, RoutedEventArgs e)
    {
        var peer = (sender as Control)?.Tag as string ?? _activePeer;
        if (!string.IsNullOrWhiteSpace(peer))
            RequestStopTab(peer);
        e.Handled = true;
    }

    private void CloseCurrentTab_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_activePeer))
            RequestStopTab(_activePeer);
        e.Handled = true;
    }

    private void NextTab_Click(object? sender, RoutedEventArgs e) => SelectNextTab();

    private void RequestStopTab(string peer)
    {
        try { _onStopTab(peer.Trim()); } catch { /* ignore */ }
    }

    private void WindowClose_Click(object? sender, RoutedEventArgs e)
    {
        NotifyWindowClose();
        Close();
    }

    private void NotifyWindowClose()
    {
        if (_closingFromSession || _userCloseRaised) return;
        _userCloseRaised = true;
        try { _onCloseWindow(); } catch { /* ignore */ }
    }

    private void AddTab_Click(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> items;
        try { items = _listAddablePeers(); }
        catch { items = []; }

        var menu = new ContextMenu();
        if (items.Count == 0)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "无可用设备",
                IsEnabled = false,
                Icon = CreateAddMenuDeviceIcon(muted: true)
            });
        }
        else
        {
            foreach (var name in items)
            {
                var mi = new MenuItem
                {
                    Header = name,
                    Tag = name,
                    FontSize = 12,
                    Icon = CreateAddMenuDeviceIcon(muted: false)
                };
                mi.Click += async (_, _) =>
                {
                    if (mi.Tag is string remote)
                    {
                        try { await _startProjection(remote); }
                        catch { /* ignore */ }
                    }
                };
                menu.Items.Add(mi);
            }
        }

        menu.Open(AddTabBtn);
    }

    private static Viewbox CreateAddMenuDeviceIcon(bool muted)
    {
        var stroke = Brush.Parse(muted ? "#6B7280" : "#D1D5DB");
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(
                "M 1.5,2.5 H 12.5 V 9.5 H 1.5 Z M 5,11.5 H 9 M 7,9.5 V 11.5"),
            Stroke = stroke,
            StrokeThickness = 1.2,
            Stretch = Stretch.Uniform,
            Width = 14,
            Height = 12
        };
        return new Viewbox
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Child = path
        };
    }

    private void TryAttachCapture()
    {
        if (_captureBound) return;
        if (_session?.CanSendReverseInput != true) return;
        _captureBound = true;
        _reverseGate.OnCaptureAttached();
        ContentHost.PointerMoved += OnPointerMoved;
        ContentHost.PointerPressed += OnPointerPressed;
        ContentHost.PointerReleased += OnPointerReleased;
        ContentHost.PointerWheelChanged += OnPointerWheel;
    }

    private void DetachCapture()
    {
        if (!_captureBound) return;
        _captureBound = false;
        _reverseGate.OnCaptureDetached();
        ContentHost.PointerMoved -= OnPointerMoved;
        ContentHost.PointerPressed -= OnPointerPressed;
        ContentHost.PointerReleased -= OnPointerReleased;
        ContentHost.PointerWheelChanged -= OnPointerWheel;
    }

    private bool TryMap(PointerEventArgs e, out double nx, out double ny)
    {
        var p = e.GetPosition(ContentHost);
        return ScreenExtendReverseInput.TryMapUniformContent(
            ContentHost.Bounds.Width, ContentHost.Bounds.Height, ActiveContentW, ActiveContentH, p.X, p.Y, out nx, out ny);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _cursorIdle.NotifyLocalMouseMoved();
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendMove(nx, ny)) return;
        _ = _session.SendReverseMoveAsync(nx, ny);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        var button = MapButton(e);
        if (button < 0) return;
        e.Handled = true;
        _ = _session.SendReverseButtonAsync(nx, ny, button, down: true);
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        var button = MapButton(e);
        if (button < 0) return;
        e.Handled = true;
        _ = _session.SendReverseButtonAsync(nx, ny, button, down: false);
    }

    private void OnPointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_session == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        var delta = (int)Math.Round(e.Delta.Y * 120);
        if (delta == 0)
            delta = (int)Math.Round(e.Delta.X * 120);
        if (delta == 0) return;
        e.Handled = true;
        var horizontal = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
        _ = _session.SendReverseWheelAsync(nx, ny, delta, horizontal);
    }

    private static int MapButton(PointerEventArgs e)
    {
        var props = e.GetCurrentPoint(null).Properties;
        if (props.IsLeftButtonPressed || props.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed
            || props.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased)
            return 0;
        if (props.IsRightButtonPressed || props.PointerUpdateKind == PointerUpdateKind.RightButtonPressed
            || props.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
            return 1;
        if (props.IsMiddleButtonPressed || props.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed
            || props.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased)
            return 2;
        return -1;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && _chromeFullscreen)
        {
            SelectNextTab();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_chromeFullscreen)
            {
                SetChromeFullscreen(false);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_activePeer))
                RequestStopTab(_activePeer);
            return;
        }

        if (_session?.CanSendReverseInput != true) return;
        var vk = ToWindowsVk(e.Key);
        if (vk == 0) return;
        e.Handled = true;
        _ = _session.SendReverseKeyAsync(vk, down: true, extended: IsExtended(e.Key));
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) return;
        if (_session?.CanSendReverseInput != true) return;
        var vk = ToWindowsVk(e.Key);
        if (vk == 0) return;
        e.Handled = true;
        _ = _session.SendReverseKeyAsync(vk, down: false, extended: IsExtended(e.Key));
    }

    private static ushort ToWindowsVk(Key key) => key switch
    {
        Key.Back => 0x08,
        Key.Tab => 0x09,
        Key.Return => 0x0D,
        Key.Pause => 0x13,
        Key.CapsLock => 0x14,
        Key.Escape => 0x1B,
        Key.Space => 0x20,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.PrintScreen => 0x2C,
        Key.Insert => 0x2D,
        Key.Delete => 0x2E,
        Key.D0 => 0x30,
        Key.D1 => 0x31,
        Key.D2 => 0x32,
        Key.D3 => 0x33,
        Key.D4 => 0x34,
        Key.D5 => 0x35,
        Key.D6 => 0x36,
        Key.D7 => 0x37,
        Key.D8 => 0x38,
        Key.D9 => 0x39,
        Key.A => 0x41,
        Key.B => 0x42,
        Key.C => 0x43,
        Key.D => 0x44,
        Key.E => 0x45,
        Key.F => 0x46,
        Key.G => 0x47,
        Key.H => 0x48,
        Key.I => 0x49,
        Key.J => 0x4A,
        Key.K => 0x4B,
        Key.L => 0x4C,
        Key.M => 0x4D,
        Key.N => 0x4E,
        Key.O => 0x4F,
        Key.P => 0x50,
        Key.Q => 0x51,
        Key.R => 0x52,
        Key.S => 0x53,
        Key.T => 0x54,
        Key.U => 0x55,
        Key.V => 0x56,
        Key.W => 0x57,
        Key.X => 0x58,
        Key.Y => 0x59,
        Key.Z => 0x5A,
        Key.LWin => 0x5B,
        Key.RWin => 0x5C,
        Key.NumPad0 => 0x60,
        Key.NumPad1 => 0x61,
        Key.NumPad2 => 0x62,
        Key.NumPad3 => 0x63,
        Key.NumPad4 => 0x64,
        Key.NumPad5 => 0x65,
        Key.NumPad6 => 0x66,
        Key.NumPad7 => 0x67,
        Key.NumPad8 => 0x68,
        Key.NumPad9 => 0x69,
        Key.Multiply => 0x6A,
        Key.Add => 0x6B,
        Key.Subtract => 0x6D,
        Key.Decimal => 0x6E,
        Key.Divide => 0x6F,
        Key.F1 => 0x70,
        Key.F2 => 0x71,
        Key.F3 => 0x72,
        Key.F4 => 0x73,
        Key.F5 => 0x74,
        Key.F6 => 0x75,
        Key.F7 => 0x76,
        Key.F8 => 0x77,
        Key.F9 => 0x78,
        Key.F10 => 0x79,
        Key.F11 => 0x7A,
        Key.F12 => 0x7B,
        Key.NumLock => 0x90,
        Key.Scroll => 0x91,
        Key.LeftShift => 0xA0,
        Key.RightShift => 0xA1,
        Key.LeftCtrl => 0xA2,
        Key.RightCtrl => 0xA3,
        Key.LeftAlt => 0xA4,
        Key.RightAlt => 0xA5,
        Key.OemSemicolon => 0xBA,
        Key.OemPlus => 0xBB,
        Key.OemComma => 0xBC,
        Key.OemMinus => 0xBD,
        Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF,
        Key.OemTilde => 0xC0,
        Key.OemOpenBrackets => 0xDB,
        Key.OemPipe => 0xDC,
        Key.OemCloseBrackets => 0xDD,
        Key.OemQuotes => 0xDE,
        _ => 0
    };

    private static bool IsExtended(Key key)
        => key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Insert or Key.Delete or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.NumLock
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt;

    public void ShowFullscreen()
    {
        _chromeFullscreen = false;
        ApplyChromeVisibility();
        Topmost = true;
        _cursorIdle.EndSession();
        Cursor = new Cursor(StandardCursorType.Arrow);
        Show();
        WindowState = WindowState.Maximized;
        Activate();
        Focus();
        TryAttachCapture();
    }

    private void SetChromeFullscreen(bool on)
    {
        _chromeFullscreen = on;
        ApplyChromeVisibility();
        WindowState = on ? WindowState.FullScreen : WindowState.Maximized;
        Activate();
    }

    private void ApplyChromeVisibility()
    {
        TitleBar.IsVisible = !_chromeFullscreen;
        TitleBar.Height = _chromeFullscreen ? 0 : 36;
        FsToolbarLayer.IsVisible = _chromeFullscreen;
        if (_chromeFullscreen)
        {
            _fsToolbarExpanded = true;
            FsToolbarExtra.IsVisible = true;
            _cursorIdle.BeginSession();
            Cursor = new Cursor(StandardCursorType.None);
            ToolTip.SetTip(FullscreenBtn, "退出全屏");
            Dispatcher.UIThread.Post(ResetFsToolbarToTopCenter, DispatcherPriority.Loaded);
        }
        else
        {
            _fsToolbarDragging = false;
            _cursorIdle.EndSession();
            Cursor = new Cursor(StandardCursorType.Arrow);
            ToolTip.SetTip(FullscreenBtn, "全屏");
        }
    }

    private void ResetFsToolbarToTopCenter()
    {
        if (!_chromeFullscreen || FsToolbarLayer.Bounds.Width <= 0) return;
        FsToolbar.Measure(Size.Infinity);
        var tw = FsToolbar.DesiredSize.Width > 0 ? FsToolbar.DesiredSize.Width : 90;
        var x = Math.Max(8, (FsToolbarLayer.Bounds.Width - tw) / 2);
        Canvas.SetLeft(FsToolbar, x);
        Canvas.SetTop(FsToolbar, 10);
    }

    private void FsToolbar_PointerEntered(object? sender, PointerEventArgs e)
    {
        _cursorIdle.NotifyLocalMouseMoved();
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    private void FsToolbar_PointerExited(object? sender, PointerEventArgs e)
    {
        if (_chromeFullscreen && !_fsToolbarDragging)
            Cursor = new Cursor(StandardCursorType.None);
    }

    private void FsGrip_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FsToolbarLayer).Properties.IsLeftButtonPressed) return;
        _fsToolbarDragging = true;
        _fsToolbarMoved = false;
        _fsDragStart = e.GetPosition(FsToolbarLayer);
        _fsToolbarOrigin = new Point(
            double.IsNaN(Canvas.GetLeft(FsToolbar)) ? 0 : Canvas.GetLeft(FsToolbar),
            double.IsNaN(Canvas.GetTop(FsToolbar)) ? 0 : Canvas.GetTop(FsToolbar));
        e.Pointer.Capture(FsGripBtn);
        e.Handled = true;
    }

    private void FsGrip_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_fsToolbarDragging) return;
        var p = e.GetPosition(FsToolbarLayer);
        var dx = p.X - _fsDragStart.X;
        var dy = p.Y - _fsDragStart.Y;
        if (!_fsToolbarMoved && dx * dx + dy * dy < 16) return;
        _fsToolbarMoved = true;
        PlaceFsToolbar(_fsToolbarOrigin.X + dx, _fsToolbarOrigin.Y + dy);
        _cursorIdle.NotifyLocalMouseMoved();
        e.Handled = true;
    }

    private void FsGrip_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_fsToolbarDragging) return;
        e.Pointer.Capture(null);
        var wasDrag = _fsToolbarMoved;
        _fsToolbarDragging = false;
        _fsToolbarMoved = false;
        if (!wasDrag)
            ToggleFsToolbarExpanded();
        e.Handled = true;
    }

    private void FsToolbarLayer_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_fsToolbarDragging)
            FsGrip_PointerMoved(FsGripBtn, e);
    }

    private void FsToolbarLayer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_fsToolbarDragging)
            FsGrip_PointerReleased(FsGripBtn, e);
    }

    private void ToggleFsToolbarExpanded()
    {
        _fsToolbarExpanded = !_fsToolbarExpanded;
        FsToolbarExtra.IsVisible = _fsToolbarExpanded;
        PlaceFsToolbar(Canvas.GetLeft(FsToolbar), Canvas.GetTop(FsToolbar));
    }

    private void PlaceFsToolbar(double x, double y)
    {
        FsToolbar.Measure(Size.Infinity);
        var tw = Math.Max(28, FsToolbar.Bounds.Width > 0 ? FsToolbar.Bounds.Width : FsToolbar.DesiredSize.Width);
        var th = Math.Max(28, FsToolbar.Bounds.Height > 0 ? FsToolbar.Bounds.Height : FsToolbar.DesiredSize.Height);
        var maxX = Math.Max(0, FsToolbarLayer.Bounds.Width - tw);
        var maxY = Math.Max(0, FsToolbarLayer.Bounds.Height - th);
        Canvas.SetLeft(FsToolbar, Math.Clamp(x, 0, maxX));
        Canvas.SetTop(FsToolbar, Math.Clamp(y, 0, maxY));
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled) return;
        // Tab / 按钮上不拖窗
        if (e.Source is Visual v)
        {
            for (Visual? cur = v; cur != null; cur = cur.GetVisualParent())
            {
                if (cur is Border { Tag: string }) return;
                if (cur is Button) return;
            }
        }

        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            try { BeginMoveDrag(e); } catch { /* ignore */ }
        }
    }

    private void ContentHost_PointerMoved(object? sender, PointerEventArgs e)
        => _cursorIdle.NotifyLocalMouseMoved();

    private void Fullscreen_Click(object? sender, RoutedEventArgs e)
        => SetChromeFullscreen(!_chromeFullscreen);

    private void Minimize_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e)
    {
        if (_chromeFullscreen)
        {
            _chromeFullscreen = false;
            ApplyChromeVisibility();
            WindowState = WindowState.Maximized;
            MaxBtn.Content = "❐";
            ToolTip.SetTip(MaxBtn, "还原");
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaxBtn.Content = "□";
            ToolTip.SetTip(MaxBtn, "最大化");
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaxBtn.Content = "❐";
            ToolTip.SetTip(MaxBtn, "还原");
        }
    }

    public static Bitmap? DecodeJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            return new Bitmap(ms);
        }
        catch
        {
            return null;
        }
    }

    public void Present(string peer, Bitmap bmp, long capturedUnixMs = 0)
    {
        peer = peer.Trim();
        EnsureTab(peer);
        var tab = FindTab(peer);
        if (tab == null)
        {
            bmp.Dispose();
            return;
        }

        tab.ContentW = bmp.PixelSize.Width;
        tab.ContentH = bmp.PixelSize.Height;
        var old = tab.FrameBitmap;
        tab.FrameBitmap = bmp;
        try { old?.Dispose(); } catch { /* ignore */ }
        UpdateLatencyForTab(tab, capturedUnixMs);

        if (!string.Equals(_activePeer, peer, StringComparison.OrdinalIgnoreCase))
            return;

        FrameImage.Source = tab.FrameBitmap;
        if (WaitingText.IsVisible)
            WaitingText.IsVisible = false;
    }

    public void Present(Bitmap bmp, long capturedUnixMs = 0)
    {
        var peer = _activePeer ?? _session?.Peer?.Trim();
        if (string.IsNullOrWhiteSpace(peer))
        {
            bmp.Dispose();
            return;
        }

        Present(peer, bmp, capturedUnixMs);
    }

    private void UpdateLatencyForTab(SinkTabState tab, long capturedUnixMs)
    {
        var sample = ProjectionLatency.ComputeMs(capturedUnixMs, ref tab.LatencySkewBaseline);
        if (sample == null)
        {
            if (tab.LatencyEma <= 0)
            {
                ApplyLatencyTipForTab(tab, Loc.T("Proj.LatencyTipWait"));
                DrawSignalOn(tab.SignalCanvas, ref tab.LastSignal, new ProjectionLatency.Signal(0, 0));
            }
            return;
        }

        var ms = ProjectionLatency.Smooth(sample, ref tab.LatencyEma);
        ApplyLatencyTipForTab(tab, string.Format(Loc.T("Proj.LatencyTip"), ms));
        DrawSignalOn(tab.SignalCanvas, ref tab.LastSignal, ProjectionLatency.ToSignal(ms));
    }

    private void ApplyLatencyTipForTab(SinkTabState tab, string tip)
    {
        tab.LatencyTip = tip;
        ToolTip.SetTip(tab.TabBorder, tip);
        if (string.Equals(tab.Peer, _activePeer, StringComparison.OrdinalIgnoreCase))
            ApplyActiveLatencyTip();
    }

    private void ApplyActiveLatencyTip()
    {
        var tab = FindTab(_activePeer);
        var tip = tab?.LatencyTip ?? Loc.T("Proj.LatencyTipWait");
        if (FsGripBtn != null)
            ToolTip.SetTip(FsGripBtn, tip);
    }

    private static void DrawSignalOn(Canvas canvas, ref ProjectionLatency.Signal last, ProjectionLatency.Signal signal)
    {
        if (last.Equals(signal) && canvas.Children.Count > 0)
            return;
        last = signal;
        canvas.Children.Clear();
        var brush = signal.Tier switch
        {
            1 => SignalGreen,
            2 => SignalYellow,
            3 => SignalRed,
            _ => SignalMuted
        };
        var active = Math.Clamp(signal.Bars, 0, 4);
        double[] heights = [3, 5.5, 8, 11];
        for (var i = 0; i < 4; i++)
        {
            var on = i < active;
            var bar = new Rectangle
            {
                Width = 2.2,
                Height = heights[i],
                Fill = on ? brush : SignalMuted,
                RadiusX = 0.6,
                RadiusY = 0.6,
                Opacity = on ? 1 : 0.35
            };
            Canvas.SetLeft(bar, i * 3.5);
            Canvas.SetBottom(bar, 0);
            canvas.Children.Add(bar);
        }
    }

    public void CloseFromSession()
    {
        _closingFromSession = true;
        DetachCapture();
        try
        {
            Topmost = false;
            WindowState = WindowState.Normal;
            Hide();
            Close();
        }
        catch { /* ignore */ }
    }

    private void DisposeAllTabBitmaps()
    {
        try
        {
            FrameImage.Source = null;
            foreach (var t in _tabs)
            {
                try { t.FrameBitmap?.Dispose(); } catch { /* ignore */ }
                t.FrameBitmap = null;
            }
        }
        catch { /* ignore */ }
    }
}
