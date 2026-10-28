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
    private static readonly object SettingsGate = new();
    private static IReadOnlyDictionary<string, string> _settings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>运行前由宿主绑定本程序 settings.json（覆盖 @fly-settings 默认）。</summary>
    public static void BindSettings(IReadOnlyDictionary<string, string>? values)
    {
        lock (SettingsGate)
        {
            _settings = values == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>运行结束清空绑定，避免串到下一程序。</summary>
    public static void ClearSettings()
    {
        lock (SettingsGate)
            _settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string? Raw(string key)
    {
        lock (SettingsGate)
            return _settings.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>读取布尔设置；缺省返回 <paramref name="fallback"/>。</summary>
    public static bool GetBool(string key, bool fallback = false)
        => ProgramSettingsSchema.AsBool(Raw(key), fallback);

    /// <summary>读取浮点设置。</summary>
    public static double GetDouble(string key, double fallback = 0)
        => ProgramSettingsSchema.AsDouble(Raw(key), fallback);

    /// <summary>读取字符串设置。</summary>
    public static string GetString(string key, string fallback = "")
    {
        var v = Raw(key);
        return string.IsNullOrEmpty(v) ? fallback : v;
    }

    /// <summary>读取字符串列表（换行/逗号分隔）。</summary>
    public static string[] GetStringList(string key)
        => ProgramSettingsSchema.AsStringList(Raw(key)).ToArray();

    /// <summary>读取命中后动作；无法解析时返回 <paramref name="fallback"/>。</summary>
    public static ProgramHitAction GetAction(string key, ProgramHitAction fallback = ProgramHitAction.SwipeUp)
        => ProgramSettingsSchema.AsAction(Raw(key), fallback);

    /// <summary>读取归一化矩形；未设置返回 null。</summary>
    public static NormRect? GetRect(string key)
        => NormRect.TryParse(Raw(key));

    /// <summary>当前是否横屏（宽 &gt; 高）。选区只看此项，不分正/反。</summary>
    public static bool IsLandscape
        => ProgramRegionKeys.IsLandscape(ScreenWidth, ScreenHeight);

    /// <summary>
    /// 显示旋转角（度）：0 / 90 / 180 / 270。仅诊断用；区域键仍按 <see cref="IsLandscape"/>。
    /// </summary>
    public static int DisplayRotation
        => ProgramDevice.Current?.DisplayRotationDegrees ?? 0;

    /// <summary>
    /// 当前朝向的识别区键。横/竖互不回退；仅竖屏可回退旧键 <c>ocrRegion</c>。
    /// 未标定返回朝向主键（<see cref="GetRect"/> 为空）。
    /// </summary>
    public static string CurrentOcrRegionKey()
    {
        var land = IsLandscape;
        var primary = ProgramRegionKeys.OcrKey(land);
        if (HasConfiguredRect(primary)) return primary;
        if (!land && HasConfiguredRect(ProgramRegionKeys.LegacyOcr))
            return ProgramRegionKeys.LegacyOcr;
        return primary;
    }

    /// <summary>
    /// 当前朝向的操作区键。横/竖互不回退；仅竖屏可回退旧键 <c>actionRegion</c>。
    /// 未标定返回朝向主键（执行时走屏幕中央默认位）。
    /// </summary>
    public static string CurrentActionRegionKey()
    {
        var land = IsLandscape;
        var primary = ProgramRegionKeys.ActionKey(land);
        if (HasConfiguredRect(primary)) return primary;
        if (!land && HasConfiguredRect(ProgramRegionKeys.LegacyAction))
            return ProgramRegionKeys.LegacyAction;
        return primary;
    }

    /// <summary>当前朝向是否已标定识别区（未标定则脚本应只用 HasText、不做 OCR）。</summary>
    public static bool HasOcrRegionConfigured()
        => HasConfiguredRect(CurrentOcrRegionKey());

    /// <summary>当前朝向是否已标定操作区（未标定则 <see cref="PerformActionInRegion"/> 用屏幕中央）。</summary>
    public static bool HasActionRegionConfigured()
        => HasConfiguredRect(CurrentActionRegionKey());

    private static bool HasConfiguredRect(string key)
    {
        var r = GetRect(key);
        return r != null && !r.Value.IsEmpty;
    }

    /// <summary>
    /// 对设置中的识别区做 OCR（像素由当前屏幕尺寸换算）。
    /// 未配置识别区时返回空串。省略 <paramref name="key"/> 时按当前朝向选键。
    /// </summary>
    public static string OcrSettingRegion(string? key = null)
    {
        var k = string.IsNullOrWhiteSpace(key) ? CurrentOcrRegionKey() : key!;
        var rect = GetRect(k);
        if (rect == null || rect.Value.IsEmpty) return "";
        var (l, t, r, b) = rect.Value.ToPixels(ScreenWidth, ScreenHeight);
        return Ocr(l, t, r, b);
    }

    /// <summary>
    /// 在设置的操作区内执行动作（上滑等沿区内中线；点击取中心）。
    /// 省略 <paramref name="regionKey"/> 时按当前朝向选键。
    /// </summary>
    public static bool PerformActionInRegion(ProgramHitAction action, string? regionKey = null, double longPressSec = 0.7)
    {
        regionKey = string.IsNullOrWhiteSpace(regionKey) ? CurrentActionRegionKey() : regionKey;
        var rect = GetRect(regionKey);
        if (rect == null || rect.Value.IsEmpty)
        {
            // 未标定操作区：竖屏/横屏均取当前屏幕中央（滑动沿中线）
            return action switch
            {
                ProgramHitAction.SwipeUp => Swipe(0.5, 0.72, 0.5, 0.28, 560),
                ProgramHitAction.SwipeDown => Swipe(0.5, 0.28, 0.5, 0.72, 420),
                ProgramHitAction.SwipeLeft => Swipe(0.78, 0.5, 0.22, 0.5, 360),
                ProgramHitAction.SwipeRight => Swipe(0.22, 0.5, 0.78, 0.5, 360),
                ProgramHitAction.Tap => Tap(0.5, 0.5),
                ProgramHitAction.DoubleTap => DoubleTap(0.5, 0.5),
                ProgramHitAction.LongPress => LongPress(0.5, 0.5, SecToMs(longPressSec)),
                _ => false
            };
        }

        var x0 = rect.Value.X;
        var y0 = rect.Value.Y;
        var x1 = rect.Value.X + rect.Value.W;
        var y1 = rect.Value.Y + rect.Value.H;
        var cx = (x0 + x1) / 2;
        var cy = (y0 + y1) / 2;
        // 区内滑动留边，避免擦到框外广告
        var midX = x0 + rect.Value.W * 0.35;
        var topY = y0 + rect.Value.H * 0.18;
        var botY = y0 + rect.Value.H * 0.82;
        var leftX = x0 + rect.Value.W * 0.18;
        var rightX = x0 + rect.Value.W * 0.82;

        return action switch
        {
            ProgramHitAction.SwipeUp => Swipe(midX, botY, midX, topY, 560),
            ProgramHitAction.SwipeDown => Swipe(midX, topY, midX, botY, 420),
            ProgramHitAction.SwipeLeft => Swipe(rightX, cy, leftX, cy, 360),
            ProgramHitAction.SwipeRight => Swipe(leftX, cy, rightX, cy, 360),
            ProgramHitAction.Tap => Tap(cx, cy),
            ProgramHitAction.DoubleTap => DoubleTap(cx, cy),
            ProgramHitAction.LongPress => LongPress(cx, cy, SecToMs(longPressSec)),
            _ => false
        };
    }

    private static int SecToMs(double sec)
        => Math.Clamp((int)Math.Round(Math.Max(0.2, sec) * 1000), 200, 10_000);

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

    /// <summary>按 id 常驻调试框（像素）；同 id 覆盖更新。</summary>
    public static bool SetDebugRect(string id, int left, int top, int right, int bottom, uint argb)
        => Require()?.SetDebugRect(id, left, top, right, bottom, argb) == true;

    /// <summary>按 id 常驻调试框（归一化）；旋转后由宿主按新屏重画，脚本宜用此 API。</summary>
    public static bool SetDebugRectNorm(string id, double x, double y, double w, double h, uint argb)
        => Require()?.SetDebugRectNorm(id, x, y, w, h, argb) == true;

    /// <summary>清除调试框；id 为空清除全部。</summary>
    public static bool ClearDebugRect(string? id = null)
        => Require()?.ClearDebugRect(id) == true;

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
    /// <inheritdoc cref="Fly.SetDebugRect"/>
    public bool setDebugRect(string id, int left, int top, int right, int bottom, uint argb)
        => Fly.SetDebugRect(id, left, top, right, bottom, argb);
    /// <inheritdoc cref="Fly.SetDebugRectNorm"/>
    public bool setDebugRectNorm(string id, double x, double y, double w, double h, uint argb)
        => Fly.SetDebugRectNorm(id, x, y, w, h, argb);
    /// <inheritdoc cref="Fly.ClearDebugRect"/>
    public bool clearDebugRect(string? id = null) => Fly.ClearDebugRect(id);
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
    public bool getBool(string key, bool fallback = false) => Fly.GetBool(key, fallback);
    public double getDouble(string key, double fallback = 0) => Fly.GetDouble(key, fallback);
    public string getString(string key, string fallback = "") => Fly.GetString(key, fallback);
    public string[] getStringList(string key) => Fly.GetStringList(key);
    public string getAction(string key, string fallback = "SwipeUp")
        => Fly.GetAction(key, ProgramSettingsSchema.AsAction(fallback)).ToString();
    public bool isLandscape() => Fly.IsLandscape;
    public int displayRotation() => Fly.DisplayRotation;
    public string currentOcrRegionKey() => Fly.CurrentOcrRegionKey();
    public string currentActionRegionKey() => Fly.CurrentActionRegionKey();
    public bool hasOcrRegionConfigured() => Fly.HasOcrRegionConfigured();
    public bool hasActionRegionConfigured() => Fly.HasActionRegionConfigured();
    public string ocrSettingRegion(string? key = null) => Fly.OcrSettingRegion(key);
    public bool performActionInRegion(string action, string? regionKey = null, double longPressSec = 0.7)
        => Fly.PerformActionInRegion(ProgramSettingsSchema.AsAction(action), regionKey, longPressSec);
}
