using FlyScreen.Tool.VideoWall.Contracts;
using FlyScreen.Tool.VideoWall.Sources;

namespace FlyScreen.Tool.VideoWall.Ui.Win;

internal static class SourceDescriptorBuilder
{
    public static VideoSourceDescriptor? TryBuild(
        VideoSourceKind kind,
        IReadOnlyDictionary<string, string?> values,
        CredentialStore credentials,
        out string? error)
        => Sources.SourceDescriptorBuilder.TryBuild(kind, values, credentials, out error);
}
