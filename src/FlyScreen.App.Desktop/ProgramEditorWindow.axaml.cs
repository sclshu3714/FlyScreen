using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    private bool _filePickerUpdating;
    private string _currentRelativePath = "";
    private string _baselineText = "";
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
        _catalog.NormalizeManifest(_program);
        _currentRelativePath = _program.FileName;
        InitializeComponent();
        DesktopThemeService.ApplyToChildWindow(this);

        var displayTitle = $"{Loc.T("Prog.EditorTitle")} — {program.Name} · {program.LanguageText} · {_currentRelativePath}";
        _chrome = LinuxClientChrome.Attach(
            this, ClientTitleBar, ClientTitleText, displayTitle,
            TitleMinBtn, TitleMaxBtn, TitleCloseBtn, allowMaximize: true);

        if (Application.Current?.Resources.TryGetResource("UiFont", null, out var font) == true
            && font is FontFamily ff)
            FontFamily = ff;

        ToolTip.SetTip(BtnAddFile, Loc.T("Prog.NewFile"));
        SetTool(BtnSave, BtnSaveText, "Prog.Save");
        ToolTip.SetTip(BtnDeps, Loc.T("Prog.DepsTitle"));
        BtnDeps.IsVisible = _program.Language == ProgramLanguage.CSharp;
        ToolTip.SetTip(BtnFlySettings, Loc.T("Prog.FlySettingsTitle"));
        SetTool(BtnFind, BtnFindText, "Prog.Find");
        SetTool(BtnBuild, BtnBuildText, "Prog.Build");
        SetTool(BtnDebug, BtnDebugText, "Prog.Debug");
        SetTool(BtnRun, BtnRunText, "Prog.Run");
        SetTool(BtnStop, BtnStopText, "Prog.StopRun");
        ToolTip.SetTip(BtnFileSwitch, Loc.T("Prog.SwitchFile"));
        OutputTitleText.Text = Loc.T("Prog.Output");
        OutputClearBtn.Content = Loc.T("Prog.OutputClear");
        OutputEmptyText.Text = Loc.T("Prog.OutputEmpty");
        ApplyOutputExpanded(false);
        RefreshDebugUi();
        RefreshFilePicker();

        CodeEditor.Options.EnableHyperlinks = false;
        CodeEditor.Options.ConvertTabsToSpaces = true;
        FileSwitchPopup.PlacementTarget = BtnFileSwitch;
        LoadEditorText(_catalog.ReadFile(program, _currentRelativePath));
        if (program.IsBuiltin
            || string.Equals(program.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase))
        {
            CodeEditor.IsReadOnly = true;
            BtnSave.IsEnabled = false;
            BtnBuild.IsEnabled = false;
            BtnAddFile.IsEnabled = false;
            BtnDeps.IsEnabled = false;
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
        var name = ProgramEditorLanguageMap.AvalonHighlightingNameForPath(_currentRelativePath, _program.Language);
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

    private bool IsDirty
        => !CodeEditor.IsReadOnly
           && !string.Equals(
               ProgramCatalog.NormalizeSourceText(CodeEditor.Text ?? ""),
               ProgramCatalog.NormalizeSourceText(_baselineText),
               StringComparison.Ordinal);

    private void LoadEditorText(string text)
    {
        var next = text ?? "";
        // 换 Document 比只改 Text 更能保证视图立刻刷新
        CodeEditor.Document = new AvaloniaEdit.Document.TextDocument(next);
        CodeEditor.CaretOffset = 0;
        try { CodeEditor.ScrollToHome(); } catch { /* ignore */ }
        _baselineText = next;
        ApplyHighlighting();
        RefreshBpHint();
    }

    /// <summary>按当前文件类型自动格式化。</summary>
    private bool ApplyAutoFormat()
    {
        var current = CodeEditor.Text ?? "";
        string formatted;
        if (ProgramCatalog.IsConfigSchemaFile(_currentRelativePath))
            formatted = ProgramFlySettingsStore.NormalizeSchemaText(current);
        else
            formatted = ProgramEditorFormatter.Format(current, _program.Language);
        if (string.Equals(current, formatted, StringComparison.Ordinal)) return false;
        var caret = CodeEditor.CaretOffset;
        CodeEditor.Text = formatted;
        CodeEditor.CaretOffset = Math.Clamp(caret, 0, formatted.Length);
        RefreshBpHint();
        return true;
    }

    /// <returns>true=可继续；false=取消。</returns>
    private async Task<bool> PromptSaveIfDirtyAsync()
    {
        if (!IsDirty) return true;
        var r = await PromptYesNoCancelAsync(Loc.T("Prog.UnsavedChanges"));
        if (r == null) return false;
        if (r == true)
        {
            try { SaveSilent(); }
            catch (Exception ex)
            {
                await ShowAlertAsync(ex.Message);
                return false;
            }
        }
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

    private async Task<bool> ConfirmAsync(string message)
    {
        var r = await PromptYesNoCancelAsync(message, includeCancel: false);
        return r == true;
    }

    /// <returns>true=是/保存；false=否/不保存；null=取消。</returns>
    private async Task<bool?> PromptYesNoCancelAsync(string message, bool includeCancel = true)
    {
        var tcs = new TaskCompletionSource<bool?>();
        var yes = new Button { Content = Loc.T("Prog.Save"), Width = 72, Margin = new Thickness(0, 0, 8, 0) };
        var no = new Button { Content = Loc.T("Prog.DontSave"), Width = 88, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = Loc.T("Prog.DlgCancel"), Width = 72, IsVisible = includeCancel };
        if (!includeCancel)
        {
            yes.Content = "Yes";
            no.Content = "No";
            no.Margin = new Thickness(0);
        }
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
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { yes, no, cancel }
                    }
                }
            }
        };
        yes.Click += (_, _) => { tcs.TrySetResult(true); win.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); win.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(null); win.Close(); };
        win.Closed += (_, _) => tcs.TrySetResult(includeCancel ? null : false);
        await win.ShowDialog(this);
        return await tcs.Task;
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
        SaveSilent();
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
        var entryOverride = string.Equals(_currentRelativePath, _program.FileName, StringComparison.OrdinalIgnoreCase)
            ? CodeEditor.Text
            : null;
        var result = await ProgramCompiler.CompileProgramAsync(_catalog, _program, entryOverride);
        AppLog.Info(result.FormatReport($"—— {Loc.T("Prog.Build")} {_program.Name} ——"), _program.Id, _program.Name);
        RefreshOutput();
        return result.Success;
    }

    private void RefreshEditorTitle()
    {
        var t = $"{Loc.T("Prog.EditorTitle")} — {_program.Name} · {_program.LanguageText} · {_currentRelativePath}";
        Title = t;
        _chrome?.SetTitle(t);
    }

    private void RefreshFilePicker()
    {
        FileSwitchLabel.Text = ProgramEditorFileBadge.DisplayName(_currentRelativePath);
        FileSwitchList.Children.Clear();
        foreach (var f in _catalog.ListEditorFiles(_program))
            FileSwitchList.Children.Add(BuildFileSwitchRow(f));
        RefreshEditorTitle();
    }

    private Control BuildFileSwitchRow(string relativePath)
    {
        var isCurrent = string.Equals(relativePath, _currentRelativePath, StringComparison.OrdinalIgnoreCase);
        var isEntry = string.Equals(relativePath, _program.FileName, StringComparison.OrdinalIgnoreCase);
        var canDelete = !isEntry && !CodeEditor.IsReadOnly
                        && !ProgramCatalog.IsConfigSchemaFile(relativePath);

        var del = new Button
        {
            Content = "×",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 14,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            IsVisible = canDelete && isCurrent
        };
        ToolTip.SetTip(del, Loc.T("Prog.RemoveFile"));
        del.Click += async (_, e) =>
        {
            e.Handled = true;
            FileSwitchPopup.IsOpen = false;
            await RemoveFileAtAsync(relativePath);
        };

        IBrush? blueSoft = null;
        IBrush? subtle = null;
        if (Application.Current?.Resources.TryGetResource("BlueSoft", null, out var bs) == true)
            blueSoft = bs as IBrush;
        if (Application.Current?.Resources.TryGetResource("SubtleBg", null, out var sb) == true)
            subtle = sb as IBrush;

        var badge = new TextBlock
        {
            Text = ProgramEditorFileBadge.ForPath(relativePath),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 22
        };
        var name = new TextBlock
        {
            Text = ProgramEditorFileBadge.DisplayName(relativePath),
            FontSize = 12,
            FontWeight = isCurrent ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(del, 2);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(badge);
        grid.Children.Add(name);
        grid.Children.Add(del);

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 4, 6),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Background = isCurrent ? blueSoft : Brushes.Transparent,
            Child = grid
        };
        row.PointerEntered += (_, _) =>
        {
            if (!isCurrent && subtle != null) row.Background = subtle;
            if (canDelete) del.IsVisible = true;
        };
        row.PointerExited += (_, _) =>
        {
            row.Background = isCurrent ? blueSoft : Brushes.Transparent;
            if (canDelete && !isCurrent) del.IsVisible = false;
        };
        // PointerPressed：赶在 IsLightDismiss 关闭 Popup 之前完成切文件
        row.PointerPressed += async (_, e) =>
        {
            if (e.Handled) return;
            // 点删除按钮时不切换文件
            if (e.Source is Visual v)
            {
                for (var cur = v as Visual; cur != null; cur = cur.GetVisualParent())
                {
                    if (ReferenceEquals(cur, del)) return;
                }
            }
            e.Handled = true;
            FileSwitchPopup.IsOpen = false;
            await SwitchToFileAsync(relativePath);
        };
        return row;
    }

    private void FileSwitch_Click(object? sender, RoutedEventArgs e)
    {
        RefreshFilePicker();
        FileSwitchPopup.PlacementTarget = BtnFileSwitch;
        FileSwitchPopup.IsOpen = true;
    }

    private async Task SwitchToFileAsync(string next)
    {
        if (string.IsNullOrWhiteSpace(next)) return;
        var same = string.Equals(next, _currentRelativePath, StringComparison.OrdinalIgnoreCase);
        if (!same && !await PromptSaveIfDirtyAsync()) return;
        _currentRelativePath = next;
        // 同路径也强制重载，避免徽标已切、缓冲仍是旧文件内容
        LoadEditorText(_catalog.ReadFile(_program, _currentRelativePath));
        RefreshFilePicker();
    }

    private async void AddFile_Click(object? sender, RoutedEventArgs e)
    {
        if (CodeEditor.IsReadOnly) return;
        var ext = ProgramLanguageInfo.Extension(_program.Language);
        var suggested = SuggestExtraPath(ext);
        var path = await PromptTextSimpleAsync(Loc.T("Prog.NewFile"), Loc.T("Prog.AddFilePrompt"), suggested);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!await PromptSaveIfDirtyAsync()) return;
        try
        {
            var rel = _catalog.AddFile(_program, path.Trim());
            _currentRelativePath = rel;
            LoadEditorText(_catalog.ReadFile(_program, rel));
            RefreshFilePicker();
        }
        catch (Exception ex) { await ShowAlertAsync(ex.Message); }
    }

    private async Task RemoveFileAtAsync(string relativePath)
    {
        if (CodeEditor.IsReadOnly) return;
        if (string.Equals(relativePath, _program.FileName, StringComparison.OrdinalIgnoreCase))
        {
            await ShowAlertAsync(Loc.T("Prog.CannotRemoveEntry"));
            return;
        }
        if (!await ConfirmAsync(Loc.T("Prog.RemoveFile") + "?\n" + relativePath)) return;
        try
        {
            _catalog.RemoveFile(_program, relativePath);
            if (string.Equals(_currentRelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                _currentRelativePath = _program.FileName;
                LoadEditorText(_catalog.ReadFile(_program, _currentRelativePath));
            }
            RefreshFilePicker();
        }
        catch (Exception ex) { await ShowAlertAsync(ex.Message); }
    }

    private async void Deps_Click(object? sender, RoutedEventArgs e)
    {
        if (_program.Language != ProgramLanguage.CSharp) return;
        await ShowDepsDialogAsync();
    }

    private async void FlySettings_Click(object? sender, RoutedEventArgs e)
    {
        var win = new ProgramFlySettingsDesignerWindow(_program, this);
        await win.ShowDialog(this);
    }

    private string SuggestExtraPath(string ext)
    {
        for (var i = 1; i < 100; i++)
        {
            var rel = $"lib/Module{i}{ext}";
            if (!_catalog.ListProgramFiles(_program)
                    .Any(f => string.Equals(f, rel, StringComparison.OrdinalIgnoreCase)))
                return rel;
        }
        return "lib/Extra" + ext;
    }

    private async Task<string?> PromptTextSimpleAsync(string title, string label, string initial)
    {
        var tb = new TextBox { Text = initial };
        var tcs = new TaskCompletionSource<string?>();
        var dlg = new Window
        {
            Title = title,
            Width = 420,
            Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false
        };
        var okBtn = new Button { Content = "OK", Width = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancelBtn = new Button { Content = "Cancel", Width = 72 };
        okBtn.Click += (_, _) => { tcs.TrySetResult(tb.Text); dlg.Close(); };
        cancelBtn.Click += (_, _) => { tcs.TrySetResult(null); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(null); // OK/Cancel 已 TrySet 时忽略
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 12, 0, 0),
            Children = { okBtn, cancelBtn }
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var body = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, tb } };
        var root = new DockPanel { Margin = new Thickness(12), Children = { buttons, body } };
        dlg.Content = root;
        await dlg.ShowDialog(this);
        return await tcs.Task;
    }

    private async Task ShowDepsDialogAsync()
    {
        _catalog.NormalizeManifest(_program);
        var list = new ListBox { Margin = new Thickness(12, 12, 12, 0) };
        void Reload()
        {
            list.Items.Clear();
            foreach (var p in _program.Packages)
                list.Items.Add($"NuGet: {p.Id} {(string.IsNullOrWhiteSpace(p.Version) ? "" : "(" + p.Version + ")")}");
            foreach (var d in _program.LocalDlls)
                list.Items.Add($"DLL: {d}");
        }
        Reload();
        var dlg = new Window
        {
            Title = Loc.T("Prog.DepsTitle"),
            Width = 480,
            Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var addNuget = new Button { Content = Loc.T("Prog.AddNuget"), Height = 28, Padding = new Thickness(10, 0) };
        var addDll = new Button { Content = Loc.T("Prog.AddDll"), Height = 28, Padding = new Thickness(10, 0) };
        var remove = new Button { Content = Loc.T("Prog.RemoveDep"), Height = 28, Padding = new Thickness(10, 0) };
        addNuget.Click += async (_, _) =>
        {
            var id = await PromptTextSimpleAsync(Loc.T("Prog.AddNuget"), Loc.T("Prog.NugetId"), "");
            if (string.IsNullOrWhiteSpace(id)) return;
            var ver = await PromptTextSimpleAsync(Loc.T("Prog.AddNuget"), Loc.T("Prog.NugetVersion"), "") ?? "";
            _program.Packages.RemoveAll(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            _program.Packages.Add(new NugetPackageRef { Id = id.Trim(), Version = ver.Trim() });
            ProgramNugetResolver.SyncCsproj(_catalog, _program);
            _catalog.SaveMeta(_program);
            Reload();
        };
        addDll.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new Avalonia.Platform.Storage.FilePickerFileType("DLL") { Patterns = ["*.dll"] }
                ]
            });
            var path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                _catalog.AddLocalDll(_program, path);
                Reload();
            }
            catch (Exception ex) { await ShowAlertAsync(ex.Message); }
        };
        remove.Click += async (_, _) =>
        {
            var idx = list.SelectedIndex;
            if (idx < 0) return;
            if (idx < _program.Packages.Count)
            {
                _program.Packages.RemoveAt(idx);
                ProgramNugetResolver.SyncCsproj(_catalog, _program);
                _catalog.SaveMeta(_program);
            }
            else
            {
                var dllIdx = idx - _program.Packages.Count;
                if (dllIdx >= 0 && dllIdx < _program.LocalDlls.Count)
                {
                    try { _catalog.RemoveLocalDll(_program, _program.LocalDlls[dllIdx]); }
                    catch (Exception ex) { await ShowAlertAsync(ex.Message); }
                }
            }
            Reload();
        };
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(12),
            Children = { addNuget, addDll, remove }
        };
        DockPanel.SetDock(bar, Dock.Bottom);
        dlg.Content = new DockPanel { Children = { bar, list } };
        await dlg.ShowDialog(this);
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
        SetDebugTool(Loc.T("Prog.Pause"), "GeoPauseBars");
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
        if (this.TryGetResource(geoKey, ActualThemeVariant, out var r) && r is Geometry g)
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
        _catalog.WriteFile(_program, _currentRelativePath, CodeEditor.Text ?? "");
        _baselineText = CodeEditor.Text ?? "";
    }
}
