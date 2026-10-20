using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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
    private string _debugLabel = "调试";
    private string _runLabel = "运行";
    private string _stopLabel = "停止";
    private readonly ObservableCollection<AppLogEntry> _outputItems = [];
    private bool _outputExpanded;
    private bool _outputRefreshPending;
    private bool _outputScrollPending;
    private DateTime _outputSince = DateTime.MinValue;

    // 通用字形：软盘、实心三角(调试)、空心三角(运行)、红方块(停止)；返回为等长折线 Path
    private const string IcSave = "💾";
    private const string IcDebug = "▶";
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
        _debugLabel = Loc.T("Prog.Debug");
        _runLabel = Loc.T("Prog.Run");
        _stopLabel = Loc.T("Prog.StopRun");
        ApplyToolLabels(compact: false);
        ToolTipProperties.SetText(BtnClose, "关闭并返回");
        ToolTipProperties.SetText(BtnSave, _saveLabel);
        ToolTipProperties.SetText(BtnDebug, _debugLabel);
        ToolTipProperties.SetText(BtnRun, _runLabel);
        ToolTipProperties.SetText(BtnStop, _stopLabel);
        OutputTitleText.Text = Loc.T("Prog.Output");
        OutputClearBtn.Text = Loc.T("Prog.OutputClear");
        OutputEmptyText.Text = Loc.T("Prog.OutputEmpty");
        OutputList.ItemsSource = _outputItems;
        ApplyOutputExpanded(false);

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
                Background = ProgramEditorTheme.EditorBackgroundHex(dark)
            };
        }
    }

    public sealed class EditorBootstrap
    {
        public string Value { get; set; } = "";
        public string Language { get; set; } = "plaintext";
        public int[] Breakpoints { get; set; } = [];
        public int MaxLines { get; set; } = ProgramEditorLimits.MaxLines;
        public string[] Keywords { get; set; } = [];
        public string Theme { get; set; } = "vs";
        public string Background { get; set; } = "#FFFFFF";
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
#if ANDROID
        // 每次出现都重新挂上：软键盘曾误触发 OnDisappearing 时可能被卸掉
        RemoteTextInputHub.Register(this);
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
                    if (root.TryGetProperty("line", out var lineEl) && lineEl.TryGetInt32(out var l))
                        _caretLine = l;
                    RefreshBpHint();
                }
                break;
            case "caret":
                if (root.TryGetProperty("line", out var caret) && caret.TryGetInt32(out var cl))
                    _caretLine = cl;
                if (root.TryGetProperty("lineCount", out var lc) && lc.TryGetInt32(out var total))
                    _lineCount = total;
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
        // 工具条统一图标钮（36 宽）；文案只放 ToolTip，避免返回钮被文字撑宽
        _toolsCompact = compact;
        BtnClose.Text = " ";
        BtnSave.Text = IcSave;
        BtnDebug.Text = IcDebug;
        BtnRun.Text = IcRun;
        BtnStop.Text = IcStop;
        BtnStop.TextColor = Color.FromArgb("#DC2626");
        foreach (var b in new[] { BtnClose, BtnSave, BtnDebug, BtnRun, BtnStop })
        {
            b.Padding = new Thickness(0);
            b.WidthRequest = 36;
            b.MinimumWidthRequest = 36;
            b.MaximumWidthRequest = 36;
        }
        BtnCloseHost.WidthRequest = 36;
        BtnCloseHost.MinimumWidthRequest = 36;
        BtnCloseHost.MaximumWidthRequest = 36;
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
        _catalog.WriteSource(_program, text);
        _catalog.SaveMeta(_program);
        AppLog.Info($"已保存程序「{_program.Name}」");
    }

    private async void Save_Clicked(object? sender, EventArgs e)
        => await SaveFromEditorAsync();

    private async void Debug_Clicked(object? sender, EventArgs e)
    {
        var original = await GetEditorTextAsync();
        original = ProgramEditorFormatter.Format(original, _program.Language);
        if (!await EnsureWithinLineLimitAsync(original)) return;
        EnsureOutputVisible();
        await SaveFromEditorAsync(original);
        original = _pendingSource ?? original;
        if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
        {
            var injected = PythonBreakpointInjector.Inject(original, _program.Breakpoints);
            _catalog.WriteSource(_program, injected);
            AppLog.Info($"已为「{_program.Name}」注入 {PythonBreakpointInjector.CountInjected(_program.Breakpoints, original)} 处 Python breakpoint()", _program.Id, _program.Name);
        }
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
        RefreshOutput();
    }

    private async void Close_Clicked(object? sender, EventArgs e)
        => await CloseAsync();

    private int _closeStopArmed;

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

    private int _teardownArmed;

    private void TeardownEditorSession()
    {
        if (Interlocked.Exchange(ref _teardownArmed, 1) != 0) return;
#if ANDROID
        RemoteTextInputHub.Unregister(this);
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

    private void EvalEditorJs(string script)
    {
        try
        {
            if (CodeWeb.Handler?.PlatformView is global::Android.Webkit.WebView awv)
            {
                awv.EvaluateJavascript(script, null);
                return;
            }
        }
        catch { /* fall through */ }

        try { _ = CodeWeb.EvaluateJavaScriptAsync(script); }
        catch { /* WebView 未就绪 */ }
    }
#endif
}
