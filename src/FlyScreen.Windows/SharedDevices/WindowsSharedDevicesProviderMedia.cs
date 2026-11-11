using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using FlyScreen.Services;
using FlyScreen.ToolSdk;

namespace FlyScreen.SharedDevices;

/// <summary>
/// Windows 提供方本机测试：摄像头 JPEG 上行、麦克风 PCM 上行、扬声器试音。
/// 对齐 Android <c>AndroidSharedDevicesProviderMedia</c>。
/// </summary>
public sealed class WindowsSharedDevicesProviderMedia : IDisposable
{
    /// <summary>
    /// Maui-Win 等环境可注入 MediaCapture 采集（MF 枚举常失败）。
    /// 返回 true 表示已接管摄像头循环。
    /// </summary>
    public static Func<string?, Func<bool>, Action<byte[]>, Action<string>, bool>? CameraCaptureOverride { get; set; }

    private readonly ISharedDevicesRuntime _svc;
    private readonly object _gate = new();
    private int _disposed;

    private Thread? _camThread;
    private volatile bool _camRun;
    private Thread? _micThread;
    private volatile bool _micRun;
    private IntPtr _toneWave;

    public WindowsSharedDevicesProviderMedia(ISharedDevicesRuntime svc)
    {
        _svc = svc ?? throw new ArgumentNullException(nameof(svc));
        _svc.ProviderTestCommand += OnTestCommand;
        _svc.SessionChanged += OnSessionChanged;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _svc.ProviderTestCommand -= OnTestCommand;
        _svc.SessionChanged -= OnSessionChanged;
        StopAll();
    }

    private void OnSessionChanged()
    {
        if (!_svc.IsProviderSessionActive)
            StopAll();
    }

    private void OnTestCommand(string kind, bool start)
    {
        if (!start)
        {
            StopKind(kind);
            return;
        }

        _ = Task.Run(() =>
        {
            try { StartKind(kind); }
            catch (Exception ex)
            {
                AppLog.Warn("共享设备本机测试失败: " + ex.Message);
                _svc.PublishStatus("本机测试失败：" + ex.Message);
            }
        });
    }

