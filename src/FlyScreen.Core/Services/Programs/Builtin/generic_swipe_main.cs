// @fly-settings
// bool enableOcr | 启用OCR | true
// bool debugOcr | 调试OCR | false
// bool debugLog | 调试日志 | false
// string[] appHints | 应用包名关键词 | com.kylin.read,kylin.read,kylin,hongguo,phoenix.read,dragon.read,fanqie,novel.read
// string[] swipeHints | 识别文案 | 上滑继续观看漫剧,上滑继续观看短剧,上滑继续观看剧集,上滑继续观看,继续观看漫剧,继续观看短剧,继续观看剧集
// enum action | 命中后操作 | SwipeUp | SwipeUp,SwipeDown,SwipeLeft,SwipeRight,Tap,DoubleTap,LongPress
// double longPressSec | 长按秒数 | 0.7
// rect ocrRegionPortrait | 竖屏识别区 |
// rect actionRegionPortrait | 竖屏操作区 |
// rect ocrRegionLandscape | 横屏识别区 |
// rect actionRegionLandscape | 横屏操作区 |
// @fly-settings-end
//
// 内置通用识别操作 v8：HasText →（仅当前朝向已标定识别区才）OCR → 文案含「7s / 7秒」则等满再操作。
// 横/竖识别区互不回退；当前朝向未标定则不做 OCR。两侧都未标定 ≈ 未启用 OCR。
// debugOcr：非目标红 / 目标蓝 / 命中绿。触发成功后 coolUntil≈2.5s + Delay(800ms)。

Console.WriteLine("=== 通用识别操作（内置） ===");
Console.WriteLine($"时间 {DateTime.Now:HH:mm:ss}  Ready={Fly.Ready}  App={Fly.App}");

if (!Fly.Ready)
{
    Console.WriteLine("未开启无障碍，正在打开设置…");
    Fly.OpenSettings();
    return;
}

var enableOcr = Fly.GetBool("enableOcr", true);
// 竖屏+横屏识别区都未标定：没有可 OCR 的区域，等同关闭 OCR
if (enableOcr && !Fly.HasAnyOcrRegionConfigured())
{
    enableOcr = false;
    Console.WriteLine("竖屏/横屏识别区均未标定 → 等价未启用 OCR（仅 HasText）");
}
var debugOcr = enableOcr && Fly.GetBool("debugOcr", false);
var debugLog = Fly.GetBool("debugLog", false);
var appHints = Fly.GetStringList("appHints");
var swipeHints = Fly.GetStringList("swipeHints");
var action = Fly.GetAction("action", ProgramHitAction.SwipeUp);
var longPressSec = Fly.GetDouble("longPressSec", 0.7);

if (appHints.Length == 0)
{
    appHints = new[] { "kylin", "hongguo", "fanqie" };
}
if (swipeHints.Length == 0)
{
    swipeHints = new[] { "上滑继续观看", "继续观看短剧", "继续观看剧集" };
}

const uint ColorRed = 0xFFFF0000u;
const uint ColorBlue = 0xFF2196F3u;
const uint ColorGreen = 0xFF4CAF50u;

Console.WriteLine($"设置 enableOcr={enableOcr} debugOcr={debugOcr} debugLog={debugLog} action={action} longPressSec={longPressSec}");
Console.WriteLine($"竖屏识别={Fly.GetString("ocrRegionPortrait")} 竖屏操作={Fly.GetString("actionRegionPortrait")}");
Console.WriteLine($"横屏识别={Fly.GetString("ocrRegionLandscape")} 横屏操作={Fly.GetString("actionRegionLandscape")}");
Console.WriteLine($"当前朝向={(Fly.IsLandscape ? "横屏" : "竖屏")} {Fly.ScreenWidth}x{Fly.ScreenHeight} Rotation={Fly.DisplayRotation} 本朝向识别区已标定={Fly.HasOcrRegionConfigured()} 任一识别区={Fly.HasAnyOcrRegionConfigured()} key={Fly.CurrentOcrRegionKey()}");

static string Compact(string? s)
{
    if (string.IsNullOrEmpty(s)) return "";
    var sb = new System.Text.StringBuilder(s.Length);
    foreach (var ch in s)
    {
        if (!char.IsWhiteSpace(ch) && ch is not '\u200b' and not '\ufeff')
            sb.Append(ch);
    }
    return sb.ToString();
}

string? MatchHints(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    var c = Compact(raw);
    if (!c.Contains("上滑", StringComparison.Ordinal)
        && !c.Contains("继续观看", StringComparison.Ordinal))
        return null;
    foreach (var tip in swipeHints)
    {
        if (c.Contains(Compact(tip), StringComparison.OrdinalIgnoreCase))
            return tip;
    }
    if (c.Contains("上滑", StringComparison.Ordinal) && c.Contains("继续", StringComparison.Ordinal))
        return "上滑继续";
    return null;
}

