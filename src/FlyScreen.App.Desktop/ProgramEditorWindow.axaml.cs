using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi;

public partial class ProgramEditorWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private readonly ObservableCollection<AppLogEntry> _outputItems = [];
    private int _caretLine = 1;
    private bool _toolModeUpdating;
    private bool _toolsCompact;
    private bool _outputExpanded;
    private bool _outputRefreshPending;
    private DateTime _outputSince = DateTime.MinValue;

    public ProgramEditorWindow() : this(new UserProgram { Name = "design" })
    {
    }

    public ProgramEditorWindow(UserProgram program)
    {
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _runner = ProgramService.Instance.Runner;
        InitializeComponent();

        Title = $"{Loc.T("Prog.EditorTitle")} — {program.Name} · {program.LanguageText} · {program.FileName}";
        SetTool(BtnSave, BtnSaveText, "Prog.Save");
        SetTool(BtnDebug, BtnDebugText, "Prog.Debug");
        SetTool(BtnClearAllBp, BtnClearAllBpText, "Prog.ClearAllBp");
        SetTool(BtnRun, BtnRunText, "Prog.Run");
        SetTool(BtnStop, BtnStopText, "Prog.StopRun");
        OutputTitleText.Text = Loc.T("Prog.Output");
        OutputClearBtn.Content = Loc.T("Prog.OutputClear");
        OutputEmptyText.Text = Loc.T("Prog.OutputEmpty");
        OutputList.ItemsSource = _outputItems;
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
        Closed += (_, _) => AppLog.Changed -= OnAppLogChanged;
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
        _outputItems.Clear();
        OutputEmptyText.IsVisible = true;
    }

    private void ApplyOutputExpanded(bool expanded)
    {
        _outputExpanded = expanded;
        OutputBody.IsVisible = expanded;
        OutputToggleGlyph.Text = expanded ? "▼" : "▲";
        ToolTip.SetTip(OutputDock, expanded ? Loc.T("Prog.OutputHide") : Loc.T("Prog.OutputShow"));
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

        OutputEmptyText.IsVisible = _outputItems.Count == 0;
        if (added && _outputExpanded && _outputItems.Count > 0)
            OutputList.ScrollIntoView(_outputItems[^1]);
    }

    private static bool SameOutputEntry(AppLogEntry a, AppLogEntry b)
        => ReferenceEquals(a, b)
           || (a.Time == b.Time && a.Level == b.Level && a.Message == b.Message);

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

    private static readonly IBrush BpBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));

    private void RefreshLineNumbers()
    {
        var text = (CodeBox.Text ?? "").Replace("\r\n", "\n");
        var lines = Math.Max(1, text.Split('\n').Length);
        LineNumbers.Inlines.Clear();
        for (var i = 1; i <= lines; i++)
        {
            if (_program.Breakpoints.Contains(i))
            {
                LineNumbers.Inlines.Add(new Run("⬤")
                {
                    Foreground = BpBrush,
                    FontSize = 12
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

    private void LineNumbers_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var text = (CodeBox.Text ?? "").Replace("\r\n", "\n");
        var lineCount = Math.Max(1, text.Split('\n').Length);
        var h = LineNumbers.Bounds.Height;
        if (h <= 1) return;
        var pad = LineNumbers.Padding.Top;
        var y = Math.Max(0, e.GetPosition(LineNumbers).Y - pad);
        var contentH = Math.Max(1, h - pad - LineNumbers.Padding.Bottom);
        var line = (int)Math.Clamp(Math.Floor(y / contentH * lineCount) + 1, 1, lineCount);
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
