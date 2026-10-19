using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Programs;

namespace FlyScreen.AppMaui;

public partial class ProgramEditorPage : ContentPage
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private readonly TaskCompletionSource _closed = new();
    private int _caretLine = 1;

    public Task WhenClosed => _closed.Task;

    public ProgramEditorPage(UserProgram program)
    {
        InitializeComponent();
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _runner = ProgramService.Instance.Runner;

        Title = Loc.T("Prog.EditorTitle") + " — " + program.Name;
        TitleText.Text = $"{program.Name}  ·  {program.LanguageText}  ·  {program.FileName}";
        BtnSave.Text = Loc.T("Prog.Save");
        BtnDebug.Text = Loc.T("Prog.Debug");
        BtnBreakpoint.Text = Loc.T("Prog.Breakpoint");
        BtnClearBp.Text = Loc.T("Prog.ClearBp");
        BtnClearAllBp.Text = Loc.T("Prog.ClearAllBp");
        BtnRun.Text = Loc.T("Prog.Run");
        BtnStop.Text = Loc.T("Prog.StopRun");

        CodeBox.Text = _catalog.ReadSource(program);
        RefreshBpHint();
        CodeBox.TextChanged += (_, _) => EstimateCaretLine();
    }

    private void EstimateCaretLine()
    {
        // Editor 无可靠 CaretIndex 跨平台 API：用文本行数作为近似，断点打在末行时可手改
        var text = CodeBox.Text ?? "";
        _caretLine = Math.Max(1, text.Split('\n').Length);
        RefreshBpHint();
    }

    private void RefreshBpHint()
    {
        var bps = _program.Breakpoints.Count == 0
            ? "无断点"
            : "断点: " + string.Join(",", _program.Breakpoints.OrderBy(x => x));
        BpHintText.Text = $"当前行约 {_caretLine}  ·  {bps}（移动端断点以末行为准，可后续扩展）";
    }

    private void Save_Clicked(object? sender, EventArgs e)
    {
        _catalog.WriteSource(_program, CodeBox.Text ?? "");
        _catalog.SaveMeta(_program);
        AppLog.Info($"已保存程序「{_program.Name}」");
    }

    private async void Debug_Clicked(object? sender, EventArgs e)
    {
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
        }
    }

    private void Breakpoint_Clicked(object? sender, EventArgs e)
    {
        EstimateCaretLine();
        if (!_program.Breakpoints.Contains(_caretLine))
            _program.Breakpoints.Add(_caretLine);
        _program.Breakpoints.Sort();
        _catalog.SaveMeta(_program);
        RefreshBpHint();
    }

    private void ClearBp_Clicked(object? sender, EventArgs e)
    {
        EstimateCaretLine();
        _program.Breakpoints.Remove(_caretLine);
        _catalog.SaveMeta(_program);
        RefreshBpHint();
    }

    private void ClearAllBp_Clicked(object? sender, EventArgs e)
    {
        _program.Breakpoints.Clear();
        _catalog.SaveMeta(_program);
        RefreshBpHint();
    }

    private async void Run_Clicked(object? sender, EventArgs e)
    {
        SaveSilent();
        try { await _runner.StartAsync(_program); }
        catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); }
    }

    private async void Stop_Clicked(object? sender, EventArgs e)
    {
        try { await _runner.StopAsync(_program); }
        catch (Exception ex) { await DisplayAlertAsync(Title, ex.Message, "OK"); }
    }

    private async void Close_Clicked(object? sender, EventArgs e)
    {
        _closed.TrySetResult();
        await Navigation.PopModalAsync();
    }

    protected override void OnDisappearing()
    {
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
