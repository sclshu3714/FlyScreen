namespace FlyScreen.Services.Programs;

/// <summary>
/// 用户程序控本机（手机）的宿主能力。由各端注册实现；未注册时脚本调用会失败并写日志。
/// </summary>
public interface IProgramDeviceControl
{
    /// <summary>无障碍等前置条件是否就绪。</summary>
    bool IsReady { get; }

    /// <summary>当前前台应用包名；不可用时为 null。</summary>
    string? ForegroundPackage { get; }

    /// <summary>屏幕像素宽高；失败时为 (0,0)。</summary>
    (int Width, int Height) ScreenSize { get; }

    /// <summary>可见节点中是否包含指定文案（子串匹配）。</summary>
    bool ContainsText(string text);

    /// <summary>收集当前可见文本（去重，最多 <paramref name="max"/> 条）。</summary>
    IReadOnlyList<string> CollectTexts(int max = 80);

    /// <summary>
    /// 截取屏幕矩形区域做 OCR，返回识别到的全部文本；失败或无文字返回空字符串。
    /// 坐标为屏幕像素（与 <see cref="ScreenSize"/> 同源），可为任意对角顺序。
    /// </summary>
    string OcrRegion(int left, int top, int right, int bottom);

    /// <summary>点击第一个包含文案的节点中心；找不到返回 false。</summary>
    bool TapText(string text);

    /// <summary>按归一化坐标点击（0..1）。</summary>
    bool Tap(double nx, double ny);

    /// <summary>按归一化坐标滑动。</summary>
    bool Swipe(double x1, double y1, double x2, double y2, int durationMs = 320);

    /// <summary>屏幕中部向上滑（distance 为屏高比例，默认 0.45）。</summary>
    bool SwipeUp(double distance = 0.45, int durationMs = 320);

    /// <summary>屏幕中部向下滑。</summary>
    bool SwipeDown(double distance = 0.45, int durationMs = 320);

    /// <summary>系统返回。</summary>
    bool Back();

    /// <summary>系统 Home。</summary>
    bool Home();

    /// <summary>打开系统无障碍设置（引导用户开启玄鸟注入）。</summary>
    void OpenAccessibilitySettings();
}

/// <summary>宿主注册入口；脚本通过 <see cref="Fly"/> 调用。</summary>
public static class ProgramDevice
{
    public static IProgramDeviceControl? Current { get; set; }
}
