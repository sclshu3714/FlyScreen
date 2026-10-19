using System.Collections.ObjectModel;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Programs;
using Microsoft.Maui.Controls.Shapes;

namespace FlyScreen.AppMaui;

public partial class ProgramEditorPage : ContentPage
{
    private const double LineRowHeight = 20; // 与 Consolas 13 行高大致匹配

    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private readonly TaskCompletionSource _closed = new();
    private int _caretLine = 1;
    private bool _toolsCompact;
    private string _closeLabel = "关闭";
    private string _saveLabel = "保存";
    private string _debugLabel = "调试";
    private string _clearBpLabel = "清除断点";
    private string _runLabel = "运行";
    private string _stopLabel = "停止";
    private readonly ObservableCollection<AppLogEntry> _outputItems = [];
    private bool _outputExpanded;
    private bool _outputRefreshPending;
    private bool _outputScrollPending;
    private DateTime _outputSince = DateTime.MinValue;

    private const string IcClose = "←";
    private const string IcSave = "↓";
    private const string IcDebug = "※";
    private const string IcClear = "✕";
    private const string IcRun = "▶";
    private const string IcStop = "■";

    public Task WhenClosed => _closed.Task;

    public ProgramEditorPage(UserProgram program)
    {
        InitializeComponent();
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _runner = ProgramService.Instance.Runner;

        Title = $"{Loc.T("Prog.EditorTitle")} — {program.Name} · {program.LanguageText} · {program.FileName}";
        _closeLabel = Loc.T("Prog.DlgCancel") == "取消" ? "关闭" : "Close";
        _saveLabel = Loc.T("Prog.Save");
        _debugLabel = Loc.T("Prog.Debug");
        _clearBpLabel = Loc.T("Prog.ClearAllBp");
        _runLabel = Loc.T("Prog.Run");
        _stopLabel = Loc.T("Prog.StopRun");
        ApplyToolLabels(compact: false);
        ToolTipProperties.SetText(BtnClose, "关闭并返回");
        ToolTipProperties.SetText(BtnSave, _saveLabel);
        ToolTipProperties.SetText(BtnDebug, _debugLabel);
        ToolTipProperties.SetText(BtnClearAllBp, _clearBpLabel);
        ToolTipProperties.SetText(BtnRun, _runLabel);
        ToolTipProperties.SetText(BtnStop, _stopLabel);
        OutputTitleText.Text = Loc.T("Prog.Output");
        OutputClearBtn.Text = Loc.T("Prog.OutputClear");
        OutputEmptyText.Text = Loc.T("Prog.OutputEmpty");
        OutputList.ItemsSource = _outputItems;
        ApplyOutputExpanded(false);

        // 导航栏返回（部分平台 Modal+NavigationPage 会显示）
        ToolbarItems.Add(new ToolbarItem
        {
            Text = _closeLabel,
            Priority = 0,
            Order = ToolbarItemOrder.Primary,
            Command = new Command(async () => await CloseAsync())
        });

        CodeBox.Text = _catalog.ReadSource(program);
        RebuildLineGutter();
        RefreshBpHint();
        RefreshOutput();
        CodeBox.TextChanged += (_, _) =>
        {
            RebuildLineGutter();
            EstimateCaretLine();
        };
        SizeChanged += (_, _) => UpdateToolMode();
        AppLog.Changed += OnAppLogChanged;
    }

