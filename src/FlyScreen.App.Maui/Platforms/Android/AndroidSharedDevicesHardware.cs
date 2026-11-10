#if ANDROID
using System.Collections;
using Android.Hardware;
using Android.Media;
using Android.OS;
using FlyScreen.Services.Tools.SharedDevices;
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
            if (am != null && Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                // 反射 getDevices(int)：返回 AudioDeviceInfo[]
                var method = am.Class.GetMethod("getDevices", Java.Lang.Integer.Type!);
                var raw = method?.Invoke(am, new Java.Lang.Integer(inputs ? 1 : 2));
                IEnumerable? seq = raw as System.Collections.IEnumerable;
                if (seq != null)
                {
                    foreach (var o in seq)
                    {
                        if (o is not AudioDeviceInfo d) continue;
                        if (inputs)
                        {
                            if (d.Type is AudioDeviceType.BuiltinEarpiece or AudioDeviceType.Telephony)
                                continue;
                        }
                        else if (d.Type is AudioDeviceType.BuiltinEarpiece or AudioDeviceType.Telephony)
                        {
                            continue;
                        }

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
        catch
        {
            // fall through
        }

        if (list.Count == 0)
            return FallbackAudio(inputs);

        if (!list.Any(d => d.IsDefault))
            list[0] = CloneDefault(list[0]);
        return list;
    }

    private static IReadOnlyList<SharedLocalDevice> FallbackAudio(bool inputs)
    {
        var kind = inputs ? SharedDeviceKinds.Microphone : SharedDeviceKinds.Speaker;
        return
        [
            new SharedLocalDevice
            {
                Kind = kind,
                Id = "default",
                Name = "系统默认",
                IsDefault = true
            }
        ];
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
