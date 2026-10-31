namespace FlyScreen.Services.Programs;

/// <summary>
/// 用户程序脚本 API（C# / JS 共用语义）。
/// <para>
/// <b>前置条件</b>：在手机玄鸟上运行，并开启「玄鸟键鼠注入」无障碍服务后，
/// <see cref="Ready"/> 为 true；电脑端无设备实现，调用会写警告日志并返回失败值。
/// </para>
/// <para>
/// <b>坐标</b>：归一化 <c>0..1</c>（左上→右下）；像素与 <see cref="ScreenWidth"/>/<see cref="ScreenHeight"/> 同源。
/// </para>
/// </summary>
public static class Fly
{
    /// <summary>
    /// 无障碍是否就绪。为 false 时应调用 <see cref="OpenSettings"/> 引导用户开启，
    /// 或结束脚本等待用户开启后再跑。
    /// </summary>
    public static bool Ready
        => ProgramDevice.Current?.IsReady == true;

    /// <summary>
    /// 当前前台应用包名（如 <c>com.kylin.read</c>）。
    /// 服务未就绪或读不到窗口树时为 null。
    /// </summary>
    public static string? App
        => ProgramDevice.Current?.ForegroundPackage;

    /// <summary>屏幕宽度（像素）。失败时为 0。</summary>
    public static int ScreenWidth
        => ProgramDevice.Current?.ScreenSize.Width ?? 0;

    /// <summary>屏幕高度（像素）。失败时为 0。</summary>
    public static int ScreenHeight
        => ProgramDevice.Current?.ScreenSize.Height ?? 0;

    /// <summary>
    /// 当前可见无障碍节点的文本或 ContentDescription 是否包含 <paramref name="text"/>（子串匹配）。
    /// 适合判断广告提示、按钮文案是否出现。
    /// </summary>
    /// <param name="text">要查找的文案；null / 空串返回 false。</param>
    public static bool HasText(string? text)
        => !string.IsNullOrEmpty(text) && Require()?.ContainsText(text) == true;

    /// <summary>
    /// 是否存在指定 viewId 的节点（完整 id，如 <c>com.app:id/submit</c>）。
    /// </summary>
    public static bool HasId(string? viewId)
        => !string.IsNullOrEmpty(viewId) && Require()?.ContainsViewId(viewId) == true;

    /// <summary>
    /// ContentDescription 是否包含 <paramref name="desc"/>（子串）。
    /// 图标按钮常只有描述没有文字时使用。
    /// </summary>
    public static bool HasDesc(string? desc)
        => !string.IsNullOrEmpty(desc) && Require()?.ContainsDesc(desc) == true;

    /// <summary>
    /// 收集当前界面可见文本（去重），最多 <paramref name="max"/> 条。
    /// 可用于调试或模糊匹配。
    /// </summary>
    public static string[] Texts(int max = 80)
        => Require()?.CollectTexts(max).ToArray() ?? [];

    /// <summary>
    /// 查找第一个包含 <paramref name="text"/> 的节点屏幕包围盒。
    /// 成功返回长度为 4 的数组 <c>[left, top, right, bottom]</c>（像素）；找不到返回 null。
    /// </summary>
    public static int[]? TextBounds(string? text)
        => string.IsNullOrEmpty(text) ? null : Require()?.FindTextBounds(text);

    /// <summary>
    /// 局部屏幕 OCR：截取矩形 (left,top)–(right,bottom)（像素）并识别文字。
    /// 识别失败、无文字或系统不支持（需 Android 11+）时返回空字符串。
    /// 对角坐标顺序可颠倒。
    /// </summary>
    public static string Ocr(int left, int top, int right, int bottom)
        => Require()?.OcrRegion(left, top, right, bottom) ?? "";

    /// <summary>
    /// 在屏幕上描边调试矩形，便于核对 OCR/点击区域。
    /// </summary>
    /// <param name="left">左（像素）。</param>
    /// <param name="top">上（像素）。</param>
    /// <param name="right">右（像素）。</param>
    /// <param name="bottom">下（像素）。</param>
    /// <param name="argb">颜色 0xAARRGGBB，默认不透明红 <c>0xFFFF0000</c>。</param>
    /// <param name="durationMs">显示时长毫秒，默认 1200。</param>
    /// <returns>是否成功加上浮层（失败时见日志；全屏视频可能仍看不见）。</returns>
    public static bool DrawRect(int left, int top, int right, int bottom, uint argb = 0xFFFF0000u, int durationMs = 1200)
        => Require()?.DrawRect(left, top, right, bottom, argb, durationMs) == true;

