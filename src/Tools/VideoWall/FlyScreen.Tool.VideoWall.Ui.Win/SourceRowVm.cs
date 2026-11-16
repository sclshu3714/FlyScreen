using System.ComponentModel;
using System.Runtime.CompilerServices;
using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

public sealed class SourceRowVm : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public VideoSourceKind Kind { get; init; }
    public string KindText { get; init; } = "";
    public required string AddressRedacted { get; init; }
    public string? Group { get; init; }
    public required string StatusText { get; init; }
    public string? CoverPath { get; init; }

    public VideoSourceDescriptor? Source { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string PlayLabel => Loc.T("Tool.VideoWall.Play");
    public string EditLabel => Loc.T("Tool.VideoWall.Editing");
    public string DeleteLabel => Loc.T("Tool.VideoWall.Delete");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
