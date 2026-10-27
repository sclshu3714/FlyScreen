namespace FlyScreen.Helpers;

/// <summary>
/// 自定义确认框结果。Primary / Secondary 对应主次操作；Cancel 为取消。
/// 例：删除文件=Primary，仅删记录=Secondary。
/// </summary>
public enum UiConfirmChoice
{
    Cancel = 0,
    Secondary = 1,
    Primary = 2,
}
