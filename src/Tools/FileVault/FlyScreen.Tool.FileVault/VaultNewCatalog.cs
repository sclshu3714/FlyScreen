using System.Runtime.InteropServices;
using FlyScreen.Helpers.Localization;
using Microsoft.Win32;

namespace FlyScreen.Tool.FileVault;

public enum VaultNewKind
{
    Folder,
    Shortcut,
    File
}

public sealed class VaultNewEntry
{
    public VaultNewKind Kind { get; init; }
    public string MenuTitle { get; init; } = "";
    public string SuggestedName { get; init; } = "";
    public string Extension { get; init; } = "";
    public string IconCode => VaultNewIcons.Code(Kind, Extension);
    public string IconColor => VaultNewIcons.Color(Kind, Extension);
    public string? TemplatePath { get; init; }
    public byte[]? TemplateBytes { get; init; }
}

/// <summary>对齐资源管理器「新建」：文件夹、快捷方式，以及本机 ShellNew 能生成的类型。</summary>
public static class VaultNewIcons
{
    public static string Code(VaultNewKind kind, string extension)
    {
        if (kind == VaultNewKind.Folder) return "icon.file.folder";
        if (kind == VaultNewKind.Shortcut) return "icon.file.shortcut";
        return extension.ToLowerInvariant() switch
        {
            ".accdb" or ".mdb" or ".accde" => "icon.file.access",
            ".bmp" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".tif" or ".tiff" or ".ico" => "icon.file.image",
            ".doc" or ".docx" or ".docm" => "icon.file.word",
            ".ppt" or ".pptx" or ".pptm" => "icon.file.ppt",
            ".txt" or ".log" or ".csv" => "icon.file.text",
            ".rtf" => "icon.file.rtf",
            ".xls" or ".xlsx" or ".xlsm" => "icon.file.excel",
            ".zip" or ".7z" or ".rar" => "icon.file.zip",
            _ => "icon.file.generic"
        };
    }

    public static string Color(VaultNewKind kind, string extension) => Code(kind, extension) switch
    {
        "icon.file.folder" => "#E8B931",
        "icon.file.shortcut" => "#3B82F6",
        "icon.file.access" => "#A4373A",
        "icon.file.image" => "#7C6BB5",
        "icon.file.word" => "#2B579A",
        "icon.file.ppt" => "#D24726",
        "icon.file.text" => "#64748B",
        "icon.file.rtf" => "#2B579A",
        "icon.file.excel" => "#217346",
        "icon.file.zip" => "#C48A12",
        _ => "#64748B"
    };
}

public static class VaultNewCatalog
{
    private static IReadOnlyList<VaultNewEntry>? _cache;

    public static IReadOnlyList<VaultNewEntry> Load() => _cache ??= Build();

    private static IReadOnlyList<VaultNewEntry> Build()
    {
        var list = new List<VaultNewEntry>
        {
            new()
            {
                Kind = VaultNewKind.Folder,
                MenuTitle = Loc.T("Tool.FileVault.NewItemFolder"),
                SuggestedName = Loc.T("Tool.FileVault.NewFolder")
            }
        };
        if (OperatingSystem.IsWindows())
        {
            list.Add(new VaultNewEntry
            {
                Kind = VaultNewKind.Shortcut,
                MenuTitle = Loc.T("Tool.FileVault.NewItemShortcut"),
                Extension = ".lnk"
            });
            foreach (var entry in LoadShellNew())
                list.Add(entry);
        }
        else
        {
            var title = Loc.T("Tool.FileVault.NewText");
            list.Add(new VaultNewEntry
            {
                Kind = VaultNewKind.File,
                MenuTitle = title,
                Extension = ".txt",
                SuggestedName = Loc.T("Tool.FileVault.NewFilePrefix") + title + ".txt"
            });
        }
        return list;
    }

    private static IEnumerable<VaultNewEntry> LoadShellNew()
    {
        var found = new Dictionary<string, VaultNewEntry>(StringComparer.OrdinalIgnoreCase);
        var order = ReadOrder();
        try
        {
            using var root = Registry.ClassesRoot;
            foreach (var ext in order)
                TryAdd(root, found, ext);
            foreach (var name in root.GetSubKeyNames())
            {
                if (name.Length < 2 || name[0] != '.' || found.ContainsKey(name))
                    continue;
                TryAdd(root, found, name);
            }
        }
        catch
        {
            // 注册表读不全时仍返回已收集到的项
        }

        EnsureBuiltin(found, ".txt");
        EnsureBuiltin(found, ".bmp");
        foreach (var entry in found.Values.OrderBy(e => e.Extension, StringComparer.OrdinalIgnoreCase))
            yield return entry;
    }

    private static void EnsureBuiltin(Dictionary<string, VaultNewEntry> found, string ext)
    {
        if (found.ContainsKey(ext))
            return;
        var title = TypeName(ext);
        found[ext] = new VaultNewEntry
        {
            Kind = VaultNewKind.File,
            MenuTitle = title,
            Extension = ext,
            SuggestedName = Loc.T("Tool.FileVault.NewFilePrefix") + title + ext,
            TemplateBytes = ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ? TinyBmp : null
        };
    }

