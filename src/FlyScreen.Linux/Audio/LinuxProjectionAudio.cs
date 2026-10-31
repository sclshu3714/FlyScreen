using System.Diagnostics;
using FlyScreen.Services.ScreenExtend;

namespace FlyScreen.Linux.Audio;

/// <summary>
/// Linux：空输出接走正在播放的声音并录制，本机扬声器不再出声；对端 PCM 用 paplay 播放。
/// </summary>
public sealed class LinuxProjectionAudio : IProjectionAudioEngine
{
    private readonly object _gate = new();
    private Process? _record;
    private Process? _play;
    private int _playRate;
    private int _playChannels;
    private string? _savedSink;
    private string? _moduleId;
    private Thread? _readThread;
    private Thread? _watchThread;
    private Process? _subscribe;
    private volatile bool _run;
    private volatile bool _holdSink;

    public bool TryStartCapture(Action<byte[], int, int> onPcm, out string? error)
    {
        StopCapture();
        if (!OperatingSystem.IsLinux())
        {
            error = "非 Linux";
            return false;
        }

        var load = Run("pactl", "load-module module-null-sink sink_name=flyscreen_proj sink_properties=device.description=FlyScreen");
        if (load.Code != 0 || string.IsNullOrWhiteSpace(load.Text))
        {
            error = "无法创建投影用的空音频输出（需要 PulseAudio / PipeWire）";
            return false;
        }

        _moduleId = load.Text.Trim().Split('\n', '\r')[0].Trim();
        _savedSink = Run("pactl", "get-default-sink").Text.Trim();
        Run("pactl", "set-default-sink flyscreen_proj");
        MoveInputsTo("flyscreen_proj");

        try
        {
            _record = Process.Start(new ProcessStartInfo
            {
                FileName = "parec",
                Arguments = "-d flyscreen_proj.monitor --format=s16le --rate=48000 --channels=2 --latency-msec=40",
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
        }
        catch
        {
            _record = null;
        }

        if (_record == null)
        {
            StopCapture();
            error = "无法录制投影声音（缺少 parec）";
            return false;
        }

        _run = true;
        _holdSink = true;
        _readThread = new Thread(() => ReadLoop(_record, onPcm)) { IsBackground = true, Name = "FlyScreen.ProjAudio" };
        _readThread.Start();
        _watchThread = new Thread(WatchSink) { IsBackground = true, Name = "FlyScreen.ProjAudioSink" };
        _watchThread.Start();
        error = null;
        return true;
    }

    public void StopCapture()
    {
        _run = false;
        _holdSink = false;
        try { _subscribe?.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { _record?.Kill(entireProcessTree: true); } catch { /* ignore */ }
        try { _readThread?.Join(800); } catch { /* ignore */ }
        try { _watchThread?.Join(2500); } catch { /* ignore */ }
        _record = null;
        _readThread = null;
        _watchThread = null;
        _subscribe = null;
        if (!string.IsNullOrWhiteSpace(_savedSink))
        {
            Run("pactl", "set-default-sink " + _savedSink);
            MoveInputsTo(_savedSink);
        }

        if (!string.IsNullOrWhiteSpace(_moduleId))
            Run("pactl", "unload-module " + _moduleId);
        _savedSink = null;
        _moduleId = null;
    }

    public bool TryStartPlayback(out string? error)
    {
        error = OperatingSystem.IsLinux() ? null : "非 Linux";
        return OperatingSystem.IsLinux();
    }

    public void SubmitPlayback(byte[] pcm, int sampleRate, int channels)
    {
        if (!OperatingSystem.IsLinux() || pcm.Length == 0 || sampleRate <= 0 || channels is < 1 or > 2)
            return;
        lock (_gate)
        {
            if (_play == null || _play.HasExited || _playRate != sampleRate || _playChannels != channels)
            {
                ClosePlay();
                try
                {
                    _play = Process.Start(new ProcessStartInfo
                    {
                        FileName = "paplay",
                        Arguments = $"--raw --format=s16le --rate={sampleRate} --channels={channels} --latency-msec=60",
                        RedirectStandardInput = true,
                        UseShellExecute = false
                    });
                }
                catch
                {
                    _play = null;
                    return;
                }

                _playRate = sampleRate;
                _playChannels = channels;
            }

            try
            {
                _play?.StandardInput.BaseStream.Write(pcm, 0, pcm.Length);
                _play?.StandardInput.BaseStream.Flush();
            }
            catch
            {
                ClosePlay();
            }
        }
    }

    public void StopPlayback()
    {
        lock (_gate) ClosePlay();
    }

    private void ClosePlay()
    {
        try { _play?.StandardInput.Close(); } catch { /* ignore */ }
        try
        {
            if (_play is { HasExited: false })
                _play.Kill(entireProcessTree: true);
        }
        catch { /* ignore */ }
        _play = null;
        _playRate = 0;
        _playChannels = 0;
    }

    private void ReadLoop(Process proc, Action<byte[], int, int> onPcm)
    {
        var buf = new byte[3840];
        try
        {
            var stream = proc.StandardOutput.BaseStream;
            while (_run)
            {
                var n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                var copy = new byte[n];
                Buffer.BlockCopy(buf, 0, copy, 0, n);
                try { onPcm(copy, 48000, 2); } catch { /* ignore */ }
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>音量面板或新播放流会把输出拨回真实扬声器，投影期间持续接回空输出。</summary>
    private void WatchSink()
    {
        try
        {
            _subscribe = Process.Start(new ProcessStartInfo
            {
                FileName = "pactl",
                Arguments = "subscribe",
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
        }
        catch
        {
            _subscribe = null;
        }

        ReapplySink();
        if (_subscribe == null)
        {
            while (_holdSink)
            {
                Thread.Sleep(300);
                ReapplySink();
            }

            return;
        }

        try
        {
            var stream = _subscribe.StandardOutput;
            while (_holdSink && !_subscribe.HasExited)
            {
                var line = stream.ReadLine();
                if (line == null) break;
                if (line.Contains("sink", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("server", StringComparison.OrdinalIgnoreCase))
                    ReapplySink();
            }
        }
        catch { /* ignore */ }
    }

    private void ReapplySink()
    {
        if (!_holdSink) return;
        var cur = Run("pactl", "get-default-sink").Text.Trim();
        if (!_holdSink) return;
        if (!string.Equals(cur, "flyscreen_proj", StringComparison.Ordinal))
            Run("pactl", "set-default-sink flyscreen_proj");
        if (_holdSink) MoveInputsTo("flyscreen_proj");
    }

    private static void MoveInputsTo(string sink)
    {
        var list = Run("pactl", "list short sink-inputs").Text;
        foreach (var line in list.Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out var id)) continue;
            Run("pactl", $"move-sink-input {id} {sink}");
        }
    }

    private static (int Code, string Text) Run(string file, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (p == null) return (-1, "");
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return (p.ExitCode, text);
        }
        catch
        {
            return (-1, "");
        }
    }
}
