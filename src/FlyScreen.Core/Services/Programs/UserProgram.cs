using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

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
        }
    }

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
