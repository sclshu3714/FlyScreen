using FlyScreen.ToolSdk;
using FlyScreen.Ui.Maui;
using Microsoft.Maui.Controls.Shapes;
#if WINDOWS
using FlyScreen.AppMaui.Platforms.Windows;
#endif

namespace FlyScreen.AppMaui.Tools.Ui;

/// <summary>Maui：按 schema 逐步提问（ActionSheet / Prompt / 自定义目录框）。</summary>
internal static class GenericToolUninstallDialog
{
    public static async Task<ToolUninstallAnswers> ShowAsync(string toolId, ToolUninstallUiSchema schema)
    {
        _ = toolId;
        var page = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page == null)
            return ToolUninstallAnswers.Cancel();

        var values = new Dictionary<string, string>(schema.Defaults, StringComparer.OrdinalIgnoreCase);

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
                        var picked = await page.DisplayActionSheetAsync(
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
                            keyboard: Keyboard.Default);
                        if (input == null)
                            return ToolUninstallAnswers.Cancel();
                        values[field.Id] = input;
                        progressed = true;
                        break;
                    }
                    case "folder":
                    {
                        if (values.ContainsKey(field.Id))
                            break;

                        var path = await ShowFolderPathDialogAsync(page, schema, field).ConfigureAwait(true);
                        if (path == null && field.Required)
                            return ToolUninstallAnswers.Cancel();
                        values[field.Id] = path ?? "";
                        progressed = true;
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

    /// <summary>
    /// 居中卡片弹窗：可手输 + 框内嵌选夹按钮。
    /// 返回 null=取消；""=留空（非必填）。
    /// </summary>
    private static async Task<string?> ShowFolderPathDialogAsync(
        Page host, ToolUninstallUiSchema schema, ToolUninstallUiField field)
    {
        var tcs = new TaskCompletionSource<string?>();
        var closing = false;

        var entry = new Entry
        {
            Placeholder = field.Required ? "输入或选择输出目录" : "可留空；也可输入或选择目录",
            FontSize = 14,
            HeightRequest = 40,
            VerticalOptions = LayoutOptions.Fill,
            Margin = new Thickness(0, 0, 36, 0)
        };

        var browse = new Button
        {
            Text = "📁",
            FontSize = 16,
            WidthRequest = 36,
            HeightRequest = 36,
            Padding = 0,
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            AutomationId = "browse-folder"
        };
        browse.Clicked += async (_, _) =>
        {
            var picked = await PickFolderPathAsync(field.Label).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(picked))
                entry.Text = picked;
        };

        var inputChrome = new Border
        {
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Color.FromArgb("#404040")),
            Background = new SolidColorBrush(Color.FromArgb("#2A2A2A")),
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Padding = new Thickness(10, 2, 2, 2),
            Content = new Grid { Children = { entry, browse } }
        };

        var hint = new Label
        {
            Text = field.Required
                ? field.Label
                : field.Label + "（可留空）",
            FontSize = 13,
            LineBreakMode = LineBreakMode.WordWrap
        };

        var body = new VerticalStackLayout
        {
            Spacing = 10,
            Padding = new Thickness(20, 16, 20, 8),
            Children = { hint, inputChrome }
        };

        var ok = new Button { Text = "确定" };
        var cancel = new Button { Text = "取消" };
        if (Application.Current?.Resources.TryGetValue("DlgPrimaryBtn", out var primaryStyle) == true
            && primaryStyle is Style ps)
            ok.Style = ps;
        if (Application.Current?.Resources.TryGetValue("DlgNormalBtn", out var normalStyle) == true
            && normalStyle is Style ns)
            cancel.Style = ns;

        var footer = new HorizontalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = LayoutOptions.End,
            Children = { cancel, ok }
        };

        var shell = new MauiDialogShell
        {
            Title = string.IsNullOrWhiteSpace(schema.Title) ? "选择输出目录" : schema.Title,
            ShowClose = true,
            SizeMode = MauiDialogSize.Form,
            Body = body,
            Footer = footer
        };

        var dialogPage = new ContentPage
        {
            Title = shell.Title,
            BackgroundColor = Colors.Transparent,
            Content = shell
        };
        Helpers.MauiThemeService.PrepareOverlayPage(dialogPage);

        void ApplyShellSize()
        {
            var w = dialogPage.Width > 1 ? dialogPage.Width : 480;
            var h = dialogPage.Height > 1 ? dialogPage.Height : 360;
            shell.ApplyHostSize(w, h);
        }
        dialogPage.SizeChanged += (_, _) => ApplyShellSize();
        dialogPage.Appearing += (_, _) => ApplyShellSize();

        async Task CloseAsync(string? result)
        {
            if (closing) return;
            closing = true;
            tcs.TrySetResult(result);
            try
            {
                if (host.Navigation.ModalStack.Contains(dialogPage))
                    await host.Navigation.PopModalAsync(animated: true).ConfigureAwait(true);
            }
            catch { /* ignore */ }
        }

        shell.CloseRequested += async (_, _) => await CloseAsync(null).ConfigureAwait(true);
        ok.Clicked += async (_, _) =>
        {
            var text = entry.Text?.Trim() ?? "";
            if (field.Required && string.IsNullOrWhiteSpace(text))
                return;
            await CloseAsync(text).ConfigureAwait(true);
        };
        cancel.Clicked += async (_, _) => await CloseAsync(null).ConfigureAwait(true);
        dialogPage.Disappearing += (_, _) =>
        {
            if (!closing)
                tcs.TrySetResult(null);
        };

        try
        {
            await host.Navigation.PushModalAsync(dialogPage, animated: true).ConfigureAwait(true);
            ApplyShellSize();
            return await tcs.Task.ConfigureAwait(true);
        }
        catch
        {
            var fallback = await host.DisplayPromptAsync(
                schema.Title,
                field.Label + (field.Required ? "" : "（可留空）"),
                "确定", "取消",
                initialValue: entry.Text ?? "").ConfigureAwait(true);
            return fallback;
        }
    }

    private static async Task<string?> PickFolderPathAsync(string title)
    {
#if WINDOWS
        try
        {
            return await MainThread.InvokeOnMainThreadAsync(() =>
                Win32PathPicker.PickFolder(title));
        }
        catch
        {
            return null;
        }
#else
        // 非 Windows：解密输出需要可写本地路径，系统选夹多为 SAF URI，暂仅支持手输
        await Task.CompletedTask;
        _ = title;
        return null;
#endif
    }
}