    private void OnAppLogChanged()
    {
        if (_outputRefreshPending) return;
        _outputRefreshPending = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _outputRefreshPending = false;
            RefreshOutput();
        });
    }

    private void OutputHeader_Tapped(object? sender, TappedEventArgs e)
        => ApplyOutputExpanded(!_outputExpanded);

    private void OutputClear_Clicked(object? sender, EventArgs e)
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
        ToolTipProperties.SetText(OutputHeader, expanded ? Loc.T("Prog.OutputHide") : Loc.T("Prog.OutputShow"));
        if (expanded)
            ScrollOutputToEnd();
    }

    private void EnsureOutputVisible()
    {
        if (!_outputExpanded)
            ApplyOutputExpanded(true);
    }

    /// <summary>只 Add 不从头删；新日志始终滚到底部。</summary>
    private void RefreshOutput()
    {
        var items = AppLog.Snapshot(false, _program.Id, _program.Name)
            .Where(e => e.Time >= _outputSince)
            .ToList();
        var added = AppLog.SyncTo(_outputItems, items);
        OutputEmptyText.IsVisible = _outputItems.Count == 0;
        if (added && _outputExpanded)
            ScrollOutputToEnd();
    }

    private void ScrollOutputToEnd()
    {
        if (!_outputExpanded || _outputItems.Count == 0) return;
        if (_outputScrollPending) return;
        _outputScrollPending = true;
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(50), () =>
        {
            _outputScrollPending = false;
            if (!_outputExpanded || _outputItems.Count == 0) return;
            try
            {
                OutputList.ScrollTo(_outputItems[^1], position: ScrollToPosition.End, animate: false);
            }
            catch { /* ignore */ }
        });
    }

    private void ApplyToolLabels(bool compact)
    {
        _toolsCompact = compact;
        BtnClose.Text = compact ? IcClose : $"{IcClose}  {_closeLabel}";
        BtnSave.Text = compact ? IcSave : $"{IcSave}  {_saveLabel}";
        BtnDebug.Text = compact ? IcDebug : $"{IcDebug}  {_debugLabel}";
        BtnClearAllBp.Text = compact ? IcClear : $"{IcClear}  {_clearBpLabel}";
        BtnRun.Text = compact ? IcRun : $"{IcRun}  {_runLabel}";
        BtnStop.Text = compact ? IcStop : $"{IcStop}  {_stopLabel}";
        var pad = compact ? new Thickness(8, 0) : new Thickness(10, 0);
        foreach (var b in new[] { BtnClose, BtnSave, BtnDebug, BtnClearAllBp, BtnRun, BtnStop })
            b.Padding = pad;
    }

    private void UpdateToolMode()
    {
        var compact = Width > 0 && Width < 640;
        if (compact == _toolsCompact) return;
        ApplyToolLabels(compact);
    }

    private void EstimateCaretLine()
    {
        var text = CodeBox.Text ?? "";
        _caretLine = Math.Max(1, text.Replace("\r\n", "\n").Split('\n').Length);
        RefreshBpHint();
    }

    private void RebuildLineGutter()
    {
        var text = (CodeBox.Text ?? "").Replace("\r\n", "\n");
        var lineCount = Math.Max(1, text.Split('\n').Length);
        LineGutter.Children.Clear();
        for (var i = 1; i <= lineCount; i++)
        {
            var line = i;
            var hasBp = _program.Breakpoints.Contains(line);
            var row = new Grid
            {
                HeightRequest = LineRowHeight,
                ColumnDefinitions = new ColumnDefinitionCollection
                {
                    new(new GridLength(16)),
                    new(GridLength.Star)
                },
                Padding = new Thickness(2, 0, 4, 0)
            };
            // 红色实心圆，比原 ● 大约一号
            row.Add(new Ellipse
            {
                WidthRequest = 11,
                HeightRequest = 11,
                Fill = hasBp ? Color.FromArgb("#EF4444") : Colors.Transparent,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true
            }, 0);
            row.Add(new Label
            {
                Text = line.ToString().PadLeft(3),
                FontFamily = "Consolas",
                FontSize = 13,
                TextColor = Color.FromArgb("#9CA3AF"),
                HorizontalTextAlignment = TextAlignment.End,
                VerticalTextAlignment = TextAlignment.Center,
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true
            }, 1);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ToggleBreakpoint(line);
            row.GestureRecognizers.Add(tap);
            LineGutter.Children.Add(row);
        }
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
        _caretLine = line;
        RebuildLineGutter();
        RefreshBpHint();
    }

    private void RefreshBpHint()
    {
        var bps = _program.Breakpoints.Count == 0
            ? "无断点"
            : "断点: " + string.Join(",", _program.Breakpoints.OrderBy(x => x));
        BpHintText.Text = $"行 {_caretLine}  ·  {bps}";
    }

    private void Save_Clicked(object? sender, EventArgs e)
    {
        _catalog.WriteSource(_program, CodeBox.Text ?? "");
        _catalog.SaveMeta(_program);
        AppLog.Info($"已保存程序「{_program.Name}」");
    }

    private async void Debug_Clicked(object? sender, EventArgs e)
    {
        EnsureOutputVisible();
        var original = CodeBox.Text ?? "";
        SaveSilent();
        if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
            InjectPythonBreakpoints();
        try { await _runner.StartAsync(_program, debug: true); }
        catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); }
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

    private void ClearAllBp_Clicked(object? sender, EventArgs e)
    {
        _program.Breakpoints.Clear();
        _catalog.SaveMeta(_program);
        RebuildLineGutter();
        RefreshBpHint();
    }

    private async void Run_Clicked(object? sender, EventArgs e)
    {
        EnsureOutputVisible();
        SaveSilent();
        try { await _runner.StartAsync(_program); }
        catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); }
        RefreshOutput();
    }

    private async void Stop_Clicked(object? sender, EventArgs e)
    {
        try { await _runner.StopAsync(_program); }
        catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); }
        RefreshOutput();
    }

    private async void Close_Clicked(object? sender, EventArgs e)
        => await CloseAsync();

    private int _closeStopArmed;

    /// <summary>关闭编辑器：立即返回；运行中则后台异步停止。</summary>
    private void StopOnCloseIfNeeded()
    {
        if (Interlocked.Exchange(ref _closeStopArmed, 1) != 0) return;
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

    private async Task CloseAsync()
    {
        StopOnCloseIfNeeded();
        _closed.TrySetResult();
        try
        {
            if (Navigation.ModalStack.Count > 0)
                await Navigation.PopModalAsync();
            else if (Navigation.NavigationStack.Count > 1)
                await Navigation.PopAsync();
        }
        catch
        {
            /* 已关闭 */
        }
    }

    protected override void OnDisappearing()
    {
        StopOnCloseIfNeeded();
        base.OnDisappearing();
        _closed.TrySetResult();
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
