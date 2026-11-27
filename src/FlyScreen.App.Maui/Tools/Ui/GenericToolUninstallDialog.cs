using FlyScreen.ToolSdk;

namespace FlyScreen.AppMaui.Tools.Ui;

/// <summary>Maui：按 schema 逐步提问（ActionSheet / Prompt）。</summary>
internal static class GenericToolUninstallDialog
{
    public static async Task<ToolUninstallAnswers> ShowAsync(string toolId, ToolUninstallUiSchema schema)
    {
        _ = toolId;
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null)
            return ToolUninstallAnswers.Cancel();

        var values = new Dictionary<string, string>(schema.Defaults, StringComparer.OrdinalIgnoreCase);

        // 多轮：choice 变化可能露出新字段
        for (var pass = 0; pass < 8; pass++)
        {
            var progressed = false;
            foreach (var field in schema.Fields)
            {
                if (!ToolUninstallUiEvaluator.IsVisible(field, values))
                    continue;
                if (values.ContainsKey(field.Id) && pass > 0
                    && field.Type.Equals("choice", StringComparison.OrdinalIgnoreCase))
                    continue;

                switch (field.Type.ToLowerInvariant())
                {
                    case "choice":
                    {
                        var options = (field.Options ?? []).Select(o => o.Label).ToArray();
                        if (options.Length == 0) break;
                        var picked = await page.DisplayActionSheet(
                            field.Label, "取消", null, options);
                        if (picked == null || picked == "取消")
                            return ToolUninstallAnswers.Cancel();
                        var opt = field.Options!.FirstOrDefault(o => o.Label == picked);
                        if (opt == null) return ToolUninstallAnswers.Cancel();
                        values[field.Id] = opt.Value;
                        progressed = true;
                        break;
                    }
                    case "password":
                    case "confirm":
                    {
                        if (values.ContainsKey(field.Id) && !string.IsNullOrEmpty(values[field.Id]))
                            break;
                        var input = await page.DisplayPromptAsync(schema.Title, field.Label,
                            "确定", "取消",
                            keyboard: field.Type.Equals("password", StringComparison.OrdinalIgnoreCase)
                                ? Keyboard.Default
                                : Keyboard.Default);
                        if (input == null)
                            return ToolUninstallAnswers.Cancel();
                        values[field.Id] = input;
                        progressed = true;
                        break;
                    }
                    case "folder":
                    {
                        if (values.ContainsKey(field.Id) && !string.IsNullOrEmpty(values[field.Id]))
                            break;
#if WINDOWS
                        try
                        {
                            var picker = new Windows.Storage.Pickers.FolderPicker();
                            picker.FileTypeFilter.Add("*");
                            var hwnd = ((MauiWinUIWindow)Application.Current!.Windows[0].Handler!.PlatformView!).WindowHandle;
                            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                            var folder = await picker.PickSingleFolderAsync();
                            if (folder == null)
                                return ToolUninstallAnswers.Cancel();
                            values[field.Id] = folder.Path;
                            progressed = true;
                        }
                        catch
                        {
                            return ToolUninstallAnswers.Cancel();
                        }
#else
                        var path = await page.DisplayPromptAsync(schema.Title, field.Label + "（路径）", "确定", "取消");
                        if (path == null) return ToolUninstallAnswers.Cancel();
                        values[field.Id] = path;
                        progressed = true;
#endif
                        break;
                    }
                }
            }

            var missing = ToolUninstallUiEvaluator.ValidateRequired(schema, values);
            if (missing == null)
                return ToolUninstallAnswers.From(values);
            if (!progressed)
                break;
        }

        return ToolUninstallAnswers.Cancel();
    }
}
