using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FlyScreen.DesktopUi.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi;

public partial class ProgramEditorWindow : Window
{
    private const double LineRowHeight = 18;
    private static readonly IBrush BpBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly IBrush LineFg = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private LinuxClientChrome? _chrome;
    private int _caretLine = 1;
    private bool _toolModeUpdating;
    private bool _toolsCompact;
    private bool _outputExpanded;
    private bool _outputRefreshPending;
    private DateTime _outputSince = DateTime.MinValue;
    private AppLogEntry? _outputLastWritten;

    public ProgramEditorWindow() : this(new UserProgram { Name = "design" })
    {
    }

    public ProgramEditorWindow(UserProgram program)
    {
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _runner = ProgramService.Instance.Runner;
        InitializeComponent();

        var displayTitle = $"{Loc.T("Prog.EditorTitle")} — {program.Name} · {program.LanguageText} · {program.FileName}";
        _chrome = LinuxClientChrome.Attach(
            this, ClientTitleBar, ClientTitleText, displayTitle,
            TitleMinBtn, TitleMaxBtn, TitleCloseBtn, allowMaximize: true);

        if (Application.Current?.Resources.TryGetResource("UiFont", null, out var font) == true
            && font is FontFamily ff)
            FontFamily = ff;

        SetTool(BtnSave, BtnSaveText, "Prog.Save");
        SetTool(BtnDebug, BtnDebugText, "Prog.Debug");
        SetTool(BtnClearAllBp, BtnClearAllBpText, "Prog.ClearAllBp");
        SetTool(BtnRun, BtnRunText, "Prog.Run");
        SetTool(BtnStop, BtnStopText, "Prog.StopRun");
        OutputTitleText.Text = Loc.T("Prog.Output");
        OutputClearBtn.Content = Loc.T("Prog.OutputClear");
        OutputEmptyText.Text = Loc.T("Prog.OutputEmpty");
        ApplyOutputExpanded(false);

        CodeBox.Text = _catalog.ReadSource(program);
        RefreshLineNumbers();
        RefreshBpHint();
        RefreshOutput();
        CodeBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty)
                UpdateCaretLine();
        };
        Opened += (_, _) => UpdateToolMode();
        AppLog.Changed += OnAppLogChanged;
        // 关闭立即返回；若仍在运行/调试则后台异步停止
        Closed += (_, _) => StopOnCloseIfNeeded();
    }

    private int _closeStopArmed;

    /// <summary>关闭编辑器：不阻塞 UI；运行中则 fire-and-forget 停止进程。</summary>
    private void StopOnCloseIfNeeded()
    {
        if (System.Threading.Interlocked.Exchange(ref _closeStopArmed, 1) != 0) return;
        AppLog.Changed -= OnAppLogChanged;
        if (!_runner.IsRunning(_program.Id)) return;

        var prog = _program;
        var runner = _runner;
        _ = Task.Run(async () =>
        {
            try { await runner.StopAsync(prog).ConfigureAwait(false); }
            catch (Exception ex)
            {
                AppLog.Error(ex, $"关闭编辑器时停止「{prog.Name}」失败", prog.Id, prog.Name);
            }
        });
    }

    private void OnAppLogChanged()
    {
        if (_outputRefreshPending) return;
        _outputRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _outputRefreshPending = false;
            RefreshOutput();
        });
    }

    private void OutputHeader_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>() != null)
            return;
        ApplyOutputExpanded(!_outputExpanded);
    }

    private void OutputClear_Click(object? sender, RoutedEventArgs e)
    {
        _outputSince = DateTime.Now;
        _outputLastWritten = null;
        OutputText.Inlines.Clear();
        OutputEmptyText.IsVisible = true;
    }

    private void ApplyOutputExpanded(bool expanded)
    {
        _outputExpanded = expanded;
        OutputBody.IsVisible = expanded;
        OutputToggleGlyph.Text = expanded ? "▼" : "▲";
        ToolTip.SetTip(OutputDock, expanded ? Loc.T("Prog.OutputHide") : Loc.T("Prog.OutputShow"));
        if (expanded)
            ScrollOutputToEnd();
    }

    private void EnsureOutputVisible()
    {
        if (!_outputExpanded)
            ApplyOutputExpanded(true);
    }

    /// <summary>Inlines 追加，避免 ItemsSource 整表刷新。</summary>
    private void RefreshOutput()
    {
        var items = AppLog.Snapshot(false, _program.Id, _program.Name)
            .Where(e => e.Time >= _outputSince)
            .ToList();
        var chunk = AppLog.FormatNewLines(items, ref _outputLastWritten);
        if (chunk != null)
            OutputText.Inlines.Add(new Run(chunk));
        OutputEmptyText.IsVisible = OutputText.Inlines.Count == 0;
        if (chunk != null && _outputExpanded)
            ScrollOutputToEnd();
    }

    private void ScrollOutputToEnd()
    {
        if (!_outputExpanded || OutputText.Inlines.Count == 0) return;
        OutputScroll.ScrollToEnd();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_outputExpanded) return;
            var max = Math.Max(0, OutputScroll.Extent.Height - OutputScroll.Viewport.Height);
            OutputScroll.Offset = new Vector(OutputScroll.Offset.X, max);
        }, DispatcherPriority.Loaded);
    }

    private static void SetTool(Button button, TextBlock label, string key)
    {
        label.Text = Loc.T(key);
        ToolTip.SetTip(button, label.Text);
    }

    private void ToolBarHost_SizeChanged(object? sender, SizeChangedEventArgs e)
        => UpdateToolMode();

    private void UpdateToolMode()
    {
        if (!IsLoaded || _toolModeUpdating || ToolBarHost.Bounds.Width <= 1)
            return;
        _toolModeUpdating = true;
        try
        {
            SetToolsCompact(false);
            ToolBar.Measure(new Size(double.PositiveInfinity, 48));
            SetToolsCompact(ToolBar.DesiredSize.Width > ToolBarHost.Bounds.Width + 0.5);
        }
        finally
        {
            _toolModeUpdating = false;
        }
    }

    private void SetToolsCompact(bool compact)
    {
        if (_toolsCompact == compact && BtnSaveText.IsVisible == !compact)
            return;
        _toolsCompact = compact;
        BtnSaveText.IsVisible = !compact;
        BtnDebugText.IsVisible = !compact;
        BtnClearAllBpText.IsVisible = !compact;
        BtnRunText.IsVisible = !compact;
        BtnStopText.IsVisible = !compact;
        var pad = compact ? new Thickness(8, 0) : new Thickness(10, 0);
        BtnSave.Padding = pad;
        BtnDebug.Padding = pad;
        BtnClearAllBp.Padding = pad;
        BtnRun.Padding = pad;
        BtnStop.Padding = pad;
    }

    private void UpdateCaretLine()
    {
        var idx = CodeBox.CaretIndex;
        var text = CodeBox.Text ?? "";
        _caretLine = 1;
        for (var i = 0; i < idx && i < text.Length; i++)
            if (text[i] == '\n') _caretLine++;
        RefreshBpHint();
    }

    private void CodeBox_TextChanged(object? sender, TextChangedEventArgs e)
        => RefreshLineNumbers();

    private void RefreshLineNumbers()
    {
        var text = (CodeBox.Text ?? "").Replace("\r\n", "\n");
        var lines = Math.Max(1, text.Split('\n').Length);
        LineNumbers.Children.Clear();
        for (var i = 1; i <= lines; i++)
        {
            var line = i;
            var hasBp = _program.Breakpoints.Contains(line);
            var row = new Grid
            {
                Height = LineRowHeight,
                Margin = new Thickness(2, 0, 4, 0),
                ColumnDefinitions = new ColumnDefinitions("16,*"),
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = line
            };
            row.Children.Add(new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = hasBp ? BpBrush : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            });
            var num = new TextBlock
            {
                Text = line.ToString().PadLeft(3),
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Menlo"),
                FontSize = 13,
                Foreground = LineFg,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            Grid.SetColumn(num, 1);
            row.Children.Add(num);
            row.PointerPressed += LineRow_PointerPressed;
            LineNumbers.Children.Add(row);
        }
    }

    private void RefreshBpHint()
    {
        var bps = _program.Breakpoints.Count == 0
            ? "无断点"
            : "断点: " + string.Join(",", _program.Breakpoints.OrderBy(x => x));
        BpHintText.Text = $"行 {_caretLine}  ·  {bps}";
    }

    private void LineRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Grid { Tag: int line }) return;
        if (_program.Breakpoints.Contains(line))
            _program.Breakpoints.Remove(line);
        else
        {
            _program.Breakpoints.Add(line);
            _program.Breakpoints.Sort();
        }
        _catalog.SaveMeta(_program);
        _caretLine = line;
        RefreshLineNumbers();
        RefreshBpHint();
        e.Handled = true;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _catalog.WriteSource(_program, CodeBox.Text ?? "");
        _catalog.SaveMeta(_program);
        AppLog.Info($"已保存程序「{_program.Name}」");
    }

    private async void Debug_Click(object? sender, RoutedEventArgs e)
    {
        EnsureOutputVisible();
        var original = CodeBox.Text ?? "";
        SaveSilent();
        if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
            InjectPythonBreakpoints();
        else
            AppLog.Info($"调试「{_program.Name}」：断点已记录（完整调试器后续扩展）", _program.Id, _program.Name);
        try { await _runner.StartAsync(_program, debug: true); }
        catch (Exception ex) { AppLog.Error(ex, "调试启动失败", _program.Id, _program.Name); }
        finally
        {
            if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
            {
                await Task.Delay(300);
                _catalog.WriteSource(_program, original);
            }
            RefreshOutput();
        }
    }

    private void ClearAllBp_Click(object? sender, RoutedEventArgs e)
    {
        _program.Breakpoints.Clear();
        _catalog.SaveMeta(_program);
        RefreshLineNumbers();
        RefreshBpHint();
    }

    private async void Run_Click(object? sender, RoutedEventArgs e)
    {
        EnsureOutputVisible();
        SaveSilent();
        try { await _runner.StartAsync(_program); }
        catch (Exception ex) { AppLog.Error(ex, "运行失败", _program.Id, _program.Name); }
        RefreshOutput();
    }

    private async void Stop_Click(object? sender, RoutedEventArgs e)
    {
        try { await _runner.StopAsync(_program); }
        catch (Exception ex) { AppLog.Error(ex, "停止失败", _program.Id, _program.Name); }
        RefreshOutput();
    }

    private void SaveSilent()
    {
        _catalog.WriteSource(_program, CodeBox.Text ?? "");
        _catalog.SaveMeta(_program);
    }

    private void InjectPythonBreakpoints()
    {
        var lines = (CodeBox.Text ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        foreach (var line in _program.Breakpoints.Where(l => l >= 1 && l <= lines.Count).Distinct().OrderByDescending(l => l))
        {
            var idx = line - 1;
            if (!lines[idx].Contains("breakpoint()", StringComparison.Ordinal))
                lines.Insert(idx, "breakpoint()  # flyscreen-bp");
        }
        _catalog.WriteSource(_program, string.Join("\n", lines));
    }
}
