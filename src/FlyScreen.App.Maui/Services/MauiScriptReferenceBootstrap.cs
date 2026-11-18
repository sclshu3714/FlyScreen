using FlyScreen.Services.Programs;

namespace FlyScreen.AppMaui.Services;

/// <summary>
/// Android 等无磁盘程序集路径的宿主：把 FlyScreen.Core PE 注册给脚本编译器。
/// 框架引用已嵌入 Core（Microsoft.NETCore.App.Ref）；此处只补宿主程序集。
/// </summary>
internal static class MauiScriptReferenceBootstrap
{
    public static void TryRegister()
    {
#if ANDROID
        TryRegisterAndroidAsset("FlyScreen.Core", "ScriptRefs/FlyScreen.Core.dll");
#endif
    }

#if ANDROID
    private static void TryRegisterAndroidAsset(string simpleName, string assetPath)
    {
        try
        {
            var assets = Android.App.Application.Context?.Assets;
            if (assets == null)
            {
                FlyScreen.Services.AppLog.Warn("脚本宿主引用注册跳过：Android Assets 尚未就绪。");
                return;
            }

            using var stream = assets.Open(assetPath);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            if (bytes.Length == 0) return;
            ProgramScriptHost.RegisterReferenceImage(simpleName, bytes);
        }
        catch (Exception ex)
        {
            // 若 TryGetRawMetadata 可用仍可能编译成功；仅记警告
            try
            {
                FlyScreen.Services.AppLog.Warn(
                    $"脚本宿主引用注册失败（{simpleName} ← {assetPath}）: {ex.Message}");
            }
            catch
            {
                // ignore
            }
        }
    }
#endif
}
