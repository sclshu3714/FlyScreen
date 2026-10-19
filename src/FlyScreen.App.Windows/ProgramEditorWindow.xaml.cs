using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Media = System.Windows.Media;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Programs;

namespace FlyScreen;

public partial class ProgramEditorWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private readonly ObservableCollection<AppLogEntry> _outputItems = [];
    private int _caretLine = 1;
    private bool _toolModeUpdating;
    private bool _outputExpanded;
    private bool _outputRefreshPending;
    private DateTime _outputSince = DateTime.MinValue;

    public static readonly DependencyProperty ToolsCompactProperty =
        DependencyProperty.Register(nameof(ToolsCompact), typeof(bool), typeof(ProgramEditorWindow), new PropertyMetadata(false));

    public bool ToolsCompact
    {
        get => (bool)GetValue(ToolsCompactProperty);
        set => SetValue(ToolsCompactProperty, value);
    }

    private string BaseTitle =>
        $"{Loc.T("Prog.EditorTitle")} — {_program.Name} · {_program.LanguageText} · {_program.FileName}";

    public ProgramEditorWindow(UserProgram program)
    {
        InitializeComponent();
        ThemeService.ApplyToChildWindow(this);
        Loaded += (_, _) => ThemeService.ApplyToChildWindow(this);
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _runner = ProgramService.Instance.Runner;

        Title = BaseTitle;
        SetToolButton(BtnSave, BtnSaveText, "Prog.Save");
        SetToolButton(BtnDebug, BtnDebugText, "Prog.Debug");
        SetToolButton(BtnClearAllBp, BtnClearAllBpText, "Prog.ClearAllBp");
        SetToolButton(BtnRun, BtnRunText, "Prog.Run");
        SetToolButton(BtnStop, BtnStopText, "Prog.StopRun");
        OutputTitleText.Text = Loc.T("Prog.Output");
        OutputClearBtn.Content = Loc.T("Prog.OutputClear");
        OutputEmptyText.Text = Loc.T("Prog.OutputEmpty");
        OutputList.ItemsSource = _outputItems;
        ApplyOutputExpanded(false);

        CodeBox.Text = _catalog.ReadSource(program);
        RefreshLineNumbers();
        RefreshBpHint();
        RefreshOutput();

        CodeBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(CodeBox_ScrollChanged), true);
        AppLog.Changed += OnAppLogChanged;
        Closed += (_, _) => AppLog.Changed -= OnAppLogChanged;
    }

    private void OnAppLogChanged()
    {
        if (_outputRefreshPending) return;
        _outputRefreshPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _outputRefreshPending = false;
            RefreshOutput();
        });
    }

    private void OutputHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestorButton(d) != null)
            return; // 点「清除」不切换折叠
        ApplyOutputExpanded(!_outputExpanded);
    }

    private static System.Windows.Controls.Button? FindAncestorButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Button b) return b;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void OutputClear_Click(object sender, RoutedEventArgs e)
    {
        _outputSince = DateTime.Now;
        _outputItems.Clear();
        OutputEmptyText.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void ApplyOutputExpanded(bool expanded)
    {
        _outputExpanded = expanded;
        OutputBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        OutputToggleGlyph.Text = expanded ? "▼" : "▲";
        OutputDock.ToolTip = expanded ? Loc.T("Prog.OutputHide") : Loc.T("Prog.OutputShow");
    }

    private void EnsureOutputVisible()
    {
        if (!_outputExpanded)
            ApplyOutputExpanded(true);
    }

    /// <summary>增量同步输出列表，避免整表重绑导致闪烁。</summary>
    private void RefreshOutput()
    {
        var items = AppLog.Snapshot(false, _program.Id, _program.Name)
            .Where(e => e.Time >= _outputSince)
            .ToList();

        var common = 0;
        var n = Math.Min(_outputItems.Count, items.Count);
        while (common < n && SameOutputEntry(_outputItems[common], items[common]))
            common++;

        for (var i = _outputItems.Count - 1; i >= common; i--)
            _outputItems.RemoveAt(i);

        var added = false;
        for (var i = common; i < items.Count; i++)
        {
            _outputItems.Add(items[i]);
            added = true;
        }

        OutputEmptyText.Visibility = _outputItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (added && _outputExpanded && _outputItems.Count > 0)
            OutputList.ScrollIntoView(_outputItems[^1]);
    }

    private static bool SameOutputEntry(AppLogEntry a, AppLogEntry b)
        => ReferenceEquals(a, b)
           || (a.Time == b.Time && a.Level == b.Level && a.Message == b.Message);

    private static void SetToolButton(System.Windows.Controls.Button button, TextBlock label, string key)
    {
        label.Text = Loc.T(key);
        button.ToolTip = label.Text;
    }

    private void ToolBarHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateToolMode();

    private void UpdateToolMode()
    {
        if (!IsLoaded || _toolModeUpdating || ToolBarHost.ActualWidth <= 1) return;
        _toolModeUpdating = true;
        try
        {
            ToolsCompact = false;
            ToolBar.Measure(new System.Windows.Size(double.PositiveInfinity, 48));
            ToolsCompact = ToolBar.DesiredSize.Width > ToolBarHost.ActualWidth + 0.5;
        }
        finally
        {
            _toolModeUpdating = false;
        }
    }

    private void CodeBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0) return;
        LineScroll.ScrollToVerticalOffset(e.VerticalOffset);
    }

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshLineNumbers();

    private void CodeBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var idx = CodeBox.CaretIndex;
        var text = CodeBox.Text ?? "";
        _caretLine = 1;
        for (var i = 0; i < idx && i < text.Length; i++)
            if (text[i] == '\n') _caretLine++;
        RefreshBpHint();
    }

    private static readonly SolidColorBrush BpBrush = CreateBpBrush();

    private static SolidColorBrush CreateBpBrush()
    {
        var b = new SolidColorBrush(Media.Color.FromRgb(0xEF, 0x44, 0x44));
        b.Freeze();
        return b;
    }

    private void RefreshLineNumbers()
    {
        var text = CodeBox.Text ?? "";
        var lines = Math.Max(1, text.Replace("\r\n", "\n").Split('\n').Length);
        LineNumbers.Inlines.Clear();
        for (var i = 1; i <= lines; i++)
        {
            if (_program.Breakpoints.Contains(i))
            {
                // ⬤ 比 ● 大约一号，红色填充
                LineNumbers.Inlines.Add(new Run("⬤")
                {
                    Foreground = BpBrush,
                    FontSize = 12,
                    BaselineAlignment = BaselineAlignment.Center
                });
            }
            else
            {
                LineNumbers.Inlines.Add(new Run("  "));
            }

            LineNumbers.Inlines.Add(new Run(i.ToString().PadLeft(3)));
            if (i < lines)
                LineNumbers.Inlines.Add(new Run("\n"));
        }
    }

    private void RefreshBpHint()
    {
        var bps = _program.Breakpoints.Count == 0
            ? "无断点"
            : "断点: " + string.Join(",", _program.Breakpoints.OrderBy(x => x));
        BpHintText.Text = $"行 {_caretLine}  ·  {bps}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _catalog.WriteSource(_program, CodeBox.Text ?? "");
        _catalog.SaveMeta(_program);
        AppLog.Info($"已保存程序「{_program.Name}」", _program.Id, _program.Name);
        StatusTextToast("已保存");
    }

    private async void Debug_Click(object sender, RoutedEventArgs e)
    {
        EnsureOutputVisible();
        var original = CodeBox.Text ?? "";
        SaveSilent();
        if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
            InjectPythonBreakpointsBeforeRun();
        else
            AppLog.Info($"调试「{_program.Name}」：断点已记录（完整调试器后续扩展）", _program.Id, _program.Name);
        try
        {
            await _runner.StartAsync(_program, debug: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            // 磁盘恢复为编辑器原文，避免把 breakpoint() 写回工程
            if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
            {
                await Task.Delay(300);
                _catalog.WriteSource(_program, original);
            }
            RefreshOutput();
        }
    }

    private void LineNumbers_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var line = LineFromGutterClick(e.GetPosition(LineNumbers));
        if (line == null) return;
        ToggleBreakpoint(line.Value);
        e.Handled = true;
    }

    private int? LineFromGutterClick(System.Windows.Point point)
    {
        var total = Math.Max(1, (CodeBox.Text ?? "").Replace("\r\n", "\n").Split('\n').Length);
        var h = LineNumbers.ActualHeight;
        if (h <= 1 || total <= 0) return null;
        var pad = LineNumbers.Padding.Top;
        var y = Math.Max(0, point.Y - pad);
        var contentH = Math.Max(1, h - pad - LineNumbers.Padding.Bottom);
        var line = (int)Math.Floor(y / contentH * total) + 1;
        return line >= 1 && line <= total ? line : null;
    }

    private void ToggleBreakpoint(int line)
    {
        if (_program.Breakpoints.Remove(line))
            AppLog.Info($"「{_program.Name}」取消断点 @ 行 {line}", _program.Id, _program.Name);
        else
        {
            _program.Breakpoints.Add(line);
            _program.Breakpoints.Sort();
            AppLog.Info($"「{_program.Name}」断点 @ 行 {line}", _program.Id, _program.Name);
        }
        _catalog.SaveMeta(_program);
        RefreshLineNumbers();
        RefreshBpHint();
    }

    private void ClearAllBp_Click(object sender, RoutedEventArgs e)
    {
        _program.Breakpoints.Clear();
        _catalog.SaveMeta(_program);
        RefreshLineNumbers();
        RefreshBpHint();
        AppLog.Info($"「{_program.Name}」已清除全部断点", _program.Id, _program.Name);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        EnsureOutputVisible();
        SaveSilent();
        try { await _runner.StartAsync(_program, debug: false); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        RefreshOutput();
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try { await _runner.StopAsync(_program); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
        RefreshOutput();
    }

    private void SaveSilent()
    {
        _catalog.WriteSource(_program, CodeBox.Text ?? "");
        _catalog.SaveMeta(_program);
    }

    /// <summary>Python 基础调试：在断点行前插入 breakpoint()（保存临时覆盖，运行后由用户源文件仍为编辑器内容）。</summary>
    private void InjectPythonBreakpointsBeforeRun()
    {
        var lines = (CodeBox.Text ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        var inject = _program.Breakpoints.Where(l => l >= 1 && l <= lines.Count).Distinct().OrderByDescending(l => l);
        foreach (var line in inject)
        {
            var idx = line - 1;
            if (!lines[idx].Contains("breakpoint()", StringComparison.Ordinal))
                lines.Insert(idx, "breakpoint()  # flyscreen-bp");
        }
        _catalog.WriteSource(_program, string.Join("\n", lines));
        // 还原编辑器展示为未注入版本（磁盘为调试副本）— 用户下次保存会覆盖
        AppLog.Info($"已为「{_program.Name}」注入 {_program.Breakpoints.Count} 处 Python breakpoint()", _program.Id, _program.Name);
    }

    private void StatusTextToast(string msg)
    {
        Title = BaseTitle + "  ·  " + msg;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => { Title = BaseTitle; timer.Stop(); };
        timer.Start();
    }
}
