using FlyScreen.Helpers.Localization;
using FlyScreen.Services.Tools;
using FlyScreen.Tool.FileVault.Store;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.FileVault;

/// <summary>文件保险箱工具模块（.flytool ALC 入口）。</summary>
public sealed class Module : IToolModule, IToolUninstallPolicy
{
    public const string MenuStop = "stop";
    public const string MenuLock = "lock";
    public const string UiPickFolder = "pick-folder";
    public const string UiPickFiles = "pick-files";
    public const string UiPromptPassword = "prompt-password";
    public const string UiPromptText = "prompt-text";
    public const string UiShowWindow = "show-window";
    public const string UiCloseWindow = "close-window";

    private readonly FileVaultService _service = new();
    private IToolHostServices? _host;

    static Module()
    {
        FileVaultLocalization.EnsureRegistered();
        FileVaultIcons.EnsureRegistered();
    }

    public string Id => ToolIds.FileVault;
    public bool IsRunning => _service.IsRunning;
    public bool UsesWorkspace => false;
    public FileVaultService Service => _service;

    public event Action? StateChanged
    {
        add => _service.SessionChanged += value;
        remove => _service.SessionChanged -= value;
    }

    public Task OnLoadedAsync(IToolHostServices host, CancellationToken ct = default)
    {
        FileVaultLocalization.EnsureRegistered();
        FileVaultIcons.EnsureRegistered();
        _host = host;
        _service.AttachHost(host);
        host.RegisterService<IFileVaultService>(_service);
        host.RegisterService(_service);
        return Task.CompletedTask;
    }

    public async Task OnUnloadedAsync(CancellationToken ct = default)
    {
        if (_host?.Ui != null)
        {
            try { await _host.Ui.RequestAsync(Id, UiCloseWindow, null, ct).ConfigureAwait(true); }
            catch { /* ignore */ }
        }
        _service.Stop();
        _host?.Workspace.Close(Id);
        try
        {
            _host?.UnregisterService<IFileVaultService>();
            _host?.UnregisterService<FileVaultService>();
        }
        catch { /* ignore */ }

        ToolIconRegistry.Unregister(FileVaultIcons.Instance.IconKey);
        _host = null;
    }

    public Task<(bool Ok, string Message)> PrimaryAsync(
        ToolCardContext ctx, bool fromMenu, CancellationToken ct = default)
    {
        if (_host == null)
            return Task.FromResult((false, "工具宿主未就绪"));

        if (ctx.Row.IsRunning)
        {
            if (!fromMenu)
            {
                OpenWorkspace();
                return Task.FromResult((true, ""));
            }
            return StopInternalAsync();
        }

        if (!ctx.Row.CanStart && !ctx.Row.IsRunning)
            return Task.FromResult((false, Loc.T("Tool.FileVault.CannotStart")));

        if (!_service.IsRunning)
            _service.Start();
        OpenWorkspace();
        return Task.FromResult((true, ""));
    }

    public Task StopAsync(CancellationToken ct = default) => StopInternalAsync();

    public IReadOnlyList<ToolMenuContribution> GetMenuContributions(ToolCardContext ctx)
    {
        if (!ctx.Row.IsInstalled) return [];
        var list = new List<ToolMenuContribution>();
        if (ctx.Row.IsRunning)
        {
            list.Add(new ToolMenuContribution { ActionId = MenuLock, Label = Loc.T("Tool.FileVault.Lock") });
            list.Add(new ToolMenuContribution { ActionId = MenuStop, Label = Loc.T("Tool.Stop") });
        }
        return list;
    }

    public async Task InvokeMenuAsync(string actionId, CancellationToken ct = default)
    {
        if (string.Equals(actionId, MenuStop, StringComparison.OrdinalIgnoreCase))
        {
            await StopInternalAsync().ConfigureAwait(false);
            return;
        }
        if (string.Equals(actionId, MenuLock, StringComparison.OrdinalIgnoreCase))
            await _service.LockAsync(ct).ConfigureAwait(false);
    }

    public void OpenWorkspace()
    {
        if (_host == null) return;
        if (!_service.IsRunning)
            _service.Start();
        // 独立弹窗，不嵌入工具工作区；先刷新 UI 贡献者确保 show-window 已注册
        try { ToolService.Instance.RefreshToolUiContributors(); }
        catch { /* ignore */ }
        _ = OpenWindowSafeAsync();
    }

