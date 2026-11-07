namespace FlyScreen.Uninstaller.Services;

public readonly record struct ProgressReport(double Percent, string Message)
{
    public static ProgressReport Create(double percent, string message)
        => new(Math.Clamp(percent, 0, 100), message ?? "");
}
