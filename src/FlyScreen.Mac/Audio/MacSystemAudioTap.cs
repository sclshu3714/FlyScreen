using System.Runtime.InteropServices;
using FlyScreen.Services;

namespace FlyScreen.Mac.Audio;

/// <summary>
/// 采集 macOS 正在播放的声音（14.2 起的进程音频接口）。
/// 读取期间本机扬声器不出声，停止后恢复。
/// </summary>
internal sealed class MacSystemAudioTap : IDisposable
{
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string System = "/usr/lib/libSystem.dylib";
    private const int RtldLazy = 1;
    private const uint FormatLinearPcm = 0x6C70636D;
    private const uint FlagFloat = 1;
    private const uint FlagBigEndian = 2;
    private const uint FlagNonInterleaved = 32;
    private const uint StreamFormat = 0x73666D74;
    private const uint ScopeInput = 0x696E7074;

    private readonly AudioDeviceIOProc _proc;
    private Action<byte[], int, int>? _onPcm;
    private IntPtr _description;
    private uint _tapId;
    private uint _aggregateId;
    private IntPtr _procId;
    private int _disposed;
    private volatile bool _run;

    private int _rate = 48000;
    private int _srcChannels = 2;
    private int _outChannels = 2;
    private int _bits = 32;
    private bool _float = true;
    private bool _bigEndian;
    private bool _nonInterleaved = true;

    public int SampleRate => _rate;
    public int Channels => _outChannels;

    public MacSystemAudioTap()
    {
        _proc = OnIo;
    }

    public bool TryStart(Action<byte[], int, int> onPcm, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsMacOS())
        {
            error = "非 macOS";
            return false;
        }

