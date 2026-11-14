#if ANDROID
using Android.Hardware;
using Android.Media;
using Android.OS;
using FlyScreen.Services;
using FlyScreen.ToolSdk;
using Camera = Android.Hardware.Camera;
using Application = Android.App.Application;

namespace FlyScreen.AppMaui.Platforms.Android;

/// <summary>Android：枚举本机摄像头 / 麦克风 / 扬声器。</summary>
public sealed class AndroidSharedDevicesHardware : ISharedDevicesHardware
{
    public IReadOnlyList<SharedLocalDevice> Enumerate(string kind)
    {
        if (kind.Equals(SharedDeviceKinds.Camera, StringComparison.OrdinalIgnoreCase))
            return EnumCameras();
        if (kind.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase))
            return EnumAudio(inputs: true);
        if (kind.Equals(SharedDeviceKinds.Speaker, StringComparison.OrdinalIgnoreCase))
            return EnumAudio(inputs: false);
        return [];
    }

    private static IReadOnlyList<SharedLocalDevice> EnumCameras()
    {
        var list = new List<SharedLocalDevice>();
#pragma warning disable CA1422
        var n = Camera.NumberOfCameras;
        var info = new Camera.CameraInfo();
        for (var i = 0; i < n; i++)
        {
            Camera.GetCameraInfo(i, info);
            var facing = info.Facing == CameraFacing.Front ? "前置" : "后置";
            list.Add(new SharedLocalDevice
            {
                Kind = SharedDeviceKinds.Camera,
                Id = i.ToString(),
                Name = $"摄像头 {i + 1}（{facing}）",
                IsDefault = info.Facing == CameraFacing.Back || (i == 0 && list.Count == 0)
            });
        }
#pragma warning restore CA1422
        if (list.Count > 0 && !list.Any(d => d.IsDefault))
            list[0] = new SharedLocalDevice
            {
                Kind = list[0].Kind,
                Id = list[0].Id,
                Name = list[0].Name,
                IsDefault = true
            };
        return list;
    }

    private static IReadOnlyList<SharedLocalDevice> EnumAudio(bool inputs)
    {
        var list = new List<SharedLocalDevice>();
        try
        {
            var am = Application.Context.GetSystemService(global::Android.Content.Context.AudioService) as AudioManager;
            // 勿用 Java 反射 getDevices：部分机型 Method 解析失败会得到空列表，
            // 导致只宣告摄像头、申请端看不到麦克风/扬声器。
            if (am != null && Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                var flags = inputs ? GetDevicesTargets.Inputs : GetDevicesTargets.Outputs;
                var devices = am.GetDevices(flags);
                if (devices != null)
                {
                    foreach (var d in devices)
                    {
                        if (d == null) continue;
                        if (d.Type is AudioDeviceType.BuiltinEarpiece or AudioDeviceType.Telephony)
                            continue;

                        var name = string.IsNullOrWhiteSpace(d.ProductName)
                            ? DescribeAudioType(d.Type, inputs)
                            : d.ProductName.ToString()!;
                        list.Add(new SharedLocalDevice
                        {
                            Kind = inputs ? SharedDeviceKinds.Microphone : SharedDeviceKinds.Speaker,
                            Id = d.Id.ToString(),
                            Name = name,
                            IsDefault = IsLikelyDefault(d.Type, inputs)
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Android 枚举音频设备失败: " + ex.Message);
        }

        // 手机必有内置麦/扬声器；枚举失败时仍宣告种类，否则对端申请对话框会隐藏勾选项
        if (list.Count == 0)
        {
            list.Add(new SharedLocalDevice
            {
                Kind = inputs ? SharedDeviceKinds.Microphone : SharedDeviceKinds.Speaker,
                Id = "default",
                Name = inputs ? "内置麦克风" : "内置扬声器",
                IsDefault = true
            });
            return list;
        }

        if (!list.Any(d => d.IsDefault))
            list[0] = CloneDefault(list[0]);
        return list;
    }

    private static SharedLocalDevice CloneDefault(SharedLocalDevice d) => new()
    {
        Kind = d.Kind,
        Id = d.Id,
        Name = d.Name,
        IsDefault = true
    };

    private static bool IsLikelyDefault(AudioDeviceType type, bool inputs) => inputs
        ? type is AudioDeviceType.BuiltinMic or AudioDeviceType.WiredHeadset
        : type is AudioDeviceType.BuiltinSpeaker or AudioDeviceType.WiredHeadphones
            or AudioDeviceType.BluetoothA2dp;

    private static string DescribeAudioType(AudioDeviceType type, bool inputs) => type switch
    {
        AudioDeviceType.BuiltinMic => "内置麦克风",
        AudioDeviceType.BuiltinSpeaker => "内置扬声器",
        AudioDeviceType.WiredHeadset => "有线耳机麦",
        AudioDeviceType.WiredHeadphones => "有线耳机",
        AudioDeviceType.BluetoothSco => "蓝牙通话",
        AudioDeviceType.BluetoothA2dp => "蓝牙音频",
        AudioDeviceType.UsbDevice => "USB 音频",
        _ => inputs ? $"输入设备 ({type})" : $"输出设备 ({type})"
    };
}
#endif
