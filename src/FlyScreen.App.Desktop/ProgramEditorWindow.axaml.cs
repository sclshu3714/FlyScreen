using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Programs;

namespace FlyScreen.DesktopUi;

public partial class ProgramEditorWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private int _caretLine = 1;

    public ProgramEditorWindow() : this(new UserProgram { Name = "design" })
    {
    }

    public ProgramEditorWindow(UserProgram program)
    {
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _runner = ProgramService.Instance.Runner;
        InitializeComponent();

        Title = Loc.T("Prog.EditorTitle") + " — " + program.Name;
        TitleText.Text = $"{program.Name}  ·  {program.LanguageText}  ·  {program.FileName}";
        BtnSave.Content = Loc.T("Prog.Save");
        BtnDebug.Content = Loc.T("Prog.Debug");
        BtnBreakpoint.Content = Loc.T("Prog.Breakpoint");
        BtnClearBp.Content = Loc.T("Prog.ClearBp");
        BtnClearAllBp.Content = Loc.T("Prog.ClearAllBp");
        BtnRun.Content = Loc.T("Prog.Run");
        BtnStop.Content = Loc.T("Prog.StopRun");

        CodeBox.Text = _catalog.ReadSource(program);
        RefreshLineNumbers();
        RefreshBpHint();
        CodeBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.CaretIndexProperty)
                UpdateCaretLine();
        };
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

    private void RefreshLineNumbers()
    {
        var text = CodeBox.Text ?? "";
        var lines = Math.Max(1, text.Split('\n').Length);
        var sb = new StringBuilder();
        for (var i = 1; i <= lines; i++)
        {
            var mark = _program.Breakpoints.Contains(i) ? "●" : " ";
            sb.Append(mark).Append(i.ToString().PadLeft(3)).Append('\n');
        }
        LineNumbers.Text = sb.ToString().TrimEnd('\n');
    }

    private void RefreshBpHint()
    {
        var bps = _program.Breakpoints.Count == 0
            ? "无断点"
            : "断点: " + string.Join(",", _program.Breakpoints.OrderBy(x => x));
        BpHintText.Text = $"行 {_caretLine}  ·  {bps}";
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _catalog.WriteSource(_program, CodeBox.Text ?? "");
        _catalog.SaveMeta(_program);
        AppLog.Info($"已保存程序「{_program.Name}」");
    }

    private async void Debug_Click(object? sender, RoutedEventArgs e)
    {
        var original = CodeBox.Text ?? "";
        SaveSilent();
        if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
            InjectPythonBreakpoints();
        else
            AppLog.Info($"调试「{_program.Name}」：断点已记录（完整调试器后续扩展）");
        try { await _runner.StartAsync(_program, debug: true); }
        catch (Exception ex) { AppLog.Error(ex, "调试启动失败"); }
        finally
        {
            if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
            {
                await Task.Delay(300);
                _catalog.WriteSource(_program, original);
            }
        }
    }

    private void Breakpoint_Click(object? sender, RoutedEventArgs e)
    {
        UpdateCaretLine();
        if (!_program.Breakpoints.Contains(_caretLine))
            _program.Breakpoints.Add(_caretLine);
        _program.Breakpoints.Sort();
        _catalog.SaveMeta(_program);
        RefreshLineNumbers();
        RefreshBpHint();
    }

    private void ClearBp_Click(object? sender, RoutedEventArgs e)
    {
        UpdateCaretLine();
        _program.Breakpoints.Remove(_caretLine);
        _catalog.SaveMeta(_program);
        RefreshLineNumbers();
        RefreshBpHint();
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
        SaveSilent();
        try { await _runner.StartAsync(_program); }
        catch (Exception ex) { AppLog.Error(ex, "运行失败"); }
    }

    private async void Stop_Click(object? sender, RoutedEventArgs e)
    {
        try { await _runner.StopAsync(_program); }
        catch (Exception ex) { AppLog.Error(ex, "停止失败"); }
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
