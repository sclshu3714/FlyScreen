using System.Runtime.InteropServices;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Mac.Audio;

/// <summary>macOS：播放对端传来的 PCM。本端发起投影时尝试静音输出；没有系统回环则不采集。</summary>
public sealed class MacProjectionAudio : IProjectionAudioEngine
{
    private readonly object _gate = new();
    private IntPtr _queue;
    private int _rate;
    private int _channels;
    private readonly AudioQueueOutputCallback _done = OnBufferDone;

    public bool TryStartCapture(Action<byte[], int, int> onPcm, out string? error)
    {
        _ = onPcm;
        error = OperatingSystem.IsMacOS()
            ? "macOS 暂不能采集系统声音，请从 Windows 或手机发起带声音的投影"
            : "非 macOS";
        return false;
    }

    public void StopCapture() { }

    public bool TryStartPlayback(out string? error)
    {
        error = OperatingSystem.IsMacOS() ? null : "非 macOS";
        return OperatingSystem.IsMacOS();
    }

    public void SubmitPlayback(byte[] pcm, int sampleRate, int channels)
    {
        if (!OperatingSystem.IsMacOS() || pcm.Length == 0 || sampleRate <= 0 || channels is < 1 or > 2)
            return;
        lock (_gate)
        {
            if (_queue == IntPtr.Zero || _rate != sampleRate || _channels != channels)
            {
                CloseQueue();
                if (!OpenQueue(sampleRate, channels)) return;
            }

            if (AudioQueueAllocateBuffer(_queue, (uint)pcm.Length, out var buffer) != 0 || buffer == IntPtr.Zero)
                return;
            var hdr = Marshal.PtrToStructure<AudioQueueBuffer>(buffer);
            if (hdr.mAudioData == IntPtr.Zero)
            {
                AudioQueueFreeBuffer(_queue, buffer);
                return;
            }

            Marshal.Copy(pcm, 0, hdr.mAudioData, pcm.Length);
            hdr.mAudioDataByteSize = (uint)pcm.Length;
            Marshal.StructureToPtr(hdr, buffer, false);
            if (AudioQueueEnqueueBuffer(_queue, buffer, 0, IntPtr.Zero) != 0)
                AudioQueueFreeBuffer(_queue, buffer);
        }
    }

    public void StopPlayback()
    {
        lock (_gate) CloseQueue();
    }

    private bool OpenQueue(int sampleRate, int channels)
    {
        var fmt = new AudioStreamBasicDescription
        {
            SampleRate = sampleRate,
            FormatID = 0x6C70636D,
            FormatFlags = 12,
            BytesPerPacket = (uint)(channels * 2),
            FramesPerPacket = 1,
            BytesPerFrame = (uint)(channels * 2),
            ChannelsPerFrame = (uint)channels,
            BitsPerChannel = 16
        };
        if (AudioQueueNewOutput(ref fmt, _done, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out _queue) != 0
            || _queue == IntPtr.Zero)
        {
            _queue = IntPtr.Zero;
            return false;
        }

        _rate = sampleRate;
        _channels = channels;
        AudioQueueStart(_queue, IntPtr.Zero);
        return true;
    }

    private void CloseQueue()
    {
        if (_queue == IntPtr.Zero) return;
        AudioQueueStop(_queue, true);
        AudioQueueDispose(_queue, true);
        _queue = IntPtr.Zero;
        _rate = 0;
        _channels = 0;
    }

    private static void OnBufferDone(IntPtr user, IntPtr queue, IntPtr buffer)
    {
        try { AudioQueueFreeBuffer(queue, buffer); } catch { /* ignore */ }
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

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioQueueBuffer
    {
        public uint mAudioDataBytesCapacity;
        public IntPtr mAudioData;
        public uint mAudioDataByteSize;
        public IntPtr mUserData;
        public uint mPacketDescriptionCapacity;
        public IntPtr mPacketDescriptions;
        public uint mPacketDescriptionCount;
    }

    private delegate void AudioQueueOutputCallback(IntPtr userData, IntPtr queue, IntPtr buffer);

    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueNewOutput(
        ref AudioStreamBasicDescription format,
        AudioQueueOutputCallback callback,
        IntPtr userData,
        IntPtr runLoop,
        IntPtr runLoopMode,
        uint flags,
        out IntPtr queue);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueAllocateBuffer(IntPtr queue, uint size, out IntPtr buffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueEnqueueBuffer(IntPtr queue, IntPtr buffer, uint descCount, IntPtr descs);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueFreeBuffer(IntPtr queue, IntPtr buffer);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStart(IntPtr queue, IntPtr startTime);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueStop(IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);

    [DllImport(AudioToolbox)]
    private static extern int AudioQueueDispose(IntPtr queue, [MarshalAs(UnmanagedType.I1)] bool immediate);
}
