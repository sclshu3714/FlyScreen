using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlyScreen.Editor;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Programs;
using ICSharpCode.AvalonEdit.Highlighting;

namespace FlyScreen;

public partial class ProgramEditorWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private readonly BreakpointMargin _bpMargin;
    private int _caretLine = 1;
    private bool _toolModeUpdating;
    private bool _outputExpanded;
    private bool _outputRefreshPending;
    private DateTime _outputSince = DateTime.MinValue;
    private AppLogEntry? _outputLastWritten;

    public static readonly DependencyProperty ToolsCompactProperty =
        DependencyProperty.Register(nameof(ToolsCompact), typeof(bool), typeof(ProgramEditorWindow), new PropertyMetadata(false));

    public static readonly RoutedUICommand FormatCommand =
        new("Format", nameof(FormatCommand), typeof(ProgramEditorWindow));

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
        ApplyOutputExpanded(false);

        CodeEditor.Options.EnableHyperlinks = false;
        CodeEditor.Options.ConvertTabsToSpaces = true;
        CodeEditor.Text = _catalog.ReadSource(program);
        ApplyHighlighting();
        _ = new EditorCompletion(CodeEditor, () => _program.Language);
        _bpMargin = new BreakpointMargin(() => _program.Breakpoints, ToggleBreakpoint);
        CodeEditor.TextArea.LeftMargins.Insert(0, _bpMargin);
        CodeEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            _caretLine = CodeEditor.TextArea.Caret.Line;
            RefreshBpHint();
        };
        CodeEditor.TextChanged += (_, _) => RefreshBpHint();
        RefreshBpHint();
        RefreshOutput();

        AppLog.Changed += OnAppLogChanged;
        Closed += (_, _) => StopOnCloseIfNeeded();
    }

    private void ApplyHighlighting()
    {
        var name = ProgramEditorLanguageMap.AvalonHighlightingName(_program.Language);
        var def = HighlightingManager.Instance.GetDefinition(name);
        if (def == null && name is "PowerShell" or "Bash" or "Batch")
            def = HighlightingManager.Instance.GetDefinition("JavaScript");
        EditorHighlightingTheme.Apply(CodeEditor, def);
    }

    private int _closeStopArmed;

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
        Dispatcher.BeginInvoke(() =>
        {
            _outputRefreshPending = false;
            RefreshOutput();
        });
    }

    private void OutputHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestorButton(d) != null)
            return;
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
        _outputLastWritten = null;
        OutputBox.Clear();
        OutputEmptyText.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void ApplyOutputExpanded(bool expanded)
    {
        _outputExpanded = expanded;
        OutputBody.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        OutputToggleGlyph.Text = expanded ? "▼" : "▲";
        OutputDock.ToolTip = expanded ? Loc.T("Prog.OutputHide") : Loc.T("Prog.OutputShow");
        if (expanded)
            ScrollOutputToEnd();
    }

    private void EnsureOutputVisible()
    {
        if (!_outputExpanded)
            ApplyOutputExpanded(true);
    }

    private void RefreshOutput()
    {
        var items = AppLog.Snapshot(false, _program.Id, _program.Name)
            .Where(e => e.Time >= _outputSince)
            .ToList();
        var chunk = AppLog.FormatNewLines(items, ref _outputLastWritten);
        if (chunk != null)
            OutputBox.AppendText(chunk);
        OutputEmptyText.Visibility = OutputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (chunk != null && _outputExpanded)
            ScrollOutputToEnd();
    }

    private void ScrollOutputToEnd()
    {
        if (!_outputExpanded || OutputBox.Text.Length == 0) return;
        OutputBox.CaretIndex = OutputBox.Text.Length;
        OutputBox.ScrollToEnd();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_outputExpanded) return;
            OutputBox.CaretIndex = OutputBox.Text.Length;
            OutputBox.ScrollToEnd();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

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

    private void RefreshBpHint()
    {
        // 断点已在左侧 gutter 可见，工具条只显示行数状态，避免挤占按钮
        BpHintText.Text = ProgramEditorLimits.StatusFragment(_caretLine, CodeEditor.Text);
        BpHintText.Foreground = ProgramEditorLimits.ExceedsLimit(CodeEditor.Text, out _)
            ? System.Windows.Media.Brushes.DarkOrange
            : (System.Windows.Media.Brush)FindResource("Muted");
    }

    private bool EnsureWithinLineLimit()
    {
        var text = CodeEditor.Text ?? "";
        if (!ProgramEditorLimits.ExceedsLimit(text, out var n)) return true;
        MessageBox.Show(this, ProgramEditorLimits.ExceedMessage(n), Title,
            MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshBpHint();
        return false;
    }

    /// <summary>按语言自动格式化；返回是否改动了文本。</summary>
    private bool ApplyAutoFormat(bool toast = false)
    {
        var current = CodeEditor.Text ?? "";
        var formatted = ProgramEditorFormatter.Format(current, _program.Language);
        if (string.Equals(current, formatted, StringComparison.Ordinal)) return false;
        var caret = CodeEditor.CaretOffset;
        CodeEditor.Document.Text = formatted;
        CodeEditor.CaretOffset = Math.Clamp(caret, 0, formatted.Length);
        RefreshBpHint();
        if (toast) StatusTextToast(Loc.T("Prog.Formatted"));
        return true;
    }

    private void Format_Executed(object sender, ExecutedRoutedEventArgs e)
        => ApplyAutoFormat(toast: true);

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e)
        => Save_Click(sender, e);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyAutoFormat();
        if (!EnsureWithinLineLimit()) return;
        _catalog.WriteSource(_program, CodeEditor.Text ?? "");
        _catalog.SaveMeta(_program);
        AppLog.Info($"已保存程序「{_program.Name}」", _program.Id, _program.Name);
        StatusTextToast("已保存");
    }

    private async void Debug_Click(object sender, RoutedEventArgs e)
    {
        ApplyAutoFormat();
        if (!EnsureWithinLineLimit()) return;
        EnsureOutputVisible();
        var original = CodeEditor.Text ?? "";
        SaveSilent();
        if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
        {
            var injected = PythonBreakpointInjector.Inject(original, _program.Breakpoints);
            _catalog.WriteSource(_program, injected);
            AppLog.Info($"已为「{_program.Name}」注入 {PythonBreakpointInjector.CountInjected(_program.Breakpoints, original)} 处 Python breakpoint()", _program.Id, _program.Name);
        }
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
            if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
            {
                await Task.Delay(300);
                _catalog.WriteSource(_program, original);
            }
            RefreshOutput();
        }
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
        _bpMargin.InvalidateVisual();
        RefreshBpHint();
    }

    private void ClearAllBp_Click(object sender, RoutedEventArgs e)
    {
        _program.Breakpoints.Clear();
        _catalog.SaveMeta(_program);
        _bpMargin.InvalidateVisual();
        RefreshBpHint();
        AppLog.Info($"「{_program.Name}」已清除全部断点", _program.Id, _program.Name);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        ApplyAutoFormat();
        if (!EnsureWithinLineLimit()) return;
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
        ApplyAutoFormat();
        if (!ProgramEditorLimits.IsWithinLimit(CodeEditor.Text)) return;
        _catalog.WriteSource(_program, CodeEditor.Text ?? "");
        _catalog.SaveMeta(_program);
    }

    private void StatusTextToast(string msg)
    {
        Title = BaseTitle + "  ·  " + msg;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => { Title = BaseTitle; timer.Stop(); };
        timer.Start();
    }
}
