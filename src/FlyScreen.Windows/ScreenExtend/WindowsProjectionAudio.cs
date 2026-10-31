using System.Runtime.InteropServices;
using FlyScreen.Services;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.ScreenExtend;

/// <summary>
/// Windows 投影声音：进程回环采集（静音扬声器后仍能采到），waveOut 播放对端 PCM。
/// </summary>
public sealed class WindowsProjectionAudio : IProjectionAudioEngine
{
    private readonly object _capGate = new();
    private readonly object _playGate = new();
    private Thread? _thread;
    private volatile bool _captureRun;
    private TaskCompletionSource<string?>? _openTcs;
    private Action<byte[], int, int>? _onPcm;
    private EndpointMute? _mute;
    private IntPtr _wave;
    private int _playRate;
    private int _playChannels;
    private bool _loggedPlayOk;
    private bool _loggedPlayFail;
    private readonly List<PlayBuf> _pool = [];
    private const uint WhdrPrepared = 0x2;
    private const uint WhdrInQueue = 0x10;

    public bool TryStartCapture(Action<byte[], int, int> onPcm, out string? error)
    {
        StopCapture();
        _onPcm = onPcm;
        _captureRun = true;
        _openTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "FlyScreen.ProjAudio" };
        _thread.Start();
        if (!_openTcs.Task.Wait(5000))
        {
            error = "打开系统声音超时";
            StopCapture();
            return false;
        }

        error = _openTcs.Task.Result;
        if (error != null)
        {
            StopCapture();
            return false;
        }

