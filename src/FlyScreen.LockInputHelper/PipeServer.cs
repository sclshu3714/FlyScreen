using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FlyScreen.LockInput;

namespace FlyScreen.LockInputHelper;

internal static class PipeServer
{
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
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    0,
                    0,
                    CreatePipeSecurity());

                server.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
                using var reader = new StreamReader(server, new UTF8Encoding(false));
                while (!ct.IsCancellationRequested && server.IsConnected)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;
                    HandleLine(line);
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

    private static void HandleLine(string line)
    {
        var cmd = LockScreenInputProtocol.Decode(line);
        if (cmd == null || string.IsNullOrWhiteSpace(cmd.Op)) return;
        switch (cmd.Op.Trim().ToLowerInvariant())
        {
            case "ping":
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
}
