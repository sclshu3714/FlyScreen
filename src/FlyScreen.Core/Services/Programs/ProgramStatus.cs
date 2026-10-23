namespace FlyScreen.Services.Programs;

public enum ProgramStatus
{
    Idle = 0,
    Running = 1,
    Stopping = 2,
    Error = 3
}

public static class ProgramStatusInfo
{
    public static string Display(ProgramStatus status) => status switch
    {
        ProgramStatus.Running => "运行中",
        ProgramStatus.Stopping => "停止中",
        ProgramStatus.Error => "错误",
        _ => "未运行"
    };
}