        IntPtr pool = IntPtr.Zero;
        try
        {
            dlopen(CoreAudio, RtldLazy);
            pool = objc_autoreleasePoolPush();
            var cls = objc_getClass("CATapDescription");
            if (cls == IntPtr.Zero)
            {
                error = "当前 macOS 不能采集系统声音（需要 14.2 或更高版本），画面仍会投影";
                return false;
            }

            var empty = Send(objc_getClass("NSArray"), Sel("array"));
            _description = Send1(Send(cls, Sel("alloc")), Sel("initStereoGlobalTapButExcludeProcesses:"), empty);
            if (_description == IntPtr.Zero)
            {
                error = "无法创建系统声音采集";
                return false;
            }

            Send1(_description, Sel("setName:"), Ns("玄鸟"));
            if (Responds(_description, "setPrivate:"))
                SendBool(_description, Sel("setPrivate:"), true);
            if (Responds(_description, "setMuteBehavior:"))
                SendInt(_description, Sel("setMuteBehavior:"), 2);

            var tapStatus = AudioHardwareCreateProcessTap(_description, out _tapId);
            if (tapStatus != 0 || _tapId == 0)
            {
                error = "无法采集系统声音。请到系统设置 → 隐私与安全性 → 屏幕与系统音频录制，允许玄鸟后再试"
                    + (tapStatus == 0 ? "" : $"（{tapStatus}）");
                Cleanup();
                return false;
            }

            var uuid = UuidString(_description);
            if (string.IsNullOrEmpty(uuid))
            {
                error = "系统声音采集没有标识";
                Cleanup();
                return false;
            }

            var dict = AggregateDescription(uuid);
            if (dict == IntPtr.Zero)
            {
                error = "当前系统缺少音频采集接口";
                Cleanup();
                return false;
            }

            var aggStatus = AudioHardwareCreateAggregateDevice(dict, out _aggregateId);
            if (aggStatus != 0 || _aggregateId == 0)
            {
                error = "无法打开系统声音（" + aggStatus + "）";
                Cleanup();
                return false;
            }

            if (!ReadFormat())
            {
                error = "无法读取系统声音格式";
                Cleanup();
                return false;
            }

            _onPcm = onPcm;
            _run = true;
            var procStatus = AudioDeviceCreateIOProcID(_aggregateId, _proc, IntPtr.Zero, out _procId);
            if (procStatus != 0 || _procId == IntPtr.Zero)
            {
                error = "无法开始读取系统声音（" + procStatus + "）";
                Cleanup();
                return false;
            }

            var startStatus = AudioDeviceStart(_aggregateId, _procId);
            if (startStatus != 0)
            {
                error = "无法开始采集系统声音（" + startStatus + "）";
                Cleanup();
                return false;
            }

            AppLog.Info($"投影声音：采集 {_rate} Hz / {_outChannels} 声道，本机扬声器在采集期间静音");
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            Cleanup();
            error = "当前 macOS 不能采集系统声音（需要 14.2 或更高版本），画面仍会投影";
            return false;
        }
        catch (Exception ex)
        {
            Cleanup();
            error = "采集系统声音失败：" + ex.Message;
            return false;
        }
        finally
        {
            if (pool != IntPtr.Zero)
            {
                try { objc_autoreleasePoolPop(pool); } catch { /* ignore */ }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _run = false;
        Cleanup();
        _onPcm = null;
    }

    private void Cleanup()
    {
        _run = false;
        var aggregate = _aggregateId;
        var proc = _procId;
        _procId = IntPtr.Zero;
        if (aggregate != 0 && proc != IntPtr.Zero)
        {
            try { AudioDeviceStop(aggregate, proc); } catch { /* ignore */ }
            try { AudioDeviceDestroyIOProcID(aggregate, proc); } catch { /* ignore */ }
        }

        if (_aggregateId != 0)
        {
            try { AudioHardwareDestroyAggregateDevice(_aggregateId); } catch { /* ignore */ }
            _aggregateId = 0;
        }

        if (_tapId != 0)
        {
            try { AudioHardwareDestroyProcessTap(_tapId); } catch { /* ignore */ }
            _tapId = 0;
        }

        var description = _description;
        _description = IntPtr.Zero;
        if (description != IntPtr.Zero)
        {
            try { Send(description, Sel("release")); } catch { /* ignore */ }
        }
    }

    private bool ReadFormat()
    {
        var address = new AudioObjectPropertyAddress
        {
            Selector = StreamFormat,
            Scope = ScopeInput,
            Element = 0
        };
        var size = Marshal.SizeOf<AudioStreamBasicDescription>();
        var format = new AudioStreamBasicDescription();
        if (AudioObjectGetPropertyData(_aggregateId, ref address, 0, IntPtr.Zero, ref size, ref format) != 0)
            return false;
        if (format.FormatID != FormatLinearPcm || format.SampleRate < 8000 || format.ChannelsPerFrame == 0)
            return false;

        _rate = (int)Math.Round(format.SampleRate);
        _srcChannels = (int)format.ChannelsPerFrame;
        _outChannels = Math.Clamp(_srcChannels, 1, 2);
        _bits = (int)format.BitsPerChannel;
        _float = (format.FormatFlags & FlagFloat) != 0;
        _bigEndian = (format.FormatFlags & FlagBigEndian) != 0;
        _nonInterleaved = (format.FormatFlags & FlagNonInterleaved) != 0;
        if (!_float && _bits != 16 && _bits != 32)
            return false;
        return true;
    }

    private int OnIo(uint device, IntPtr now, IntPtr inputData, IntPtr inputTime, IntPtr outputData, IntPtr outputTime, IntPtr client)
    {
        _ = device;
        _ = now;
        _ = inputTime;
        _ = outputData;
        _ = outputTime;
        _ = client;
        if (!_run || inputData == IntPtr.Zero || _onPcm == null) return 0;
        try
        {
            var pcm = ToS16(inputData);
            if (pcm != null)
                _onPcm(pcm, _rate, _outChannels);
        }
        catch
        {
            /* 单段失败继续 */
        }

        return 0;
    }

    private byte[]? ToS16(IntPtr list)
    {
        var buffers = Marshal.ReadInt32(list);
        if (buffers <= 0) return null;
        var sampleBytes = _float || _bits > 16 ? 4 : 2;
        int frames;
        if (_nonInterleaved)
        {
            var bytes = Marshal.ReadInt32(list + 8 + 4);
            if (bytes <= 0) return null;
            frames = bytes / sampleBytes;
        }
        else
        {
            var bytes = Marshal.ReadInt32(list + 8 + 4);
            var stride = sampleBytes * Math.Max(1, _srcChannels);
            if (bytes <= 0 || stride <= 0) return null;
            frames = bytes / stride;
        }

        if (frames <= 0) return null;
        var pcm = new byte[frames * _outChannels * 2];
        if (_nonInterleaved)
        {
            var count = Math.Min(_outChannels, buffers);
            for (var c = 0; c < count; c++)
            {
                var data = Marshal.ReadIntPtr(list + 8 + (c * 16) + 8);
                if (data == IntPtr.Zero) continue;
                for (var i = 0; i < frames; i++)
                    WriteS16(pcm, (i * _outChannels + c) * 2, ReadSample(data, i));
            }
        }
        else
        {
            var data = Marshal.ReadIntPtr(list + 16);
            if (data == IntPtr.Zero) return null;
            var src = Math.Max(1, _srcChannels);
            for (var i = 0; i < frames; i++)
            {
                for (var c = 0; c < _outChannels; c++)
                    WriteS16(pcm, (i * _outChannels + c) * 2, ReadSample(data, i * src + c));
            }
        }

        return pcm;
    }

    private short ReadSample(IntPtr data, int index)
    {
        if (_float)
        {
            var raw = Read32(data, index);
            var f = BitConverter.Int32BitsToSingle(raw);
            if (f > 1) f = 1;
            if (f < -1) f = -1;
            return (short)Math.Round(f * 32767.0);
        }

        if (_bits <= 16)
            return Read16(data, index);
        return (short)(Read32(data, index) >> 16);
    }

    private int Read32(IntPtr data, int index)
    {
        var v = Marshal.ReadInt32(data, index * 4);
        if (!_bigEndian) return v;
        return (v << 24)
            | ((v & 0xFF00) << 8)
            | ((v >> 8) & 0xFF00)
            | (v >> 24 & 0xFF);
    }

    private short Read16(IntPtr data, int index)
    {
        var v = Marshal.ReadInt16(data, index * 2);
        if (!_bigEndian) return v;
        return (short)((v << 8) | ((v >> 8) & 0xFF));
    }

    private static void WriteS16(byte[] pcm, int offset, short sample)
    {
        pcm[offset] = (byte)sample;
        pcm[offset + 1] = (byte)(sample >> 8);
    }

    private IntPtr AggregateDescription(string uuid)
    {
        var nameKey = Key("kAudioAggregateDeviceNameKey");
        var uidKey = Key("kAudioAggregateDeviceUIDKey");
        var privateKey = Key("kAudioAggregateDeviceIsPrivateKey");
        var stackedKey = Key("kAudioAggregateDeviceIsStackedKey");
        var autoKey = Key("kAudioAggregateDeviceTapAutoStartKey");
        var subsKey = Key("kAudioAggregateDeviceSubDeviceListKey");
        var tapsKey = Key("kAudioAggregateDeviceTapListKey");
        var tapUidKey = Key("kAudioSubTapUIDKey");
        var driftKey = Key("kAudioSubTapDriftCompensationKey");
        if (nameKey == IntPtr.Zero || uidKey == IntPtr.Zero || privateKey == IntPtr.Zero
            || tapsKey == IntPtr.Zero || tapUidKey == IntPtr.Zero || driftKey == IntPtr.Zero)
            return IntPtr.Zero;

        var tap = Send(objc_getClass("NSMutableDictionary"), Sel("dictionary"));
        Send2(tap, Sel("setObject:forKey:"), Ns(uuid), tapUidKey);
        if (driftKey != IntPtr.Zero)
            Send2(tap, Sel("setObject:forKey:"), Bool(true), driftKey);

        var dict = Send(objc_getClass("NSMutableDictionary"), Sel("dictionary"));
        Send2(dict, Sel("setObject:forKey:"), Ns("玄鸟"), nameKey);
        Send2(dict, Sel("setObject:forKey:"), Ns(Guid.NewGuid().ToString("N")), uidKey);
        Send2(dict, Sel("setObject:forKey:"), Bool(true), privateKey);
        if (stackedKey != IntPtr.Zero)
            Send2(dict, Sel("setObject:forKey:"), Bool(false), stackedKey);
        if (autoKey != IntPtr.Zero)
            Send2(dict, Sel("setObject:forKey:"), Bool(true), autoKey);
        if (subsKey != IntPtr.Zero)
            Send2(dict, Sel("setObject:forKey:"), Send(objc_getClass("NSArray"), Sel("array")), subsKey);
        Send2(dict, Sel("setObject:forKey:"), Send1(objc_getClass("NSArray"), Sel("arrayWithObject:"), tap), tapsKey);
        return dict;
    }

    private static string? UuidString(IntPtr description)
    {
        var uuid = Send(description, Sel("UUID"));
        if (uuid == IntPtr.Zero) return null;
        var text = Send(uuid, Sel("UUIDString"));
        if (text == IntPtr.Zero) return null;
        var utf8 = Send(text, Sel("UTF8String"));
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static bool Responds(IntPtr obj, string selector)
    {
        try { return SendResponds(obj, Sel("respondsToSelector:"), Sel(selector)); }
        catch { return false; }
    }

    private static IntPtr Key(string symbol)
    {
        var slot = dlsym((IntPtr)(-2), symbol);
        return slot == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(slot);
    }

    private static IntPtr Ns(string text) => SendUtf8(objc_getClass("NSString"), Sel("stringWithUTF8String:"), text);

    private static IntPtr Bool(bool value) => SendBoolRet(objc_getClass("NSNumber"), Sel("numberWithBool:"), value);

    private static IntPtr Sel(string name) => sel_registerName(name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AudioDeviceIOProc(
        uint device, IntPtr now, IntPtr inputData, IntPtr inputTime,
        IntPtr outputData, IntPtr outputTime, IntPtr clientData);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioObjectPropertyAddress
    {
        public uint Selector;
        public uint Scope;
        public uint Element;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioStreamBasicDescription
    {
        public double SampleRate;
        public uint FormatID;
        public uint FormatFlags;
        public uint BytesPerPacket;
        public uint FramesPerPacket;
        public uint BytesPerFrame;
        public uint ChannelsPerFrame;
        public uint BitsPerChannel;
        public uint Reserved;
    }

    [DllImport(CoreAudio)]
    private static extern int AudioHardwareCreateProcessTap(IntPtr description, out uint tapId);

    [DllImport(CoreAudio)]
    private static extern int AudioHardwareDestroyProcessTap(uint tapId);

    [DllImport(CoreAudio)]
    private static extern int AudioHardwareCreateAggregateDevice(IntPtr description, out uint deviceId);

    [DllImport(CoreAudio)]
    private static extern int AudioHardwareDestroyAggregateDevice(uint deviceId);

    [DllImport(CoreAudio)]
    private static extern int AudioObjectGetPropertyData(
        uint objectId, ref AudioObjectPropertyAddress address, uint qualifierSize, IntPtr qualifier,
        ref int dataSize, ref AudioStreamBasicDescription data);

    [DllImport(CoreAudio)]
    private static extern int AudioDeviceCreateIOProcID(uint device, AudioDeviceIOProc proc, IntPtr client, out IntPtr procId);

    [DllImport(CoreAudio)]
    private static extern int AudioDeviceDestroyIOProcID(uint device, IntPtr procId);

    [DllImport(CoreAudio)]
    private static extern int AudioDeviceStart(uint device, IntPtr procId);

    [DllImport(CoreAudio)]
    private static extern int AudioDeviceStop(uint device, IntPtr procId);

    [DllImport(System)]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport(System)]
    private static extern IntPtr dlsym(IntPtr handle, string symbol);

    [DllImport(Objc)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(Objc)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(Objc)]
    private static extern IntPtr objc_autoreleasePoolPush();

    [DllImport(Objc)]
    private static extern void objc_autoreleasePoolPop(IntPtr pool);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send1(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send2(IntPtr receiver, IntPtr selector, IntPtr arg, IntPtr arg2);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendUtf8(IntPtr receiver, IntPtr selector, string utf8);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void SendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendBoolRet(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    private static extern void SendInt(IntPtr receiver, IntPtr selector, nint arg);

    [DllImport(Objc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendResponds(IntPtr receiver, IntPtr selector, IntPtr arg);
}
