namespace FlyScreen.AppMaui.Controls;

/// <summary>
/// 组字输入框：Android 使用独立 Handler + AppCompatEditText，避免 MAUI Entry 的
/// NoSuggestions 等 InputType 导致系统拼音不出候选。
/// </summary>
public class ComposeImeView : Entry
{
}
