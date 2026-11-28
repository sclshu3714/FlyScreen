using System.IO.Pipes;
using System.Text;
using FlyScreen.ScreenExtend;

namespace FlyScreen.LockInput;

/// <summary>用户态 → 锁屏助手命名管道客户端（InOut：注入单向，desktop/capture 带应答）。</summary>
public static class LockScreenInputClient
{
    private static readonly object Gate = new();
    private static NamedPipeClientStream? _pipe;
    private static StreamWriter? _writer;
    private static StreamReader? _reader;

    public static bool TrySend(LockScreenInputProtocol.Command cmd)
    {
        try
        {
            lock (Gate)
            {
                EnsureConnectedUnlocked();
                _writer!.WriteLine(LockScreenInputProtocol.Encode(cmd));
                _writer.Flush();
            }

            return true;
        }
        catch
        {
            Reset();
            return false;
        }
    }

    public static bool TryMoveAbsoluteNormalized(double nx, double ny)
        => TrySend(new LockScreenInputProtocol.Command
        {
            Op = "moveAbs",
            Nx = Math.Clamp(nx, 0, 1),
            Ny = Math.Clamp(ny, 0, 1)
        });

    public static bool TryMoveRelative(int dx, int dy)
        => TrySend(new LockScreenInputProtocol.Command { Op = "moveRel", Dx = dx, Dy = dy });

    public static bool TryMouseButton(int button, bool down)
        => TrySend(new LockScreenInputProtocol.Command { Op = "button", Button = button, Down = down });

    public static bool TryMouseWheel(int delta, bool horizontal)
        => TrySend(new LockScreenInputProtocol.Command
        {
            Op = "wheel",
            Delta = delta,
            Horizontal = horizontal
        });

    public static bool TryKey(ushort vk, bool down, bool extended)
        => TrySend(new LockScreenInputProtocol.Command
        {
            Op = "key",
            Vk = vk,
            Down = down,
            Extended = extended
        });

    public static bool TryInsertText(string text)
        => TrySend(new LockScreenInputProtocol.Command { Op = "text", Text = text });

    public static bool TryPing()
        => TrySend(new LockScreenInputProtocol.Command { Op = "ping" });

    /// <summary>查询助手所见输入桌面名；失败返回 false。</summary>
    public static bool TryQueryDesktop(out string? name, out bool secure)
    {
        name = null;
        secure = false;
        var resp = TryRequest(new LockScreenInputProtocol.Command { Op = "desktop" });
        if (resp is not { Ok: true })
            return false;
        name = resp.Desktop;
        secure = resp.Secure == true
                 || SecureDesktopInjector.IsSecureDesktopName(name);
        return true;
    }

    /// <summary>由 SYSTEM 会话助手在输入桌面抓 JPEG。</summary>
    public static bool TryCaptureJpeg(
        int left, int top, int width, int height,
        long jpegQuality, int maxEdge, bool drawCursor,
        out ScreenCaptureJpeg.Frame frame)
    {
        frame = default;
        var resp = TryRequest(new LockScreenInputProtocol.Command
        {
            Op = "capture",
            Left = left,
            Top = top,
            Width = width,
            Height = height,
            Quality = jpegQuality,
            MaxEdge = maxEdge,
            DrawCursor = drawCursor
        });
        if (resp is not { Ok: true } || string.IsNullOrEmpty(resp.JpegBase64))
            return false;
        try
        {
            var jpeg = Convert.FromBase64String(resp.JpegBase64);
            if (jpeg.Length == 0) return false;
            frame = new ScreenCaptureJpeg.Frame(
                jpeg,
                resp.Width ?? width,
                resp.Height ?? height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static LockScreenInputProtocol.Response? TryRequest(LockScreenInputProtocol.Command cmd)
    {
        try
        {
            lock (Gate)
            {
                EnsureConnectedUnlocked();
                _writer!.WriteLine(LockScreenInputProtocol.Encode(cmd));
                _writer.Flush();

                // 抓屏应答可能较大；ReadLine 阻塞直到助手写完一行
                var line = _reader!.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                    return null;
                return LockScreenInputProtocol.DecodeResponse(line);
            }
        }
        catch
        {
            Reset();
            return null;
        }
    }

    private static void EnsureConnectedUnlocked()
    {
        if (_pipe is { IsConnected: true } && _writer != null && _reader != null)
            return;

        ResetUnlocked();
        _pipe = new NamedPipeClientStream(
            ".",
            LockScreenInputProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        _pipe.Connect(800);
        var encoding = new UTF8Encoding(false);
        _writer = new StreamWriter(_pipe, encoding, bufferSize: 64 * 1024, leaveOpen: true)
        {
            AutoFlush = true
        };
        _reader = new StreamReader(_pipe, encoding, detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024 * 1024, leaveOpen: true);
    }

    public static void Reset()
    {
        lock (Gate) ResetUnlocked();
    }

    private static void ResetUnlocked()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _reader?.Dispose(); } catch { /* ignore */ }
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _reader = null;
        _pipe = null;
    }
}