string? FindHitA11y()
{
    foreach (var tip in swipeHints)
    {
        if (Fly.HasText(tip))
            return tip;
    }
    return null;
}

/// <summary>从 OCR/无障碍文案解析倒计时秒数。「7s后可继续上滑观看」「3秒后…」；无则 0。上限 30。
/// 不用 Regex：手机 Roslyn 脚本默认未引用 System.Text.RegularExpressions。</summary>
static int TryParseCountdownSec(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return 0;
    var c = Compact(raw);
    static bool IsDigit(char ch) => ch >= '0' && ch <= '9';
    static bool IsLetter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');
    for (var i = 0; i < c.Length; i++)
    {
        if (!IsDigit(c[i])) continue;
        var j = i;
        while (j < c.Length && IsDigit(c[j])) j++;
        var len = j - i;
        if (len is < 1 or > 2)
        {
            i = j - 1;
            continue;
        }
        if (!int.TryParse(c.Substring(i, len), out var sec))
        {
            i = j - 1;
            continue;
        }
        // 「7s」「7S」后不可再跟英文字母（避免误伤）
        if (j < c.Length && (c[j] is 's' or 'S')
            && (j + 1 >= c.Length || !IsLetter(c[j + 1])))
            return Math.Clamp(sec, 0, 30);
        // 「7秒」
        if (j < c.Length && c[j] == '秒')
            return Math.Clamp(sec, 0, 30);
        i = j - 1;
    }
    return 0;
}

var skipOcrLog = 0;

/// <returns>hit=匹配到的提示；raw=OCR 原文（供解析倒计时）。</returns>
(string? hit, string raw) FindHitOcr()
{
    // enableOcr 已在启动时按「是否有任一识别区」收紧；此处再按当前朝向把关
    if (!enableOcr) return (null, "");
    if (!Fly.HasOcrRegionConfigured())
    {
        skipOcrLog++;
        if (skipOcrLog == 1 || skipOcrLog % 15 == 0)
        {
            var orient = Fly.IsLandscape ? "横屏" : "竖屏";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {orient}识别区未标定，跳过OCR（仅 HasText）{Fly.ScreenWidth}x{Fly.ScreenHeight} rot={Fly.DisplayRotation}");
        }
        return (null, "");
    }

    var ocrKey = Fly.CurrentOcrRegionKey();
    var ocr = (Fly.OcrSettingRegion(ocrKey) ?? "").Trim();
    if (debugOcr || debugLog)
    {
        var orient = Fly.IsLandscape ? "横屏" : "竖屏";
        var preview = ocr.Length == 0 ? "" : (ocr.Length <= 80 ? ocr : ocr.Substring(0, 80) + "…");
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] OCR {orient} key={ocrKey} len={ocr.Length} [{preview}]");
    }
    return (MatchHints(ocr), ocr);
}

void ShowDebugFrames(uint ocrColor, uint actionColor)
{
    if (!debugOcr) return;
    // 归一化下发：宿主按当前屏尺寸换算像素；框上会标「识别区/操作区」
    var ocrKey = Fly.CurrentOcrRegionKey();
    var actKey = Fly.CurrentActionRegionKey();
    var ocrRect = Fly.GetRect(ocrKey);
    var actRect = Fly.GetRect(actKey);
    if (ocrRect != null && !ocrRect.Value.IsEmpty)
    {
        var r = ocrRect.Value;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 画识别区 key={ocrKey} norm={r} screen={Fly.ScreenWidth}x{Fly.ScreenHeight}");
        Fly.SetDebugRectNorm("ocr", r.X, r.Y, r.W, r.H, ocrColor);
    }
    if (actRect != null && !actRect.Value.IsEmpty)
    {
        var r = actRect.Value;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 画操作区 key={actKey} norm={r} screen={Fly.ScreenWidth}x{Fly.ScreenHeight}");
        Fly.SetDebugRectNorm("action", r.X, r.Y, r.W, r.H, actionColor);
    }
}

long coolUntil = 0;
var tipArmed = true;
var missAppLog = 0;
var lastApp = "";
var missLog = 0;
var lastOrientLand = Fly.IsLandscape;
var lastSw = Fly.ScreenWidth;
var lastSh = Fly.ScreenHeight;

