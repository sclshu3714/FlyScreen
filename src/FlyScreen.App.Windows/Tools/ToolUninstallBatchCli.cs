using System.IO;
using System.Text.Json;
using FlyScreen.Services;
using FlyScreen.Services.Tools;
using FlyScreen.ToolSdk;

namespace FlyScreen.Tools;

/// <summary>产品卸载器调用：<c>FlyScreen.exe --tool-uninstall-batch answers.json</c>。</summary>
public static class ToolUninstallBatchCli
{
    public const string Flag = "--tool-uninstall-batch";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool TryGetAnswersPath(string[] args, out string path)
    {
        path = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(args[i], "/tool-uninstall-batch", StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 >= args.Length) return false;
            path = args[i + 1].Trim().Trim('"');
            return !string.IsNullOrWhiteSpace(path);
        }
        return false;
    }

    public static async Task<int> RunAsync(string answersPath, CancellationToken ct = default)
    {
        if (!File.Exists(answersPath))
        {
            AppLog.Error($"批处理卸载文件不存在: {answersPath}");
            return 2;
        }

        ToolUninstallBatchFile batch;
        try
        {
            var json = await File.ReadAllTextAsync(answersPath, ct).ConfigureAwait(false);
            batch = JsonSerializer.Deserialize<ToolUninstallBatchFile>(json, JsonOptions)
                    ?? new ToolUninstallBatchFile();
        }
        catch (Exception ex)
        {
            AppLog.Error("解析批处理卸载文件失败: " + ex.Message);
            return 3;
        }

        var items = batch.Tools
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => (
                t.Id.Trim(),
                (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(
                    t.Answers ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase)))
            .ToList();

        if (items.Count == 0)
        {
            AppLog.Info("批处理卸载：无工具条目，跳过");
            return 0;
        }

        var result = await ToolService.Instance.UninstallBatchAsync(items, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            AppLog.Error("批处理卸载失败: " + result.Message);
            return 1;
        }

        AppLog.Info(result.Message);
        return 0;
    }
}
