using System.IO;
using System.Text.Json;
using FlyScreen.Setup.Common;
using FlyScreen.ToolSdk;

namespace FlyScreen.Uninstaller.Services;

/// <summary>从安装目录探测已装插件与可选 uninstall-ui.json（通用，无工具特判）。</summary>
public static class InstalledToolsProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public sealed record ToolInfo(
        string Id,
        string Version,
        string InstallPath,
        string DisplayName,
        ToolUninstallUiSchema? UninstallUi);

    public static IReadOnlyList<ToolInfo> ListInstalled(string productInstallDir)
    {
        var list = new List<ToolInfo>();
        var toolsRoot = ToolsUserState.FindToolsRoot(productInstallDir);
        if (toolsRoot == null) return list;

        var stateFile = Path.Combine(toolsRoot, "tools-install.json");
        if (!File.Exists(stateFile)) return list;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(stateFile));
            if (!doc.RootElement.TryGetProperty("tools", out var toolsEl)
                && !doc.RootElement.TryGetProperty("Tools", out toolsEl))
                return list;

            foreach (var el in toolsEl.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString()
                    : el.TryGetProperty("Id", out idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(id)) continue;

                var version = el.TryGetProperty("version", out var vEl) ? vEl.GetString()
                    : el.TryGetProperty("Version", out vEl) ? vEl.GetString() : "";
                var path = el.TryGetProperty("path", out var pEl) ? pEl.GetString()
                    : el.TryGetProperty("Path", out pEl) ? pEl.GetString() : "";
                if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                    continue;

                var display = ReadDisplayName(path, id);
                var schema = TryReadUninstallUi(path);
                list.Add(new ToolInfo(id, version ?? "", path, display, schema));
            }
        }
        catch
        {
            return list;
        }

        return list;
    }

    public static ToolUninstallUiSchema? TryReadUninstallUi(string toolInstallPath)
    {
        var file = Path.Combine(toolInstallPath, ToolUninstallUiSchema.FileName);
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<ToolUninstallUiSchema>(File.ReadAllText(file), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadDisplayName(string toolInstallPath, string fallbackId)
    {
        var manifest = Path.Combine(toolInstallPath, "manifest.json");
        if (!File.Exists(manifest)) return fallbackId;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var s = name.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s!;
            }
        }
        catch { /* ignore */ }
        return fallbackId;
    }
}
