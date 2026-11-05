namespace FlyScreen.Setup.Models;

public readonly record struct PublishProgress(double Percent, string Message)
{
    public static PublishProgress Create(double percent, string message)
        => new(Math.Clamp(percent, 0, 100), message ?? "");
}
