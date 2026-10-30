// @fly-settings
// bool enableOcr | 启用OCR | true
// bool debugOcr | 调试OCR | false
// enum logLevel | 日志级别 | 信息 | 错误,警告,信息,调试
// bool debugLog | 调试浮层 | false
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
// 内置通用识别操作 v11：HasText →（仅当前朝向已标定识别区才）OCR → 文案含「7s / 7秒」则等满，再多停 0.5s 后操作。
// 横/竖识别区互不回退；当前朝向未标定则不做 OCR。两侧都未标定 ≈ 未启用 OCR。
// tipArmed：仅成功执行后解除；失败保持武装并可重试。静默等待/冷却打调试心跳。
// logLevel：错误/警告/信息/调试 — 仅控制脚本日志落盘；调试浮层只看 debugLog。

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
var logLevel = Fly.GetLogLevel("logLevel", AppLogLevel.Info);
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

Console.WriteLine($"设置 enableOcr={enableOcr} debugOcr={debugOcr} logLevel={logLevel} debugLog={debugLog} action={action} longPressSec={longPressSec}");
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
        Fly.LogDebug($"[{DateTime.Now:HH:mm:ss}] OCR {orient} key={ocrKey} len={ocr.Length} [{preview}]");
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
        Fly.LogDebug($"[{DateTime.Now:HH:mm:ss}] 画识别区 key={ocrKey} norm={r} screen={Fly.ScreenWidth}x{Fly.ScreenHeight}");
        Fly.SetDebugRectNorm("ocr", r.X, r.Y, r.W, r.H, ocrColor);
    }
    if (actRect != null && !actRect.Value.IsEmpty)
    {
        var r = actRect.Value;
        Fly.LogDebug($"[{DateTime.Now:HH:mm:ss}] 画操作区 key={actKey} norm={r} screen={Fly.ScreenWidth}x{Fly.ScreenHeight}");
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
long lastHeartbeatMs = 0;
var holdTipLog = 0;
const int HeartbeatMs = 60_000;

void Heartbeat(string reason)
{
    var now = Environment.TickCount64;
    if (now - lastHeartbeatMs < HeartbeatMs) return;
    lastHeartbeatMs = now;
    Fly.LogDebug($"[{DateTime.Now:HH:mm:ss}] 心跳 alive tipArmed={tipArmed} cool={(coolUntil > now)} app={Fly.App} {reason}");
}

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
            Heartbeat("无前台包名");
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
            holdTipLog = 0;
            if (missAppLog == 1 || missAppLog % 20 == 0)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 前台非目标应用，等待中… app={app}");
            Heartbeat("非目标应用");
            await Delay(1200);
            continue;
        }
        missAppLog = 0;

        // 目标应用待命：识别区蓝、操作区蓝
        ShowDebugFrames(ColorBlue, ColorBlue);

        if (Environment.TickCount64 < coolUntil)
        {
            Heartbeat("冷却中");
            await Delay(400);
            continue;
        }

        string? hit = FindHitA11y();
        var hitRaw = "";
        var hitVia = "a11y";
        if (hit == null)
        {
            var (ocrHit, ocrRaw) = FindHitOcr();
            hit = ocrHit;
            hitRaw = ocrRaw;
            hitVia = "ocr";
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
            holdTipLog = 0;
            missLog++;
            if (missLog == 1 || missLog % 10 == 0)
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 未识别到提示文案");
            Heartbeat("未命中文案");
            await Delay(1000);
            continue;
        }

        missLog = 0;
        // 命中识别文案：识别区绿
        ShowDebugFrames(ColorGreen, ColorBlue);

        if (!tipArmed)
        {
            // 文案仍在树中、等待其消失后再武装 — 原先完全静默，易误判为「程序挂了」
            holdTipLog++;
            if (holdTipLog == 1 || holdTipLog % 20 == 0)
                Fly.LogDebug($"[{DateTime.Now:HH:mm:ss}] 等待提示消失后再触发 via={hitVia} hit={hit}（第{holdTipLog}次）");
            Heartbeat($"等待提示消失 hit={hit}");
            await Delay(600);
            continue;
        }

        var waitSec = TryParseCountdownSec(hitRaw);
        var actKey = Fly.CurrentActionRegionKey();
        var actMarked = Fly.HasActionRegionConfigured();
        if (waitSec > 0)
        {
            Console.WriteLine(actMarked
                ? $"[{DateTime.Now:HH:mm:ss}] 命中「{hit}」含倒计时 {waitSec}s，等满后再停 0.5s 执行 {action}（{(land ? "横屏" : "竖屏")} 操作区 {actKey} via={hitVia}）"
                : $"[{DateTime.Now:HH:mm:ss}] 命中「{hit}」含倒计时 {waitSec}s，等满后再停 0.5s 执行 {action}（操作区未标定，用屏幕中央 via={hitVia}）");
            // 倒计时刚归零时界面往往还不能滑，多停半秒
            await Delay(waitSec * 1000 + 500);
        }
        else
        {
            Console.WriteLine(actMarked
                ? $"[{DateTime.Now:HH:mm:ss}] 命中「{hit}」→ {action}（{(land ? "横屏" : "竖屏")} 操作区 {actKey} via={hitVia}）"
                : $"[{DateTime.Now:HH:mm:ss}] 命中「{hit}」→ {action}（{(land ? "横屏" : "竖屏")}操作区未标定，用屏幕中央 via={hitVia}）");
        }

        // 执行操作：识别区回蓝，操作区绿（未标定操作区时无框可画）
        ShowDebugFrames(ColorBlue, ColorGreen);
        var ok = Fly.PerformActionInRegion(action, actKey, longPressSec);
        if (ok)
        {
            // 仅成功后解除武装，避免「执行失败」后文案常驻导致数小时静默
            tipArmed = false;
            holdTipLog = 0;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 已执行 {action}");
            coolUntil = Environment.TickCount64 + 2500;
        }
        else
        {
            tipArmed = true;
            Fly.LogWarn($"[{DateTime.Now:HH:mm:ss}] 执行失败 {action}，保持武装并将重试");
            coolUntil = Environment.TickCount64 + 1500;
        }

        // 操作完成：操作区恢复蓝（仍在目标应用则识别区蓝）
        ShowDebugFrames(ColorBlue, ColorBlue);
        await Delay(800);
    }
}
finally
{
    if (debugOcr)
        Fly.ClearDebugRect();
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] 通用识别操作循环结束");
}
