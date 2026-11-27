using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Native;

namespace FlyScreen.Services.ScreenExtend;

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
    private ScreenExtendService? _service;
    private bool _closingFromSession;
    private bool _userCloseRaised;
    private bool _captureBound;
    private bool _chromeFullscreen;
    /// <summary>工作区填充（等价最大化，不盖任务栏）；与 WindowState.Maximized 解耦。</summary>
    private bool _workAreaFilled;
    private bool _suppressBoundsSync;
    private Rect _normalBoundsDip = new(120, 80, 1100, 720);
    private bool _preFsWorkAreaFilled;
    private Rect _preFsBoundsDip = new(120, 80, 1100, 720);
    private bool _fsToolbarExpanded = true;
    private bool _fsToolbarDragging;
    private bool _fsToolbarMoved;
    private Point _fsDragStart;
    private Point _fsToolbarOrigin;

    private static readonly SolidColorBrush TabActiveBg = Brush("#2C2C30");
    private static readonly SolidColorBrush TabInactiveBg = Brush("#18181B");
    private static readonly SolidColorBrush SignalGreen = Brush("#22C55E");
    private static readonly SolidColorBrush SignalYellow = Brush("#EAB308");
    private static readonly SolidColorBrush SignalRed = Brush("#EF4444");
    private static readonly SolidColorBrush SignalMuted = Brush("#6B7280");

    private sealed class SinkTabState
    {
        public required string Peer { get; init; }
        public required Border TabBorder { get; init; }
        public required Canvas SignalCanvas { get; init; }
        public required TextBlock NameText { get; init; }
        public WriteableBitmap? FrameBitmap;
        public int ContentW;
        public int ContentH;
        public double LatencyEma;
        public long LatencySkewBaseline = long.MinValue;
        public ProjectionLatency.Signal LastSignal;
        public string LatencyTip = "";
    }

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
        Closing += (_, _) => NotifyWindowClose();
        Closed += (_, _) =>
        {
            DetachCapture();
            _cursorIdle.EndSession();
            _cursorIdle.Dispose();
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        };
        Activated += (_, _) =>
        {
            Focus();
            TryAttachCapture();
        };
        Deactivated += (_, _) => DetachCapture();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized && !_chromeFullscreen)
            {
                // Topmost+Maximized 易盖任务栏：改走工作区填充
                Dispatcher.BeginInvoke(() =>
                {
                    if (_chromeFullscreen) return;
                    if(WindowState == WindowState.Maximized) {
                        FillWorkArea(saveNormalBounds: !_workAreaFilled);
                        SyncResizeBorder();
                    }
                }, DispatcherPriority.Loaded);
                return;
            }

            if (WindowState == WindowState.Normal && !_chromeFullscreen && !_workAreaFilled)
                RememberNormalBounds();
            SyncResizeBorder();
            RefreshMaxIcon();
        };
        LocationChanged += (_, _) =>
        {
            if (_chromeFullscreen || _suppressBoundsSync) return;
            if (_workAreaFilled)
            {
                // 标题栏拖出工作区填充：缩回进入前的普通尺寸
                try
                {
                    var wa = GetNearestWorkAreaDip();
                    if (Math.Abs(Left - wa.X) <= 4 && Math.Abs(Top - wa.Y) <= 4)
                        return;
                    _workAreaFilled = false;
                    _suppressBoundsSync = true;
                    var screen = NativeMethods.GetCursorPos(out var pt)
                        ? new Point(pt.X, pt.Y)
                        : PointToScreen(new Point(ActualWidth / 2, 18));
                    if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: { } ct })
                        screen = ct.TransformFromDevice.Transform(screen);
                    var w = _normalBoundsDip.Width > 80 ? _normalBoundsDip.Width : Math.Max(640, ActualWidth * 0.7);
                    var h = _normalBoundsDip.Height > 80 ? _normalBoundsDip.Height : Math.Max(400, ActualHeight * 0.7);
                    Width = w;
                    Height = h;
                    Left = Math.Max(0, screen.X - w / 2);
                    Top = Math.Max(0, screen.Y - 18);
                    RefreshMaxIcon();
                }
                catch { /* ignore */ }
                finally { 
                    _suppressBoundsSync = false;
                    SyncResizeBorder();
                }
                return;
            }

            if (WindowState == WindowState.Normal)
                RememberNormalBounds();
        };
        SizeChanged += (_, _) =>
        {
            // 贴工作区过程中会短暂触发 SizeChanged，绝不能把还原尺寸写成工作区全高
            if (_suppressBoundsSync || _chromeFullscreen || _workAreaFilled) return;
            if (WindowState == WindowState.Normal)
                RememberNormalBounds();
        };
        _cursorIdle.ShowLocalCursorChanged += show =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_chromeFullscreen) return;
                Cursor = show
                    ? System.Windows.Input.Cursors.Arrow
                    : System.Windows.Input.Cursors.None;
                if (_fsToolbarDragging) return;
                FsToolbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            });
        };
    }

    public bool HasTabs => _tabs.Count > 0;

    public void BindSession(ScreenExtendService service)
    {
        _service = service;
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
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF4, 0xF6)),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var closeBtn = new System.Windows.Controls.Button
        {
            Width = 18,
            MinWidth = 0, // 勿吃全局 Fly.Button MinWidth=88
            Height = 18,
            Padding = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "结束投影",
            VerticalAlignment = VerticalAlignment.Center,
            Tag = peer
        };
        closeBtn.Click += TabClose_Click;
        closeBtn.Template = CreateTabCloseTemplate();

        var tabBody = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
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
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = tabBody,
            Tag = peer,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        // Preview：先于标题栏 DragMove；Chrome 命中可见，避免无边框窗吞掉点击
        WindowChrome.SetIsHitTestVisibleInChrome(tabBorder, true);
        WindowChrome.SetIsHitTestVisibleInChrome(closeBtn, true);
        tabBorder.PreviewMouseLeftButtonDown += TabBorder_PreviewMouseLeftButtonDown;

        var state = new SinkTabState
        {
            Peer = peer,
            TabBorder = tabBorder,
            SignalCanvas = signalCanvas,
            NameText = nameText,
            LatencyTip = Loc.T("Proj.LatencyTipWait")
        };
        tabBorder.ToolTip = state.LatencyTip;
        DrawSignalOn(signalCanvas, ref state.LastSignal, new ProjectionLatency.Signal(0, 0));
        return state;
    }

    /// <summary>确保 Tab 存在；已存在不改激活（供收帧路径）。</summary>
    public void EnsureTab(string peer, ScreenExtendService? service = null)
    {
        if (service != null)
            _service = service;
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

    /// <summary>新建或切到该 Tab（仅用户操作 / 新会话就绪时调用）。</summary>
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
        _tabs.RemoveAt(idx);

        if (_tabs.Count == 0)
        {
            _activePeer = null;
            FrameImage.Source = null;
            WaitingText.Visibility = Visibility.Visible;
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
        _service?.SetActiveSinkPeer(peer);
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
            WaitingText.Visibility = Visibility.Collapsed;
        }
        else
        {
            FrameImage.Source = null;
            WaitingText.Visibility = Visibility.Visible;
        }
    }

    private SinkTabState? FindTab(string? peer)
    {
        if (string.IsNullOrWhiteSpace(peer)) return null;
        return _tabs.Find(t => string.Equals(t.Peer, peer.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private bool HasTab(string peer)
        => FindTab(peer) != null;

    private int ActiveContentW => FindTab(_activePeer)?.ContentW ?? 0;
    private int ActiveContentH => FindTab(_activePeer)?.ContentH ?? 0;

    private void TabBorder_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 点在关闭钮上留给 Click，不切换
        if (e.OriginalSource is DependencyObject d
            && FindAncestor<System.Windows.Controls.Button>(d) != null)
            return;

        var peer = (sender as FrameworkElement)?.Tag as string
                   ?? FindAncestor<Border>(e.OriginalSource as DependencyObject)?.Tag as string;
        if (string.IsNullOrWhiteSpace(peer)) return;
        SetActiveTab(peer);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var d = start; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is T match) return match;
        }

        return null;
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        var peer = (sender as FrameworkElement)?.Tag as string ?? _activePeer;
        if (!string.IsNullOrWhiteSpace(peer))
            RequestStopTab(peer);
        e.Handled = true;
    }

    private void CloseCurrentTab_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_activePeer))
            RequestStopTab(_activePeer);
        e.Handled = true;
    }

    private void NextTab_Click(object sender, RoutedEventArgs e) => SelectNextTab();

    private void RequestStopTab(string peer)
    {
        try { _onStopTab(peer.Trim()); } catch { /* ignore */ }
    }

    private void WindowClose_Click(object sender, RoutedEventArgs e)
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

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> items;
        try { items = _listAddablePeers(); }
        catch { items = []; }

        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1F, 0x1F, 0x23)),
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46))
        };
        if (items.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem
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
                var mi = new System.Windows.Controls.MenuItem
                {
                    Header = name,
                    Tag = name,
                    FontSize = 12,
                    Padding = new Thickness(4, 2, 10, 2),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF4, 0xF6)),
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

        menu.PlacementTarget = AddTabBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>菜单设备图标固定 14px，避免系统默认 Icon 槽位被撑得过大。</summary>
    private static System.Windows.Controls.Viewbox CreateAddMenuDeviceIcon(bool muted)
    {
        var stroke = muted
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x72, 0x80))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD1, 0xD5, 0xDB));
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(
                "M 1.5,2.5 H 12.5 V 9.5 H 1.5 Z M 5,11.5 H 9 M 7,9.5 V 11.5"),
            Stroke = stroke,
            StrokeThickness = 1.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.Uniform,
            Width = 14,
            Height = 12
        };
        return new System.Windows.Controls.Viewbox
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            Child = path,
            Margin = new Thickness(0, 0, 2, 0)
        };
    }

    private static ControlTemplate CreateTabCloseTemplate()
    {
        var template = new ControlTemplate(typeof(System.Windows.Controls.Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.Name = "Bd";
        bd.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var tb = new FrameworkElementFactory(typeof(TextBlock));
        tb.SetValue(TextBlock.TextProperty, "×");
        tb.SetValue(TextBlock.FontSizeProperty, 13.0);
        tb.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0xA3, 0xAF)));
        tb.SetValue(TextBlock.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        tb.SetValue(TextBlock.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        bd.AppendChild(tb);
        template.VisualTree = bd;
        var trigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3F, 0x3F, 0x46)), "Bd"));
        template.Triggers.Add(trigger);
        return template;
    }

    private void TryAttachCapture()
    {
        if (_captureBound) return;
        if (_service?.CanSendReverseInput != true) return;
        _captureBound = true;
        _reverseGate.OnCaptureAttached();
        ContentHost.PreviewMouseMove += OnPreviewMouseMove;
        ContentHost.PreviewMouseDown += OnPreviewMouseDown;
        ContentHost.PreviewMouseUp += OnPreviewMouseUp;
        ContentHost.PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
    }

    private void DetachCapture()
    {
        if (!_captureBound) return;
        _captureBound = false;
        _reverseGate.OnCaptureDetached();
        ContentHost.PreviewMouseMove -= OnPreviewMouseMove;
        ContentHost.PreviewMouseDown -= OnPreviewMouseDown;
        ContentHost.PreviewMouseUp -= OnPreviewMouseUp;
        ContentHost.PreviewMouseWheel -= OnPreviewMouseWheel;
        PreviewKeyDown -= OnPreviewKeyDown;
        PreviewKeyUp -= OnPreviewKeyUp;
    }

    private bool TryMap(MouseEventArgs e, out double nx, out double ny)
    {
        var p = e.GetPosition(ContentHost);
        return ScreenExtendReverseInput.TryMapUniformContent(
            ContentHost.ActualWidth, ContentHost.ActualHeight, ActiveContentW, ActiveContentH, p.X, p.Y, out nx, out ny);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        _cursorIdle.NotifyLocalMouseMoved();
        if (_service == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendMove(nx, ny)) return;
        _ = _service.SendReverseMoveAsync(nx, ny);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_service == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        var button = MapButton(e.ChangedButton);
        if (button < 0) return;
        e.Handled = true;
        _ = _service.SendReverseButtonAsync(nx, ny, button, down: true);
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_service == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        var button = MapButton(e.ChangedButton);
        if (button < 0) return;
        e.Handled = true;
        _ = _service.SendReverseButtonAsync(nx, ny, button, down: false);
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_service == null || !TryMap(e, out var nx, out var ny)) return;
        if (!_reverseGate.ShouldSendPointer(nx, ny)) return;
        e.Handled = true;
        _ = _service.SendReverseWheelAsync(nx, ny, e.Delta, horizontal: false);
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab && _chromeFullscreen)
        {
            SelectNextTab();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape) return;
        if (_service?.CanSendReverseInput != true) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        e.Handled = true;
        _ = _service.SendReverseKeyAsync(vk, down: true, extended: IsExtended(key));
    }

    private void OnPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) return;
        if (_service?.CanSendReverseInput != true) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var vk = (ushort)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;
        e.Handled = true;
        _ = _service.SendReverseKeyAsync(vk, down: false, extended: IsExtended(key));
    }

    private static int MapButton(MouseButton button) => button switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        _ => -1
    };

    private static bool IsExtended(Key key)
        => key is Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Insert or Key.Delete or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.NumLock
            or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt;

    /// <summary>打开播放器：最大化（贴工作区，不盖任务栏）并显示标题栏。</summary>
    public void ShowFullscreen()
    {
        _chromeFullscreen = false;
        ApplyChromeVisibility();
        Topmost = true;
        _cursorIdle.EndSession();
        Cursor = System.Windows.Input.Cursors.Arrow;
        if (!IsVisible)
            Show();
        FillWorkArea(saveNormalBounds: true);
        TryAttachCapture();
    }

    private void ApplyChromeVisibility()
    {
        if (_chromeFullscreen)
        {
            TitleRow.Height = new GridLength(0);
            TitleBar.Visibility = Visibility.Collapsed;
            // 全屏必须卸掉 WindowChrome，否则 CaptionHeight 仍会在顶部形成隐形拖拽带
            DetachWindowChromeForFullscreen();
            FsToolbarLayer.Visibility = Visibility.Visible;
            FsToolbar.Visibility = Visibility.Visible;
            _fsToolbarExpanded = true;
            FsToolbarExtra.Visibility = Visibility.Visible;
            _cursorIdle.BeginSession();
            Cursor = System.Windows.Input.Cursors.None;
            FullscreenBtn.ToolTip = "退出全屏";
            Dispatcher.BeginInvoke(ResetFsToolbarToTopCenter, DispatcherPriority.Loaded);
        }
        else
        {
            TitleRow.Height = new GridLength(36);
            TitleBar.Visibility = Visibility.Visible;
            AttachWindowChromeForWindowed();
            FsToolbarLayer.Visibility = Visibility.Collapsed;
            FsToolbar.Visibility = Visibility.Visible;
            _fsToolbarDragging = false;
            _cursorIdle.EndSession();
            Cursor = System.Windows.Input.Cursors.Arrow;
            FullscreenBtn.ToolTip = "全屏";
        }
    }

    private void DetachWindowChromeForFullscreen()
    {
        if (WinChrome != null)
        {
            WinChrome.CaptionHeight = 0;
            WinChrome.ResizeBorderThickness = new Thickness(0);
        }

        WindowChrome.SetWindowChrome(this, null);
    }

    private void AttachWindowChromeForWindowed()
    {
        if (WinChrome == null) return;
        WinChrome.CaptionHeight = 36;
        WinChrome.ResizeBorderThickness = _workAreaFilled ? new Thickness(0) : new Thickness(4);
        WindowChrome.SetWindowChrome(this, WinChrome);
    }

    private void ResetFsToolbarToTopCenter()
    {
        if (!_chromeFullscreen || FsToolbarLayer.ActualWidth <= 0) return;
        FsToolbar.UpdateLayout();
        var tw = FsToolbar.ActualWidth > 0 ? FsToolbar.ActualWidth : 90;
        var x = Math.Max(8, (FsToolbarLayer.ActualWidth - tw) / 2);
        Canvas.SetLeft(FsToolbar, x);
        Canvas.SetTop(FsToolbar, 10);
        FsToolbar.Visibility = Visibility.Visible;
    }

    private void FsToolbar_MouseEnter(object sender, MouseEventArgs e)
    {
        _cursorIdle.NotifyLocalMouseMoved();
        Cursor = System.Windows.Input.Cursors.Arrow;
        if (_chromeFullscreen)
            FsToolbar.Visibility = Visibility.Visible;
    }

    private void FsToolbar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_chromeFullscreen && !_fsToolbarDragging && !_cursorIdle.ShowLocalCursor)
            Cursor = System.Windows.Input.Cursors.None;
    }

    private void FsToolbar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 工具按钮自身点击，不进入拖动
        if (e.OriginalSource is DependencyObject d
            && FindAncestor<System.Windows.Controls.Button>(d) is { } btn
            && !ReferenceEquals(btn, FsGripBtn))
            return;

        _fsToolbarDragging = true;
        _fsToolbarMoved = false;
        _fsDragStart = e.GetPosition(FsToolbarLayer);
        _fsToolbarOrigin = new Point(Canvas.GetLeft(FsToolbar), Canvas.GetTop(FsToolbar));
        if (double.IsNaN(_fsToolbarOrigin.X)) _fsToolbarOrigin.X = 0;
        if (double.IsNaN(_fsToolbarOrigin.Y)) _fsToolbarOrigin.Y = 0;
        FsToolbar.CaptureMouse();
        e.Handled = true;
    }

    private void FsToolbar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fsToolbarDragging || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(FsToolbarLayer);
        var dx = p.X - _fsDragStart.X;
        var dy = p.Y - _fsDragStart.Y;
        if (!_fsToolbarMoved && dx * dx + dy * dy < 16) return;
        _fsToolbarMoved = true;
        PlaceFsToolbar(_fsToolbarOrigin.X + dx, _fsToolbarOrigin.Y + dy);
        _cursorIdle.NotifyLocalMouseMoved();
        e.Handled = true;
    }

    private void FsToolbar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_fsToolbarDragging) return;
        if (FsToolbar.IsMouseCaptured)
            FsToolbar.ReleaseMouseCapture();
        var wasDrag = _fsToolbarMoved;
        _fsToolbarDragging = false;
        _fsToolbarMoved = false;
        if (!wasDrag)
            ToggleFsToolbarExpanded();
        e.Handled = true;
    }

    private void FsToolbarLayer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_fsToolbarDragging && e.LeftButton == MouseButtonState.Pressed)
            FsToolbar_PreviewMouseMove(FsToolbar, e);
    }

    private void FsToolbarLayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_fsToolbarDragging)
            FsToolbar_PreviewMouseLeftButtonUp(FsToolbar, e);
    }

    private void FsToolbarLayer_MouseLeave(object sender, MouseEventArgs e) { }

    private void ToggleFsToolbarExpanded()
    {
        _fsToolbarExpanded = !_fsToolbarExpanded;
        FsToolbarExtra.Visibility = _fsToolbarExpanded ? Visibility.Visible : Visibility.Collapsed;
        PlaceFsToolbar(Canvas.GetLeft(FsToolbar), Canvas.GetTop(FsToolbar));
    }

    private void PlaceFsToolbar(double x, double y)
    {
        FsToolbar.UpdateLayout();
        var tw = Math.Max(28, FsToolbar.ActualWidth);
        var th = Math.Max(28, FsToolbar.ActualHeight);
        var maxX = Math.Max(0, FsToolbarLayer.ActualWidth - tw);
        var maxY = Math.Max(0, FsToolbarLayer.ActualHeight - th);
        Canvas.SetLeft(FsToolbar, Math.Clamp(x, 0, maxX));
        Canvas.SetTop(FsToolbar, Math.Clamp(y, 0, maxY));
    }

    private bool IsTitleBarChromeHit(DependencyObject? src)
        => src != null
           && (FindAncestor<Border>(src) is { Tag: string }
               || FindAncestor<System.Windows.Controls.Button>(src) != null);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        if (_chromeFullscreen) return;
        // Tab / + / 窗控：交给按钮；空白区由 WindowChrome CaptionHeight 原生拖窗
        if (e.OriginalSource is DependencyObject src && IsTitleBarChromeHit(src))
            return;

        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            e.Handled = true;
        }
    }

    private void ContentHost_MouseMove(object sender, MouseEventArgs e)
        => _cursorIdle.NotifyLocalMouseMoved();

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_chromeFullscreen)
            ExitTrueFullscreen();
        else
            EnterTrueFullscreen();
    }

    private void EnterTrueFullscreen()
    {
        _preFsWorkAreaFilled = _workAreaFilled;
        if (!_workAreaFilled)
            RememberNormalBounds(force: true);
        _preFsBoundsDip = _normalBoundsDip;
        _chromeFullscreen = true;
        _workAreaFilled = false;
        ApplyChromeVisibility();
        try
        {
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.NoResize;
            DetachWindowChromeForFullscreen();
            ApplyMonitorCover();
            Dispatcher.BeginInvoke(() =>
            {
                if (!_chromeFullscreen) return;
                DetachWindowChromeForFullscreen();
                ApplyMonitorCover();
            }, DispatcherPriority.Loaded);
            RefreshMaxIcon();
            Activate();
            Focus();
        }
        catch { /* ignore */ }
    }

    private void ExitTrueFullscreen()
    {
        _chromeFullscreen = false;
        ApplyChromeVisibility();
        ResizeMode = ResizeMode.CanResize;
        // WindowChrome 重挂后尺寸会抖，延后贴齐工作区/还原
        var restoreWork = _preFsWorkAreaFilled;
        var restoreBounds = _preFsBoundsDip;
        Dispatcher.BeginInvoke(() =>
        {
            if (_chromeFullscreen) return;
            AttachWindowChromeForWindowed();
            if (restoreWork)
                FillWorkArea(saveNormalBounds: false);
            else
                RestoreNormalBounds(restoreBounds);
        }, DispatcherPriority.ApplicationIdle);
    }

    private void RememberNormalBounds(bool force = false)
    {
        if (_chromeFullscreen || _workAreaFilled) return;
        if (!force && WindowState != WindowState.Normal) return;

        // 系统 Maximized 时用 RestoreBounds，避免把全屏尺寸当成「还原目标」
        if (WindowState == WindowState.Maximized)
        {
            var rb = RestoreBounds;
            if (rb.Width > 80 && rb.Height > 80)
                _normalBoundsDip = rb;
            return;
        }

        var w = ActualWidth > 80 ? ActualWidth : Width;
        var h = ActualHeight > 80 ? ActualHeight : Height;
        if (w < 80 || h < 80) return;
        _normalBoundsDip = new Rect(Left, Top, w, h);
    }

    private bool TryGetNearestMonitorRects(out NativeMethods.RECT work, out NativeMethods.RECT monitor)
    {
        work = default;
        monitor = default;
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (hMon == IntPtr.Zero) return false;
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(hMon, ref info)) return false;
        work = info.rcWork;
        monitor = info.rcMonitor;

        // 极端情况 rcWork==rcMonitor（任务栏自动隐藏等）：主屏回退 SystemParameters.WorkArea
        if (work.Left == monitor.Left && work.Top == monitor.Top
            && work.Right == monitor.Right && work.Bottom == monitor.Bottom
            && (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0)
        {
            var wa = SystemParameters.WorkArea;
            if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: { } ct })
            {
                var toDev = ct.TransformToDevice;
                var tl = toDev.Transform(new Point(wa.Left, wa.Top));
                var br = toDev.Transform(new Point(wa.Right, wa.Bottom));
                work = new NativeMethods.RECT
                {
                    Left = (int)Math.Round(tl.X),
                    Top = (int)Math.Round(tl.Y),
                    Right = (int)Math.Round(br.X),
                    Bottom = (int)Math.Round(br.Y)
                };
            }
            else
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                work = new NativeMethods.RECT
                {
                    Left = (int)Math.Round(wa.Left * dpi.DpiScaleX),
                    Top = (int)Math.Round(wa.Top * dpi.DpiScaleY),
                    Right = (int)Math.Round(wa.Right * dpi.DpiScaleX),
                    Bottom = (int)Math.Round(wa.Bottom * dpi.DpiScaleY)
                };
            }
        }

        return true;
    }

    private Rect PhysicalToDip(NativeMethods.RECT rc)
    {
        if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: { } ct })
        {
            var m = ct.TransformFromDevice;
            var tl = m.Transform(new Point(rc.Left, rc.Top));
            var br = m.Transform(new Point(rc.Right, rc.Bottom));
            return new Rect(tl.X, tl.Y, Math.Max(MinWidth, br.X - tl.X), Math.Max(MinHeight, br.Y - tl.Y));
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            rc.Left / dpi.DpiScaleX,
            rc.Top / dpi.DpiScaleY,
            Math.Max(MinWidth, (rc.Right - rc.Left) / dpi.DpiScaleX),
            Math.Max(MinHeight, (rc.Bottom - rc.Top) / dpi.DpiScaleY));
    }

    private Rect GetNearestWorkAreaDip()
    {
        if (TryGetNearestMonitorRects(out var work, out _))
            return PhysicalToDip(work);
        var wa = SystemParameters.WorkArea;
        return new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
    }

    /// <summary>物理像素贴齐矩形；调用方负责 _suppressBoundsSync，避免嵌套清标志。</summary>
    private void PlaceWindowPhysical(NativeMethods.RECT rc, bool keepTopmost)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var w = Math.Max(1, rc.Right - rc.Left);
        var h = Math.Max(1, rc.Bottom - rc.Top);
        WindowState = WindowState.Normal;
        var dip = PhysicalToDip(rc);
        Left = dip.X;
        Top = dip.Y;
        Width = dip.Width;
        Height = dip.Height;

        var z = keepTopmost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP;
        _ = NativeMethods.SetWindowPos(
            hwnd, z, rc.Left, rc.Top, w, h,
            NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
        Topmost = keepTopmost;

        // 若 WPF/Chrome 随后又撑大，再强制一次
        if (NativeMethods.GetWindowRect(hwnd, out var got)
            && (Math.Abs(got.Left - rc.Left) > 2
                || Math.Abs(got.Top - rc.Top) > 2
                || Math.Abs((got.Right - got.Left) - w) > 2
                || Math.Abs((got.Bottom - got.Top) - h) > 2))
        {
            _ = NativeMethods.SetWindowPos(
                hwnd, z, rc.Left, rc.Top, w, h,
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_FRAMECHANGED);
        }
    }

    private void FillWorkArea(bool saveNormalBounds)
    {
        try
        {
            // 先记下还原尺寸，再标 _workAreaFilled，避免 Place 触发的 SizeChanged 覆盖
            if (saveNormalBounds && !_workAreaFilled)
                RememberNormalBounds(force: true);

            var saved = _normalBoundsDip;
            _chromeFullscreen = false;
            FsToolbarLayer.Visibility = Visibility.Collapsed;
            if (!TryGetNearestMonitorRects(out var work, out _))
                return;

            _suppressBoundsSync = true;
            _workAreaFilled = true;
            try
            {
                PlaceWindowPhysical(work, keepTopmost: true);
            }
            finally
            {
                _normalBoundsDip = saved.Width > 80 && saved.Height > 80
                    ? saved
                    : _normalBoundsDip;
                _suppressBoundsSync = false;
            }

            SyncResizeBorder();
            RefreshMaxIcon();
            Activate();

            // 布局/Chrome 稳定后再贴一次，杜绝退出全屏后高度漂回显示器全高
            var workCopy = work;
            var savedCopy = _normalBoundsDip;
            Dispatcher.BeginInvoke(() =>
            {
                if (_chromeFullscreen || !_workAreaFilled) return;
                _suppressBoundsSync = true;
                try
                {
                    PlaceWindowPhysical(workCopy, keepTopmost: true);
                    _normalBoundsDip = savedCopy;
                    SyncResizeBorder();
                }
                finally { _suppressBoundsSync = false; }
            }, DispatcherPriority.ApplicationIdle);
        }
        catch { /* ignore */ }
    }

    private void RestoreNormalBounds(Rect bounds)
    {
        try
        {
            _suppressBoundsSync = true;
            _workAreaFilled = false;
            FsToolbarLayer.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Normal;
            var w = bounds.Width > 80 ? bounds.Width : 1100;
            var h = bounds.Height > 80 ? bounds.Height : 720;
            // 工作区填充后 Left/Top 可能是屏幕原点；还原必须用进入最大化前的坐标
            Left = bounds.X;
            Top = bounds.Y;
            Width = Math.Max(MinWidth, w);
            Height = Math.Max(MinHeight, h);
            _normalBoundsDip = new Rect(Left, Top, Width, Height);

            // 物理像素再盖一次，避免 DPI/Chrome 把尺寸漂回去
            if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: { } ct })
            {
                var toDev = ct.TransformToDevice;
                var tl = toDev.Transform(new Point(Left, Top));
                var br = toDev.Transform(new Point(Left + Width, Top + Height));
                PlaceWindowPhysical(new NativeMethods.RECT
                {
                    Left = (int)Math.Round(tl.X),
                    Top = (int)Math.Round(tl.Y),
                    Right = (int)Math.Round(br.X),
                    Bottom = (int)Math.Round(br.Y)
                }, keepTopmost: true);
            }

            SyncResizeBorder();
            RefreshMaxIcon();
            Activate();
        }
        catch { /* ignore */ }
        finally { _suppressBoundsSync = false; }
    }

    private void ApplyMonitorCover()
    {
        if (!_chromeFullscreen) return;
        if (!TryGetNearestMonitorRects(out _, out var monitor)) return;
        _suppressBoundsSync = true;
        try { PlaceWindowPhysical(monitor, keepTopmost: true); }
        finally { _suppressBoundsSync = false; }
    }

    private void SyncResizeBorder()
    {
        if (_chromeFullscreen)
        {
            DetachWindowChromeForFullscreen();
            return;
        }

        if (WinChrome == null) return;
        WinChrome.ResizeBorderThickness = _workAreaFilled ? new Thickness(0) : new Thickness(4);
        WinChrome.CaptionHeight = 36;
        // 确保窗化态仍挂着 Chrome（全屏卸掉后需恢复）
        if (WindowChrome.GetWindowChrome(this) == null)
            WindowChrome.SetWindowChrome(this, WinChrome);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        if (_chromeFullscreen)
        {
            ExitTrueFullscreen();
            return;
        }

        if (_workAreaFilled)
            RestoreNormalBounds(_normalBoundsDip);
        else
            FillWorkArea(saveNormalBounds: true);
    }

    private void RefreshMaxIcon()
    {
        if (MaxIcon == null) return;
        var restored = _workAreaFilled || WindowState == WindowState.Maximized;
        MaxIcon.Data = restored
            ? Geometry.Parse("M 5,3 H 13 V 11 H 11 M 3,5 H 11 V 13 H 3 Z")
            : Geometry.Parse("M 3,3 H 13 V 13 H 3 Z");
        MaxBtn.ToolTip = restored ? "还原" : "最大化";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 全屏：禁止任何非客户区拖拽（含残留 Caption / 顶部边缘）
        const int wmNcHitTest = 0x0084;
        const int htClient = 1;
        if (msg == wmNcHitTest && _chromeFullscreen)
        {
            handled = true;
            return (IntPtr)htClient;
        }

        const int wmGetMinMaxInfo = 0x0024;
        if (msg != wmGetMinMaxInfo)
            return IntPtr.Zero;

        if (_chromeFullscreen)
            return IntPtr.Zero;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var mon = info.rcMonitor;
        var work = info.rcWork;
        mmi.MaxPosition.X = work.Left - mon.Left;
        mmi.MaxPosition.Y = work.Top - mon.Top;
        mmi.MaxSize.X = work.Right - work.Left;
        mmi.MaxSize.Y = work.Bottom - work.Top;
        mmi.MaxTrackSize = mmi.MaxSize;
        Marshal.StructureToPtr(mmi, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativeMethods.POINT Reserved;
        public NativeMethods.POINT MaxSize;
        public NativeMethods.POINT MaxPosition;
        public NativeMethods.POINT MinTrackSize;
        public NativeMethods.POINT MaxTrackSize;
    }

    public readonly record struct DecodedFrame(int Width, int Height, byte[] Bgra, int Stride);

    public static DecodedFrame? DecodeJpeg(byte[] jpeg)
    {
        try
        {
            using var ms = new MemoryStream(jpeg);
            using var bmp = new System.Drawing.Bitmap(ms);
            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            try
            {
                var stride = data.Stride;
                var abs = Math.Abs(stride);
                var bytes = new byte[abs * bmp.Height];
                if (stride >= 0)
                {
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                }
                else
                {
                    for (var y = 0; y < bmp.Height; y++)
                        Marshal.Copy(data.Scan0 + stride * (bmp.Height - 1 - y), bytes, y * abs, abs);
                }

                return new DecodedFrame(bmp.Width, bmp.Height, bytes, abs);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch
        {
            return null;
        }
    }

    public void Present(string peer, DecodedFrame frame, long capturedUnixMs = 0)
    {
        peer = peer.Trim();
        EnsureTab(peer);
        var tab = FindTab(peer);
        if (tab == null) return;

        tab.ContentW = frame.Width;
        tab.ContentH = frame.Height;
        if (tab.FrameBitmap == null
            || tab.FrameBitmap.PixelWidth != frame.Width
            || tab.FrameBitmap.PixelHeight != frame.Height)
        {
            tab.FrameBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgra32, null);
        }

        tab.FrameBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Bgra, frame.Stride, 0);
        UpdateLatencyForTab(tab, capturedUnixMs);

        if (!string.Equals(_activePeer, peer, StringComparison.OrdinalIgnoreCase))
            return;

        FrameImage.Source = tab.FrameBitmap;
        if (WaitingText.Visibility != Visibility.Collapsed)
            WaitingText.Visibility = Visibility.Collapsed;
    }

    public void Present(DecodedFrame frame, long capturedUnixMs = 0)
    {
        var peer = _activePeer ?? _service?.Peer?.Trim();
        if (string.IsNullOrWhiteSpace(peer)) return;
        Present(peer, frame, capturedUnixMs);
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
        tab.TabBorder.ToolTip = tip;
        if (string.Equals(tab.Peer, _activePeer, StringComparison.OrdinalIgnoreCase))
            ApplyActiveLatencyTip();
    }

    private void ApplyActiveLatencyTip()
    {
        var tab = FindTab(_activePeer);
        var tip = tab?.LatencyTip ?? Loc.T("Proj.LatencyTipWait");
        if (FsGripBtn != null)
            FsGripBtn.ToolTip = tip;
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
            var bar = new System.Windows.Shapes.Rectangle
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

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (_chromeFullscreen)
        {
            ExitTrueFullscreen();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_activePeer))
            RequestStopTab(_activePeer);
    }

    private static SolidColorBrush Brush(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}
