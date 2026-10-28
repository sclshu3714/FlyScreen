using System.Collections.Concurrent;
using System.Text.Json;
using FlyScreen.AppMaui.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Programs;
#if ANDROID
using FlyScreen.AppMaui.Platforms.Android;
#endif

namespace FlyScreen.AppMaui;

#if ANDROID
public partial class ProgramEditorPage : ContentPage, IRemoteTextInputSink
#else
public partial class ProgramEditorPage : ContentPage
#endif
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private readonly TaskCompletionSource _closed = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _valueWaiters = new();
    private int _caretLine = 1;
    private int _lineCount = 1;
    private bool _toolsCompact;
    private bool _editorReady;
    private bool _contentPushed;
    private bool _readyPollStarted;
    private string? _pendingSource;
    private string _closeLabel = "关闭";
    private string _saveLabel = "保存";
    private string _findLabel = "查找";
    private string _buildLabel = "生成";
    private string _debugLabel = "调试";
    private string _runLabel = "运行";
    private string _stopLabel = "停止";
    private bool _outputExpanded;
    private bool _outputRefreshPending;
    private bool _outputScrollPending;
    private DateTime _outputSince = DateTime.MinValue;
    private AppLogEntry? _outputLastWritten;
    private ProgramDebugGate? _debugGate;
    private int? _lastPausedLineLogged;
    private int? _lastDebugLineSent;
    private IDispatcherTimer? _debugPollTimer;

    // 通用字形：软盘、生成、运行、停止；调试钮用 Path（见 BtnDebugIcon）
    private const string IcSave = "💾";
    private const string IcFind = "🔍";
    private const string IcPaste = "📋";
    private const string IcBuild = "🔨";
    private const string IcRun = "▷";
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
        _findLabel = Loc.T("Prog.Find");
        _buildLabel = Loc.T("Prog.Build");
        _debugLabel = Loc.T("Prog.Debug");
        _runLabel = Loc.T("Prog.Run");
        _stopLabel = Loc.T("Prog.StopRun");
        ApplyToolLabels(compact: false);
        ToolTipProperties.SetText(BtnClose, "关闭并返回");
        ToolTipProperties.SetText(BtnSave, _saveLabel);
        ToolTipProperties.SetText(BtnFind, _findLabel + " (Ctrl+F)");
        ToolTipProperties.SetText(BtnPaste, "粘贴");
        ToolTipProperties.SetText(BtnBuild, _buildLabel);
        ToolTipProperties.SetText(BtnDebug, _debugLabel);
        ToolTipProperties.SetText(BtnRun, _runLabel);
        ToolTipProperties.SetText(BtnStop, _stopLabel);
        OutputTitleText.Text = Loc.T("Prog.Output");
        OutputClearBtn.Text = Loc.T("Prog.OutputClear");
        OutputEmptyText.Text = Loc.T("Prog.OutputEmpty");
        ApplyOutputExpanded(false);
        if (IsSourceReadOnly)
        {
            BtnSave.IsEnabled = false;
            BtnBuild.IsEnabled = false;
            BtnPaste.IsEnabled = false;
            ToolTipProperties.SetText(BtnSave, Loc.T("Prog.BuiltinSourceReadonly"));
            ToolTipProperties.SetText(BtnBuild, Loc.T("Prog.BuiltinSourceReadonly"));
            ToolTipProperties.SetText(BtnPaste, Loc.T("Prog.BuiltinSourceReadonly"));
        }

        ToolbarItems.Add(new ToolbarItem
        {
            Text = _closeLabel,
            Priority = 0,
            Order = ToolbarItemOrder.Primary,
            Command = new Command(async () => await CloseAsync())
        });

        _pendingSource = _catalog.ReadSource(program);
        // JS 通过 InvokeDotNet('GetBootstrap') 主动拉取源码（不依赖宿主先推送）
        CodeWeb.SetInvokeJavaScriptTarget(new EditorDotNetBridge(this));
        RefreshBpHint();
        RefreshOutput();
        SizeChanged += (_, _) => UpdateToolMode();
        AppLog.Changed += OnAppLogChanged;
    }

    /// <summary>供 Monaco bridge.js 的 HybridWebView.InvokeDotNet 调用。</summary>
    public sealed class EditorDotNetBridge
    {
        private readonly ProgramEditorPage _page;
        public EditorDotNetBridge(ProgramEditorPage page) => _page = page;

        public EditorBootstrap GetBootstrap()
        {
            var source = _page._pendingSource ?? _page._catalog.ReadSource(_page._program);
            _page._pendingSource = source;
            _page._editorReady = true;
            _page._contentPushed = true;
            var lang = _page._program.Language;
            _page._lineCount = ProgramEditorLimits.CountLines(source);
            var dark = MauiThemeService.IsDark(MauiThemeService.Current);
            return new EditorBootstrap
            {
                Value = source ?? "",
                Language = ProgramEditorLanguageMap.MonacoLanguageId(lang),
                Breakpoints = _page._program.Breakpoints.ToArray(),
                MaxLines = ProgramEditorLimits.MaxLines,
                Keywords = ProgramEditorCompletions.Keywords(lang).ToArray(),
                Theme = ProgramEditorTheme.MonacoThemeId(dark),
                Background = ProgramEditorTheme.EditorBackgroundHex(dark),
                ReadOnly = _page.IsSourceReadOnly
            };
        }

        /// <summary>
        /// Android WebView 粘贴时 JS clipboardData 常为空；由宿主读系统剪贴板再 flyType。
        /// </summary>
        public string GetClipboardText()
        {
            try
            {
                if (MainThread.IsMainThread)
                    return ReadClipboardTextNow();
                return MainThread.InvokeOnMainThreadAsync(ReadClipboardTextNow)
                    .ConfigureAwait(false).GetAwaiter().GetResult() ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string ReadClipboardTextNow()
        {
            try
            {
#if ANDROID
                var ctx = global::Android.App.Application.Context;
                if (ctx != null)
                {
                    var cm = ctx.GetSystemService(global::Android.Content.Context.ClipboardService)
                        as global::Android.Content.ClipboardManager;
                    var clip = cm?.PrimaryClip;
                    if (clip is { ItemCount: > 0 })
                    {
                        var item = clip.GetItemAt(0);
                        var coerced = item?.CoerceToText(ctx)?.ToString();
                        if (!string.IsNullOrEmpty(coerced))
                            return coerced;
                    }
                }
#endif
                // 兜底：MAUI Clipboard（可能异步；此处尽量同步取）
                if (Clipboard.Default.HasText)
                {
                    var t = Clipboard.Default.GetTextAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                    return t ?? "";
                }
            }
            catch
            {
                /* ignore */
            }

            return "";
        }
    }

    private bool IsSourceReadOnly
        => _program.IsBuiltin
           || string.Equals(_program.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase);

    public sealed class EditorBootstrap
    {
        public string Value { get; set; } = "";
        public string Language { get; set; } = "plaintext";
        public int[] Breakpoints { get; set; } = [];
        public int MaxLines { get; set; } = ProgramEditorLimits.MaxLines;
        public string[] Keywords { get; set; } = [];
        public string Theme { get; set; } = "vs";
        public string Background { get; set; } = "#FFFFFF";
        /// <summary>内置程序：可读/复制/查找，不可改字。</summary>
        public bool ReadOnly { get; set; }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        // 每次出现都重新挂上：软键盘曾误触发 OnDisappearing 时可能被卸掉
        RemoteTextInputHub.Register(this);
        Services.CompositionCaretAnchor.RequestRefresh = () =>
            EvalEditorJs("window.flyReportCaret&&window.flyReportCaret()");
#endif
        StartReadyPoll();
    }

    protected override void OnDisappearing()
    {
        // 注意：Android 弹出系统输入法也会走 OnDisappearing，此处绝不能 Unregister / 关页
        base.OnDisappearing();
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        TeardownEditorSession();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync();
        return true;
    }

    private void StartReadyPoll()
    {
        if (_readyPollStarted) return;
        _readyPollStarted = true;
        _ = PollEditorReadyAsync();
    }

    private async Task PollEditorReadyAsync()
    {
        // 不依赖 JS→C# 的 ready 消息：轮询 flyEditorReady 后用 EvaluateJavaScript 注入源码
        for (var i = 0; i < 60 && !_contentPushed; i++)
        {
            try
            {
                var flag = await CodeWeb.EvaluateJavaScriptAsync(
                    "window.flyEditorReady===true?'1':'0'").ConfigureAwait(true);
                if (flag == "1" || flag == "\"1\"")
                {
                    _editorReady = true;
                    await PushInitialStateAsync().ConfigureAwait(true);
                    return;
                }
            }
            catch { /* WebView 尚未完成导航 */ }

            await Task.Delay(200).ConfigureAwait(true);
        }
    }

    private void OnHybridMessage(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Message)) return;
        // 必须在主线程解析并处理：JsonElement 依赖 JsonDocument，
        // 若在 BeginInvoke 前 using 释放，会 ObjectDisposedException(JsonDocument)。
        var json = e.Message;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) return;
                HandleEditorMessage(typeEl.GetString(), root);
            }
            catch
            {
                /* ignore malformed / disposed races */
            }
        });
    }

    private void HandleEditorMessage(string? type, JsonElement root)
    {
        switch (type)
        {
            case "ready":
                _editorReady = true;
                // JS 可能已通过 InvokeDotNet 拉过内容；若仍空则再推一次
                if (!_contentPushed)
                    _ = PushInitialStateAsync();
                break;
            case "value":
                if (root.TryGetProperty("requestId", out var rid)
                    && rid.GetString() is { } id
                    && _valueWaiters.TryRemove(id, out var tcs))
                {
                    var value = root.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
                    tcs.TrySetResult(value);
                }
                break;
            case "breakpointToggled":
                if (root.TryGetProperty("breakpoints", out var bps) && bps.ValueKind == JsonValueKind.Array)
                {
                    _program.Breakpoints.Clear();
                    foreach (var item in bps.EnumerateArray())
                    {
                        if (item.TryGetInt32(out var line) && line >= 1)
                            _program.Breakpoints.Add(line);
                    }
                    _program.Breakpoints.Sort();
                    _catalog.SaveMeta(_program);
                    (_debugGate ?? ProgramDebugGate.Current)?.SetBreakpoints(_program.Breakpoints);
                    if (root.TryGetProperty("line", out var lineEl) && lineEl.TryGetInt32(out var l))
                        _caretLine = l;
                    RefreshBpHint();
                }
                break;
            case "debugStepOver":
                TryStep(over: true);
                break;
            case "debugStepInto":
                TryStep(over: false);
                break;
            case "caret":
                if (root.TryGetProperty("line", out var caret) && caret.TryGetInt32(out var cl))
                    _caretLine = cl;
                if (root.TryGetProperty("lineCount", out var lc) && lc.TryGetInt32(out var total))
                    _lineCount = total;
                if (root.TryGetProperty("caretX", out var cxEl) && cxEl.TryGetDouble(out var cx)
                    && root.TryGetProperty("caretY", out var cyEl) && cyEl.TryGetDouble(out var cy))
                    Services.CompositionCaretAnchor.UpdateFromWebView(cx, cy, CodeWeb);
                RefreshBpHint();
                break;
            case "lineLimitExceeded":
                if (root.TryGetProperty("lineCount", out var elc) && elc.TryGetInt32(out var eln))
                    _lineCount = eln;
                RefreshBpHint();
                _ = DisplayAlertAsync(Title, ProgramEditorLimits.ExceedMessage(_lineCount), "OK");
                break;
            case "saveRequested":
                var text = root.TryGetProperty("value", out var sv)
                    ? ProgramCatalog.NormalizeSourceText(sv.GetString())
                    : null;
                _ = SaveFromEditorAsync(text);
                break;
            case "formatRequested":
                var raw = root.TryGetProperty("value", out var fv)
                    ? ProgramCatalog.NormalizeSourceText(fv.GetString())
                    : null;
                _ = FormatAndPushAsync(raw);
                break;
        }
    }

    private async Task FormatAndPushAsync(string? knownText = null)
    {
        var text = knownText ?? await GetEditorTextAsync();
        var formatted = ProgramEditorFormatter.Format(text, _program.Language);
        if (!string.Equals(text, formatted, StringComparison.Ordinal))
        {
            _pendingSource = formatted;
            _lineCount = ProgramEditorLimits.CountLines(formatted);
            await SendEditorAsync(new
            {
                type = "setValue",
                value = formatted,
                language = ProgramEditorLanguageMap.MonacoLanguageId(_program.Language)
            }).ConfigureAwait(true);
            RefreshBpHint();
        }
    }

    private async Task PushInitialStateAsync()
    {
        var source = _pendingSource ?? _catalog.ReadSource(_program);
        var lang = _program.Language;
        var dark = MauiThemeService.IsDark(MauiThemeService.Current);
        var payload = new
        {
            type = "init",
            value = source,
            language = ProgramEditorLanguageMap.MonacoLanguageId(lang),
            breakpoints = _program.Breakpoints.ToArray(),
            maxLines = ProgramEditorLimits.MaxLines,
            keywords = ProgramEditorCompletions.Keywords(lang).ToArray(),
            theme = ProgramEditorTheme.MonacoThemeId(dark),
            background = ProgramEditorTheme.EditorBackgroundHex(dark)
        };
        await SendEditorAsync(payload).ConfigureAwait(true);
        _contentPushed = true;
        _pendingSource = source;
        _lineCount = ProgramEditorLimits.CountLines(source);
        RefreshBpHint();
    }

    private async Task SendEditorAsync(object payload)
    {
        if (!_editorReady) return;
        var json = JsonSerializer.Serialize(payload);
        try
        {
            // EvaluateJavaScript 比 SendRawMessage 更稳：不依赖 HybridWebViewMessageReceived
            var arg = JsonSerializer.Serialize(json);
            await CodeWeb.EvaluateJavaScriptAsync(
                $"window.flyHandleHostMessage&&window.flyHandleHostMessage({arg})").ConfigureAwait(true);
            return;
        }
        catch { /* fall through */ }

        try { CodeWeb.SendRawMessage(json); }
        catch { /* WebView 未就绪 */ }
    }

    private void SendEditor(object payload)
        => _ = SendEditorAsync(payload);

    private async Task<string> GetEditorTextAsync(TimeSpan? timeout = null)
    {
        if (!_editorReady)
            return ProgramCatalog.NormalizeSourceText(_pendingSource ?? _catalog.ReadSource(_program));

        // 不要用 EvaluateJavaScript 取多行文本：WebView2 返回值易变成字面量 \r\n 并写坏文件。
        // 走 HybridWebView JSON 消息，GetString() 能正确还原换行。
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _valueWaiters[id] = tcs;
        try { CodeWeb.SendRawMessage(JsonSerializer.Serialize(new { type = "getValue", requestId = id })); }
        catch
        {
            _valueWaiters.TryRemove(id, out _);
            return ProgramCatalog.NormalizeSourceText(_pendingSource ?? _catalog.ReadSource(_program));
        }

        var delay = timeout ?? TimeSpan.FromSeconds(3);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(delay)).ConfigureAwait(true);
        if (completed != tcs.Task)
        {
            _valueWaiters.TryRemove(id, out _);
            return ProgramCatalog.NormalizeSourceText(_pendingSource ?? _catalog.ReadSource(_program));
        }
        return ProgramCatalog.NormalizeSourceText(await tcs.Task.ConfigureAwait(true));
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
        _outputLastWritten = null;
        OutputLabel.Text = "";
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

    private void RefreshOutput()
    {
        var items = AppLog.Snapshot(false, _program.Id, _program.Name)
            .Where(e => e.Time >= _outputSince)
            .ToList();
        var chunk = AppLog.FormatNewLines(items, ref _outputLastWritten);
        if (chunk != null)
            OutputLabel.Text = (OutputLabel.Text ?? "") + chunk;
        OutputEmptyText.IsVisible = string.IsNullOrEmpty(OutputLabel.Text);
        if (chunk != null && _outputExpanded)
            ScrollOutputToEnd();
    }

    private void ScrollOutputToEnd()
    {
        if (!_outputExpanded || string.IsNullOrEmpty(OutputLabel.Text)) return;
        if (_outputScrollPending) return;
        _outputScrollPending = true;
        // 等布局算出 ContentSize 再滚到底，避免仍停在顶部
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(80), async () =>
        {
            _outputScrollPending = false;
            if (!_outputExpanded || string.IsNullOrEmpty(OutputLabel.Text)) return;
            try
            {
                await OutputScroll.ScrollToAsync(OutputLabel, ScrollToPosition.End, animated: false)
                    .ConfigureAwait(true);
            }
            catch
            {
                try
                {
                    var y = Math.Max(0, OutputScroll.ContentSize.Height - OutputScroll.Height);
                    await OutputScroll.ScrollToAsync(0, y, animated: false).ConfigureAwait(true);
                }
                catch { /* ignore */ }
            }
        });
    }

    private void ApplyToolLabels(bool compact)
    {
        // 工具条统一图标钮（36 宽）；文案只放 ToolTip，避免返回钮被文字撑宽
        _toolsCompact = compact;
        BtnClose.Text = " ";
        BtnSave.Text = IcSave;
        BtnFind.Text = IcFind;
        BtnPaste.Text = IcPaste;
        BtnBuild.Text = IcBuild;
        BtnDebug.Text = " ";
        BtnRun.Text = IcRun;
        BtnStop.Text = IcStop;
        BtnStop.TextColor = Color.FromArgb("#DC2626");
        foreach (var b in new[] { BtnClose, BtnSave, BtnFind, BtnPaste, BtnBuild, BtnDebug, BtnRun, BtnStop })
        {
            b.Padding = new Thickness(0);
            b.WidthRequest = 36;
            b.MinimumWidthRequest = 36;
            b.MaximumWidthRequest = 36;
        }
        BtnCloseHost.WidthRequest = 36;
        BtnCloseHost.MinimumWidthRequest = 36;
        BtnCloseHost.MaximumWidthRequest = 36;
        BtnDebugHost.WidthRequest = 36;
        BtnDebugHost.MinimumWidthRequest = 36;
        BtnDebugHost.MaximumWidthRequest = 36;
        RefreshDebugUi();
    }

    private void UpdateToolMode()
    {
        // 图标钮固定尺寸，不再随宽度切换文案
        if (!_toolsCompact)
            ApplyToolLabels(compact: true);
    }

    private void RefreshBpHint()
    {
        // 断点已在左侧 gutter 可见，工具条只显示行数状态，避免挤占按钮
        var lines = Loc.T("Prog.LineLimitStatus", _caretLine, _lineCount, ProgramEditorLimits.MaxLines);
        if (_lineCount > ProgramEditorLimits.MaxLines)
            lines += "  ·  " + Loc.T("Prog.LineLimitBlocked");
        BpHintText.Text = lines;
        BpHintText.TextColor = _lineCount > ProgramEditorLimits.MaxLines
            ? Color.FromArgb("#D97706")
            : Color.FromArgb("#6B7280");
    }

    private async Task<bool> EnsureWithinLineLimitAsync(string? text = null)
    {
        text ??= await GetEditorTextAsync();
        if (!ProgramEditorLimits.ExceedsLimit(text, out var n))
        {
            _lineCount = n;
            RefreshBpHint();
            return true;
        }
        _lineCount = n;
        RefreshBpHint();
        await DisplayAlertAsync(Title, ProgramEditorLimits.ExceedMessage(n), "OK");
        return false;
    }

    private async Task SaveFromEditorAsync(string? knownText = null)
    {
        var text = knownText ?? await GetEditorTextAsync();
        var formatted = ProgramEditorFormatter.Format(text, _program.Language);
        if (!string.Equals(text, formatted, StringComparison.Ordinal))
        {
            text = formatted;
            _pendingSource = formatted;
            _lineCount = ProgramEditorLimits.CountLines(formatted);
            await SendEditorAsync(new
            {
                type = "setValue",
                value = formatted,
                language = ProgramEditorLanguageMap.MonacoLanguageId(_program.Language)
            }).ConfigureAwait(true);
            RefreshBpHint();
        }
        if (!await EnsureWithinLineLimitAsync(text)) return;
        if (_program.IsBuiltin
            || string.Equals(_program.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase))
        {
            await DisplayAlertAsync(_saveLabel, Loc.T("Prog.BuiltinSourceReadonly"), "OK");
            return;
        }
        _catalog.WriteSource(_program, text);
        _catalog.SaveMeta(_program);
        AppLog.Info($"已保存程序「{_program.Name}」");
    }

    private void Paste_Clicked(object? sender, EventArgs e)
    {
        if (IsSourceReadOnly) return;
        EvalEditorJs("window.flyPasteFromHost&&window.flyPasteFromHost()");
    }

    private void Find_Clicked(object? sender, EventArgs e)
        => MonacoAction("find");

    private async void Save_Clicked(object? sender, EventArgs e)
        => await SaveFromEditorAsync();

    private async void Build_Clicked(object? sender, EventArgs e)
        => await TryBuildAsync();

    private async Task<bool> TryBuildAsync()
    {
        var text = await GetEditorTextAsync();
        text = ProgramEditorFormatter.Format(text, _program.Language);
        if (!await EnsureWithinLineLimitAsync(text)) return false;
        EnsureOutputVisible();
        await SaveFromEditorAsync(text);
        text = _pendingSource ?? text;
        var file = string.IsNullOrWhiteSpace(_program.FileName) ? "main.cs" : _program.FileName;
        var result = ProgramCompiler.Compile(_program.Language, text, file);
        AppLog.Info(result.FormatReport($"—— {Loc.T("Prog.Build")} {_program.Name} ——"), _program.Id, _program.Name);
        RefreshOutput();
        return result.Success;
    }

    private async void Debug_Clicked(object? sender, EventArgs e)
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

        var original = await GetEditorTextAsync();
        original = ProgramEditorFormatter.Format(original, _program.Language);
        if (!await EnsureWithinLineLimitAsync(original)) return;
        EnsureOutputVisible();
        await SaveFromEditorAsync(original);
        original = _pendingSource ?? original;

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
        catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); DetachDebugGate(); }
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
        => MainThread.BeginInvokeOnMainThread(RefreshDebugUi);

    private void StartDebugPoll()
    {
        StopDebugPoll();
        _debugPollTimer = Dispatcher.CreateTimer();
        _debugPollTimer.Interval = TimeSpan.FromMilliseconds(400);
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
        if (!running || gate == null || gate.Phase == ProgramDebugPhase.Idle)
        {
            SetDebugIcon(pausedBars: false, Loc.T("Prog.Debug"), "#2563EB");
            SetEditorDebugLine(null, force: false);
            return;
        }

        if (gate.Phase == ProgramDebugPhase.Paused)
        {
            // 暂停中：绿色 ▶ = 继续；黄底 + 三角指示当前执行行
            SetDebugIcon(pausedBars: false, Loc.T("Prog.Continue"), "#16A34A");
            var line = gate.PausedLine;
            // 暂停期间每次轮询都重发装饰（首次可能 editor 未就绪被丢弃）
            SetEditorDebugLine(line is > 0 ? line : null, force: true);
            if (line is > 0 && _lastPausedLineLogged != line)
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
            return;
        }

        _lastPausedLineLogged = null;
        SetEditorDebugLine(null, force: false);
        // 运行中：⏸ 暂停
        SetDebugIcon(pausedBars: true, Loc.T("Prog.Pause"), "#D97706");
    }

    /// <summary>直接调 JS flySetDebugLine，避免 JSON 消息路径丢失。</summary>
    private void SetEditorDebugLine(int? line, bool force)
    {
        if (!force && _lastDebugLineSent == line) return;
        _lastDebugLineSent = line;
        var n = line is > 0 ? line.Value : -1;
        // 优先直调；再兜底走 host message
        EvalEditorJs($"window.flySetDebugLine&&window.flySetDebugLine({n})");
        SendEditor(new { type = "setDebugLine", line = n });
    }

    private void SetDebugIcon(bool pausedBars, string tip, string colorHex)
    {
        BtnDebug.Text = " ";
        BtnDebugIconPlay.IsVisible = !pausedBars;
        BtnDebugIconPause.IsVisible = pausedBars;
        if (!pausedBars)
            BtnDebugIconPlay.Fill = new SolidColorBrush(Color.FromArgb(colorHex));
        else
            BtnDebugIconPause.Fill = new SolidColorBrush(Color.FromArgb(colorHex));
        ToolTipProperties.SetText(BtnDebug, tip);
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

    private async void Run_Clicked(object? sender, EventArgs e)
    {
        if (!await EnsureWithinLineLimitAsync()) return;
        EnsureOutputVisible();
        await SaveFromEditorAsync();
        try { await _runner.StartAsync(_program); }
        catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); }
        RefreshOutput();
    }

    private async void Stop_Clicked(object? sender, EventArgs e)
    {
        try { await _runner.StopAsync(_program); }
        catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); }
        StopDebugPoll();
        DetachDebugGate();
        RefreshOutput();
    }

    private async void Close_Clicked(object? sender, EventArgs e)
        => await CloseAsync();

    private int _closeStopArmed;

    private void StopOnCloseIfNeeded()
    {
        if (Interlocked.Exchange(ref _closeStopArmed, 1) != 0) return;
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

    private int _teardownArmed;

    private void TeardownEditorSession()
    {
        if (Interlocked.Exchange(ref _teardownArmed, 1) != 0) return;
#if ANDROID
        RemoteTextInputHub.Unregister(this);
        Services.CompositionCaretAnchor.RequestRefresh = null;
        Services.CompositionCaretAnchor.Clear();
#endif
        StopOnCloseIfNeeded();
        _closed.TrySetResult();
    }

    private async Task CloseAsync()
    {
        TeardownEditorSession();
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

#if ANDROID
    // —— 跨屏：可打印字符由电脑组字后经 TextInput 写入；此处只处理导航/编辑键 ——

    public bool TryAcceptRemoteText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        void Run() => MonacoType(text);
        if (MainThread.IsMainThread) Run();
        else MainThread.BeginInvokeOnMainThread(Run);
        return true;
    }

    public bool TryHandleRemoteKey(ushort virtualKey, bool down, bool shift, bool caps, bool ctrl)
    {
        if (MainThread.IsMainThread)
            return HandleRemoteKeyCore(virtualKey, down, shift, caps, ctrl);

        var handled = false;
        using var done = new ManualResetEventSlim(false);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { handled = HandleRemoteKeyCore(virtualKey, down, shift, caps, ctrl); }
            finally { done.Set(); }
        });
        done.Wait(TimeSpan.FromSeconds(2));
        return handled;
    }

    private bool HandleRemoteKeyCore(ushort vk, bool down, bool shift, bool caps, bool ctrl)
    {
        _ = shift;
        _ = caps;

        // 修饰键：吞掉，避免再走本机 IME 代理
        if (vk is 0x10 or 0xA0 or 0xA1 or 0x14)
            return true;

        if (!down)
            return true;

        if (ctrl)
            return false;

        // 导航 / 退格 / 回车等
        if (TryMonacoSpecial(vk))
            return true;

        // A-Z、数字、符号：绝不直灌字母。中文由电脑 CompositionUpdate 浮层选词，
        // 英文/确认文本走 TextInput → TryAcceptRemoteText。
        if (IsPrintableCompositionKey(vk))
            return true;

        return false;
    }

    private static bool IsPrintableCompositionKey(ushort vk)
        => vk is (>= 0x30 and <= 0x39)
            or (>= 0x41 and <= 0x5A)
            or (>= 0x60 and <= 0x69)
            or 0x20
            or 0xBA or 0xBB or 0xBC or 0xBD or 0xBE or 0xBF or 0xC0
            or 0xDB or 0xDC or 0xDD or 0xDE;

    private bool TryMonacoSpecial(ushort vk)
    {
        var action = vk switch
        {
            0x08 => "backspace",
            0x2E => "delete",
            0x0D => "enter",
            0x09 => "tab",
            0x25 => "left",
            0x26 => "up",
            0x27 => "right",
            0x28 => "down",
            0x24 => "home",
            0x23 => "end",
            _ => null
        };
        if (action == null) return false;
        MonacoAction(action);
        return true;
    }
#endif

    private void MonacoType(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var escaped = JsonSerializer.Serialize(text);
        EvalEditorJs($"window.flyType && window.flyType({escaped})");
    }

    private void MonacoAction(string action)
    {
        var name = JsonSerializer.Serialize(action);
        EvalEditorJs($"window.flyEditorAction && window.flyEditorAction({name})");
    }

    /// <summary>向 Monaco 注入脚本（Android 走原生 EvaluateJavascript，其它端走 HybridWebView）。</summary>
    private void EvalEditorJs(string script)
    {
#if ANDROID
        try
        {
            if (CodeWeb.Handler?.PlatformView is global::Android.Webkit.WebView awv)
            {
                awv.EvaluateJavascript(script, null);
                return;
            }
        }
        catch { /* fall through */ }
#endif
        try { _ = CodeWeb.EvaluateJavaScriptAsync(script); }
        catch { /* WebView 未就绪 */ }
    }
}
