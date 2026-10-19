using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlyScreen.Helpers;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services;
using FlyScreen.Services.Programs;

namespace FlyScreen;

public partial class ProgramEditorWindow : Window
{
    private readonly UserProgram _program;
    private readonly ProgramCatalog _catalog;
    private readonly ProgramRunner _runner;
    private int _caretLine = 1;
    private bool _toolModeUpdating;

    public static readonly DependencyProperty ToolsCompactProperty =
        DependencyProperty.Register(nameof(ToolsCompact), typeof(bool), typeof(ProgramEditorWindow), new PropertyMetadata(false));

    public bool ToolsCompact
    {
        get => (bool)GetValue(ToolsCompactProperty);
        set => SetValue(ToolsCompactProperty, value);
    }

    private string BaseTitle =>
        $"{Loc.T("Prog.EditorTitle")} — {_program.Name} · {_program.LanguageText} · {_program.FileName}";

    public ProgramEditorWindow(UserProgram program)
    {
        InitializeComponent();
        ThemeService.ApplyToChildWindow(this);
        Loaded += (_, _) => ThemeService.ApplyToChildWindow(this);
        _program = program;
        _catalog = ProgramService.Instance.Catalog;
        _runner = ProgramService.Instance.Runner;

        Title = BaseTitle;
        SetToolButton(BtnSave, BtnSaveText, "Prog.Save");
        SetToolButton(BtnDebug, BtnDebugText, "Prog.Debug");
        SetToolButton(BtnClearAllBp, BtnClearAllBpText, "Prog.ClearAllBp");
        SetToolButton(BtnRun, BtnRunText, "Prog.Run");
        SetToolButton(BtnStop, BtnStopText, "Prog.StopRun");

        CodeBox.Text = _catalog.ReadSource(program);
        RefreshLineNumbers();
        RefreshBpHint();

        CodeBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(CodeBox_ScrollChanged), true);
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
            ToolsCompact = false;
            ToolBar.Measure(new System.Windows.Size(double.PositiveInfinity, 48));
            ToolsCompact = ToolBar.DesiredSize.Width > ToolBarHost.ActualWidth + 0.5;
        }
        finally
        {
            _toolModeUpdating = false;
        }
    }

    private void CodeBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange == 0) return;
        LineNumbers.ScrollToVerticalOffset(e.VerticalOffset);
    }

    private void CodeBox_TextChanged(object sender, TextChangedEventArgs e)
        => RefreshLineNumbers();

    private void CodeBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var idx = CodeBox.CaretIndex;
        var text = CodeBox.Text ?? "";
        _caretLine = 1;
        for (var i = 0; i < idx && i < text.Length; i++)
            if (text[i] == '\n') _caretLine++;
        RefreshBpHint();
    }

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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _catalog.WriteSource(_program, CodeBox.Text ?? "");
        _catalog.SaveMeta(_program);
        AppLog.Info($"已保存程序「{_program.Name}」", _program.Id, _program.Name);
        StatusTextToast("已保存");
    }

    private async void Debug_Click(object sender, RoutedEventArgs e)
    {
        var original = CodeBox.Text ?? "";
        SaveSilent();
        if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
            InjectPythonBreakpointsBeforeRun();
        else
            AppLog.Info($"调试「{_program.Name}」：断点已记录（完整调试器后续扩展）", _program.Id, _program.Name);
        try
        {
            await _runner.StartAsync(_program, debug: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            // 磁盘恢复为编辑器原文，避免把 breakpoint() 写回工程
            if (_program.Language == ProgramLanguage.Python && _program.Breakpoints.Count > 0)
            {
                await Task.Delay(300);
                _catalog.WriteSource(_program, original);
            }
        }
    }

    private void LineNumbers_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var line = LineFromGutterClick(e.GetPosition(LineNumbers));
        if (line == null) return;
        ToggleBreakpoint(line.Value);
        e.Handled = true;
    }

    private int? LineFromGutterClick(System.Windows.Point point)
    {
        var index = LineNumbers.GetCharacterIndexFromPoint(point, snapToText: true);
        var text = LineNumbers.Text ?? "";
        if (index < 0 || text.Length == 0) return null;
        if (index > text.Length) index = text.Length;
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        var total = Math.Max(1, (CodeBox.Text ?? "").Replace("\r\n", "\n").Split('\n').Length);
        return line >= 1 && line <= total ? line : null;
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
        RefreshLineNumbers();
        RefreshBpHint();
    }

    private void ClearAllBp_Click(object sender, RoutedEventArgs e)
    {
        _program.Breakpoints.Clear();
        _catalog.SaveMeta(_program);
        RefreshLineNumbers();
        RefreshBpHint();
        AppLog.Info($"「{_program.Name}」已清除全部断点", _program.Id, _program.Name);
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        SaveSilent();
        try { await _runner.StartAsync(_program, debug: false); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try { await _runner.StopAsync(_program); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SaveSilent()
    {
        _catalog.WriteSource(_program, CodeBox.Text ?? "");
        _catalog.SaveMeta(_program);
    }

    /// <summary>Python 基础调试：在断点行前插入 breakpoint()（保存临时覆盖，运行后由用户源文件仍为编辑器内容）。</summary>
    private void InjectPythonBreakpointsBeforeRun()
    {
        var lines = (CodeBox.Text ?? "").Replace("\r\n", "\n").Split('\n').ToList();
        var inject = _program.Breakpoints.Where(l => l >= 1 && l <= lines.Count).Distinct().OrderByDescending(l => l);
        foreach (var line in inject)
        {
            var idx = line - 1;
            if (!lines[idx].Contains("breakpoint()", StringComparison.Ordinal))
                lines.Insert(idx, "breakpoint()  # flyscreen-bp");
        }
        _catalog.WriteSource(_program, string.Join("\n", lines));
        // 还原编辑器展示为未注入版本（磁盘为调试副本）— 用户下次保存会覆盖
        AppLog.Info($"已为「{_program.Name}」注入 {_program.Breakpoints.Count} 处 Python breakpoint()", _program.Id, _program.Name);
    }

    private void StatusTextToast(string msg)
    {
        Title = BaseTitle + "  ·  " + msg;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => { Title = BaseTitle; timer.Stop(); };
        timer.Start();
    }
}
