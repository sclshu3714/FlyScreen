using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Search;
using FlyScreen.DesktopUi.Editor;
using FlyScreen.DesktopUi.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi;

public partial class ProgramEditorWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private readonly BreakpointMargin _bpMargin;
    private readonly DebugCurrentLineRenderer _debugLineRenderer = new();
    private SearchPanel? _searchPanel;
    private LinuxClientChrome? _chrome;
    private int _caretLine = 1;
    private bool _toolModeUpdating;
    private bool _toolsCompact;
    private bool _outputExpanded;
    private bool _outputRefreshPending;
    private DateTime _outputSince = DateTime.MinValue;
    private AppLogEntry? _outputLastWritten;
    private ProgramDebugGate? _debugGate;
    private int? _lastPausedLineLogged;
    private DispatcherTimer? _debugPollTimer;

    public ProgramEditorWindow() : this(new UserProgram { Name = "design" })
    {
    }

    public ProgramEditorWindow(UserProgram program)
    {
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _runner = ProgramService.Instance.Runner;
        InitializeComponent();
        DesktopThemeService.Apply(DesktopThemeService.Current, this);

        var displayTitle = $"{Loc.T("Prog.EditorTitle")} — {program.Name} · {program.LanguageText} · {program.FileName}";
        _chrome = LinuxClientChrome.Attach(
            this, ClientTitleBar, ClientTitleText, displayTitle,
            TitleMinBtn, TitleMaxBtn, TitleCloseBtn, allowMaximize: true);

        if (Application.Current?.Resources.TryGetResource("UiFont", null, out var font) == true
            && font is FontFamily ff)
            FontFamily = ff;

        SetTool(BtnSave, BtnSaveText, "Prog.Save");
        SetTool(BtnFind, BtnFindText, "Prog.Find");
        SetTool(BtnBuild, BtnBuildText, "Prog.Build");
        SetTool(BtnDebug, BtnDebugText, "Prog.Debug");
        SetTool(BtnRun, BtnRunText, "Prog.Run");
        SetTool(BtnStop, BtnStopText, "Prog.StopRun");
        OutputTitleText.Text = Loc.T("Prog.Output");
        OutputClearBtn.Content = Loc.T("Prog.OutputClear");
        OutputEmptyText.Text = Loc.T("Prog.OutputEmpty");
        ApplyOutputExpanded(false);
        RefreshDebugUi();

        CodeEditor.Options.EnableHyperlinks = false;
        CodeEditor.Options.ConvertTabsToSpaces = true;
        CodeEditor.Text = _catalog.ReadSource(program);
        ApplyHighlighting();
        if (program.IsBuiltin
            || string.Equals(program.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase))
        {
            CodeEditor.IsReadOnly = true;
            BtnSave.IsEnabled = false;
            BtnBuild.IsEnabled = false;
            ToolTip.SetTip(BtnSave, Loc.T("Prog.BuiltinSourceReadonly"));
            ToolTip.SetTip(BtnBuild, Loc.T("Prog.BuiltinSourceReadonly"));
        }
        // Ctrl+F / Ctrl+H：内置查找/替换面板（对齐 MAUI Monaco）
        _searchPanel = SearchPanel.Install(CodeEditor);
        _ = new EditorCompletion(CodeEditor, () => _program.Language);
        _bpMargin = new BreakpointMargin(() => _program.Breakpoints, ToggleBreakpoint);
        CodeEditor.TextArea.LeftMargins.Insert(0, _bpMargin);
        _debugLineRenderer.DarkTheme = DesktopThemeService.IsDark(DesktopThemeService.Current);
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_debugLineRenderer);
        CodeEditor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            _caretLine = CodeEditor.TextArea.Caret.Line;
            RefreshBpHint();
        };
        CodeEditor.TextChanged += (_, _) => RefreshBpHint();
        RefreshBpHint();
        RefreshOutput();
        Opened += (_, _) => UpdateToolMode();
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
        StopDebugPoll();
        DetachDebugGate();
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
        OutputText?.Inlines?.Clear();
        if (OutputEmptyText != null)
            OutputEmptyText.IsVisible = true;
    }

    private void ApplyOutputExpanded(bool expanded)
    {
        _outputExpanded = expanded;
        if (OutputBody != null)
            OutputBody.IsVisible = expanded;
        if (OutputToggleGlyph != null)
            OutputToggleGlyph.Text = expanded ? "▼" : "▲";
        if (OutputDock != null)
            ToolTip.SetTip(OutputDock, expanded ? Loc.T("Prog.OutputHide") : Loc.T("Prog.OutputShow"));
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
        var inlines = OutputText?.Inlines;
        if (inlines == null || OutputEmptyText == null) return;
        var items = AppLog.Snapshot(false, _program.Id, _program.Name)
            .Where(e => e.Time >= _outputSince)
            .ToList();
        var chunk = AppLog.FormatNewLines(items, ref _outputLastWritten);
        if (chunk != null)
            inlines.Add(new Run(chunk));
        OutputEmptyText.IsVisible = inlines.Count == 0;
        if (chunk != null && _outputExpanded)
            ScrollOutputToEnd();
    }

    private void ScrollOutputToEnd()
    {
        var inlines = OutputText?.Inlines;
        if (!_outputExpanded || inlines == null || OutputScroll == null || inlines.Count == 0)
            return;
        OutputScroll.ScrollToEnd();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_outputExpanded || OutputScroll == null) return;
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
        BtnFindText.IsVisible = !compact;
        BtnBuildText.IsVisible = !compact;
        BtnDebugText.IsVisible = !compact;
        BtnRunText.IsVisible = !compact;
        BtnStopText.IsVisible = !compact;
        var pad = compact ? new Thickness(8, 0) : new Thickness(10, 0);
        BtnSave.Padding = pad;
        BtnFind.Padding = pad;
        BtnBuild.Padding = pad;
        BtnDebug.Padding = pad;
        BtnRun.Padding = pad;
        BtnStop.Padding = pad;
    }

    private void Find_Click(object? sender, RoutedEventArgs e)
    {
        CodeEditor.Focus();
        _searchPanel?.Open();
    }

    private void RefreshBpHint()
    {
        // 断点已在左侧 gutter 可见，工具条只显示行数状态，避免挤占按钮
        BpHintText.Text = ProgramEditorLimits.StatusFragment(_caretLine, CodeEditor.Text);
        BpHintText.Foreground = ProgramEditorLimits.ExceedsLimit(CodeEditor.Text, out _)
            ? Brushes.DarkOrange
            : TryBrush("Muted") ?? Brushes.Gray;
    }

    private static IBrush? TryBrush(string key)
    {
        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var r) == true
            && r is IBrush b)
            return b;
        return null;
    }

    private async Task<bool> EnsureWithinLineLimitAsync()
    {
        var text = CodeEditor.Text ?? "";
        if (!ProgramEditorLimits.ExceedsLimit(text, out var n)) return true;
        RefreshBpHint();
        await ShowAlertAsync(ProgramEditorLimits.ExceedMessage(n));
        return false;
    }

    /// <summary>按语言自动格式化。</summary>
    private bool ApplyAutoFormat()
    {
        var current = CodeEditor.Text ?? "";
        var formatted = ProgramEditorFormatter.Format(current, _program.Language);
        if (string.Equals(current, formatted, StringComparison.Ordinal)) return false;
        var caret = CodeEditor.CaretOffset;
        CodeEditor.Text = formatted;
        CodeEditor.CaretOffset = Math.Clamp(caret, 0, formatted.Length);
        RefreshBpHint();
        return true;
    }

    private async Task ShowAlertAsync(string message)
    {
        var win = new Window
        {
            Title = Loc.T("Prog.EditorTitle"),
            Width = 440,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new Button
                    {
                        Content = "OK", Width = 88, Height = 32,
                        HorizontalAlignment = HorizontalAlignment.Right
                    }
                }
            }
        };
        if (win.Content is StackPanel sp && sp.Children[^1] is Button ok)
            ok.Click += (_, _) => win.Close();
        await win.ShowDialog(this);
    }

    private void ToggleBreakpoint(int line)
    {
        if (_program.Breakpoints.Contains(line))
            _program.Breakpoints.Remove(line);
        else
        {
            _program.Breakpoints.Add(line);
            _program.Breakpoints.Sort();
        }
        _catalog.SaveMeta(_program);
        (_debugGate ?? ProgramDebugGate.Current)?.SetBreakpoints(_program.Breakpoints);
        _caretLine = line;
        _bpMargin.InvalidateVisual();
        RefreshBpHint();
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Save_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        // Ctrl+F：打开查找
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            Find_Click(sender, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Shift | KeyModifiers.Alt))
        {
            ApplyAutoFormat();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F9)
        {
            ToggleBreakpoint(_caretLine);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F10)
        {
            TryStep(over: true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11)
        {
            TryStep(over: false);
            e.Handled = true;
        }
    }

    private void TryStep(bool over)
    {
        var gate = _debugGate ?? ProgramDebugGate.Current;
        if (gate == null || !_runner.IsRunning(_program.Id)) return;
        if (gate.Phase != ProgramDebugPhase.Paused) return;
        if (over) gate.StepOver();
        else gate.StepInto();
        RefreshDebugUi();
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        ApplyAutoFormat();
        if (!await EnsureWithinLineLimitAsync()) return;
        _catalog.WriteSource(_program, CodeEditor.Text ?? "");
        _catalog.SaveMeta(_program);
        AppLog.Info($"已保存程序「{_program.Name}」");
    }

    private async void Build_Click(object? sender, RoutedEventArgs e)
        => await TryBuildAsync();

    private async Task<bool> TryBuildAsync()
    {
        ApplyAutoFormat();
        if (!await EnsureWithinLineLimitAsync()) return false;
        EnsureOutputVisible();
        SaveSilent();
        var file = string.IsNullOrWhiteSpace(_program.FileName) ? "main.cs" : _program.FileName;
        var result = ProgramCompiler.Compile(_program.Language, CodeEditor.Text ?? "", file);
        AppLog.Info(result.FormatReport($"—— {Loc.T("Prog.Build")} {_program.Name} ——"), _program.Id, _program.Name);
        RefreshOutput();
        return result.Success;
    }

    private async void Debug_Click(object? sender, RoutedEventArgs e)
    {
        var gate = ProgramDebugGate.Current;
        if (gate != null && _runner.IsRunning(_program.Id))
        {
            if (gate.Phase == ProgramDebugPhase.Paused)
                gate.Continue();
            else if (gate.Phase == ProgramDebugPhase.Running)
                gate.RequestPause();
            RefreshDebugUi();
            return;
        }

        ApplyAutoFormat();
        if (!await EnsureWithinLineLimitAsync()) return;
        EnsureOutputVisible();
        var original = CodeEditor.Text ?? "";
        SaveSilent();

        if (_program.Language == ProgramLanguage.CSharp && !await TryBuildAsync())
            return;

        if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
        {
            var injected = PythonBreakpointInjector.Inject(original, _program.Breakpoints);
            _catalog.WriteSource(_program, injected);
            AppLog.Info($"已为「{_program.Name}」注入 {PythonBreakpointInjector.CountInjected(_program.Breakpoints, original)} 处 Python breakpoint()", _program.Id, _program.Name);
        }
        try
        {
            await _runner.StartAsync(_program, debug: true);
            AttachDebugGate(ProgramDebugGate.Current);
            StartDebugPoll();
            RefreshDebugUi();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "调试启动失败", _program.Id, _program.Name);
            DetachDebugGate();
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

    private void AttachDebugGate(ProgramDebugGate? gate)
    {
        if (_debugGate != null)
            _debugGate.StateChanged -= OnDebugGateChanged;
        _debugGate = gate;
        _lastPausedLineLogged = null;
        if (_debugGate != null)
            _debugGate.StateChanged += OnDebugGateChanged;
    }

    private void DetachDebugGate()
    {
        if (_debugGate != null)
            _debugGate.StateChanged -= OnDebugGateChanged;
        _debugGate = null;
        _lastPausedLineLogged = null;
        RefreshDebugUi();
    }

    private void OnDebugGateChanged()
        => Dispatcher.UIThread.Post(RefreshDebugUi);

    private void StartDebugPoll()
    {
        StopDebugPoll();
        _debugPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _debugPollTimer.Tick += (_, _) =>
        {
            if (!_runner.IsRunning(_program.Id))
            {
                StopDebugPoll();
                DetachDebugGate();
                return;
            }
            RefreshDebugUi();
        };
        _debugPollTimer.Start();
    }

    private void StopDebugPoll()
    {
        if (_debugPollTimer == null) return;
        _debugPollTimer.Stop();
        _debugPollTimer = null;
    }

    private void RefreshDebugUi()
    {
        var gate = _debugGate ?? ProgramDebugGate.Current;
        var running = _runner.IsRunning(_program.Id);
        _debugLineRenderer.DarkTheme = DesktopThemeService.IsDark(DesktopThemeService.Current);

        if (!running || gate == null || gate.Phase == ProgramDebugPhase.Idle)
        {
            SetDebugTool(Loc.T("Prog.Debug"), "GeoPlay");
            SetDebugLineHighlight(null);
            return;
        }

        if (gate.Phase == ProgramDebugPhase.Paused)
        {
            SetDebugTool(Loc.T("Prog.Continue"), "GeoPlay");
            var line = gate.PausedLine;
            SetDebugLineHighlight(line is > 0 ? line : null);
            if (line is > 0)
            {
                try
                {
                    CodeEditor.ScrollToLine(line.Value);
                    CodeEditor.TextArea.Caret.Line = line.Value;
                    CodeEditor.TextArea.Caret.Column = 1;
                }
                catch { /* ignore */ }

                if (_lastPausedLineLogged != line)
                {
                    _lastPausedLineLogged = line;
                    var locals = gate.Locals;
                    var sb = new System.Text.StringBuilder();
                    sb.Append(Loc.T("Prog.DebugPaused", line.Value));
                    if (locals.Count > 0)
                    {
                        sb.Append("  ").Append(Loc.T("Prog.DebugLocals"));
                        foreach (var kv in locals)
                            sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value).Append(';');
                    }
                    AppLog.Info(sb.ToString(), _program.Id, _program.Name);
                    RefreshOutput();
                }
            }
            return;
        }

        _lastPausedLineLogged = null;
        SetDebugLineHighlight(null);
        SetDebugTool(Loc.T("Prog.Pause"), "GeoPause");
    }

    private void SetDebugLineHighlight(int? line)
    {
        if (_debugLineRenderer.Line == line) return;
        _debugLineRenderer.Line = line;
        _bpMargin.SetCurrentLine(line);
        CodeEditor.TextArea.TextView.InvalidateLayer(AvaloniaEdit.Rendering.KnownLayer.Background);
    }

    private void SetDebugTool(string label, string geoKey)
    {
        BtnDebugText.Text = label;
        ToolTip.SetTip(BtnDebug, label + " (F9 断点 · F10 逐过程 · F11 逐语句)");
        if (Resources.TryGetResource(geoKey, null, out var r) && r is Geometry g)
            BtnDebugIcon.Data = g;
    }

    private async void Run_Click(object? sender, RoutedEventArgs e)
    {
        ApplyAutoFormat();
        if (!await EnsureWithinLineLimitAsync()) return;
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
        StopDebugPoll();
        DetachDebugGate();
        RefreshOutput();
    }

    private void SaveSilent()
    {
        ApplyAutoFormat();
        if (!ProgramEditorLimits.IsWithinLimit(CodeEditor.Text)) return;
        _catalog.WriteSource(_program, CodeEditor.Text ?? "");
        _catalog.SaveMeta(_program);
    }
}
