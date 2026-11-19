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
    private string _baselineText = "";
    private string _currentRelativePath = "";
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
        _catalog.NormalizeManifest(_program);
        _currentRelativePath = _program.FileName;
        // 全页 Modal：接入全局主题色板（与弹层一致用实心表面）
        Helpers.MauiThemeService.ApplyToDialog(this);

        Title = $"{Loc.T("Prog.EditorTitle")} — {program.Name} · {program.LanguageText} · {_currentRelativePath}";
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
        ToolTipProperties.SetText(BtnAddFile, Loc.T("Prog.NewFile"));
        ToolTipProperties.SetText(BtnDeps, Loc.T("Prog.DepsTitle"));
        BtnDepsHost.IsVisible = _program.Language == ProgramLanguage.CSharp;
        ToolTipProperties.SetText(BtnFlySettings, Loc.T("Prog.FlySettingsTitle"));
        ToolTipProperties.SetText(BtnFileSwitch, Loc.T("Prog.SwitchFile"));
        OutputTitleText.Text = Loc.T("Prog.Output");
        OutputClearBtn.Text = Loc.T("Prog.OutputClear");
        OutputEmptyText.Text = Loc.T("Prog.OutputEmpty");
        ApplyOutputExpanded(false);
        if (IsSourceReadOnly)
        {
            BtnSave.IsEnabled = false;
            BtnBuild.IsEnabled = false;
            BtnPaste.IsEnabled = false;
            BtnAddFile.IsEnabled = false;
            BtnDeps.IsEnabled = false;
            ToolTipProperties.SetText(BtnSave, Loc.T("Prog.BuiltinSourceReadonly"));
            ToolTipProperties.SetText(BtnBuild, Loc.T("Prog.BuiltinSourceReadonly"));
            ToolTipProperties.SetText(BtnPaste, Loc.T("Prog.BuiltinSourceReadonly"));
        }

        RefreshFilePicker();
        _pendingSource = _catalog.ReadFile(program, _currentRelativePath);
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
            var source = _page._pendingSource ?? _page._catalog.ReadFile(_page._program, _page._currentRelativePath);
            _page._pendingSource = source;
            _page._baselineText = source ?? "";
            _page._editorReady = true;
            _page._contentPushed = true;
            var lang = _page._program.Language;
            _page._lineCount = ProgramEditorLimits.CountLines(source);
            var dark = MauiThemeService.IsDark(MauiThemeService.Current);
            return new EditorBootstrap
            {
                Value = source ?? "",
                Language = ProgramEditorLanguageMap.MonacoLanguageIdForPath(
                    _page._currentRelativePath, lang),
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

    private string FormatForCurrentFile(string text)
    {
        if (ProgramCatalog.IsConfigSchemaFile(_currentRelativePath))
            return ProgramFlySettingsStore.NormalizeSchemaText(text);
        return ProgramEditorFormatter.Format(text, _program.Language);
    }

    private string MonacoLanguageForCurrentFile()
        => ProgramEditorLanguageMap.MonacoLanguageIdForPath(_currentRelativePath, _program.Language);

    private async Task FormatAndPushAsync(string? knownText = null)
    {
        var text = knownText ?? await GetEditorTextAsync();
        var formatted = FormatForCurrentFile(text);
        if (!string.Equals(text, formatted, StringComparison.Ordinal))
        {
            _pendingSource = formatted;
            _lineCount = ProgramEditorLimits.CountLines(formatted);
            await SendEditorAsync(new
            {
                type = "setValue",
                value = formatted,
                language = MonacoLanguageForCurrentFile()
            }).ConfigureAwait(true);
            RefreshBpHint();
        }
    }

    private async Task PushInitialStateAsync()
    {
        var source = _pendingSource ?? _catalog.ReadFile(_program, _currentRelativePath);
        var dark = MauiThemeService.IsDark(MauiThemeService.Current);
        var payload = new
        {
            type = "init",
            value = source,
            language = MonacoLanguageForCurrentFile(),
            breakpoints = _program.Breakpoints.ToArray(),
            maxLines = ProgramEditorLimits.MaxLines,
            keywords = ProgramEditorCompletions.Keywords(_program.Language).ToArray(),
            theme = ProgramEditorTheme.MonacoThemeId(dark),
            background = ProgramEditorTheme.EditorBackgroundHex(dark)
        };
        await SendEditorAsync(payload).ConfigureAwait(true);
        _contentPushed = true;
        _pendingSource = source;
        _baselineText = source;
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

    /// <summary>
    /// 把当前文件缓冲推到 Monaco。
    /// 优先 InvokeDotNet 回拉（与启动同路径，大 CS 也稳）；再尝试 flyApplyFile / 消息总线。
    /// </summary>
    private async Task PushCurrentFileToEditorAsync()
    {
        var text = _pendingSource ?? "";
        var language = MonacoLanguageForCurrentFile();
        _lineCount = ProgramEditorLimits.CountLines(text);
        RefreshBpHint();

        if (!_editorReady)
            return;

        // 大文件经 EvaluateJavaScript 内联字符串易失败；回拉走 HybridWebView.InvokeDotNet
        try
        {
            var result = await CodeWeb.EvaluateJavaScriptAsync(
                "window.flyReloadFromHost&&window.flyReloadFromHost()").ConfigureAwait(true);
            if (!string.IsNullOrEmpty(result) && result != "null" && result != "no-hwv"
                && result != "empty" && !result.StartsWith("err", StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch { /* try push */ }

        try
        {
            var v = JsonSerializer.Serialize(text);
            var lang = JsonSerializer.Serialize(language);
            var result = await CodeWeb.EvaluateJavaScriptAsync(
                $"window.flyApplyFile&&window.flyApplyFile({v},{lang})").ConfigureAwait(true);
            if (!string.IsNullOrEmpty(result) && result != "null" && result != "no-editor"
                && !result.StartsWith("err", StringComparison.OrdinalIgnoreCase))
                return;
        }
        catch { /* fall through */ }

        await SendEditorAsync(new { type = "setValue", value = text, language }).ConfigureAwait(true);
    }

    private void SendEditor(object payload)
        => _ = SendEditorAsync(payload);

    private async Task<string> GetEditorTextAsync(TimeSpan? timeout = null)
    {
        if (!_editorReady)
            return ProgramCatalog.NormalizeSourceText(_pendingSource ?? _catalog.ReadFile(_program, _currentRelativePath));

        // 不要用 EvaluateJavaScript 取多行文本：WebView2 返回值易变成字面量 \r\n 并写坏文件。
        // 走 HybridWebView JSON 消息，GetString() 能正确还原换行。
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _valueWaiters[id] = tcs;
        try { CodeWeb.SendRawMessage(JsonSerializer.Serialize(new { type = "getValue", requestId = id })); }
        catch
        {
            _valueWaiters.TryRemove(id, out _);
            return ProgramCatalog.NormalizeSourceText(_pendingSource ?? _catalog.ReadFile(_program, _currentRelativePath));
        }

        var delay = timeout ?? TimeSpan.FromSeconds(3);
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(delay)).ConfigureAwait(true);
        if (completed != tcs.Task)
        {
            _valueWaiters.TryRemove(id, out _);
            return ProgramCatalog.NormalizeSourceText(_pendingSource ?? _catalog.ReadFile(_program, _currentRelativePath));
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
        var formatted = FormatForCurrentFile(text);
        if (!string.Equals(text, formatted, StringComparison.Ordinal))
        {
            text = formatted;
            _pendingSource = formatted;
            _lineCount = ProgramEditorLimits.CountLines(formatted);
            await SendEditorAsync(new
            {
                type = "setValue",
                value = formatted,
                language = MonacoLanguageForCurrentFile()
            }).ConfigureAwait(true);
            RefreshBpHint();
        }
        if (!await EnsureWithinLineLimitAsync(text)) return;
        var isSchema = ProgramCatalog.IsConfigSchemaFile(_currentRelativePath);
        if (!isSchema && (_program.IsBuiltin
            || string.Equals(_program.Id, BuiltinPrograms.GenericSwipeId, StringComparison.OrdinalIgnoreCase)))
        {
            await DisplayAlertAsync(_saveLabel, Loc.T("Prog.BuiltinSourceReadonly"), "OK");
            return;
        }
        _catalog.WriteFile(_program, _currentRelativePath, text);
        _pendingSource = text;
        _baselineText = text;
        AppLog.Info($"已保存程序「{_program.Name}」");
    }

    private async Task<bool> PromptSaveIfDirtyAsync()
    {
        if (IsSourceReadOnly && !ProgramCatalog.IsConfigSchemaFile(_currentRelativePath))
            return true;
        var text = await GetEditorTextAsync();
        if (string.Equals(
                ProgramCatalog.NormalizeSourceText(text),
                ProgramCatalog.NormalizeSourceText(_baselineText),
                StringComparison.Ordinal))
            return true;
        var action = await DisplayActionSheetAsync(
            Loc.T("Prog.UnsavedChanges"),
            Loc.T("Prog.DlgCancel"),
            destruction: null,
            Loc.T("Prog.Save"),
            Loc.T("Prog.DontSave"));
        if (string.IsNullOrEmpty(action) || action == Loc.T("Prog.DlgCancel"))
            return false;
        if (action == Loc.T("Prog.Save"))
            await SaveFromEditorAsync(text);
        return true;
    }

    private void RefreshFilePicker()
    {
        BtnFileSwitch.Text = ProgramEditorFileBadge.DisplayName(_currentRelativePath) + " ▾";
        Title = $"{Loc.T("Prog.EditorTitle")} — {_program.Name} · {_program.LanguageText} · {_currentRelativePath}";
        RebuildFileSwitchList();
    }

    private void RebuildFileSwitchList()
    {
        FileSwitchList.Children.Clear();
        foreach (var f in _catalog.ListEditorFiles(_program))
            FileSwitchList.Children.Add(BuildFileSwitchRow(f));
    }

    private View BuildFileSwitchRow(string relativePath)
    {
        var isCurrent = string.Equals(relativePath, _currentRelativePath, StringComparison.OrdinalIgnoreCase);
        var isEntry = string.Equals(relativePath, _program.FileName, StringComparison.OrdinalIgnoreCase);
        var canDelete = !isEntry && !IsSourceReadOnly
                        && !ProgramCatalog.IsConfigSchemaFile(relativePath);

        var badge = new Label
        {
            Text = ProgramEditorFileBadge.ForPath(relativePath),
            FontSize = 10,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#2563EB"),
            VerticalOptions = LayoutOptions.Center,
            Margin = new Thickness(0, 0, 8, 0),
            MinimumWidthRequest = 22
        };
        var name = new Label
        {
            Text = ProgramEditorFileBadge.DisplayName(relativePath),
            FontSize = 13,
            FontAttributes = isCurrent ? FontAttributes.Bold : FontAttributes.None,
            VerticalOptions = LayoutOptions.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
            HorizontalOptions = LayoutOptions.Fill
        };
        var del = new Button
        {
            Text = "×",
            WidthRequest = 28,
            HeightRequest = 28,
            Padding = new Thickness(0),
            FontSize = 16,
            BackgroundColor = Colors.Transparent,
            TextColor = Color.FromArgb("#6B7280"),
            IsVisible = canDelete
        };
        ToolTipProperties.SetText(del, Loc.T("Prog.RemoveFile"));
        del.Clicked += async (_, _) =>
        {
            FileSwitchOverlay.IsVisible = false;
            await RemoveFileAtAsync(relativePath);
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Auto),
                new(GridLength.Star),
                new(GridLength.Auto)
            },
            Padding = new Thickness(8, 6),
            BackgroundColor = isCurrent ? Color.FromArgb("#DBEAFE") : Colors.Transparent
        };
        row.Add(badge);
        Grid.SetColumn(name, 1);
        row.Add(name);
        Grid.SetColumn(del, 2);
        row.Add(del);

        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            FileSwitchOverlay.IsVisible = false;
            await SwitchToFileAsync(relativePath);
        };
        row.GestureRecognizers.Add(tap);
        return row;
    }

    private void FileSwitch_Clicked(object? sender, EventArgs e)
    {
        RebuildFileSwitchList();
        // 相对根布局定位到文件按钮下方
        var btn = GetOffsetIn(BtnFileSwitch, RootLayout);
        var top = Math.Max(36, btn.Y + BtnFileSwitch.Height + 4);
        var right = Math.Max(0, RootLayout.Width - (btn.X + BtnFileSwitch.Width));
        FileSwitchPanel.Margin = new Thickness(0, top, right, 0);
        FileSwitchOverlay.IsVisible = true;
    }

    private static Point GetOffsetIn(VisualElement child, VisualElement ancestor)
    {
        double x = 0, y = 0;
        VisualElement? cur = child;
        while (cur != null && !ReferenceEquals(cur, ancestor))
        {
            x += cur.X;
            y += cur.Y;
            cur = cur.Parent as VisualElement;
        }
        return new Point(x, y);
    }

    private void FileSwitchOverlay_Tapped(object? sender, TappedEventArgs e)
        => FileSwitchOverlay.IsVisible = false;

    private async Task SwitchToFileAsync(string next)
    {
        if (string.IsNullOrWhiteSpace(next)) return;
        var same = string.Equals(next, _currentRelativePath, StringComparison.OrdinalIgnoreCase);
        try
        {
            if (!same && !await PromptSaveIfDirtyAsync()) return;
            _currentRelativePath = next;
            // 同路径也强制重载，避免徽标已切、Monaco 仍是旧缓冲
            _pendingSource = _catalog.ReadFile(_program, _currentRelativePath);
            _baselineText = _pendingSource ?? "";
            RefreshFilePicker();
            await PushCurrentFileToEditorAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Title, ex.Message, "OK");
            RefreshFilePicker();
        }
    }

    private async void AddFile_Clicked(object? sender, EventArgs e)
    {
        if (IsSourceReadOnly) return;
        var ext = ProgramLanguageInfo.Extension(_program.Language);
        var suggested = SuggestExtraPath(ext);
        var path = await DisplayPromptAsync(Loc.T("Prog.NewFile"), Loc.T("Prog.AddFilePrompt"),
            initialValue: suggested, maxLength: 120);
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (!await PromptSaveIfDirtyAsync()) return;
            var rel = _catalog.AddFile(_program, path.Trim());
            _currentRelativePath = rel;
            _pendingSource = _catalog.ReadFile(_program, rel);
            _baselineText = _pendingSource ?? "";
            RefreshFilePicker();
            await PushCurrentFileToEditorAsync();
        }
        catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); }
    }

    private async Task RemoveFileAtAsync(string relativePath)
    {
        if (IsSourceReadOnly) return;
        if (string.Equals(relativePath, _program.FileName, StringComparison.OrdinalIgnoreCase))
        {
            await DisplayAlertAsync(Title, Loc.T("Prog.CannotRemoveEntry"), "OK");
            return;
        }
        if (!await DisplayAlertAsync(Title, Loc.T("Prog.RemoveFile") + "?\n" + relativePath, "OK", "Cancel"))
            return;
        try
        {
            _catalog.RemoveFile(_program, relativePath);
            if (string.Equals(_currentRelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                _currentRelativePath = _program.FileName;
                _pendingSource = _catalog.ReadFile(_program, _currentRelativePath);
                _baselineText = _pendingSource ?? "";
                await PushCurrentFileToEditorAsync();
            }
            RefreshFilePicker();
            RefreshBpHint();
        }
        catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); }
    }

    private async void FlySettings_Clicked(object? sender, EventArgs e)
    {
        try
        {
            var page = new ProgramFlySettingsDesignerPage(_program);
            page.BackgroundColor = Colors.Transparent;
            await Navigation.PushModalAsync(page, animated: true);
            await page.WhenClosed;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync(Title, ex.Message, "OK");
        }
    }

    private async void Deps_Clicked(object? sender, EventArgs e)
    {
        if (_program.Language != ProgramLanguage.CSharp) return;
        _catalog.NormalizeManifest(_program);
        var lines = new List<string>();
        foreach (var p in _program.Packages)
            lines.Add($"NuGet: {p.Id} {p.Version}");
        foreach (var d in _program.LocalDlls)
            lines.Add($"DLL: {d}");
        var summary = lines.Count == 0 ? "(empty)" : string.Join("\n", lines);
        var action = await DisplayActionSheetAsync(
            Loc.T("Prog.DepsTitle") + "\n" + summary,
            "Cancel",
            null,
            Loc.T("Prog.AddNuget"),
            Loc.T("Prog.AddDll"),
            Loc.T("Prog.RemoveDep"));
        if (action == Loc.T("Prog.AddNuget"))
        {
            var id = await DisplayPromptAsync(Loc.T("Prog.AddNuget"), Loc.T("Prog.NugetId"));
            if (string.IsNullOrWhiteSpace(id)) return;
            var ver = await DisplayPromptAsync(Loc.T("Prog.AddNuget"), Loc.T("Prog.NugetVersion")) ?? "";
            _program.Packages.RemoveAll(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
            _program.Packages.Add(new NugetPackageRef { Id = id.Trim(), Version = ver.Trim() });
            ProgramNugetResolver.SyncCsproj(_catalog, _program);
            _catalog.SaveMeta(_program);
            if (!ProgramNugetResolver.CanRestoreOnline)
                await DisplayAlertAsync(Title, Loc.T("Prog.NugetMobileHint"), "OK");
        }
        else if (action == Loc.T("Prog.AddDll"))
        {
            await DisplayAlertAsync(Title,
                "请将 DLL 复制到程序目录 refs/ 后，在电脑端用「添加本地 DLL」登记；或手动把相对路径写入 LocalDlls。",
                "OK");
        }
        else if (action == Loc.T("Prog.RemoveDep"))
        {
            if (_program.Packages.Count > 0)
            {
                _program.Packages.RemoveAt(_program.Packages.Count - 1);
                ProgramNugetResolver.SyncCsproj(_catalog, _program);
                _catalog.SaveMeta(_program);
            }
            else if (_program.LocalDlls.Count > 0)
            {
                try { _catalog.RemoveLocalDll(_program, _program.LocalDlls[^1]); }
                catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); }
            }
        }
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
        var entryOverride = string.Equals(_currentRelativePath, _program.FileName, StringComparison.OrdinalIgnoreCase)
            ? text
            : null;
        var result = await ProgramCompiler.CompileProgramAsync(_catalog, _program, entryOverride);
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
            // 编辑器以 Modal 打开；勿 PopAsync，避免 Shell 根页被弹出导致主窗关闭
            if (Navigation.ModalStack.Count > 0)
                await Navigation.PopModalAsync();
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
