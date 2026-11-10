using Avalonia.Controls;
using Avalonia.Threading;
using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.SharedDevices;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.SharedDevices.Ui.Desktop;

public sealed class SharedDevicesDesktopUiContributor : IToolUiContributor
{
    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        var owner = context.Owner as Window;
        var setStatus = context.ReportStatus;
        var tools = ToolService.Instance;

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.UiStart,
            (arg, _) =>
            {
                var sd = arg as SharedDevicesService ?? tools.GetToolService<SharedDevicesService>();
                if (sd == null)
                {
                    Dispatcher.UIThread.Post(() => setStatus?.Invoke("共享设备服务未就绪"));
                    return Task.FromResult<object?>(null);
                }

                var targets = sd.ListApplicantTargets()
                    .Where(t => t.CanProvide)
                    .Select(t => t.PeerName)
                    .ToList();
                if (targets.Count == 0)
                {
                    Dispatcher.UIThread.Post(() => setStatus?.Invoke(Loc.T("Tool.SharedDevices.NoTargets")));
                    return Task.FromResult<object?>(null);
                }

                return Task.FromResult<object?>(new SharedDevicesStartUiResult
                {
                    Peer = targets[0],
                    Kinds = SharedDeviceKinds.All
                });
            });

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.UiIncoming,
            async (arg, _) =>
            {
                if (arg is not SharedDevicesIncomingUiArgs incoming || owner == null)
                    return null;
                return await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var win = new SharedDevicesIncomingWindow(incoming.Service, incoming.Request);
                    var ok = await win.ShowDialog<bool?>(owner) == true && win.Accepted;
                    return (object?)new SharedDevicesIncomingUiResult
                    {
                        Accepted = ok,
                        Remember = win.Remember,
                        DeviceIds = win.SelectedDevices
                    };
                });
            });

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.MenuTest,
            async (_, _) =>
            {
                if (owner == null) return null;
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var win = new SharedDevicesTestWindow();
                    await win.ShowDialog(owner);
                });
                return null;
            });

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.MenuManage,
            async (arg, _) =>
            {
                var sd = arg as SharedDevicesService ?? tools.GetToolService<SharedDevicesService>();
                if (sd == null || owner == null) return null;
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var win = new SharedDevicesManageWindow(sd);
                    await win.ShowDialog(owner);
                });
                return null;
            });
    }
}
