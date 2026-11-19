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
using ICSharpCode.AvalonEdit.Search;

namespace FlyScreen;

public partial class ProgramEditorWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private readonly BreakpointMargin _bpMargin;
    private readonly DebugCurrentLineRenderer _debugLineRenderer = new();
    private SearchPanel? _searchPanel;
    private int _caretLine = 1;
    private bool _toolModeUpdating;
    private bool _outputExpanded;
    private bool _outputRefreshPending;
    private bool _filePickerUpdating;
    private string _currentRelativePath = "";
    private string _baselineText = "";
    private DateTime _outputSince = DateTime.MinValue;
    private AppLogEntry? _outputLastWritten;
    private ProgramDebugGate? _debugGate;
    private int? _lastPausedLineLogged;
    private System.Windows.Threading.DispatcherTimer? _debugPollTimer;

    public static readonly DependencyProperty ToolsCompactProperty =
        DependencyProperty.Register(nameof(ToolsCompact), typeof(bool), typeof(ProgramEditorWindow), new PropertyMetadata(false));

    public static readonly RoutedUICommand FormatCommand =
        new("Format", nameof(FormatCommand), typeof(ProgramEditorWindow));

    public static readonly RoutedUICommand ToggleBreakpointCommand =
        new("ToggleBreakpoint", nameof(ToggleBreakpointCommand), typeof(ProgramEditorWindow));

    public static readonly RoutedUICommand StepOverCommand =
        new("StepOver", nameof(StepOverCommand), typeof(ProgramEditorWindow));

    public static readonly RoutedUICommand StepIntoCommand =
        new("StepInto", nameof(StepIntoCommand), typeof(ProgramEditorWindow));

    public bool ToolsCompact
    {
        get => (bool)GetValue(ToolsCompactProperty);
        set => SetValue(ToolsCompactProperty, value);
    }

    private string BaseTitle =>
        $"{Loc.T("Prog.EditorTitle")} — {_program.Name} · {_program.LanguageText} · {_currentRelativePath}";

    public ProgramEditorWindow(UserProgram program)
    {
        InitializeComponent();
        ThemeService.ApplyToChildWindow(this);
        Loaded += (_, _) => ThemeService.ApplyToChildWindow(this);
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _runner = ProgramService.Instance.Runner;
        _catalog.NormalizeManifest(_program);
        _currentRelativePath = _program.FileName;

        Title = BaseTitle;
        BtnAddFile.ToolTip = Loc.T("Prog.NewFile");
        SetToolButton(BtnSave, BtnSaveText, "Prog.Save");
        BtnDeps.ToolTip = Loc.T("Prog.DepsTitle");
        BtnDeps.Visibility = _program.Language == ProgramLanguage.CSharp
            ? Visibility.Visible : Visibility.Collapsed;
        BtnFlySettings.ToolTip = Loc.T("Prog.FlySettingsTitle");
        SetToolButton(BtnFind, BtnFindText, "Prog.Find");
        SetToolButton(BtnBuild, BtnBuildText, "Prog.Build");
        SetToolButton(BtnDebug, BtnDebugText, "Prog.Debug");
        SetToolButton(BtnRun, BtnRunText, "Prog.Run");
        SetToolButton(BtnStop, BtnStopText, "Prog.StopRun");
        BtnFileSwitch.ToolTip = Loc.T("Prog.SwitchFile");
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
            BtnFileSwitch.IsEnabled = true; // 只读仍可切换查看
            BtnSave.ToolTip = Loc.T("Prog.BuiltinSourceReadonly");
            BtnBuild.ToolTip = Loc.T("Prog.BuiltinSourceReadonly");
        }
        // Ctrl+F / Ctrl+H：内置查找/替换面板（对齐 MAUI Monaco）
        _searchPanel = SearchPanel.Install(CodeEditor);
        _ = new EditorCompletion(CodeEditor, () => _program.Language);
        _bpMargin = new BreakpointMargin(() => _program.Breakpoints, ToggleBreakpoint);
        CodeEditor.TextArea.LeftMargins.Insert(0, _bpMargin);
        _debugLineRenderer.DarkTheme = ThemeService.IsDark(ThemeService.Current);
        CodeEditor.TextArea.TextView.BackgroundRenderers.Add(_debugLineRenderer);
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
        DetachDebugGate();
        StopDebugPoll();
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
            // 先尝试完整文案单行；放不下则藏文案，仍放不下由 WrapPanel 换两行
            ToolsCompact = false;
            ToolBar.Measure(new System.Windows.Size(double.PositiveInfinity, 48));
            if (ToolBar.DesiredSize.Width > ToolBarHost.ActualWidth + 0.5)
                ToolsCompact = true;
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
        CodeEditor.Document = new ICSharpCode.AvalonEdit.Document.TextDocument(next);
        CodeEditor.CaretOffset = 0;
        CodeEditor.ScrollToHome();
        _baselineText = next;
        ApplyHighlighting();
        RefreshBpHint();
    }

    /// <summary>按当前文件类型自动格式化；返回是否改动了文本。</summary>
    private bool ApplyAutoFormat(bool toast = false)
    {
        var current = CodeEditor.Text ?? "";
        string formatted;
        if (ProgramCatalog.IsConfigSchemaFile(_currentRelativePath))
            formatted = ProgramFlySettingsStore.NormalizeSchemaText(current);
        else
            formatted = ProgramEditorFormatter.Format(current, _program.Language);
        if (string.Equals(current, formatted, StringComparison.Ordinal)) return false;
        var caret = CodeEditor.CaretOffset;
        CodeEditor.Document.Text = formatted;
        CodeEditor.CaretOffset = Math.Clamp(caret, 0, formatted.Length);
        RefreshBpHint();
        if (toast) StatusTextToast(Loc.T("Prog.Formatted"));
        return true;
    }

    /// <returns>true=可继续切换；false=用户取消。</returns>
    private bool PromptSaveIfDirty()
    {
        if (!IsDirty) return true;
        var r = MessageBox.Show(this, Loc.T("Prog.UnsavedChanges"), Title,
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes)
        {
            try { SaveSilent(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        return true;
    }

    private void Format_Executed(object sender, ExecutedRoutedEventArgs e)
        => ApplyAutoFormat(toast: true);

    private void Save_Executed(object sender, ExecutedRoutedEventArgs e)
        => Save_Click(sender, e);

    private void Find_Executed(object sender, ExecutedRoutedEventArgs e)
        => Find_Click(sender, e);

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        CodeEditor.Focus();
        _searchPanel?.Open();
    }

    private void ToggleBreakpoint_Executed(object sender, ExecutedRoutedEventArgs e)
        => ToggleBreakpoint(_caretLine);

    private void StepOver_Executed(object sender, ExecutedRoutedEventArgs e)
        => TryStep(over: true);

    private void StepInto_Executed(object sender, ExecutedRoutedEventArgs e)
        => TryStep(over: false);

    private void TryStep(bool over)
    {
        var gate = _debugGate ?? ProgramDebugGate.Current;
        if (gate == null || !_runner.IsRunning(_program.Id)) return;
        if (gate.Phase != ProgramDebugPhase.Paused) return;
        if (over) gate.StepOver();
        else gate.StepInto();
        RefreshDebugUi();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ApplyAutoFormat();
        if (!EnsureWithinLineLimit()) return;
        SaveSilent();
        AppLog.Info($"已保存程序「{_program.Name}」", _program.Id, _program.Name);
        StatusTextToast("已保存");
    }

    private async void Build_Click(object sender, RoutedEventArgs e)
        => await TryBuildAsync(showToast: true);

    private async Task<bool> TryBuildAsync(bool showToast)
    {
        ApplyAutoFormat();
        if (!EnsureWithinLineLimit()) return false;
        EnsureOutputVisible();
        SaveSilent();
        var entryOverride = string.Equals(_currentRelativePath, _program.FileName, StringComparison.OrdinalIgnoreCase)
            ? CodeEditor.Text
            : null;
        var result = await ProgramCompiler.CompileProgramAsync(_catalog, _program, entryOverride)
            .ConfigureAwait(true);
        AppLog.Info(result.FormatReport($"—— {Loc.T("Prog.Build")} {_program.Name} ——"), _program.Id, _program.Name);
        RefreshOutput();
        if (showToast)
            StatusTextToast(result.Success ? Loc.T("Prog.BuildOk") : Loc.T("Prog.BuildFail"));
        return result.Success;
    }

    private void RefreshFilePicker()
    {
        FileSwitchLabel.Text = ProgramEditorFileBadge.DisplayName(_currentRelativePath);
        FileSwitchList.Children.Clear();
        foreach (var f in _catalog.ListEditorFiles(_program))
            FileSwitchList.Children.Add(BuildFileSwitchRow(f));
        Title = BaseTitle;
    }

    private FrameworkElement BuildFileSwitchRow(string relativePath)
    {
        var isCurrent = string.Equals(relativePath, _currentRelativePath, StringComparison.OrdinalIgnoreCase);
        var isEntry = string.Equals(relativePath, _program.FileName, StringComparison.OrdinalIgnoreCase);
        var canDelete = !isEntry && !CodeEditor.IsReadOnly
                        && !ProgramCatalog.IsConfigSchemaFile(relativePath); // schema 用「配置」维护，列表不提供删

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new System.Windows.Thickness(8, 6, 4, 6),
            Margin = new System.Windows.Thickness(0, 1, 0, 1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = isCurrent
                ? (System.Windows.Media.Brush)FindResource("BlueSoft")
                : System.Windows.Media.Brushes.Transparent,
            Tag = relativePath
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new TextBlock
        {
            Text = ProgramEditorFileBadge.ForPath(relativePath),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("Blue"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MinWidth = 22
        };
        var name = new TextBlock
        {
            Text = ProgramEditorFileBadge.DisplayName(relativePath),
            FontSize = 12,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);

        var del = new System.Windows.Controls.Button
        {
            Content = "×",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            FontSize = 14,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (System.Windows.Media.Brush)FindResource("Muted"),
            Visibility = canDelete ? Visibility.Visible : Visibility.Hidden,
            Tag = relativePath,
            ToolTip = Loc.T("Prog.RemoveFile")
        };
        Grid.SetColumn(del, 2);
        del.Click += (_, e) =>
        {
            e.Handled = true;
            FileSwitchPopup.IsOpen = false;
            RemoveFileAt(relativePath);
        };

        row.MouseEnter += (_, _) =>
        {
            if (!isCurrent)
                row.Background = (System.Windows.Media.Brush)FindResource("SubtleBg");
            if (canDelete) del.Visibility = Visibility.Visible;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = isCurrent
                ? (System.Windows.Media.Brush)FindResource("BlueSoft")
                : System.Windows.Media.Brushes.Transparent;
            if (canDelete && !isCurrent) del.Visibility = Visibility.Hidden;
        };
        // PreviewDown：避免 Popup StaysOpen=False 在 Up 前关闭导致切文件丢失
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.Handled) return;
            // 点删除按钮时不切换文件
            for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (ReferenceEquals(d, del)) return;
            }
            e.Handled = true;
            FileSwitchPopup.IsOpen = false;
            SwitchToFile(relativePath);
        };

        // 当前项始终显示 X（可删时）；其它项悬停才显示
        if (canDelete && isCurrent) del.Visibility = Visibility.Visible;
        else if (canDelete) del.Visibility = Visibility.Hidden;

        grid.Children.Add(badge);
        grid.Children.Add(name);
        grid.Children.Add(del);
        row.Child = grid;
        return row;
    }

    private void FileSwitch_Click(object sender, RoutedEventArgs e)
    {
        RefreshFilePicker();
        FileSwitchPopup.PlacementTarget = BtnFileSwitch;
        FileSwitchPopup.IsOpen = true;
    }

    private void SwitchToFile(string next)
    {
        if (string.IsNullOrWhiteSpace(next)) return;
        var same = string.Equals(next, _currentRelativePath, StringComparison.OrdinalIgnoreCase);
        if (!same && !PromptSaveIfDirty()) return;
        _currentRelativePath = next;
        // 同路径也强制重载，避免徽标已切、缓冲仍是旧文件内容
        LoadEditorText(_catalog.ReadFile(_program, _currentRelativePath));
        RefreshFilePicker();
    }

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        if (CodeEditor.IsReadOnly) return;
        var ext = ProgramLanguageInfo.Extension(_program.Language);
        var suggested = SuggestExtraPath(ext);
        var path = PromptText(Loc.T("Prog.NewFile"), Loc.T("Prog.AddFilePrompt"), suggested);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!PromptSaveIfDirty()) return;
        try
        {
            var rel = _catalog.AddFile(_program, path.Trim());
            _currentRelativePath = rel;
            LoadEditorText(_catalog.ReadFile(_program, rel));
            RefreshFilePicker();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveFileAt(string relativePath)
    {
        if (CodeEditor.IsReadOnly) return;
        if (string.Equals(relativePath, _program.FileName, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, Loc.T("Prog.CannotRemoveEntry"), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, Loc.T("Prog.RemoveFile") + "?\n" + relativePath, Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
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
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Deps_Click(object sender, RoutedEventArgs e)
    {
        if (_program.Language != ProgramLanguage.CSharp) return;
        ShowDepsDialog();
    }

    private void FlySettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new ProgramFlySettingsDesignerWindow(_program, owner: this);
        win.ShowDialog();
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
        return $"lib/Module{Guid.NewGuid():N}{ext}"[..20] + ext;
    }

    private string? PromptText(string title, string label, string initial)
    {
        var dlg = new Window
        {
            Title = title,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 420,
            Height = 160,
            ResizeMode = ResizeMode.NoResize,
            Background = Background,
            Foreground = Foreground
        };
        var root = new DockPanel { Margin = new System.Windows.Thickness(12) };
        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new System.Windows.Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var ok = new System.Windows.Controls.Button
        {
            Content = "OK", Width = 72, Height = 28,
            Margin = new System.Windows.Thickness(0, 0, 8, 0), IsDefault = true
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "Cancel", Width = 72, Height = 28, IsCancel = true
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var tb = new System.Windows.Controls.TextBox
        {
            Text = initial, Margin = new System.Windows.Thickness(0, 8, 0, 0)
        };
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(tb);
        root.Children.Add(buttons);
        root.Children.Add(body);
        dlg.Content = root;
        string? result = null;
        ok.Click += (_, _) => { result = tb.Text; dlg.DialogResult = true; };
        cancel.Click += (_, _) => { dlg.DialogResult = false; };
        return dlg.ShowDialog() == true ? result : null;
    }

    private void ShowDepsDialog()
    {
        _catalog.NormalizeManifest(_program);
        var dlg = new Window
        {
            Title = Loc.T("Prog.DepsTitle"),
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 480,
            Height = 360,
            Background = Background,
            Foreground = Foreground
        };
        var list = new System.Windows.Controls.ListBox { Margin = new System.Windows.Thickness(12, 12, 12, 0) };
        void Reload()
        {
            list.Items.Clear();
            foreach (var p in _program.Packages)
                list.Items.Add($"NuGet: {p.Id} {(string.IsNullOrWhiteSpace(p.Version) ? "" : "(" + p.Version + ")")}");
            foreach (var d in _program.LocalDlls)
                list.Items.Add($"DLL: {d}");
        }
        Reload();
        var bar = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new System.Windows.Thickness(12),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        var addNuget = new System.Windows.Controls.Button
        {
            Content = Loc.T("Prog.AddNuget"), Height = 28,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Padding = new System.Windows.Thickness(10, 0, 10, 0)
        };
        var addDll = new System.Windows.Controls.Button
        {
            Content = Loc.T("Prog.AddDll"), Height = 28,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Padding = new System.Windows.Thickness(10, 0, 10, 0)
        };
        var remove = new System.Windows.Controls.Button
        {
            Content = Loc.T("Prog.RemoveDep"), Height = 28,
            Padding = new System.Windows.Thickness(10, 0, 10, 0)
        };
        bar.Children.Add(addNuget);
        bar.Children.Add(addDll);
        bar.Children.Add(remove);
        addNuget.Click += (_, _) =>
        {
            var id = PromptText(Loc.T("Prog.AddNuget"), Loc.T("Prog.NugetId"), "");
            if (string.IsNullOrWhiteSpace(id)) return;
            var ver = PromptText(Loc.T("Prog.AddNuget"), Loc.T("Prog.NugetVersion"), "") ?? "";
            _program.Packages.RemoveAll(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            _program.Packages.Add(new NugetPackageRef { Id = id.Trim(), Version = ver.Trim() });
            ProgramNugetResolver.SyncCsproj(_catalog, _program);
            _catalog.SaveMeta(_program);
            Reload();
        };
        addDll.Click += (_, _) =>
        {
            var ofd = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "DLL (*.dll)|*.dll",
                CheckFileExists = true
            };
            if (ofd.ShowDialog(this) != true) return;
            try
            {
                _catalog.AddLocalDll(_program, ofd.FileName);
                Reload();
            }
            catch (Exception ex)
            {
                MessageBox.Show(dlg, ex.Message, dlg.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        remove.Click += (_, _) =>
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
                    catch (Exception ex)
                    {
                        MessageBox.Show(dlg, ex.Message, dlg.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            Reload();
        };
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);
        root.Children.Add(list);
        dlg.Content = root;
        dlg.ShowDialog();
    }

    private async void Debug_Click(object sender, RoutedEventArgs e)
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
        if (!EnsureWithinLineLimit()) return;
        EnsureOutputVisible();
        var original = CodeEditor.Text ?? "";
        SaveSilent();

        if (_program.Language == ProgramLanguage.CSharp && !await TryBuildAsync(showToast: false))
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
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
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
        => Dispatcher.BeginInvoke(RefreshDebugUi);

    private void StartDebugPoll()
    {
        StopDebugPoll();
        _debugPollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
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
        _debugLineRenderer.DarkTheme = ThemeService.IsDark(ThemeService.Current);

        if (!running || gate == null || gate.Phase == ProgramDebugPhase.Idle)
        {
            SetDebugTool(Loc.T("Prog.Debug"), "GeoPlay");
            SetDebugLineHighlight(null);
            return;
        }

        if (gate.Phase == ProgramDebugPhase.Paused)
        {
            // 暂停中：按钮变为「继续」（VS 语义）；黄色行背景表示当前停在何处
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
                    CodeEditor.TextArea.Caret.BringCaretToView();
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
        CodeEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
    }

    private void SetDebugTool(string label, string geoKey)
    {
        BtnDebugText.Text = label;
        BtnDebug.ToolTip = label + " (F5/调试钮 · F10 逐过程 · F11 逐语句 · F9 断点)";
        if (TryFindResource(geoKey) is System.Windows.Media.Geometry g)
            BtnDebugIcon.Data = g;
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
        (_debugGate ?? ProgramDebugGate.Current)?.SetBreakpoints(_program.Breakpoints);
        _bpMargin.InvalidateVisual();
        RefreshBpHint();
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

    private void StatusTextToast(string msg)
    {
        Title = BaseTitle + "  ·  " + msg;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => { Title = BaseTitle; timer.Stop(); };
        timer.Start();
    }
}
