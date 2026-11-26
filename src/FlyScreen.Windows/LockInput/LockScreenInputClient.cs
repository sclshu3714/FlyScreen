using System.IO.Pipes;
using System.Text;

namespace FlyScreen.LockInput;

/// <summary>用户态 → 锁屏助手命名管道客户端。</summary>
public static class LockScreenInputClient
{
    private static readonly object Gate = new();
    private static NamedPipeClientStream? _pipe;
    private static StreamWriter? _writer;

    public static bool TrySend(LockScreenInputProtocol.Command cmd)
    {
        try
        {
            EnsureConnected();
            var line = LockScreenInputProtocol.Encode(cmd);
            lock (Gate)
            {
                _writer!.WriteLine(line);
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

    private static void EnsureConnected()
    {
        lock (Gate)
        {
            if (_pipe is { IsConnected: true } && _writer != null)
                return;

            ResetUnlocked();
            _pipe = new NamedPipeClientStream(
                ".",
                LockScreenInputProtocol.PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            _pipe.Connect(400);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    public static void Reset()
    {
        lock (Gate) ResetUnlocked();
    }

    private static void ResetUnlocked()
    {
        try { _writer?.Dispose(); } catch { /* ignore */ }
        try { _pipe?.Dispose(); } catch { /* ignore */ }
        _writer = null;
        _pipe = null;
    }
}
