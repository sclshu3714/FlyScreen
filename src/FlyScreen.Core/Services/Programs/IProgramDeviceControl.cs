namespace FlyScreen.Services.Programs;

/// <summary>
/// 用户程序控本机（手机）的宿主能力。由各端注册实现；未注册时脚本调用会失败并写日志。
/// <para>坐标约定：</para>
/// <list type="bullet">
/// <item><description>归一化坐标 <c>nx/ny</c>：相对全屏 0..1（左上为 0,0，右下为 1,1）。</description></item>
/// <item><description>像素坐标：与 <see cref="ScreenSize"/> 同源的屏幕像素。</description></item>
/// </list>
/// </summary>
public interface IProgramDeviceControl
{
    /// <summary>无障碍等前置条件是否就绪。</summary>
    bool IsReady { get; }

    /// <summary>当前前台应用包名；不可用时为 null。</summary>
    string? ForegroundPackage { get; }

    /// <summary>屏幕像素宽高；失败时为 (0,0)。</summary>
    (int Width, int Height) ScreenSize { get; }

    /// <summary>
    /// 当前显示旋转角（度）：0 / 90 / 180 / 270。
    /// 与选区无关（选区仍按宽高）；供日志排查四向放置。不可用时为 0。
    /// </summary>
    int DisplayRotationDegrees { get; }

    /// <summary>
    /// 系统 UI 朝向是否横屏（Android <c>Configuration.orientation</c>）。
    /// null 表示宿主未提供，由 <see cref="ScreenSize"/> 宽高推断。
    /// 用于纠正部分机型 GetRealSize 与真实朝向不一致时误选横/竖识别区。
    /// </summary>
    bool? UiIsLandscape { get; }

    /// <summary>可见节点文本/描述中是否包含指定文案（子串匹配）。</summary>
    bool ContainsText(string text);

    /// <summary>可见节点中是否存在指定 viewId（如 <c>com.app:id/btn</c>）。</summary>
    bool ContainsViewId(string viewId);

    /// <summary>可见节点 ContentDescription 是否包含指定描述（子串）。</summary>
    bool ContainsDesc(string desc);

    /// <summary>收集当前可见文本（去重，最多 <paramref name="max"/> 条）。</summary>
    IReadOnlyList<string> CollectTexts(int max = 80);

    /// <summary>
    /// 查找第一个包含文案的节点屏幕包围盒，返回 <c>[left, top, right, bottom]</c>（像素）；
    /// 找不到返回 null。
    /// </summary>
    int[]? FindTextBounds(string text);

    /// <summary>
    /// 截取屏幕矩形区域做 OCR，返回识别到的全部文本；失败或无文字返回空字符串。
    /// 坐标为屏幕像素，可为任意对角顺序。
    /// </summary>
    string OcrRegion(int left, int top, int right, int bottom);

    /// <summary>
    /// 在屏幕上描边调试矩形，持续 <paramref name="durationMs"/> 毫秒后消失。
    /// <paramref name="argb"/> 为 0xAARRGGBB（如红色 0xFFFF0000）。
    /// </summary>
    bool DrawRect(int left, int top, int right, int bottom, uint argb, int durationMs);

    /// <summary>
    /// 按 id 显示/更新常驻调试框（识别区、操作区可并存）。像素坐标。
    /// </summary>
    bool SetDebugRect(string id, int left, int top, int right, int bottom, uint argb);

    /// <summary>
    /// 按 id 显示/更新常驻调试框（归一化 0..1）；旋转后宿主按新屏尺寸重放。
    /// </summary>
    bool SetDebugRectNorm(string id, double x, double y, double w, double h, uint argb);

    /// <summary>清除指定 id 的调试框；id 为空则清除全部。</summary>
    bool ClearDebugRect(string? id = null);

    /// <summary>点击第一个包含文案的节点中心；找不到返回 false。</summary>
    bool TapText(string text);

    /// <summary>长按第一个包含文案的节点中心。</summary>
    bool LongPressText(string text, int durationMs = 700);

    /// <summary>点击指定 viewId 的节点中心。</summary>
    bool TapViewId(string viewId);

    /// <summary>点击 ContentDescription 包含指定描述的节点中心。</summary>
    bool TapDesc(string desc);

    /// <summary>按归一化坐标点击（0..1）。</summary>
    bool Tap(double nx, double ny);

    /// <summary>按屏幕像素坐标点击。</summary>
    bool TapPx(int x, int y);

    /// <summary>按归一化坐标双击。</summary>
    bool DoubleTap(double nx, double ny);

    /// <summary>按归一化坐标长按；<paramref name="durationMs"/> 为按住时长。</summary>
    bool LongPress(double nx, double ny, int durationMs = 700);

    /// <summary>按归一化坐标滑动。</summary>
    bool Swipe(double x1, double y1, double x2, double y2, int durationMs = 320);

    /// <summary>屏幕中部向上滑（distance 为屏高比例）。</summary>
    bool SwipeUp(double distance = 0.45, int durationMs = 320);

    /// <summary>屏幕中部向下滑。</summary>
    bool SwipeDown(double distance = 0.45, int durationMs = 320);

    /// <summary>屏幕中部向左滑（distance 为屏宽比例）。</summary>
    bool SwipeLeft(double distance = 0.45, int durationMs = 320);

    /// <summary>屏幕中部向右滑。</summary>
    bool SwipeRight(double distance = 0.45, int durationMs = 320);

    /// <summary>对当前可滚动节点执行向前滚动（无障碍 ScrollForward）；失败时尝试上滑手势。</summary>
    bool ScrollForward();

    /// <summary>对当前可滚动节点执行向后滚动；失败时尝试下滑手势。</summary>
    bool ScrollBackward();

    /// <summary>
    /// 轮询直到出现指定文案或超时。成功返回 true。
    /// </summary>
    bool WaitText(string text, int timeoutMs = 8000, int pollMs = 250);

    /// <summary>
    /// 轮询直到指定文案消失或超时。成功（已消失）返回 true。
    /// </summary>
    bool WaitGone(string text, int timeoutMs = 8000, int pollMs = 250);

    /// <summary>向当前焦点可编辑框写入文本（覆盖原内容）。</summary>
    bool Input(string text);

    /// <summary>清空当前焦点可编辑框。</summary>
    bool ClearInput();

    /// <summary>对焦点编辑框执行回车/确认（IME Enter）。</summary>
    bool Enter();

    /// <summary>系统返回。</summary>
    bool Back();

    /// <summary>系统 Home。</summary>
    bool Home();

    /// <summary>打开最近任务。</summary>
    bool Recents();

    /// <summary>打开通知栏。</summary>
    bool Notifications();

    /// <summary>打开快捷设置（部分系统支持）。</summary>
    bool QuickSettings();

    /// <summary>锁屏（Android 9+，部分机型可能无效）。</summary>
    bool LockScreen();

    /// <summary>触发系统截屏（Android 9+）。</summary>
    bool TakeScreenshot();

    /// <summary>打开系统无障碍设置（引导用户开启玄鸟注入）。</summary>
    void OpenAccessibilitySettings();
}

/// <summary>宿主注册入口；脚本通过 <see cref="Fly"/> 调用。</summary>
public static class ProgramDevice
{
    public static IProgramDeviceControl? Current { get; set; }
}
