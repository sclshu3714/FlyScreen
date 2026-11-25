using System.Reflection;
using FlyScreen.Ui.Maui;

namespace FlyScreen.Tool.VideoWall.Ui.Maui;

/// <summary>
/// Android 系统级画中画（Activity PiP，浮在桌面/其它应用之上）。
/// Ui.Maui 无 android TFM，经反射调用 Mono.Android / MAUI Platform API。
/// </summary>
internal static class VideoWallMauiAndroidPip
{
    public static bool IsSupported
        => OperatingSystem.IsAndroidVersionAtLeast(26);

    public static bool IsActive
    {
        get
        {
            if (!IsSupported) return false;
            try
            {
                var activity = GetCurrentActivity();
                if (activity is null) return false;
                var prop = activity.GetType().GetProperty("IsInPictureInPictureMode");
                return prop?.GetValue(activity) is true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool TryEnter(int aspectW = 16, int aspectH = 9)
    {
        if (!IsSupported) return false;
        try
        {
            var activity = GetCurrentActivity();
            if (activity is null) return false;

            var rationalType = Type.GetType("Android.Util.Rational, Mono.Android");
            var builderType = Type.GetType("Android.App.PictureInPictureParams+Builder, Mono.Android");
            var paramsType = Type.GetType("Android.App.PictureInPictureParams, Mono.Android");
            if (rationalType is null || builderType is null || paramsType is null)
                return false;

            var rational = Activator.CreateInstance(rationalType, aspectW, aspectH);
            var builder = Activator.CreateInstance(builderType);
            if (rational is null || builder is null) return false;

            builderType.GetMethod("SetAspectRatio", [rationalType])?.Invoke(builder, [rational]);
            var pipParams = builderType.GetMethod("Build", Type.EmptyTypes)?.Invoke(builder, null);
            if (pipParams is null) return false;

            var enter = activity.GetType().GetMethod(
                "EnterPictureInPictureMode",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: [paramsType],
                modifiers: null);
            if (enter is null) return false;

            var result = enter.Invoke(activity, [pipParams]);
            return result is true || result is null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>请求退出系统 PiP（把任务拉回前台全屏）。</summary>
    public static bool TryLeave()
    {
        if (!IsSupported) return false;
        try
        {
            var activity = GetCurrentActivity();
            if (activity is null) return false;
            if (!IsActive) return true;

            var amType = Type.GetType("Android.App.ActivityManager, Mono.Android");
            var getService = activity.GetType().GetMethod("GetSystemService", [typeof(string)]);
            var am = getService?.Invoke(activity, ["activity"]);
            if (am is null || amType is null) return false;

            var taskIdProp = activity.GetType().GetProperty("TaskId");
            var taskId = taskIdProp?.GetValue(activity);
            if (taskId is null) return false;

            var move = amType.GetMethod("MoveTaskToFront", [typeof(int), typeof(int)]);
            move?.Invoke(am, [taskId, 2]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Subscribe(Action<bool> handler)
        => OsPictureInPictureBridge.ModeChanged += handler;

    public static void Unsubscribe(Action<bool> handler)
        => OsPictureInPictureBridge.ModeChanged -= handler;

    private static object? GetCurrentActivity()
    {
        // net10.0 / windows TFM 没有 Platform.CurrentActivity，运行时反射取
        foreach (var asmName in new[]
                 {
                     "Microsoft.Maui.Essentials",
                     "Microsoft.Maui.Controls",
                     "Microsoft.Maui.ApplicationModel"
                 })
        {
            var platformType = Type.GetType($"Microsoft.Maui.ApplicationModel.Platform, {asmName}");
            var prop = platformType?.GetProperty("CurrentActivity", BindingFlags.Public | BindingFlags.Static);
            var value = prop?.GetValue(null);
            if (value is not null) return value;
        }

        // 回退：扫已加载程序集
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var platformType = asm.GetType("Microsoft.Maui.ApplicationModel.Platform");
                var prop = platformType?.GetProperty("CurrentActivity", BindingFlags.Public | BindingFlags.Static);
                var value = prop?.GetValue(null);
                if (value is not null) return value;
            }
            catch
            {
                /* ignore */
            }
        }

        return null;
    }
}
