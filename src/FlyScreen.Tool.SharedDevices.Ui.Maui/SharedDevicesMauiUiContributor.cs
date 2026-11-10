using FlyScreen.Services.Tools;
using FlyScreen.Tool.SharedDevices;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.SharedDevices.Ui.Maui;

public sealed class SharedDevicesMauiUiContributor : IToolUiContributor
{
    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        var tools = ToolService.Instance;
        INavigation Nav()
            => context.GetNavigation?.Invoke() as INavigation
               ?? throw new InvalidOperationException("Maui Navigation 未注入");

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.UiStart,
            async (arg, _) =>
            {
                var sd = arg as SharedDevicesService ?? tools.GetToolService<SharedDevicesService>();
                if (sd == null) return null;
                return await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var page = new SharedDevicesStartPage(sd.ListApplicantTargets());
                    await Nav().PushModalAsync(page, animated: true);
                    var (peer, kinds) = await page.Result;
                    if (string.IsNullOrEmpty(peer) || kinds.Length == 0)
                        return null;
                    return (object?)new SharedDevicesStartUiResult
                    {
                        Peer = peer!,
                        Kinds = kinds
                    };
                });
            });

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.UiIncoming,
            async (arg, _) =>
            {
                if (arg is not SharedDevicesIncomingUiArgs incoming)
                    return null;
                return await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var page = new SharedDevicesIncomingPage(incoming.Service, incoming.Request);
                    await Nav().PushModalAsync(page, animated: true);
                    var (accept, remember, devices) = await page.Result;
                    return (object?)new SharedDevicesIncomingUiResult
                    {
                        Accepted = accept,
                        Remember = remember,
                        DeviceIds = devices
                    };
                });
            });

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.MenuTest,
            async (_, _) =>
            {
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var page = new SharedDevicesTestPage();
                    await Nav().PushModalAsync(page, animated: true);
                    await page.Closed;
                });
                return null;
            });

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.MenuManage,
            async (arg, _) =>
            {
                var sd = arg as SharedDevicesService ?? tools.GetToolService<SharedDevicesService>();
                if (sd == null) return null;
                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    var page = new SharedDevicesManagePage(sd);
                    await Nav().PushModalAsync(page, animated: true);
                    await page.Closed;
                });
                return null;
            });
    }
}