    private static List<string> ReadOrder()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew");
                if (key?.GetValue("Classes") is string[] classes && classes.Length > 0)
                    return classes.Where(s => !string.IsNullOrWhiteSpace(s) && s[0] == '.').ToList();
            }
            catch
            {
                // ignore
            }
        }
        return [];
    }

    private static void TryAdd(RegistryKey root, Dictionary<string, VaultNewEntry> found, string ext)
    {
        if (SkipExtension(ext))
            return;
        try
        {
            using var extKey = root.OpenSubKey(ext);
            if (extKey == null)
                return;
            using var shellNew = OpenShellNew(root, extKey);
            if (shellNew == null || shellNew.GetValue("Handler") != null)
                return;
            if (!TryReadTemplate(shellNew, out var templatePath, out var bytes))
                return;
            var title = TypeName(ext);
            if (string.IsNullOrWhiteSpace(title) || title.Equals(ext, StringComparison.OrdinalIgnoreCase))
                title = FriendlyName(root, extKey, shellNew, ext);
            found[ext] = new VaultNewEntry
            {
                Kind = VaultNewKind.File,
                MenuTitle = title,
                Extension = ext,
                SuggestedName = Loc.T("Tool.FileVault.NewFilePrefix") + title + ext,
                TemplatePath = templatePath,
                TemplateBytes = bytes
            };
        }
        catch
        {
            // ignore
        }
    }

    private static RegistryKey? OpenShellNew(RegistryKey root, RegistryKey extKey)
    {
        var direct = extKey.OpenSubKey("ShellNew");
        if (direct != null && HasPayload(direct))
            return direct;
        direct?.Dispose();

        var progId = extKey.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(progId))
            return null;
        var nested = extKey.OpenSubKey(progId + "\\ShellNew");
        if (nested != null && HasPayload(nested))
            return nested;
        nested?.Dispose();
        var fromProg = root.OpenSubKey(progId + "\\ShellNew");
        if (fromProg != null && HasPayload(fromProg))
            return fromProg;
        fromProg?.Dispose();
        return null;
    }

    private static bool SkipExtension(string ext) =>
        ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".library-ms", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".contact", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".msi", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".mst", StringComparison.OrdinalIgnoreCase)
        || ext.StartsWith(".appx", StringComparison.OrdinalIgnoreCase)
        || ext.StartsWith(".msix", StringComparison.OrdinalIgnoreCase);

    private static bool HasPayload(RegistryKey key) =>
        key.GetValue("NullFile") != null
        || key.GetValue("Data") != null
        || key.GetValue("FileName") != null
        || key.GetValue("Command") != null;

    private static bool TryReadTemplate(RegistryKey shellNew, out string? templatePath, out byte[]? bytes)
    {
        templatePath = null;
        bytes = null;
        if (shellNew.GetValue("FileName") is string fileName)
            templatePath = ResolveTemplate(fileName);
        if (templatePath != null)
            return true;
        if (shellNew.GetValue("Data") is byte[] data && data.Length > 0)
        {
            bytes = data;
            return true;
        }
        return shellNew.GetValue("NullFile") != null || shellNew.GetValue("Command") != null;
    }

    private static string TypeName(string ext)
    {
        var info = new ShFileInfo();
        SHGetFileInfo("file" + ext, 0x80, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), 0x400 | 0x10);
        return string.IsNullOrWhiteSpace(info.szTypeName) ? ext : info.szTypeName;
    }

    private static string? ResolveTemplate(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;
        fileName = Environment.ExpandEnvironmentVariables(fileName.Trim().Trim('"'));
        if (File.Exists(fileName))
            return fileName;
        var name = Path.GetFileName(fileName);
        if (string.IsNullOrEmpty(name))
            return null;
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Templates),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Templates"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Templates"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ShellNew")
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root))
                continue;
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    private static string FriendlyName(RegistryKey root, RegistryKey extKey, RegistryKey shellNew, string ext)
    {
        if (shellNew.GetValue("MenuText") is string menu && !string.IsNullOrWhiteSpace(menu))
            return Expand(menu);
        var progId = extKey.GetValue(null) as string;
        if (!string.IsNullOrWhiteSpace(progId))
        {
            using var prog = root.OpenSubKey(progId);
            if (prog?.GetValue(null) is string name && !string.IsNullOrWhiteSpace(name))
                return Expand(name);
        }
        return ext;
    }

    private static string Expand(string value)
    {
        if (!value.StartsWith('@'))
            return value;
        var buf = new System.Text.StringBuilder(512);
        return SHLoadIndirectString(value, buf, buf.Capacity, IntPtr.Zero) == 0 && buf.Length > 0
            ? buf.ToString()
            : value.TrimStart('@');
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, System.Text.StringBuilder output, int cch, IntPtr reserved);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint cb, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private static readonly byte[] TinyBmp =
    [
        0x42, 0x4D, 0x3A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x36, 0x00, 0x00, 0x00,
        0x28, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00,
        0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0x00
    ];
}