        return true;
    }

    public void StopCapture()
    {
        _captureRun = false;
        var thread = _thread;
        _thread = null;
        try { thread?.Join(5000); } catch { /* ignore */ }
        lock (_capGate)
        {
            _mute?.Restore();
            _mute = null;
        }
    }

    public bool TryStartPlayback(out string? error)
    {
        error = null;
        _loggedPlayOk = false;
        _loggedPlayFail = false;
        return true;
    }

    public void SubmitPlayback(byte[] pcm, int sampleRate, int channels)
    {
        if (pcm.Length == 0 || sampleRate <= 0 || channels is < 1 or > 2) return;
        var align = channels * 2;
        var length = pcm.Length - (pcm.Length % align);
        if (length <= 0) return;
        lock (_playGate)
        {
            if (_wave == IntPtr.Zero || _playRate != sampleRate || _playChannels != channels)
            {
                CloseWave();
                if (!OpenWave(sampleRate, channels)) return;
            }

            // waveOut 会异步改写 WAVEHDR。按 ref 传入时封送只给一次调用的临时内存，
            // 驱动拿不到稳定地址，WHDR_DONE 也写不回托管字段，缓冲池很快被占满并丢掉后续声音。
            PlayBuf? slot = null;
            foreach (var b in _pool)
            {
                if ((HeaderFlags(b.Header) & WhdrInQueue) == 0)
                {
                    slot = b;
                    break;
                }
            }

            if (slot == null)
            {
                if (_pool.Count >= 12)
                {
                    LogPlayFail("投影声音：播放队列已满，丢掉一段");
                    return;
                }

                slot = PlayBuf.Create();
                _pool.Add(slot);
            }
            else if ((HeaderFlags(slot.Header) & WhdrPrepared) != 0)
            {
                waveOutUnprepareHeader(_wave, slot.Header, Marshal.SizeOf<WAVEHDR>());
            }

            if (slot.Data == IntPtr.Zero || slot.Capacity < length)
            {
                if (slot.Data != IntPtr.Zero) Marshal.FreeHGlobal(slot.Data);
                slot.Data = Marshal.AllocHGlobal(length);
                slot.Capacity = length;
            }

            Marshal.Copy(pcm, 0, slot.Data, length);
            Marshal.StructureToPtr(new WAVEHDR
            {
                lpData = slot.Data,
                dwBufferLength = (uint)length
            }, slot.Header, false);
            var hdrSize = Marshal.SizeOf<WAVEHDR>();
            if (waveOutPrepareHeader(_wave, slot.Header, hdrSize) != 0)
            {
                LogPlayFail("投影声音：播放缓冲准备失败");
                return;
            }

            var wr = waveOutWrite(_wave, slot.Header, hdrSize);
            if (wr != 0)
            {
                LogPlayFail($"投影声音：播放写入失败（{wr}）");
                return;
            }

            if (_loggedPlayOk) return;
            _loggedPlayOk = true;
            AppLog.Info("投影声音：开始播放对端声音");
        }
    }

    public void StopPlayback()
    {
        lock (_playGate) CloseWave();
    }

    private void CaptureLoop()
    {
        CoInitializeEx(IntPtr.Zero, 0);
        object? client = null;
        try
        {
            if (!WasapiLoopback.TryOpen(out client, out var format, out var processLoopback) || client == null)
            {
                _openTcs?.TrySetResult("无法打开系统声音回环");
                return;
            }

            var audio = (IAudioClient)client;
            var capId = typeof(IAudioCaptureClient).GUID;
            if (audio.GetService(ref capId, out var capPtr) != 0 || capPtr == IntPtr.Zero)
            {
                _openTcs?.TrySetResult("无法取得声音采集接口");
                return;
            }
            var capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capPtr);
            Marshal.Release(capPtr);
            if (audio.Start() != 0)
            {
                _openTcs?.TrySetResult("无法开始采集系统声音");
                return;
            }

            if (!processLoopback)
                AppLog.Warn("投影声音：进程回环不可用，本机扬声器仍会出声");
            else
                AppLog.Info($"投影声音：采集 {format.SampleRate} Hz / {format.Channels} 声道 / {(format.IsFloat ? "浮点" : "PCM")} {format.Bits} bit");

            _openTcs?.TrySetResult(_captureRun ? null : "已取消");
            var acc = new List<byte>(8192);
            var outCh = format.Channels > 2 ? 2 : Math.Max(1, format.Channels);
            var bytesPerFrame = outCh * 2;
            var chunk = Math.Max(bytesPerFrame, format.SampleRate * bytesPerFrame / 50);
            var startedAt = Environment.TickCount64;
            var prePeak = 0;
            var postPeak = 0;
            var muteArmed = !processLoopback;
            long muteAt = 0;
            var muteJudged = !processLoopback;
            var loggedSound = false;
            var loggedFormat = false;
            while (_captureRun)
            {
                if (capture.GetNextPacketSize(out var frames) != 0) break;
                if (frames == 0)
                {
                    Thread.Sleep(5);
                    PumpMute(ref muteArmed, ref muteAt, ref muteJudged, ref prePeak, ref postPeak, 0, processLoopback, startedAt);
                    continue;
                }

                var hr = capture.GetBuffer(out var data, out var got, out var flags, out _, out _);
                if (hr != 0)
                {
                    if (hr == 0x08890001) continue;
                    break;
                }

                var packetPeak = 0;
                var bytes = (int)got * format.BlockAlign;
                if (bytes > 0 && (flags & 2) == 0 && data != IntPtr.Zero)
                {
                    var raw = new byte[bytes];
                    Marshal.Copy(data, raw, 0, bytes);
                    var before = acc.Count;
                    var peak = AppendS16(acc, raw, format);
                    if (peak < 0 && !loggedFormat)
                    {
                        loggedFormat = true;
                        AppLog.Warn($"投影声音：采样格式无法转换（{format.Bits} bit），对端没有声音");
                    }
                    else if (peak > packetPeak)
                    {
                        packetPeak = peak;
                    }

                    if (acc.Count == before && peak >= 0 && bytes > 0 && !loggedFormat)
                    {
                        loggedFormat = true;
                        AppLog.Warn($"投影声音：采样格式没有产出数据（{format.Bits} bit {(format.IsFloat ? "float" : "pcm")}）");
                    }
                }
                else if ((flags & 2) != 0 && got > 0)
                {
                    acc.AddRange(new byte[(int)got * bytesPerFrame]);
                }

                capture.ReleaseBuffer(got);
                PumpMute(ref muteArmed, ref muteAt, ref muteJudged, ref prePeak, ref postPeak, packetPeak, processLoopback, startedAt);
                if (packetPeak > 400 && !loggedSound)
                {
                    loggedSound = true;
                    AppLog.Info("投影声音：已采集到有声数据");
                }

                while (acc.Count >= chunk)
                {
                    var take = chunk - (chunk % bytesPerFrame);
                    if (take <= 0) break;
                    var packet = acc.GetRange(0, take).ToArray();
                    acc.RemoveRange(0, take);
                    try { _onPcm?.Invoke(packet, format.SampleRate, outCh); }
                    catch { /* ignore */ }
                }
            }

            try { audio.Stop(); } catch { /* ignore */ }
        }
        catch
        {
            // 采集线程退出后由 StopCapture 恢复静音
        }
        finally
        {
            if (client != null)
            {
                try { Marshal.ReleaseComObject(client); } catch { /* ignore */ }
            }

            CoUninitialize();
            _openTcs?.TrySetResult("采集中断");
        }
    }

    /// <summary>静音扬声器前后各听一小段。若静音把回环截成无声，就恢复本机出声，避免对端一直收到静音。</summary>
    private void PumpMute(ref bool muteArmed, ref long muteAt, ref bool muteJudged,
        ref int prePeak, ref int postPeak, int packetPeak, bool processLoopback, long startedAt)
    {
        if (!processLoopback) return;
        if (!muteArmed)
        {
            if (packetPeak > prePeak) prePeak = packetPeak;
            if (prePeak <= 400 && Environment.TickCount64 - startedAt < 280) return;
            lock (_capGate)
            {
                if (_captureRun) _mute = EndpointMute.TryMuteDefault();
            }

            muteArmed = true;
            muteAt = Environment.TickCount64;
            postPeak = 0;
            return;
        }

        if (!muteJudged && _mute != null)
        {
            var elapsed = Environment.TickCount64 - muteAt;
            if (elapsed >= 250 && packetPeak > postPeak)
                postPeak = packetPeak;
            if (elapsed >= 500)
            {
                muteJudged = true;
                if (prePeak > 400 && postPeak < 40)
                {
                    lock (_capGate)
                    {
                        _mute?.Restore();
                        _mute = null;
                    }

                    AppLog.Warn("投影声音：静音扬声器后采集变成无声，已恢复本机出声，声音仍发往对端");
                    return;
                }

                AppLog.Info("投影声音：本机扬声器已静音");
            }
        }

        lock (_capGate)
        {
            if (_captureRun) _mute?.Hold();
        }
    }

    /// <summary>把一块混音追加为 s16le。无法识别的格式返回 -1。</summary>
    private static int AppendS16(List<byte> acc, byte[] raw, MixFormat format)
    {
        var ch = Math.Max(1, format.Channels);
        var outCh = ch > 2 ? 2 : ch;
        var float32 = format.IsFloat && format.Bits == 32;
        var pcm16 = format.Bits == 16 && !format.IsFloat;
        var pcm24 = format.Bits == 24 && !format.IsFloat;
        var pcm32 = format.Bits == 32 && !format.IsFloat;
        if (!float32 && !pcm16 && !pcm24 && !pcm32) return -1;

        var sampleBytes = float32 || pcm32 ? 4 : pcm24 ? 3 : 2;
        var stride = format.BlockAlign >= sampleBytes * ch ? format.BlockAlign : sampleBytes * ch;
        if (stride <= 0 || raw.Length < stride) return 0;
        var start = acc.Count;
        var frames = raw.Length / stride;
        for (var i = 0; i < frames; i++)
        {
            for (var c = 0; c < outCh; c++)
            {
                var src = c < ch ? c : 0;
                var o = i * stride + src * sampleBytes;
                if (o + sampleBytes > raw.Length) break;
                short s;
                if (float32)
                {
                    var f = BitConverter.ToSingle(raw, o);
                    if (float.IsNaN(f)) f = 0;
                    f = Math.Clamp(f, -1f, 1f);
                    s = (short)Math.Clamp((int)(f * 32767f), short.MinValue, short.MaxValue);
                }
                else if (pcm32)
                {
                    s = (short)(BitConverter.ToInt32(raw, o) >> 16);
                }
                else if (pcm24)
                {
                    var v = raw[o] | (raw[o + 1] << 8) | (raw[o + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    s = (short)(v >> 8);
                }
                else
                {
                    s = BitConverter.ToInt16(raw, o);
                }

                acc.Add((byte)s);
                acc.Add((byte)(s >> 8));
            }
        }

        return PeakS16(acc, start, acc.Count);
    }

    private static int PeakS16(List<byte> acc, int start, int end)
    {
        var peak = 0;
        for (var i = start; i + 1 < end; i += 2)
        {
            var s = (short)(acc[i] | (acc[i + 1] << 8));
            var a = s == short.MinValue ? 32768 : Math.Abs(s);
            if (a > peak) peak = a;
        }

        return peak;
    }

    private bool OpenWave(int sampleRate, int channels)
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1,
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16,
            nBlockAlign = (ushort)(channels * 2),
            cbSize = 0
        };
        fmt.nAvgBytesPerSec = fmt.nSamplesPerSec * fmt.nBlockAlign;
        var mm = waveOutOpen(out _wave, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0);
        if (mm != 0)
        {
            _wave = IntPtr.Zero;
            LogPlayFail($"投影声音：无法打开播放设备（{mm}，{sampleRate} Hz {channels} 声道）");
            return false;
        }

        _playRate = sampleRate;
        _playChannels = channels;
        return true;
    }

    private void CloseWave()
    {
        if (_wave != IntPtr.Zero)
        {
            waveOutReset(_wave);
            var size = Marshal.SizeOf<WAVEHDR>();
            foreach (var b in _pool)
            {
                if (b.Header != IntPtr.Zero && (HeaderFlags(b.Header) & WhdrPrepared) != 0)
                    waveOutUnprepareHeader(_wave, b.Header, size);
                b.Release();
            }

            _pool.Clear();
            waveOutClose(_wave);
            _wave = IntPtr.Zero;
        }
        else
        {
            foreach (var b in _pool) b.Release();
            _pool.Clear();
        }

        _playRate = 0;
        _playChannels = 0;
    }

    private void LogPlayFail(string message)
    {
        if (_loggedPlayFail) return;
        _loggedPlayFail = true;
        AppLog.Warn(message);
    }

    private static uint HeaderFlags(IntPtr hdr)
    {
        if (hdr == IntPtr.Zero) return 0;
        return Marshal.PtrToStructure<WAVEHDR>(hdr).dwFlags;
    }

    private sealed class PlayBuf
    {
        public IntPtr Header;
        public IntPtr Data;
        public int Capacity;

        public static PlayBuf Create()
        {
            var size = Marshal.SizeOf<WAVEHDR>();
            var header = Marshal.AllocHGlobal(size);
            var zeros = new byte[size];
            Marshal.Copy(zeros, 0, header, size);
            return new PlayBuf { Header = header };
        }

        public void Release()
        {
            if (Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Data);
                Data = IntPtr.Zero;
            }

            if (Header != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Header);
                Header = IntPtr.Zero;
            }

            Capacity = 0;
        }
    }

    private readonly struct MixFormat
    {
        public int SampleRate { get; init; }
        public int Channels { get; init; }
        public int Bits { get; init; }
        public int BlockAlign { get; init; }
        public bool IsFloat { get; init; }
    }

    private static class WasapiLoopback
    {
        public static bool TryOpen(out object? client, out MixFormat format, out bool processLoopback)
        {
            client = null;
            format = default;
            processLoopback = false;
            if (TryProcessLoopback(out client, out format))
            {
                processLoopback = true;
                return true;
            }

            client = null;
            return TryDeviceLoopback(out client, out format);
        }

        private static bool TryProcessLoopback(out object? client, out MixFormat format)
        {
            client = null;
            format = default;
            if (!TryActivateProcess(out client) || client == null)
                return false;
            // 官方进程回环示例把混音转成 16 位 PCM。Initialize 失败后同一个 client 不能再初始化。
            if (TryInitPcm16((IAudioClient)client, out format))
                return true;
            try { Marshal.ReleaseComObject(client); } catch { /* ignore */ }
            client = null;
            if (!TryActivateProcess(out client) || client == null)
                return false;
            if (TryInitLoopback((IAudioClient)client, out format))
                return true;
            try { Marshal.ReleaseComObject(client); } catch { /* ignore */ }
            client = null;
            return false;
        }

        private static bool TryActivateProcess(out object? client)
        {
            client = null;
            var parms = new AudioActivationParams
            {
                ActivationType = 1,
                TargetProcessId = (uint)Environment.ProcessId,
                ProcessLoopbackMode = 1
            };
            var blob = Marshal.AllocCoTaskMem(Marshal.SizeOf<AudioActivationParams>());
            var prop = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(parms, blob, false);
                var propSize = IntPtr.Size == 8 ? 24 : 16;
                prop = Marshal.AllocCoTaskMem(propSize);
                for (var i = 0; i < propSize; i++) Marshal.WriteByte(prop, i, 0);
                Marshal.WriteInt16(prop, 0, 65);
                Marshal.WriteInt32(prop, 8, Marshal.SizeOf<AudioActivationParams>());
                Marshal.WriteIntPtr(prop, IntPtr.Size == 8 ? 16 : 12, blob);

                var iid = typeof(IAudioClient).GUID;
                var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                IntPtr got = IntPtr.Zero;
                var handler = new ActivateHandler((hr, ptr) =>
                {
                    got = ptr;
                    done.TrySetResult(hr);
                });
                var opHr = ActivateAudioInterfaceAsync(
                    "VAD\\Process_Loopback", ref iid, prop, handler, out var op);
                if (opHr != 0) return false;
                if (!done.Task.Wait(3000) || done.Task.Result != 0 || got == IntPtr.Zero)
                    return false;
                client = Marshal.GetObjectForIUnknown(got);
                Marshal.Release(got);
                _ = op;
                return client != null;
            }
            catch
            {
                if (client != null)
                {
                    try { Marshal.ReleaseComObject(client); } catch { /* ignore */ }
                    client = null;
                }

                return false;
            }
            finally
            {
                if (prop != IntPtr.Zero) Marshal.FreeCoTaskMem(prop);
                Marshal.FreeCoTaskMem(blob);
            }
        }

        private static bool TryInitPcm16(IAudioClient audio, out MixFormat format)
        {
            format = default;
            var rate = 48000;
            var channels = 2;
            if (audio.GetMixFormat(out var mix) == 0 && mix != IntPtr.Zero)
            {
                var mf = ReadFormat(mix);
                Marshal.FreeCoTaskMem(mix);
                if (mf.SampleRate is >= 8000 and <= 48000) rate = mf.SampleRate;
                if (mf.Channels == 1) channels = 1;
            }

            var pcm = new WAVEFORMATEX
            {
                wFormatTag = 1,
                nChannels = (ushort)channels,
                nSamplesPerSec = (uint)rate,
                wBitsPerSample = 16,
                nBlockAlign = (ushort)(channels * 2),
                cbSize = 0
            };
            pcm.nAvgBytesPerSec = pcm.nSamplesPerSec * pcm.nBlockAlign;
            var mem = Marshal.AllocCoTaskMem(Marshal.SizeOf<WAVEFORMATEX>());
            try
            {
                Marshal.StructureToPtr(pcm, mem, false);
                const uint loopback = 0x00020000;
                const uint autoConvert = 0x80000000;
                var hr = audio.Initialize(0, loopback | autoConvert, 0, 0, mem, IntPtr.Zero);
                if (hr != 0) return false;
                format = new MixFormat
                {
                    SampleRate = rate,
                    Channels = channels,
                    Bits = 16,
                    BlockAlign = channels * 2,
                    IsFloat = false
                };
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeCoTaskMem(mem);
            }
        }

        private static bool TryDeviceLoopback(out object? client, out MixFormat format)
        {
            client = null;
            format = default;
            try
            {
                var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
                var enumId = typeof(IMMDeviceEnumerator).GUID;
                var hr = CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref enumId, out var enumObj);
                if (hr != 0 || enumObj == null) return false;
                var en = (IMMDeviceEnumerator)enumObj;
                if (en.GetDefaultAudioEndpoint(0, 0, out var device) != 0 || device == null) return false;
                var iid = typeof(IAudioClient).GUID;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out var obj) != 0 || obj == null) return false;
                client = obj;
                return TryInitLoopback((IAudioClient)client, out format);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryInitLoopback(IAudioClient audio, out MixFormat format)
        {
            format = default;
            if (audio.GetMixFormat(out var mix) != 0 || mix == IntPtr.Zero) return false;
            try
            {
                format = ReadFormat(mix);
                const uint loopback = 0x00020000;
                var hr = audio.Initialize(0, loopback, 0, 0, mix, IntPtr.Zero);
                return hr == 0 && format.SampleRate > 0 && format.Channels > 0;
            }
            finally
            {
                Marshal.FreeCoTaskMem(mix);
            }
        }

        private static MixFormat ReadFormat(IntPtr mix)
        {
            var tag = (ushort)Marshal.ReadInt16(mix, 0);
            var channels = (ushort)Marshal.ReadInt16(mix, 2);
            var rate = Marshal.ReadInt32(mix, 4);
            var align = (ushort)Marshal.ReadInt16(mix, 12);
            var bits = (ushort)Marshal.ReadInt16(mix, 14);
            var isFloat = tag == 3;
            if (tag == 0xFFFE && Marshal.ReadInt16(mix, 16) >= 22)
            {
                var sub = Marshal.ReadInt32(mix, 24);
                isFloat = sub == 3;
                if (sub == 1) isFloat = false;
            }

            return new MixFormat
            {
                SampleRate = rate,
                Channels = channels,
                Bits = bits,
                BlockAlign = align > 0 ? align : Math.Max(1, (int)channels) * Math.Max(1, bits / 8),
                IsFloat = isFloat
            };
        }
    }

    private sealed class EndpointMute
    {
        private IAudioEndpointVolume? _volume;
        private VolumeHold? _hold;
        private bool _wasMuted;
        private volatile bool _keepMuted;
        private long _nextHold;

        public static EndpointMute? TryMuteDefault()
        {
            try
            {
                var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
                var enumId = typeof(IMMDeviceEnumerator).GUID;
                if (CoCreateInstance(ref clsid, IntPtr.Zero, 23, ref enumId, out var enumObj) != 0 || enumObj == null)
                    return null;
                var en = (IMMDeviceEnumerator)enumObj;
                if (en.GetDefaultAudioEndpoint(0, 0, out var device) != 0 || device == null) return null;
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out var volObj) != 0 || volObj == null) return null;
                var vol = (IAudioEndpointVolume)volObj;
                vol.GetMute(out var muted);
                var box = new EndpointMute { _volume = vol, _wasMuted = muted, _keepMuted = true };
                vol.SetMute(true, IntPtr.Zero);
                box._hold = new VolumeHold(box);
                try { vol.RegisterControlChangeNotify(box._hold); }
                catch { box._hold = null; }
                return box;
            }
            catch
            {
                return null;
            }
        }

        public void Restore()
        {
            _keepMuted = false;
            try
            {
                if (_hold != null) _volume?.UnregisterControlChangeNotify(_hold);
            }
            catch { /* ignore */ }
            _hold = null;
            try { _volume?.SetMute(_wasMuted, IntPtr.Zero); } catch { /* ignore */ }
            _volume = null;
        }

        private void ReMute()
        {
            if (!_keepMuted) return;
            try { _volume?.SetMute(true, IntPtr.Zero); } catch { /* ignore */ }
        }

        public void Hold()
        {
            if (!_keepMuted || _volume == null) return;
            var now = Environment.TickCount64;
            if (now < _nextHold) return;
            _nextHold = now + 200;
            try
            {
                _volume.GetMute(out var muted);
                if (!muted && _keepMuted) _volume.SetMute(true, IntPtr.Zero);
            }
            catch { /* ignore */ }
        }

        private sealed class VolumeHold : IAudioEndpointVolumeCallback
        {
            private readonly EndpointMute _owner;
            private int _applying;
            public VolumeHold(EndpointMute owner) => _owner = owner;

            public int OnNotify(IntPtr notify)
            {
                if (notify == IntPtr.Zero || !_owner._keepMuted) return 0;
                // AUDIO_VOLUME_NOTIFICATION_DATA：16 字节 GUID 之后是 BOOL bMuted。
                var muted = Marshal.ReadInt32(notify, 16) != 0;
                if (muted) return 0;
                if (Interlocked.Exchange(ref _applying, 1) == 1) return 0;
                try
                {
                    if (_owner._keepMuted) _owner.ReMute();
                }
                finally
                {
                    Interlocked.Exchange(ref _applying, 0);
                }

                return 0;
            }
        }
    }

    private sealed class ActivateHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly Action<int, IntPtr> _done;
        public ActivateHandler(Action<int, IntPtr> done) => _done = done;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            int hr = -1;
            var ptr = IntPtr.Zero;
            try
            {
                operation.GetActivateResult(out hr, out ptr);
            }
            catch { /* ignore */ }
            _done(hr, ptr);
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint size);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long def, out long min);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, out IntPtr iface);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out uint flags, out ulong devicePosition, out ulong qpc);
        [PreserveSig] int ReleaseBuffer(uint numFrames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, IntPtr ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, IntPtr ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint index, float level, IntPtr ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint index, float level, IntPtr ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint index, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint index, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notify);
    }

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, out IntPtr activatedInterface);
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coinit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler handler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hwo, int device, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hwo);
}
