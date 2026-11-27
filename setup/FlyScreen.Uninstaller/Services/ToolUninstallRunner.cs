using System.IO;
using System.Text.Json;
using FlyScreen.Setup.Common;
using FlyScreen.ToolSdk;

namespace FlyScreen.Uninstaller.Services;

/// <summary>
/// 在卸载器进程内执行插件卸载决策与目录清理。
/// <para>
/// 禁止启动主程序：卸载流程必须先结束玄鸟，再清文件；拉起 FlyScreen.exe
/// （即便带 CLI 参数）也会进 WPF 主窗，属于错误设计。
/// </para>
/// </summary>
public static class ToolUninstallRunner
{
    public static async Task RunBatchAsync(
        string installDir,
        string exeName,
        ToolUninstallBatchFile batch,
        IProgress<ProgressReport>? progress,
        CancellationToken ct)
    {
        _ = exeName;
        if (batch.Tools.Count == 0) return;

        var toolsRoot = ToolsUserState.FindToolsRoot(installDir)
                        ?? Path.Combine(installDir, "Tools");

        var total = batch.Tools.Count;
        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = batch.Tools[i];
            if (string.IsNullOrWhiteSpace(item.Id)) continue;

            var id = item.Id.Trim();
            var pct = 22 + (i + 1) / (double)total * 12;
            progress?.Report(ProgressReport.Create(pct, $"正在卸载插件 {id}…"));

            await Task.Run(() => ApplyOffline(toolsRoot, id, item.Answers), ct).ConfigureAwait(false);
        }

        progress?.Report(ProgressReport.Create(35, "插件已按策略卸载"));
    }

    private static void ApplyOffline(
        string toolsRoot,
        string toolId,
        IReadOnlyDictionary<string, string>? answers)
    {
        var action = "keepEncrypted";
        if (answers != null
            && answers.TryGetValue("action", out var a)
            && !string.IsNullOrWhiteSpace(a))
            action = a.Trim();

        switch (action.ToLowerInvariant())
        {
            case "decryptkeep":
                throw new InvalidOperationException(
                    $"插件「{toolId}」选择了「解密并保留」。卸载器不能启动主程序执行解密；" +
                    "请先在玄鸟内打开该工具完成解密导出，再卸载（或改选保留加密数据 / 销毁）。");

            case "destroy":
                DestroyToolDataAndVault(toolsRoot, toolId);
                break;

            case "keepencrypted":
            default:
                // 保留加密数据 / 无策略：只卸工具文件，不动 ToolData 与保险箱目录
                break;
        }

        RemoveInstalledTool(toolsRoot, toolId);
    }

    /// <summary>
    /// 通用约定：ToolData/{id}/settings.json 若含 activeVaultRoot 则删除该目录；
    /// 再删除整个 ToolData/{id}。不特判 ToolId。
    /// </summary>
    private static void DestroyToolDataAndVault(string toolsRoot, string toolId)
    {
        var dataDir = Path.Combine(toolsRoot, "ToolData", toolId);
        var settingsPath = Path.Combine(dataDir, "settings.json");
        if (File.Exists(settingsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (TryReadString(doc.RootElement, "activeVaultRoot", out var vault)
                    || TryReadString(doc.RootElement, "ActiveVaultRoot", out vault))
                {
                    if (!string.IsNullOrWhiteSpace(vault) && Directory.Exists(vault))
                        TryDeleteDirectory(vault);
                }
            }
            catch { /* ignore parse */ }
        }

        if (Directory.Exists(dataDir))
            TryDeleteDirectory(dataDir);
    }

    private static void RemoveInstalledTool(string toolsRoot, string toolId)
    {
        var installedRoot = Path.Combine(toolsRoot, "Installed", toolId);
        if (Directory.Exists(installedRoot))
            TryDeleteDirectory(installedRoot);

        // 兼容旧布局：tools-install.json 里的 path 可能不在 Installed/{id} 下
        var statePath = Path.Combine(toolsRoot, "tools-install.json");
        if (!File.Exists(statePath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = doc.RootElement.Clone();
            if (!root.TryGetProperty("tools", out var toolsEl)
                && !root.TryGetProperty("Tools", out toolsEl))
                return;

            string? leftoverPath = null;
            foreach (var el in toolsEl.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString()
                    : el.TryGetProperty("Id", out idEl) ? idEl.GetString() : null;
                if (!string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase))
                    continue;
                leftoverPath = el.TryGetProperty("path", out var pEl) ? pEl.GetString()
                    : el.TryGetProperty("Path", out pEl) ? pEl.GetString() : null;
                break;
            }

            if (!string.IsNullOrWhiteSpace(leftoverPath) && Directory.Exists(leftoverPath))
                TryDeleteDirectory(leftoverPath);

            // 重写 state：去掉该 id
            var list = new List<JsonElement>();
            foreach (var el in toolsEl.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString()
                    : el.TryGetProperty("Id", out idEl) ? idEl.GetString() : null;
                if (string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase))
                    continue;
                list.Add(el.Clone());
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                foreach (var el in list)
                    el.WriteTo(writer);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            File.WriteAllBytes(statePath, stream.ToArray());
        }
        catch
        {
            // 目录已尽力删；状态文件失败不阻断主程序卸载
        }
    }

    private static bool TryReadString(JsonElement el, string name, out string? value)
    {
        value = null;
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
            return false;
        value = p.GetString();
        return true;
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }

            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { /* ignore */ }
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }
}
