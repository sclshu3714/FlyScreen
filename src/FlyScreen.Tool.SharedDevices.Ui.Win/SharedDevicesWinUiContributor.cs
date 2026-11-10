using System.Windows;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.SharedDevices;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.SharedDevices.Ui.Win;

public sealed class SharedDevicesWinUiContributor : IToolUiContributor
{
    public void Register(IToolUiHost ui, IToolUiContext context)
    {
        var owner = context.Owner as Window;
        var tools = ToolService.Instance;

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.UiStart,
            (arg, _) =>
            {
                var sd = arg as SharedDevicesService ?? tools.GetToolService<SharedDevicesService>();
                if (sd == null) return Task.FromResult<object?>(null);
                var dlg = new SharedDevicesStartDialog(sd.ListApplicantTargets()) { Owner = owner };
                if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.SelectedPeer))
                    return Task.FromResult<object?>(null);
                return Task.FromResult<object?>(new SharedDevicesStartUiResult
                {
                    Peer = dlg.SelectedPeer!,
                    Kinds = dlg.SelectedKinds
                });
            });

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.UiIncoming,
            (arg, _) =>
            {
                if (arg is not SharedDevicesIncomingUiArgs incoming)
                    return Task.FromResult<object?>(null);
                if (owner == null) return Task.FromResult<object?>(null);
                return owner.Dispatcher.InvokeAsync(() =>
                {
                    var dlg = new SharedDevicesIncomingDialog(incoming.Service, incoming.Request) { Owner = owner };
                    var ok = dlg.ShowDialog() == true && dlg.Accepted;
                    return (object?)new SharedDevicesIncomingUiResult
                    {
                        Accepted = ok,
                        Remember = dlg.Remember,
                        DeviceIds = dlg.SelectedDevices
                    };
                }).Task;
            });

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.MenuTest,
            (_, _) =>
            {
                var dlg = new SharedDevicesTestDialog { Owner = owner };
                dlg.ShowDialog();
                return Task.FromResult<object?>(null);
            });

        ui.RegisterHandler(ToolIds.SharedDevices, SharedDevicesToolModule.MenuManage,
            (arg, _) =>
            {
                var sd = arg as SharedDevicesService ?? tools.GetToolService<SharedDevicesService>();
                if (sd == null) return Task.FromResult<object?>(null);
                var dlg = new SharedDevicesManageDialog(sd) { Owner = owner };
                dlg.ShowDialog();
                return Task.FromResult<object?>(null);
            });
    }
}