    private async Task OpenWindowSafeAsync()
    {
        if (_host?.Ui == null) return;
        try
        {
            await _host.Ui.RequestAsync(Id, UiShowWindow, null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            try
            {
                await _host.Ui.RequestAsync(Id, "alert", ex.Message).ConfigureAwait(true);
            }
            catch { /* ignore */ }
        }
    }

    public ToolUninstallUiSchema? GetUninstallUiSchema()
    {
        // 无库时无需决策；产品卸载器仍可读包内 uninstall-ui.json
        if (!_service.HasVault && string.IsNullOrWhiteSpace(_service.VaultRoot))
            return null;
        return CreateUninstallSchema();
    }

    public async Task<bool> ApplyUninstallAnswersAsync(
        IReadOnlyDictionary<string, string> answers,
        CancellationToken ct = default)
    {
        if (!_service.HasVault && string.IsNullOrWhiteSpace(_service.VaultRoot))
            return true;

        if (!answers.TryGetValue("action", out var action)
            || string.IsNullOrWhiteSpace(action))
            action = "keepEncrypted";

        try
        {
            switch (action.Trim().ToLowerInvariant())
            {
                case "keepencrypted":
                    await _service.ApplyUninstallActionAsync(
                        VaultUninstallAction.KeepEncryptedUninstallTool, null, null, ct)
                        .ConfigureAwait(false);
                    return true;

                case "decryptkeep":
                    answers.TryGetValue("password", out var pwd);
                    answers.TryGetValue("decryptDir", out var dir);
                    if (string.IsNullOrWhiteSpace(dir))
                        throw new InvalidOperationException("请指定解密输出目录");
                    if (string.IsNullOrEmpty(pwd) && !_service.IsUnlocked)
                        throw new InvalidOperationException("请输入主密码");
                    await _service.ApplyUninstallActionAsync(
                        VaultUninstallAction.DecryptAndKeep, dir, pwd, ct).ConfigureAwait(false);
                    return true;

                case "destroy":
                    answers.TryGetValue("destroyConfirm", out var confirm);
                    var token = Loc.T("Tool.FileVault.DestroyToken");
                    if (!string.Equals(confirm, token, StringComparison.Ordinal)
                        && !string.Equals(confirm, "销毁", StringComparison.Ordinal)
                        && !string.Equals(confirm, "DESTROY", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("销毁确认字样不正确");
                    await _service.ApplyUninstallActionAsync(
                        VaultUninstallAction.Destroy, null, null, ct).ConfigureAwait(false);
                    return true;

                default:
                    throw new InvalidOperationException("未知的卸载决策: " + action);
            }
        }
        catch (Exception ex)
        {
            if (_host?.Ui != null)
                await _host.Ui.RequestAsync(Id, "alert", ex.Message, ct).ConfigureAwait(false);
            return false;
        }
    }

    public async Task<bool> PrepareUninstallAsync(CancellationToken ct = default)
    {
        var schema = GetUninstallUiSchema();
        if (schema == null)
            return true;
        if (_host?.Ui == null)
            return await ApplyUninstallAnswersAsync(schema.Defaults, ct).ConfigureAwait(false);

        var result = await _host.Ui.RequestAsync(
            Id, ToolUninstallUiSchema.RequestKind, schema, ct).ConfigureAwait(false);
        if (result is ToolUninstallAnswers { Cancelled: true })
            return false;
        if (result is ToolUninstallAnswers decided)
            return await ApplyUninstallAnswersAsync(decided.Values, ct).ConfigureAwait(false);
        if (result is IReadOnlyDictionary<string, string> map)
            return await ApplyUninstallAnswersAsync(map, ct).ConfigureAwait(false);
        return false;
    }

    public static ToolUninstallUiSchema CreateUninstallSchema() => new()
    {
        Title = Loc.T("Tool.FileVault.UninstallTitle"),
        Description = Loc.T("Tool.FileVault.UninstallHint"),
        Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["action"] = "keepEncrypted"
        },
        Fields =
        [
            new ToolUninstallUiField
            {
                Id = "action",
                Type = "choice",
                Label = Loc.T("Tool.FileVault.UninstallTitle"),
                Required = true,
                Options =
                [
                    new() { Value = "keepEncrypted", Label = Loc.T("Tool.FileVault.UninstallKeep") },
                    new() { Value = "decryptKeep", Label = Loc.T("Tool.FileVault.UninstallDecrypt") },
                    new() { Value = "destroy", Label = Loc.T("Tool.FileVault.UninstallDestroy") }
                ]
            },
            new ToolUninstallUiField
            {
                Id = "password",
                Type = "password",
                Label = Loc.T("Tool.FileVault.Password"),
                Required = true,
                VisibleWhen = "action=decryptKeep"
            },
            new ToolUninstallUiField
            {
                Id = "decryptDir",
                Type = "folder",
                Label = Loc.T("Tool.FileVault.PickDecryptDir"),
                Required = true,
                VisibleWhen = "action=decryptKeep"
            },
            new ToolUninstallUiField
            {
                Id = "destroyConfirm",
                Type = "confirm",
                Label = Loc.T("Tool.FileVault.DestroyConfirm"),
                Required = true,
                VisibleWhen = "action=destroy"
            }
        ]
    };

    private async Task<(bool Ok, string Message)> StopInternalAsync()
    {
        if (_host?.Ui != null)
        {
            try { await _host.Ui.RequestAsync(Id, UiCloseWindow, null).ConfigureAwait(true); }
            catch { /* ignore */ }
        }
        _service.Stop();
        _host?.Workspace.Close(Id);
        return (true, "");
    }
}
