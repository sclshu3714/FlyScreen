using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.Services.Programs;

public sealed class UserProgram : INotifyPropertyChanged
{
    private bool _isSelected;
    private ProgramStatus _runtimeStatus = ProgramStatus.Idle;
    private string _sizeText = "—";
    private string _statusText = ProgramStatusInfo.Display(ProgramStatus.Idle);

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ProgramLanguage Language { get; set; } = ProgramLanguage.Python;
    public string Namespace { get; set; } = "com.xuanniao.app";
    public string FileName { get; set; } = "main.py";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<int> Breakpoints { get; set; } = [];

    /// <summary>安装包内置程序：不可删除；源码由 EnsureBuiltin 按版本更新。</summary>
    public bool IsBuiltin { get; set; }

    /// <summary>内置源码版本；小于嵌入资源版本时覆盖 main 源文件。</summary>
    public int BuiltinSourceVersion { get; set; }

    [JsonIgnore]
    public string FolderName => Id;

    [JsonIgnore]
    public string LanguageText => ProgramLanguageInfo.DisplayName(Language);

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public ProgramStatus RuntimeStatus
    {
        get => _runtimeStatus;
        set
        {
            if (_runtimeStatus == value) return;
            _runtimeStatus = value;
            StatusText = ProgramStatusInfo.Display(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActivelyRunning));
            OnPropertyChanged(nameof(IsIdleForAction));
            OnPropertyChanged(nameof(ActionToggleGlyph));
            OnPropertyChanged(nameof(ActionToggleTip));
        }
    }

    /// <summary>列表行启停按钮：运行中/停止中视为可点「停止」。</summary>
    [JsonIgnore]
    public bool IsActivelyRunning
        => _runtimeStatus is ProgramStatus.Running or ProgramStatus.Stopping;

    /// <summary>与 <see cref="IsActivelyRunning"/> 相反，便于 XAML 绑定可见性。</summary>
    [JsonIgnore]
    public bool IsIdleForAction => !IsActivelyRunning;

    /// <summary>列表行启停按钮字形：▶ 启动 / ■ 停止。</summary>
    [JsonIgnore]
    public string ActionToggleGlyph => IsActivelyRunning ? "■" : "▶";

    /// <summary>列表行启停按钮提示。</summary>
    [JsonIgnore]
    public string ActionToggleTip
        => IsActivelyRunning ? Loc.T("Prog.ActStop") : Loc.T("Prog.ActStart");

    [JsonIgnore]
    public string SizeText
    {
        get => _sizeText;
        set { if (_sizeText == value) return; _sizeText = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string StatusText
    {
        get => _statusText;
        set { if (_statusText == value) return; _statusText = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