    /// <summary>
    /// 点击第一个包含 <paramref name="text"/> 的节点中心。
    /// 优先手势点击包围盒中心；无包围盒时尝试无障碍 Click。找不到返回 false。
    /// </summary>
    public static bool TapText(string? text)
        => !string.IsNullOrEmpty(text) && Require()?.TapText(text) == true;

    /// <summary>长按第一个包含文案的节点中心。</summary>
    /// <param name="durationMs">按住时长，默认约 700ms。</param>
    public static bool LongPressText(string? text, int durationMs = 700)
        => !string.IsNullOrEmpty(text) && Require()?.LongPressText(text, durationMs) == true;

    /// <summary>点击指定 viewId 节点中心。</summary>
    public static bool TapId(string? viewId)
        => !string.IsNullOrEmpty(viewId) && Require()?.TapViewId(viewId) == true;

    /// <summary>点击 ContentDescription 包含 <paramref name="desc"/> 的节点中心。</summary>
    public static bool TapDesc(string? desc)
        => !string.IsNullOrEmpty(desc) && Require()?.TapDesc(desc) == true;

    /// <summary>
    /// 按归一化坐标点击（0..1）。例如屏幕正中：<c>Fly.Tap(0.5, 0.5)</c>。
    /// </summary>
    public static bool Tap(double nx, double ny)
        => Require()?.Tap(nx, ny) == true;

    /// <summary>按屏幕像素坐标点击。</summary>
    public static bool TapPx(int x, int y)
        => Require()?.TapPx(x, y) == true;

    /// <summary>按归一化坐标双击。</summary>
    public static bool DoubleTap(double nx, double ny)
        => Require()?.DoubleTap(nx, ny) == true;

    /// <summary>按归一化坐标长按。</summary>
    public static bool LongPress(double nx, double ny, int durationMs = 700)
        => Require()?.LongPress(nx, ny, durationMs) == true;

    /// <summary>
    /// 按归一化坐标滑动：从 (x1,y1) 滑到 (x2,y2)。
    /// </summary>
    /// <param name="durationMs">滑动时长毫秒，默认 320。</param>
    public static bool Swipe(double x1, double y1, double x2, double y2, int durationMs = 320)
        => Require()?.Swipe(x1, y1, x2, y2, durationMs) == true;

    /// <summary>
    /// 屏幕偏左向上滑。<paramref name="distance"/> 为相对屏高的行程（约 0.1..0.9）。
    /// 实现侧会偏左并抬高起点，避开右下广告「查看详情」热区。
    /// </summary>
    public static bool SwipeUp(double distance = 0.45, int durationMs = 320)
        => Require()?.SwipeUp(distance, durationMs) == true;

    /// <summary>屏幕中部向下滑。</summary>
    public static bool SwipeDown(double distance = 0.45, int durationMs = 320)
        => Require()?.SwipeDown(distance, durationMs) == true;

    /// <summary>屏幕中部向左滑（适合横滑切页）。</summary>
    public static bool SwipeLeft(double distance = 0.45, int durationMs = 320)
        => Require()?.SwipeLeft(distance, durationMs) == true;

    /// <summary>屏幕中部向右滑。</summary>
    public static bool SwipeRight(double distance = 0.45, int durationMs = 320)
        => Require()?.SwipeRight(distance, durationMs) == true;

    /// <summary>
    /// 向前滚动：优先对可滚动节点执行无障碍 ScrollForward，失败则上滑手势兜底。
    /// </summary>
    public static bool ScrollForward()
        => Require()?.ScrollForward() == true;

    /// <summary>
    /// 向后滚动：优先 ScrollBackward，失败则下滑手势兜底。
    /// </summary>
    public static bool ScrollBackward()
        => Require()?.ScrollBackward() == true;

    /// <summary>
    /// 等待文案出现，最多 <paramref name="timeoutMs"/> 毫秒，每隔 <paramref name="pollMs"/> 轮询一次。
    /// 出现返回 true，超时返回 false。会阻塞当前脚本线程。
    /// </summary>
    public static bool WaitText(string? text, int timeoutMs = 8000, int pollMs = 250)
        => !string.IsNullOrEmpty(text) && Require()?.WaitText(text, timeoutMs, pollMs) == true;

