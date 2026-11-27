using FlyScreen.Helpers.Localization;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tool.FileVault;

public sealed class FileVaultLocalization : IToolLocalization
{
    public static FileVaultLocalization Instance { get; } = new();

    static FileVaultLocalization() => EnsureRegistered();

    public string SourceId => "file-vault";

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Tables { get; } =
        ToolLocalizationBuilder.Build(Zh(), En());

    public static void EnsureRegistered() => Loc.RegisterPlugin(Instance);

    private static Dictionary<string, string> Zh() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.FileVault.Name"] = "文件保险箱",
        ["Tool.FileVault.Summary"] = "将文件移入加密保险箱；锁定后外部无法查看真实文件名与内容。支持迁移、挂载浏览与卸载时解密保留或销毁。",
        ["Tool.FileVault.Hint"] = "解锁后可浏览、移入/移出；需要时再手动挂载到临时目录供系统程序打开。锁定会擦除挂载与临时明文。主密码请自行保管：丢失且无备份则无法恢复。",
        ["Tool.FileVault.SecurityNotice"] = "开源可审计：密钥不在插件内，每个保险箱随机生成主密钥，并由你的主密码保护。算法公开不影响安全。默认不绑定本机，换机记得主密码即可打开。SSD 上「安全删除」无法保证物理不可恢复。",
        ["Tool.FileVault.Create"] = "创建保险箱",
        ["Tool.FileVault.OpenExisting"] = "打开已有…",
        ["Tool.FileVault.Unlock"] = "解锁",
        ["Tool.FileVault.Lock"] = "锁定",
        ["Tool.FileVault.Import"] = "移入文件…",
        ["Tool.FileVault.ImportFolder"] = "移入文件夹…",
        ["Tool.FileVault.Export"] = "移出…",
        ["Tool.FileVault.PasswordEmpty"] = "请设置主密码。",
        ["Tool.FileVault.PasswordTooShort"] = "主密码至少 8 个字符。",
        ["Tool.FileVault.PasswordTooWeak"] = "主密码过弱，请勿使用单一重复字符。",
        ["Tool.FileVault.IdleLock"] = "空闲自动锁定（分钟）",

