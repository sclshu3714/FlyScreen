using FlyScreen.Helpers.Localization;
using FlyScreen.Tool.VideoWall.Contracts;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

public sealed class SourceRowVm
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public VideoSourceKind Kind { get; init; }
    public string KindText { get; init; } = "";
    public required string AddressRedacted { get; init; }
    public string? Group { get; init; }
    public required string StatusText { get; init; }
    public string? CoverPath { get; init; }

    public VideoSourceDescriptor? Source { get; init; }

    public string PlayLabel => Loc.T("Tool.VideoWall.Play");
    public string EditLabel => Loc.T("Tool.VideoWall.Editing");
    public string DeleteLabel => Loc.T("Tool.VideoWall.Delete");
}