    /// <summary>
    /// 等待文案消失。已不存在返回 true，超时仍存在返回 false。
    /// </summary>
    public static bool WaitGone(string? text, int timeoutMs = 8000, int pollMs = 250)
        => string.IsNullOrEmpty(text) || Require()?.WaitGone(text, timeoutMs, pollMs) == true;

    /// <summary>
    /// 向当前焦点可编辑框写入文本（覆盖原内容）。无焦点编辑框时返回 false。
    /// </summary>
    public static bool Input(string? text)
        => text != null && Require()?.Input(text) == true;

    /// <summary>清空当前焦点可编辑框。</summary>
    public static bool ClearInput()
        => Require()?.ClearInput() == true;

    /// <summary>对焦点编辑框发送回车/IME 确认。</summary>
    public static bool Enter()
        => Require()?.Enter() == true;

    /// <summary>系统返回键。</summary>
    public static bool Back()
        => Require()?.Back() == true;

    /// <summary>回到桌面（Home）。</summary>
    public static bool Home()
        => Require()?.Home() == true;

    /// <summary>打开最近任务列表。</summary>
    public static bool Recents()
        => Require()?.Recents() == true;

    /// <summary>下拉通知栏。</summary>
    public static bool Notifications()
        => Require()?.Notifications() == true;

    /// <summary>打开快捷设置面板（部分系统/版本可能无效）。</summary>
    public static bool QuickSettings()
        => Require()?.QuickSettings() == true;

    /// <summary>锁屏（Android 9+；厂商可能限制）。</summary>
    public static bool LockScreen()
        => Require()?.LockScreen() == true;

    /// <summary>触发系统截屏动作（Android 9+）。</summary>
    public static bool TakeScreenshot()
        => Require()?.TakeScreenshot() == true;

    /// <summary>
    /// 打开系统无障碍设置页，引导用户开启「玄鸟键鼠注入」。
    /// 即使 <see cref="Ready"/> 为 false 也可调用（不经过 Require）。
    /// </summary>
    public static void OpenSettings()
        => ProgramDevice.Current?.OpenAccessibilitySettings();

    private static IProgramDeviceControl? Require()
    {
        var d = ProgramDevice.Current;
        if (d == null)
        {
            AppLog.Warn("Fly：设备控制仅在手机 Android 可用；请在手机玄鸟上运行本程序（电脑端无界面注入）");
            return null;
        }
        if (!d.IsReady)
        {
            AppLog.Warn("Fly：请先开启「玄鸟键鼠注入」无障碍（高级 → 行为）");
            return null;
        }
        return d;
    }
}