        ["Tool.FileVault.Delete"] = "删除",
        ["Tool.FileVault.NewFolder"] = "新建文件夹",
        ["Tool.FileVault.Mount"] = "挂载",
        ["Tool.FileVault.Unmount"] = "卸载挂载",
        ["Tool.FileVault.OpenMount"] = "打开挂载目录",
        ["Tool.FileVault.Migrate"] = "迁移到…",
        ["Tool.FileVault.Backup"] = "导出备份包…",
        ["Tool.FileVault.Restore"] = "导入备份包…",
        ["Tool.FileVault.Up"] = "上级",
        ["Tool.FileVault.Refresh"] = "刷新",
        ["Tool.FileVault.Locked"] = "已锁定",
        ["Tool.FileVault.Unlocked"] = "已解锁",
        ["Tool.FileVault.NoVault"] = "尚未创建或打开保险箱。请先创建，或选择已有保险箱目录。",
        ["Tool.FileVault.Password"] = "主密码",
        ["Tool.FileVault.PasswordConfirm"] = "确认密码",
        ["Tool.FileVault.PasswordMismatch"] = "两次密码不一致。",
        ["Tool.FileVault.Empty"] = "当前文件夹为空",
        ["Tool.FileVault.ColName"] = "名称",
        ["Tool.FileVault.ColSize"] = "大小",
        ["Tool.FileVault.ColModified"] = "修改时间",
        ["Tool.FileVault.StatusRoot"] = "位置：{0}",
        ["Tool.FileVault.StatusMount"] = "挂载：{0}",
        ["Tool.FileVault.UninstallTitle"] = "卸载文件保险箱",
        ["Tool.FileVault.UninstallHint"] = "本机保险箱内仍有加密数据。请选择如何处理：",
        ["Tool.FileVault.UninstallDecrypt"] = "解密并保留到文件夹…",
        ["Tool.FileVault.UninstallDestroy"] = "永久销毁保险箱内全部文件",
        ["Tool.FileVault.UninstallKeep"] = "仅卸载工具，保留加密数据",
        ["Tool.FileVault.UninstallCancel"] = "取消",
        ["Tool.FileVault.DestroyConfirm"] = "此操作不可恢复。请输入「销毁」确认：",
        ["Tool.FileVault.DestroyToken"] = "销毁",
        ["Tool.FileVault.PickDecryptDir"] = "选择解密输出文件夹",
        ["Tool.FileVault.PickVaultDir"] = "选择保险箱目录",
        ["Tool.FileVault.PickMigrateDir"] = "选择迁移目标目录（须为空）",
        ["Tool.FileVault.Ok"] = "确定",
        ["Tool.FileVault.Cancel"] = "取消",
        ["Tool.FileVault.NotReady"] = "保险箱工具未就绪",
        ["Tool.FileVault.SecureDelete"] = "移入后安全删除源文件",
        ["Tool.FileVault.AutoMount"] = "解锁后自动挂载",
    };

    private static Dictionary<string, string> En() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tool.FileVault.Name"] = "File Vault",
        ["Tool.FileVault.Summary"] = "Move files into an encrypted vault. When locked, names and contents are not readable outside the vault. Supports migrate, mount, and uninstall decrypt/destroy.",
        ["Tool.FileVault.Hint"] = "After unlock you can browse and import/export; mount a temp folder manually when system apps need a path. Lock wipes the mount and temp plaintext. Keep your master password: lost with no backup means data is gone.",
        ["Tool.FileVault.SecurityNotice"] = "Open-source auditable: keys are not in the plugin. Each vault gets a random master key wrapped by your password. Public algorithms are fine. No machine-binding by default—migrate with the vault folder + password. Secure-delete on SSD is best-effort only.",
        ["Tool.FileVault.Create"] = "Create vault",
        ["Tool.FileVault.OpenExisting"] = "Open existing…",
        ["Tool.FileVault.Unlock"] = "Unlock",
        ["Tool.FileVault.Lock"] = "Lock",
        ["Tool.FileVault.Import"] = "Import files…",
        ["Tool.FileVault.ImportFolder"] = "Import folder…",
        ["Tool.FileVault.Export"] = "Export…",
        ["Tool.FileVault.PasswordEmpty"] = "Please set a master password.",
        ["Tool.FileVault.PasswordTooShort"] = "Master password must be at least 8 characters.",
        ["Tool.FileVault.PasswordTooWeak"] = "Password is too weak; do not use a single repeated character.",
        ["Tool.FileVault.IdleLock"] = "Idle auto-lock (minutes)",

        ["Tool.FileVault.Delete"] = "Delete",
        ["Tool.FileVault.NewFolder"] = "New folder",
        ["Tool.FileVault.Mount"] = "Mount",
        ["Tool.FileVault.Unmount"] = "Unmount",
        ["Tool.FileVault.OpenMount"] = "Open mount folder",
        ["Tool.FileVault.Migrate"] = "Migrate to…",
        ["Tool.FileVault.Backup"] = "Export backup…",
        ["Tool.FileVault.Restore"] = "Import backup…",
        ["Tool.FileVault.Up"] = "Up",
        ["Tool.FileVault.Refresh"] = "Refresh",
        ["Tool.FileVault.Locked"] = "Locked",
        ["Tool.FileVault.Unlocked"] = "Unlocked",
        ["Tool.FileVault.NoVault"] = "No vault yet. Create one or open an existing vault folder.",
        ["Tool.FileVault.Password"] = "Master password",
        ["Tool.FileVault.PasswordConfirm"] = "Confirm password",
        ["Tool.FileVault.PasswordMismatch"] = "Passwords do not match.",
        ["Tool.FileVault.Empty"] = "This folder is empty",
        ["Tool.FileVault.ColName"] = "Name",
        ["Tool.FileVault.ColSize"] = "Size",
        ["Tool.FileVault.ColModified"] = "Modified",
        ["Tool.FileVault.StatusRoot"] = "Location: {0}",
        ["Tool.FileVault.StatusMount"] = "Mount: {0}",
        ["Tool.FileVault.UninstallTitle"] = "Uninstall File Vault",
        ["Tool.FileVault.UninstallHint"] = "Encrypted vault data is still on this PC. Choose how to proceed:",
        ["Tool.FileVault.UninstallDecrypt"] = "Decrypt and keep to a folder…",
        ["Tool.FileVault.UninstallDestroy"] = "Permanently destroy all vault files",
        ["Tool.FileVault.UninstallKeep"] = "Uninstall tool only, keep encrypted data",
        ["Tool.FileVault.UninstallCancel"] = "Cancel",
        ["Tool.FileVault.DestroyConfirm"] = "This cannot be undone. Type DESTROY to confirm:",
        ["Tool.FileVault.DestroyToken"] = "DESTROY",
        ["Tool.FileVault.PickDecryptDir"] = "Choose folder for decrypted files",
        ["Tool.FileVault.PickVaultDir"] = "Choose vault folder",
        ["Tool.FileVault.PickMigrateDir"] = "Choose empty destination folder",
        ["Tool.FileVault.Ok"] = "OK",
        ["Tool.FileVault.Cancel"] = "Cancel",
        ["Tool.FileVault.NotReady"] = "File Vault is not ready",
        ["Tool.FileVault.SecureDelete"] = "Secure-delete sources after import",
        ["Tool.FileVault.AutoMount"] = "Auto-mount after unlock",
    };
}