    private void StartKind(string kind)
    {
        if (kind.Equals(SharedDeviceKinds.Camera, StringComparison.OrdinalIgnoreCase))
            StartCamera();
        else if (kind.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase))
            StartMic();
        else if (kind.Equals(SharedDeviceKinds.Speaker, StringComparison.OrdinalIgnoreCase))
            PlayTone();
    }

    private void StopKind(string kind)
    {
        if (kind.Equals(SharedDeviceKinds.Camera, StringComparison.OrdinalIgnoreCase))
            StopCamera();
        else if (kind.Equals(SharedDeviceKinds.Microphone, StringComparison.OrdinalIgnoreCase))
            StopMic();
        else if (kind.Equals(SharedDeviceKinds.Speaker, StringComparison.OrdinalIgnoreCase))
            StopTone();
    }

    private void StopAll()
    {
        StopCamera();
        StopMic();
        StopTone();
    }

    #region Camera

    private void StartCamera()
    {
        StopCamera();
        _camRun = true;
        _camThread = new Thread(CameraLoop) { IsBackground = true, Name = "FlyScreen.SdCam" };
        _camThread.Start();
    }

    private void StopCamera()
    {
        _camRun = false;
        try { _camThread?.Join(2000); } catch { /* ignore */ }
        _camThread = null;
    }

    private void CameraLoop()
    {
        var prefer = _svc.GetSelectedDeviceId(SharedDeviceKinds.Camera);
        var captureOverride = CameraCaptureOverride;
        if (captureOverride != null)
        {
            try
            {
                if (captureOverride(
                        prefer,
                        () => _camRun,
                        jpeg => _svc.PostCameraJpeg(jpeg),
                        msg => _svc.PublishStatus(msg)))
                    return;
            }
            catch (Exception ex)
            {
                AppLog.Warn("摄像头 MediaCapture 回退失败: " + ex.Message);
            }
        }

        CoInitializeEx(IntPtr.Zero, 0x2);
        if (MFStartup(MfVersion, 0) != 0)
        {
            _svc.PublishStatus("无法初始化摄像头组件");
            return;
        }

        IMFSourceReader? reader = null;
        try
        {
            if (!TryOpenCameraReader(out reader) || reader == null)
            {
                _svc.PublishStatus("无法打开本机摄像头");
                return;
            }

            TrySetRgb32(reader);
            if (!TryGetFrameSize(reader, out var width, out var height) || width <= 0 || height <= 0)
            {
                width = 640;
                height = 480;
            }

            _svc.PublishStatus("本机摄像头测试推流中");
            var lastMs = 0L;
            while (_camRun)
            {
                var hr = reader.ReadSample(MfSourceReaderFirstVideoStream, 0, out _, out var flags, out _, out var sample);
                if (hr != 0 || sample == null)
                {
                    Thread.Sleep(30);
                    continue;
                }

                try
                {
                    if ((flags & MfSourceReaderfEndOfStream) != 0)
                        break;
                    var now = Environment.TickCount64;
                    if (now - lastMs < 120)
                        continue;
                    lastMs = now;
                    if (TrySampleToJpeg(sample, width, height, out var jpeg) && jpeg.Length > 0)
                        _svc.PostCameraJpeg(jpeg);
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("摄像头采集异常: " + ex.Message);
            _svc.PublishStatus("摄像头采集失败：" + ex.Message);
        }
        finally
        {
            if (reader != null)
                try { Marshal.ReleaseComObject(reader); } catch { /* ignore */ }
            MFShutdown();
        }
    }

    private bool TryOpenCameraReader(out IMFSourceReader? reader)
    {
        reader = null;
        var prefer = _svc.GetSelectedDeviceId(SharedDeviceKinds.Camera);

        // Maui-Win / WinUI 上 MFEnumDeviceSources 常为空，但 SetupAPI/DirectShow 仍能列出真实摄像头。
        var links = WindowsSharedDevicesHardware.ListCameraSymbolicLinks(prefer);
        if (links.Count == 0)
            AppLog.Warn("摄像头测试：未解析到任何符号链接（列表可能只有实例 Id）");
        else
            AppLog.Info($"摄像头测试：尝试 {links.Count} 个符号链接打开");

        foreach (var link in links)
        {
            if (TryOpenCameraBySymbolicLink(link, out reader) && reader != null)
                return true;
        }

        if (!string.IsNullOrEmpty(prefer)
            && prefer.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            && TryOpenCameraBySymbolicLink(prefer, out reader) && reader != null)
            return true;

        // MF 枚举（桌面 WPF 等环境仍可用）
        if (MFCreateAttributes(out var attrs, 1) == 0 && attrs != null)
        {
            try
            {
                var srcType = MfDevsourceAttributeSourceType;
                var vidCap = MfDevsourceAttributeSourceTypeVidcapGuid;
                if (attrs.SetGUID(ref srcType, ref vidCap) == 0
                    && MFEnumDeviceSources(attrs, out var devices, out var count) == 0
                    && devices != IntPtr.Zero && count > 0)
                {
                    try
                    {
                        var activates = new List<IMFActivate>((int)count);
                        try
                        {
                            for (uint i = 0; i < count; i++)
                            {
                                var p = Marshal.ReadIntPtr(devices, (int)(i * (nuint)IntPtr.Size));
                                if (p == IntPtr.Zero) continue;
                                activates.Add((IMFActivate)Marshal.GetObjectForIUnknown(p));
                                Marshal.Release(p);
                            }

                            var preferred = new List<IMFActivate>();
                            var rest = new List<IMFActivate>();
                            foreach (var a in activates)
                            {
                                if (!string.IsNullOrEmpty(prefer) && ActivateMatches(a, prefer))
                                    preferred.Add(a);
                                else
                                    rest.Add(a);
                            }

                            foreach (var act in preferred.Concat(rest))
                            {
                                if (TryActivateReader(act, out reader) && reader != null)
                                    return true;
                            }

                            AppLog.Warn($"摄像头测试：已尝试 {activates.Count} 个 MF 设备均无法打开");
                        }
                        finally
                        {
                            foreach (var a in activates)
                                SafeRelease(a);
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(devices);
                    }
                }
                else
                {
                    AppLog.Warn("摄像头测试：MF 未枚举到视频捕获设备（已尝试符号链接回退）");
                }
            }
            finally
            {
                SafeRelease(attrs);
            }
        }

        return false;
    }

    private static bool TryOpenCameraBySymbolicLink(string symbolicLink, out IMFSourceReader? reader)
    {
        reader = null;
        if (string.IsNullOrWhiteSpace(symbolicLink)) return false;
        if (MFCreateAttributes(out var attrs, 2) != 0 || attrs == null)
            return false;
        IMFMediaSource? source = null;
        try
        {
            var srcType = MfDevsourceAttributeSourceType;
            var vidCap = MfDevsourceAttributeSourceTypeVidcapGuid;
            var linkKey = MfDevsourceAttributeSourceTypeVidcapSymbolicLink;
            if (attrs.SetGUID(ref srcType, ref vidCap) != 0)
                return false;
            if (attrs.SetString(ref linkKey, symbolicLink) != 0)
                return false;
            var hr = MFCreateDeviceSource(attrs, out source);
            if (hr != 0 || source == null)
            {
                AppLog.Warn($"摄像头测试：按符号链接打开失败 hr=0x{hr:X8}");
                return false;
            }

            hr = MFCreateSourceReaderFromMediaSource(source, null, out reader);
            if (hr != 0 || reader == null)
            {
                AppLog.Warn($"摄像头测试：符号链接 SourceReader 失败 hr=0x{hr:X8}");
                reader = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("摄像头测试：符号链接打开异常 " + ex.Message);
            reader = null;
            return false;
        }
        finally
        {
            SafeRelease(source);
            SafeRelease(attrs);
        }
    }

    private static bool TryActivateReader(IMFActivate act, out IMFSourceReader? reader)
    {
        reader = null;
        try
        {
            var iid = typeof(IMFMediaSource).GUID;
            var hr = act.ActivateObject(ref iid, out var srcObj);
            if (hr != 0 || srcObj == null)
            {
                AppLog.Warn($"摄像头测试：ActivateObject 失败 hr=0x{hr:X8}");
                return false;
            }

            var source = (IMFMediaSource)srcObj;
            try
            {
                hr = MFCreateSourceReaderFromMediaSource(source, null, out reader);
                if (hr != 0 || reader == null)
                {
                    AppLog.Warn($"摄像头测试：创建 SourceReader 失败 hr=0x{hr:X8}");
                    reader = null;
                    return false;
                }

                return true;
            }
            finally
            {
                SafeRelease(source);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("摄像头测试：打开异常 " + ex.Message);
            reader = null;
            return false;
        }
    }

    private static bool ActivateMatches(IMFActivate act, string prefer)
    {
        try
        {
            var key = MfDevsourceAttributeSourceTypeVidcapSymbolicLink;
            if (act.GetAllocatedString(ref key, out var link, out _) == 0 && !string.IsNullOrEmpty(link))
            {
                if (link.Contains(prefer, StringComparison.OrdinalIgnoreCase)
                    || prefer.Contains(link, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            key = MfDevsourceAttributeFriendlyName;
            if (act.GetAllocatedString(ref key, out var name, out _) == 0 && !string.IsNullOrEmpty(name))
            {
                if (name.Contains(prefer, StringComparison.OrdinalIgnoreCase)
                    || prefer.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* ignore */ }

        return false;
    }

    private static void TrySetRgb32(IMFSourceReader reader)
    {
        try
        {
            if (MFCreateMediaType(out var mt) != 0 || mt == null) return;
            try
            {
                var major = MfMtMajorType;
                var video = MfMediaTypeVideo;
                var subtype = MfMtSubtype;
                var rgb32 = MfVideoFormatRgb32;
                mt.SetGUID(ref major, ref video);
                mt.SetGUID(ref subtype, ref rgb32);
                reader.SetCurrentMediaType(MfSourceReaderFirstVideoStream, IntPtr.Zero, mt);
            }
            finally
            {
                SafeRelease(mt);
            }
        }
        catch { /* keep default */ }
    }

    private static bool TryGetFrameSize(IMFSourceReader reader, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            if (reader.GetCurrentMediaType(MfSourceReaderFirstVideoStream, out var mt) != 0 || mt == null)
                return false;
            try
            {
                var key = MfMtFrameSize;
                if (mt.GetUINT64(ref key, out var packed) != 0)
                    return false;
                width = (int)(packed >> 32);
                height = (int)(packed & 0xffffffff);
                return width > 0 && height > 0;
            }
            finally
            {
                SafeRelease(mt);
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySampleToJpeg(IMFSample sample, int width, int height, out byte[] jpeg)
    {
        jpeg = [];
        if (sample.ConvertToContiguousBuffer(out var buffer) != 0 || buffer == null)
            return false;
        try
        {
            if (buffer.Lock(out var data, out _, out var len) != 0 || data == IntPtr.Zero)
                return false;
            try
            {
                var need = width * height * 4;
                if (len < need || width <= 0 || height <= 0)
                    return false;

                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppRgb);
                try
                {
                    var rowBytes = width * 4;
                    var row = new byte[rowBytes];
                    for (var y = 0; y < height; y++)
                    {
                        var src = IntPtr.Add(data, (height - 1 - y) * rowBytes);
                        var dst = IntPtr.Add(bd.Scan0, y * bd.Stride);
                        Marshal.Copy(src, row, 0, rowBytes);
                        Marshal.Copy(row, 0, dst, rowBytes);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bd);
                }

                using var ms = new MemoryStream();
                var enc = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (enc != null)
                {
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(Encoder.Quality, 55L);
                    bmp.Save(ms, enc, ep);
                }
                else
                {
                    bmp.Save(ms, ImageFormat.Jpeg);
                }

                jpeg = ms.ToArray();
                return jpeg.Length > 0;
            }
            finally
            {
                buffer.Unlock();
            }
        }
        finally
        {
            SafeRelease(buffer);
        }
    }

    #endregion

    #region Microphone

    private void StartMic()
    {
        StopMic();
        _micRun = true;
        _micThread = new Thread(MicLoop) { IsBackground = true, Name = "FlyScreen.SdMic" };
        _micThread.Start();
    }

    private void StopMic()
    {
        _micRun = false;
        try { _micThread?.Join(1500); } catch { /* ignore */ }
        _micThread = null;
    }

    private void MicLoop()
    {
        CoInitializeEx(IntPtr.Zero, 0x2);
        object? clientObj = null;
        object? captureObj = null;
        try
        {
            if (!TryOpenWasapiCapture(out clientObj, out var rate, out var channels, out var isFloat)
                || clientObj == null)
            {
                _svc.PublishStatus("无法打开本机麦克风");
                return;
            }

            var audio = (IAudioClient)clientObj;
            var capId = typeof(IAudioCaptureClient).GUID;
            if (audio.GetService(ref capId, out var capPtr) != 0 || capPtr == IntPtr.Zero)
            {
                _svc.PublishStatus("无法取得麦克风采集接口");
                return;
            }

            captureObj = Marshal.GetObjectForIUnknown(capPtr);
            Marshal.Release(capPtr);
            var capture = (IAudioCaptureClient)captureObj;
            if (audio.Start() != 0)
            {
                _svc.PublishStatus("无法开始麦克风采集");
                return;
            }

            _svc.PublishStatus("本机麦克风测试推流中");
            while (_micRun)
            {
                if (capture.GetNextPacketSize(out var frames) != 0)
                    break;
                if (frames == 0)
                {
                    Thread.Sleep(5);
                    continue;
                }

                var hr = capture.GetBuffer(out var data, out var got, out var flags, out _, out _);
                if (hr != 0)
                {
                    if (hr == unchecked((int)0x08890001)) continue; // AUDCLNT_S_BUFFER_EMPTY
                    break;
                }

                try
                {
                    // flags bit1 = AUDCLNT_BUFFERFLAGS_SILENT
                    if (got > 0 && (flags & 2) == 0 && data != IntPtr.Zero)
                    {
                        var pcm = isFloat
                            ? FloatToPcm16(data, (int)got, channels)
                            : CopyPcm16(data, (int)got, channels);
                        if (pcm.Length > 0)
                            _svc.PostMicAudio(pcm, rate, channels);
                    }
                }
                finally
                {
                    capture.ReleaseBuffer(got);
                }
            }

            try { audio.Stop(); } catch { /* ignore */ }
        }
        catch (Exception ex)
        {
            AppLog.Warn("麦克风采集异常: " + ex.Message);
            _svc.PublishStatus("麦克风采集失败：" + ex.Message);
        }
        finally
        {
            SafeRelease(captureObj);
            SafeRelease(clientObj);
        }
    }

    private static byte[] CopyPcm16(IntPtr data, int frames, int channels)
    {
        var bytes = frames * channels * 2;
        if (bytes <= 0) return [];
        var pcm = new byte[bytes];
        Marshal.Copy(data, pcm, 0, bytes);
        return pcm;
    }

    private static byte[] FloatToPcm16(IntPtr data, int frames, int channels)
    {
        var samples = frames * channels;
        if (samples <= 0) return [];
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var f = Marshal.PtrToStructure<float>(data + i * 4);
            var s = (short)Math.Clamp((int)(f * 32767f), short.MinValue, short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xff);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }

        return pcm;
    }

    /// <summary>
    /// 打开麦克风：优先 PCM16 + AUTOCONVERT；失败再退回 mix（float 时在读缓冲时转换）。
    /// 注意 <see cref="IAudioClient"/> 只能有一个 Initialize，多声明会错位虚表导致 AccessViolation。
    /// </summary>
    private bool TryOpenWasapiCapture(out object? client, out int sampleRate, out int channels, out bool isFloat)
    {
        client = null;
        sampleRate = 16000;
        channels = 1;
        isFloat = false;
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            IMMDevice? device = null;
            var prefer = _svc.GetSelectedDeviceId(SharedDeviceKinds.Microphone);
            if (!string.IsNullOrEmpty(prefer))
                en.GetDevice(prefer, out device);
            if (device == null && en.GetDefaultAudioEndpoint(1 /* eCapture */, 0 /* eConsole */, out device) != 0)
                return false;
            if (device == null) return false;

            var iid = typeof(IAudioClient).GUID;
            // CLSCTX_ALL = 23；Initialize 失败后同一 IAudioClient 不能再 Initialize，须重新 Activate
            if (device.Activate(ref iid, 23, IntPtr.Zero, out var obj) != 0 || obj == null)
                return false;
            var audio = (IAudioClient)obj;

            if (audio.GetMixFormat(out var mixPtr) != 0 || mixPtr == IntPtr.Zero)
            {
                SafeRelease(audio);
                return false;
            }

            try
            {
                var mixRate = Marshal.ReadInt32(mixPtr, 4);
                var mixCh = Marshal.ReadInt16(mixPtr, 2);
                sampleRate = mixRate is >= 8000 and <= 48000 ? mixRate : 48000;
                channels = Math.Clamp((int)mixCh, 1, 2);

                // AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | SRC_DEFAULT_QUALITY
                const uint autoConvert = 0x80000000u | 0x08000000u;
                var pcmMem = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatEx>());
                try
                {
                    var pcmFmt = new WaveFormatEx
                    {
                        wFormatTag = 1,
                        nChannels = (ushort)channels,
                        nSamplesPerSec = (uint)sampleRate,
                        wBitsPerSample = 16,
                        nBlockAlign = (ushort)(channels * 2),
                        nAvgBytesPerSec = (uint)(sampleRate * channels * 2),
                        cbSize = 0
                    };
                    Marshal.StructureToPtr(pcmFmt, pcmMem, false);
                    var hr = audio.Initialize(0, autoConvert, 10_000_000, 0, pcmMem, IntPtr.Zero);
                    if (hr == 0)
                    {
                        isFloat = false;
                        client = audio;
                        audio = null!; // 所有权交给 client
                        return true;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pcmMem);
                }

                SafeRelease(audio);
                audio = null!;
                if (device.Activate(ref iid, 23, IntPtr.Zero, out obj) != 0 || obj == null)
                    return false;
                audio = (IAudioClient)obj;

                // 退回设备 mix（可能为 float / extensible）
                var hrMix = audio.Initialize(0, 0, 10_000_000, 0, mixPtr, IntPtr.Zero);
                if (hrMix != 0)
                {
                    SafeRelease(audio);
                    return false;
                }

                sampleRate = mixRate > 0 ? mixRate : sampleRate;
                channels = Math.Clamp((int)mixCh, 1, 8);
                var tag = (ushort)Marshal.ReadInt16(mixPtr, 0);
                var bits = (ushort)Marshal.ReadInt16(mixPtr, 14);
                isFloat = tag == 3;
                if (tag == 0xFFFE && Marshal.ReadInt16(mixPtr, 16) >= 22)
                {
                    var sub = Marshal.ReadInt32(mixPtr, 24);
                    isFloat = sub == 3;
                }

                if (!isFloat && bits != 16)
                {
                    SafeRelease(audio);
                    _svc.PublishStatus("麦克风格式不受支持，请改用默认麦克风");
                    return false;
                }

                client = audio;
                audio = null!;
                return true;
            }
            finally
            {
                Marshal.FreeCoTaskMem(mixPtr);
                SafeRelease(audio);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("打开麦克风失败: " + ex.Message);
            return false;
        }
    }

    #endregion

    #region Speaker tone

    private void PlayTone()
    {
        const int rate = 44100;
        const double sec = 0.7;
        const double freq = 880;
        var samples = (int)(rate * sec);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var t = i / (double)rate;
            var env = Math.Min(1.0, Math.Min(t / 0.02, (sec - t) / 0.05));
            var s = (short)(Math.Sin(2 * Math.PI * freq * t) * 0.4 * env * short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xff);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }

        lock (_gate)
        {
            StopTone();
            var fmt = new WaveFormatEx
            {
                wFormatTag = 1,
                nChannels = 1,
                nSamplesPerSec = rate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = rate * 2,
                cbSize = 0
            };
            if (waveOutOpen(out _toneWave, 0xFFFFFFFF, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0
                || _toneWave == IntPtr.Zero)
            {
                _svc.PublishStatus("无法打开本机扬声器");
                return;
            }

            var data = Marshal.AllocHGlobal(pcm.Length);
            var hdrPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());
            try
            {
                Marshal.Copy(pcm, 0, data, pcm.Length);
                Marshal.StructureToPtr(new WaveHdr
                {
                    lpData = data,
                    dwBufferLength = (uint)pcm.Length
                }, hdrPtr, false);
                if (waveOutPrepareHeader(_toneWave, hdrPtr, Marshal.SizeOf<WaveHdr>()) != 0)
                    return;
                waveOutWrite(_toneWave, hdrPtr, Marshal.SizeOf<WaveHdr>());
                Thread.Sleep((int)(sec * 1000) + 80);
                waveOutReset(_toneWave);
                waveOutUnprepareHeader(_toneWave, hdrPtr, Marshal.SizeOf<WaveHdr>());
            }
            finally
            {
                Marshal.FreeHGlobal(hdrPtr);
                Marshal.FreeHGlobal(data);
            }

            StopTone();
        }

        _svc.PublishStatus("本机扬声器测试音已播放");
    }

    private void StopTone()
    {
        if (_toneWave == IntPtr.Zero) return;
        try { waveOutReset(_toneWave); } catch { /* ignore */ }
        try { waveOutClose(_toneWave); } catch { /* ignore */ }
        _toneWave = IntPtr.Zero;
    }

    #endregion

    #region Native

    private static void SafeRelease(object? com)
    {
        if (com == null) return;
        try { Marshal.ReleaseComObject(com); } catch { /* ignore */ }
    }

    private const int MfVersion = 0x20070;
    private const int MfSourceReaderFirstVideoStream = unchecked((int)0xFFFFFFFC);
    private const int MfSourceReaderfEndOfStream = 0x2;

    private static readonly Guid MfDevsourceAttributeSourceType = new("c60ac5fe-672d-449f-a178-2c7a25f421b3");
    private static readonly Guid MfDevsourceAttributeSourceTypeVidcapGuid = new("8ac3587a-4b87-4d1a-b4b5-084ad037d1d3");
    private static readonly Guid MfDevsourceAttributeFriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    private static readonly Guid MfDevsourceAttributeSourceTypeVidcapSymbolicLink =
        new("58f0aad8-22ae-4ad4-86b3-d3b99afc6d67");
    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfVideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);

    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    private static extern int MFCreateAttributes(out IMFAttributes? attrs, uint cInitialSize);

    [DllImport("mf.dll")]
    private static extern int MFEnumDeviceSources(
        IMFAttributes attrs, out IntPtr devices, out uint count);

    [DllImport("mf.dll")]
    private static extern int MFCreateDeviceSource(IMFAttributes attrs, out IMFMediaSource? source);

    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMFMediaType? mediaType);

    [DllImport("mfreadwrite.dll")]
    private static extern int MFCreateSourceReaderFromMediaSource(
        IMFMediaSource source, IMFAttributes? attrs, out IMFSourceReader? reader);

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(
        out IntPtr phwo, uint deviceId, ref WaveFormatEx format,
        IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hwo);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hwo);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
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
    private struct WaveHdr
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

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    // 虚表须与 WASAPI IAudioClient 一一对应；不得重载 Initialize，否则后续槽位错位引发 AccessViolation
    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
            long periodicity, IntPtr format, IntPtr session);
        [PreserveSig] int GetBufferSize(out uint bufferSize);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out int flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int GetItemType(ref Guid guidKey, out int type);
        [PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out int result);
        [PreserveSig] int Compare(IMFAttributes other, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid guidKey, out int value);
        [PreserveSig] int GetUINT64(ref Guid guidKey, out ulong value);
        [PreserveSig] int GetDouble(ref Guid guidKey, out double value);
        [PreserveSig] int GetGUID(ref Guid guidKey, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid guidKey, out int length);
        [PreserveSig] int GetString(ref Guid guidKey, IntPtr buffer, int bufferSize, out int length);
        [PreserveSig] int GetAllocatedString(ref Guid guidKey,
            [MarshalAs(UnmanagedType.LPWStr)] out string value, out int length);
        [PreserveSig] int GetBlobSize(ref Guid guidKey, out int size);
        [PreserveSig] int GetBlob(ref Guid guidKey, IntPtr buf, int bufSize, out int size);
        [PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr buf, out int size);
        [PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object? value);
        [PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
        [PreserveSig] int DeleteItem(ref Guid guidKey);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid guidKey, int value);
        [PreserveSig] int SetUINT64(ref Guid guidKey, ulong value);
        [PreserveSig] int SetDouble(ref Guid guidKey, double value);
        [PreserveSig] int SetGUID(ref Guid guidKey, ref Guid value);
        [PreserveSig] int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid guidKey, IntPtr buf, int size);
        [PreserveSig] int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetItemByIndex(int index, out Guid guidKey, IntPtr value);
        [PreserveSig] int CopyAllItems(IMFAttributes dest);
    }

    [ComImport, Guid("7FEE9E9A-4A89-47A6-899C-B6A53A70FD52"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFActivate : IMFAttributes
    {
        [PreserveSig] int ActivateObject(ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object? obj);
        [PreserveSig] int ShutdownObject();
        [PreserveSig] int DetachObject();
    }

    [ComImport, Guid("279A808D-AEC8-4CDA-AFA9-D477A4FFC0A2"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaSource
    {
        void _VtblGap0_11();
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4998C3C5"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType : IMFAttributes
    {
    }

    [ComImport, Guid("70AE8772-1DC7-4037-B1CE-BBF45B9A8E04"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(int streamIndex, out int selected);
        [PreserveSig] int SetStreamSelection(int streamIndex, int selected);
        [PreserveSig] int GetNativeMediaType(int streamIndex, int typeIndex, out IMFMediaType? type);
        [PreserveSig] int GetCurrentMediaType(int streamIndex, out IMFMediaType? type);
        [PreserveSig] int SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition(ref Guid format, IntPtr position);
        [PreserveSig] int ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex,
            out int streamFlags, out long timestamp, out IMFSample? sample);
        // remaining methods unused
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        void _VtblGap0_8();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer? buffer);
    }

    [ComImport, Guid("045FA593-8799-42F0-99C0-BAAFBF95D7E5"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }

    #endregion
}
