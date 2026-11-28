using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FlyScreen.LockInput;

namespace FlyScreen.LockInputHelper;

internal static class PipeServer
{
    private const int PipeBuffer = 1024 * 1024;

    public static void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // LocalSystem 创建的管道默认 ACL 可能拒交互用户；显式放行 Authenticated Users
                server = NamedPipeServerStreamAcl.Create(
                    LockScreenInputProtocol.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    PipeBuffer,
                    PipeBuffer,
                    CreatePipeSecurity());

                server.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
                var encoding = new UTF8Encoding(false);
                using var reader = new StreamReader(server, encoding, detectEncodingFromByteOrderMarks: false,
                    bufferSize: PipeBuffer, leaveOpen: true);
                using var writer = new StreamWriter(server, encoding, bufferSize: PipeBuffer, leaveOpen: true)
                {
                    AutoFlush = true
                };
                while (!ct.IsCancellationRequested && server.IsConnected)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;
                    HandleLine(line, writer);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                Thread.Sleep(200);
            }
            finally
            {
                try { server?.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    private static void HandleLine(string line, StreamWriter writer)
    {
        var cmd = LockScreenInputProtocol.Decode(line);
        if (cmd == null || string.IsNullOrWhiteSpace(cmd.Op)) return;
        switch (cmd.Op.Trim().ToLowerInvariant())
        {
            case "ping":
                break;
            case "desktop":
            case "querydesktop":
                ReplyDesktop(writer);
                break;
            case "capture":
                ReplyCapture(cmd, writer);
                break;
            case "moveabs":
            case "move":
                SecureDesktopInjector.MoveAbsoluteNormalized(cmd.Nx ?? 0.5, cmd.Ny ?? 0.5);
                break;
            case "moverel":
            case "rel":
                SecureDesktopInjector.MoveRelative(cmd.Dx ?? 0, cmd.Dy ?? 0);
                break;
            case "button":
            case "btn":
                SecureDesktopInjector.MouseButton(cmd.Button ?? 0, cmd.Down ?? false);
                break;
            case "wheel":
                SecureDesktopInjector.MouseWheel(cmd.Delta ?? 0, cmd.Horizontal ?? false);
                break;
            case "key":
                SecureDesktopInjector.Key(cmd.Vk ?? 0, cmd.Down ?? false, cmd.Extended ?? false);
                break;
            case "text":
                if (!string.IsNullOrEmpty(cmd.Text))
                    SecureDesktopInjector.InsertText(cmd.Text);
                break;
        }
    }

    private static void ReplyDesktop(StreamWriter writer)
    {
        try
        {
            var name = SecureDesktopCapture.GetInputDesktopName();
            var secure = SecureDesktopInjector.IsSecureDesktopName(name);
            WriteResponse(writer, new LockScreenInputProtocol.Response
            {
                Op = "desktop",
                Ok = !string.IsNullOrEmpty(name),
                Desktop = name,
                Secure = secure
            });
        }
        catch (Exception ex)
        {
            WriteResponse(writer, new LockScreenInputProtocol.Response
            {
                Op = "desktop",
                Ok = false,
                Error = ex.Message
            });
        }
    }

    private static void ReplyCapture(LockScreenInputProtocol.Command cmd, StreamWriter writer)
    {
        try
        {
            var frame = SecureDesktopCapture.CaptureJpeg(
                cmd.Left ?? 0,
                cmd.Top ?? 0,
                cmd.Width ?? 0,
                cmd.Height ?? 0,
                cmd.Quality ?? 85L,
                cmd.MaxEdge ?? 0,
                cmd.DrawCursor ?? true);
            if (frame == null || frame.Value.Jpeg.Length == 0)
            {
                WriteResponse(writer, new LockScreenInputProtocol.Response
                {
                    Op = "capture",
                    Ok = false,
                    Error = "capture failed"
                });
                return;
            }

            WriteResponse(writer, new LockScreenInputProtocol.Response
            {
                Op = "capture",
                Ok = true,
                Width = frame.Value.Width,
                Height = frame.Value.Height,
                JpegBase64 = Convert.ToBase64String(frame.Value.Jpeg)
            });
        }
        catch (Exception ex)
        {
            WriteResponse(writer, new LockScreenInputProtocol.Response
            {
                Op = "capture",
                Ok = false,
                Error = ex.Message
            });
        }
    }

    private static void WriteResponse(StreamWriter writer, LockScreenInputProtocol.Response resp)
    {
        writer.WriteLine(LockScreenInputProtocol.Encode(resp));
        writer.Flush();
    }
}