try
{
    while (!ct.IsCancellationRequested)
    {
        var app = Fly.App ?? "";
        if (!string.Equals(app, lastApp, StringComparison.Ordinal))
        {
            lastApp = app;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 前台包名 → {app}");
        }

        var land = Fly.IsLandscape;
        var sw = Fly.ScreenWidth;
        var sh = Fly.ScreenHeight;
        var sizeChanged = sw != lastSw || sh != lastSh;
        if (land != lastOrientLand || sizeChanged)
        {
            lastOrientLand = land;
            lastSw = sw;
            lastSh = sh;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 朝向 → {(land ? "横屏" : "竖屏")} {sw}x{sh} Rotation={Fly.DisplayRotation}");
            if (debugOcr)
            {
                Fly.ClearDebugRect();
                await Delay(500);
                lastSw = Fly.ScreenWidth;
                lastSh = Fly.ScreenHeight;
                lastOrientLand = Fly.IsLandscape;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 朝向稳定后屏 {lastSw}x{lastSh} Rotation={Fly.DisplayRotation} ocr={Fly.CurrentOcrRegionKey()} act={Fly.CurrentActionRegionKey()}");
                // 立刻按新朝向键重画，不等下一轮业务分支
                ShowDebugFrames(ColorBlue, ColorBlue);
            }
        }

        if (string.IsNullOrEmpty(app))
        {
            if (debugOcr) ShowDebugFrames(ColorRed, ColorBlue);
            await Delay(400);
            continue;
        }

        var isTarget = appHints.Any(h =>
            app.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0);
        if (!isTarget)
        {
            // 非目标应用：识别区红，操作区蓝
            ShowDebugFrames(ColorRed, ColorBlue);
            missAppLog++;
            tipArmed = true;
            if (missAppLog == 1 || missAppLog % 20 == 0)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 前台非目标应用，等待中… app={app}");
            await Delay(1200);
            continue;
        }
        missAppLog = 0;

        // 目标应用待命：识别区蓝、操作区蓝
        ShowDebugFrames(ColorBlue, ColorBlue);

        if (Environment.TickCount64 < coolUntil)
        {
            await Delay(400);
            continue;
        }

        string? hit = FindHitA11y();
        var hitRaw = "";
        if (hit == null)
        {
            var (ocrHit, ocrRaw) = FindHitOcr();
            hit = ocrHit;
            hitRaw = ocrRaw;
        }
        else
        {
            // 无障碍命中时也尝试从可见文本里找「Ns / N秒」
            foreach (var t in Fly.Texts(40))
            {
                if (TryParseCountdownSec(t) > 0)
                {
                    hitRaw = t;
                    break;
                }
            }
        }

        // OCR 会撤框，识别后立刻按状态重画
        if (hit == null)
        {
            ShowDebugFrames(ColorBlue, ColorBlue);
            tipArmed = true;
            missLog++;
            if (missLog == 1 || missLog % 10 == 0)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 未识别到提示文案");
            await Delay(1000);
            continue;
        }

        missLog = 0;
        // 命中识别文案：识别区绿
        ShowDebugFrames(ColorGreen, ColorBlue);

        if (!tipArmed)
        {
            await Delay(600);
            continue;
        }

        tipArmed = false;
        var waitSec = TryParseCountdownSec(hitRaw);
        var actKey = Fly.CurrentActionRegionKey();
        var actMarked = Fly.HasActionRegionConfigured();
        if (waitSec > 0)
        {
            Console.WriteLine(actMarked
                ? $"[{DateTime.Now:HH:mm:ss}] 命中「{hit}」含倒计时 {waitSec}s，等待后再 {action}（{(land ? "横屏" : "竖屏")} 操作区 {actKey}）"
                : $"[{DateTime.Now:HH:mm:ss}] 命中「{hit}」含倒计时 {waitSec}s，等待后再 {action}（操作区未标定，用屏幕中央）");
            await Delay(waitSec * 1000);
        }
        else
        {
            Console.WriteLine(actMarked
                ? $"[{DateTime.Now:HH:mm:ss}] 命中「{hit}」→ {action}（{(land ? "横屏" : "竖屏")} 操作区 {actKey}）"
                : $"[{DateTime.Now:HH:mm:ss}] 命中「{hit}」→ {action}（{(land ? "横屏" : "竖屏")}操作区未标定，用屏幕中央）");
        }

        // 执行操作：识别区回蓝，操作区绿（未标定操作区时无框可画）
        ShowDebugFrames(ColorBlue, ColorGreen);
        var ok = Fly.PerformActionInRegion(action, actKey, longPressSec);
        Console.WriteLine(ok
            ? $"[{DateTime.Now:HH:mm:ss}] 已执行 {action}"
            : $"[{DateTime.Now:HH:mm:ss}] 执行失败 {action}");

        // 操作完成：操作区恢复蓝（仍在目标应用则识别区蓝）
        ShowDebugFrames(ColorBlue, ColorBlue);
        // 冷却 2.5s 内不再触发；再 Delay 0.8s。提示须先消失（tipArmed）才会下一轮真正执行。
        coolUntil = Environment.TickCount64 + 2500;
        await Delay(800);
    }
}
finally
{
    if (debugOcr)
        Fly.ClearDebugRect();
}