/// <summary>
/// 供 Jint（JavaScript）绑定的实例桥（静态类无法直接 SetValue）。
/// 方法名为 camelCase，语义与 <see cref="Fly"/> 一致。
/// </summary>
public sealed class FlyScriptBridge
{
    /// <inheritdoc cref="Fly.Ready"/>
    public bool ready() => Fly.Ready;
    /// <inheritdoc cref="Fly.App"/>
    public string? app() => Fly.App;
    /// <inheritdoc cref="Fly.ScreenWidth"/>
    public int screenWidth() => Fly.ScreenWidth;
    /// <inheritdoc cref="Fly.ScreenHeight"/>
    public int screenHeight() => Fly.ScreenHeight;
    /// <inheritdoc cref="Fly.HasText"/>
    public bool hasText(string? text) => Fly.HasText(text);
    /// <inheritdoc cref="Fly.HasId"/>
    public bool hasId(string? viewId) => Fly.HasId(viewId);
    /// <inheritdoc cref="Fly.HasDesc"/>
    public bool hasDesc(string? desc) => Fly.HasDesc(desc);
    /// <inheritdoc cref="Fly.Texts"/>
    public string[] texts(int max = 80) => Fly.Texts(max);
    /// <inheritdoc cref="Fly.TextBounds"/>
    public int[]? textBounds(string? text) => Fly.TextBounds(text);
    /// <inheritdoc cref="Fly.Ocr"/>
    public string ocr(int left, int top, int right, int bottom) => Fly.Ocr(left, top, right, bottom);
    /// <inheritdoc cref="Fly.DrawRect"/>
    public bool drawRect(int left, int top, int right, int bottom, uint argb = 0xFFFF0000u, int durationMs = 1200)
        => Fly.DrawRect(left, top, right, bottom, argb, durationMs);
    /// <inheritdoc cref="Fly.TapText"/>
    public bool tapText(string? text) => Fly.TapText(text);
    /// <inheritdoc cref="Fly.LongPressText"/>
    public bool longPressText(string? text, int durationMs = 700) => Fly.LongPressText(text, durationMs);
    /// <inheritdoc cref="Fly.TapId"/>
    public bool tapId(string? viewId) => Fly.TapId(viewId);
    /// <inheritdoc cref="Fly.TapDesc"/>
    public bool tapDesc(string? desc) => Fly.TapDesc(desc);
    /// <inheritdoc cref="Fly.Tap"/>
    public bool tap(double nx, double ny) => Fly.Tap(nx, ny);
    /// <inheritdoc cref="Fly.TapPx"/>
    public bool tapPx(int x, int y) => Fly.TapPx(x, y);
    /// <inheritdoc cref="Fly.DoubleTap"/>
    public bool doubleTap(double nx, double ny) => Fly.DoubleTap(nx, ny);
    /// <inheritdoc cref="Fly.LongPress"/>
    public bool longPress(double nx, double ny, int durationMs = 700) => Fly.LongPress(nx, ny, durationMs);
    /// <inheritdoc cref="Fly.Swipe"/>
    public bool swipe(double x1, double y1, double x2, double y2, int durationMs = 320)
        => Fly.Swipe(x1, y1, x2, y2, durationMs);
    /// <inheritdoc cref="Fly.SwipeUp"/>
    public bool swipeUp(double distance = 0.45, int durationMs = 320) => Fly.SwipeUp(distance, durationMs);
    /// <inheritdoc cref="Fly.SwipeDown"/>
    public bool swipeDown(double distance = 0.45, int durationMs = 320) => Fly.SwipeDown(distance, durationMs);
    /// <inheritdoc cref="Fly.SwipeLeft"/>
    public bool swipeLeft(double distance = 0.45, int durationMs = 320) => Fly.SwipeLeft(distance, durationMs);
    /// <inheritdoc cref="Fly.SwipeRight"/>
    public bool swipeRight(double distance = 0.45, int durationMs = 320) => Fly.SwipeRight(distance, durationMs);
    /// <inheritdoc cref="Fly.ScrollForward"/>
    public bool scrollForward() => Fly.ScrollForward();
    /// <inheritdoc cref="Fly.ScrollBackward"/>
    public bool scrollBackward() => Fly.ScrollBackward();
    /// <inheritdoc cref="Fly.WaitText"/>
    public bool waitText(string? text, int timeoutMs = 8000, int pollMs = 250) => Fly.WaitText(text, timeoutMs, pollMs);
    /// <inheritdoc cref="Fly.WaitGone"/>
    public bool waitGone(string? text, int timeoutMs = 8000, int pollMs = 250) => Fly.WaitGone(text, timeoutMs, pollMs);
    /// <inheritdoc cref="Fly.Input"/>
    public bool input(string? text) => Fly.Input(text);
    /// <inheritdoc cref="Fly.ClearInput"/>
    public bool clearInput() => Fly.ClearInput();
    /// <inheritdoc cref="Fly.Enter"/>
    public bool enter() => Fly.Enter();
    /// <inheritdoc cref="Fly.Back"/>
    public bool back() => Fly.Back();
    /// <inheritdoc cref="Fly.Home"/>
    public bool home() => Fly.Home();
    /// <inheritdoc cref="Fly.Recents"/>
    public bool recents() => Fly.Recents();
    /// <inheritdoc cref="Fly.Notifications"/>
    public bool notifications() => Fly.Notifications();
    /// <inheritdoc cref="Fly.QuickSettings"/>
    public bool quickSettings() => Fly.QuickSettings();
    /// <inheritdoc cref="Fly.LockScreen"/>
    public bool lockScreen() => Fly.LockScreen();
    /// <inheritdoc cref="Fly.TakeScreenshot"/>
    public bool takeScreenshot() => Fly.TakeScreenshot();
    /// <inheritdoc cref="Fly.OpenSettings"/>
    public void openSettings() => Fly.OpenSettings();
}
