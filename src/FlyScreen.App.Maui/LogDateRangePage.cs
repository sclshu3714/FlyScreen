using System.Globalization;
using FlyScreen.Ui.Maui;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.AppMaui;

/// <summary>
/// 日志页时间范围：统一 MauiDialogShell；日期可选；时间可输入或点时钟用 TimePicker。
/// </summary>
public sealed class LogDateRangePage : ContentPage
{
    public enum ResultKind { Cancel, Apply, Clear }

    private readonly DatePicker _fromDate = new() { HorizontalOptions = LayoutOptions.Fill };
    private readonly Entry _fromTime = CreateTimeEntry();
    private readonly DatePicker _toDate = new() { HorizontalOptions = LayoutOptions.Fill };
    private readonly Entry _toTime = CreateTimeEntry();
    private readonly Label _error = new()
    {
        TextColor = Color.FromArgb("#DC2626"),
        FontSize = 12,
        LineBreakMode = LineBreakMode.WordWrap,
        IsVisible = false
    };

    private readonly MauiDialogShell _shell = new()
    {
        SizeMode = MauiDialogSize.Form,
        ShowClose = true,
        DismissOnScrim = true
    };
    private readonly Border _timePickOverlay = new() { IsVisible = false };
    private readonly TimePicker _overlayTimePicker = new() { HorizontalOptions = LayoutOptions.Center };
    private Entry? _timePickTarget;

    public ResultKind Result { get; private set; } = ResultKind.Cancel;
    public DateTime? From { get; private set; }
    public DateTime? To { get; private set; }

    public LogDateRangePage(DateTime? currentFrom, DateTime? currentTo)
    {
        Title = Loc.T("Log.DateTitle");
        Helpers.MauiThemeService.PrepareOverlayPage(this);
        _shell.Title = Title;
        _shell.CloseRequested += async (_, _) => await CloseAsync(ResultKind.Cancel);

        var now = DateTime.Now;
        var from = currentFrom ?? now.Date;
        var to = currentTo ?? now;
        _fromDate.Date = from;
        _toDate.Date = to;
        _fromTime.Text = from.ToString("HH:mm");
        _toTime.Text = to.ToString("HH:mm");

        var ok = MakePrimaryBtn(Loc.T("Log.DateApply"));
        ok.Clicked += async (_, _) =>
        {
            if (!TryBuildRange(out var f, out var t, out var error))
            {
                _error.Text = error;
                _error.IsVisible = true;
                return;
            }
            From = f;
            To = t;
            await CloseAsync(ResultKind.Apply);
        };

        var clear = MakeNormalBtn(Loc.T("Log.DateClear"));
        clear.Clicked += async (_, _) =>
        {
            From = null;
            To = null;
            await CloseAsync(ResultKind.Clear);
        };

        var cancel = MakeNormalBtn(Loc.T("Dlg.Cancel"));
        cancel.Clicked += async (_, _) => await CloseAsync(ResultKind.Cancel);

        _shell.Body = new Border
        {
            Style = TryGetStyle("DlgCard"),
            Margin = new Thickness(20, 16, 20, 8),
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    BuildRow(Loc.T("Log.DateFrom"), _fromDate, _fromTime),
                    BuildRow(Loc.T("Log.DateTo"), _toDate, _toTime),
                    _error
                }
            }
        };
        _shell.Footer = new HorizontalStackLayout
        {
            Spacing = 0,
            HorizontalOptions = LayoutOptions.End,
            Children = { clear, cancel, ok }
        };

        BuildTimePickOverlay();

        Content = new Grid
        {
            Children = { _shell, _timePickOverlay }
        };
    }

    private async Task CloseAsync(ResultKind kind)
    {
        if (_timePickOverlay.IsVisible)
        {
            HideTimePickOverlay();
            if (kind == ResultKind.Cancel) return;
        }
        Result = kind;
        try { await Navigation.PopModalAsync().ConfigureAwait(true); }
        catch { /* ignore */ }
    }

    private void BuildTimePickOverlay()
    {
        var done = MakePrimaryBtn(Loc.T("Log.DateApply"));
        done.Clicked += (_, _) =>
        {
            if (_timePickTarget != null)
            {
                var picked = _overlayTimePicker.Time ?? TimeSpan.Zero;
                _timePickTarget.Text = FormatTime(picked);
            }
            HideTimePickOverlay();
        };
        var dismiss = MakeNormalBtn(Loc.T("Dlg.Cancel"));
        dismiss.Clicked += (_, _) => HideTimePickOverlay();

        var nested = new MauiDialogShell
        {
            Title = Loc.T("Log.Date"),
            ShowClose = false,
            SizeMode = MauiDialogSize.Form,
            Body = new Border
            {
                Style = TryGetStyle("DlgCard"),
                Margin = new Thickness(16, 12),
                Content = _overlayTimePicker
            },
            Footer = new HorizontalStackLayout
            {
                Spacing = 0,
                HorizontalOptions = LayoutOptions.End,
                Children = { dismiss, done }
            }
        };
        nested.Card.MinimumWidthRequest = 260;
        nested.Card.MaximumWidthRequest = 320;

        _timePickOverlay.BackgroundColor = Colors.Transparent;
        _timePickOverlay.Content = nested;
        _timePickOverlay.GestureRecognizers.Add(new TapGestureRecognizer());
    }

    private View BuildRow(string label, DatePicker date, Entry time)
    {
        var pickBtn = new Button
        {
            Text = "▾",
            WidthRequest = 34,
            HeightRequest = 34,
            Padding = 0,
            BackgroundColor = Colors.Transparent,
            BorderWidth = 0,
            FontSize = 14,
            TextColor = Color.FromArgb("#6B7280")
        };
        pickBtn.Clicked += (_, _) => ShowTimePickOverlay(time);

        var timeBox = new Grid
        {
            WidthRequest = 118,
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(GridLength.Star),
                new(new GridLength(34))
            },
            Children = { time, NamedCol(pickBtn, 1) }
        };

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitionCollection
            {
                new(new GridLength(48)),
                new(GridLength.Star),
                new(new GridLength(118))
            },
            ColumnSpacing = 8,
            Children =
            {
                new Label
                {
                    Text = label,
                    TextColor = Color.FromArgb("#6B7280"),
                    FontSize = 13,
                    VerticalOptions = LayoutOptions.Center
                },
                NamedCol(date, 1),
                NamedCol(timeBox, 2)
            }
        };
    }

    private void ShowTimePickOverlay(Entry target)
    {
        _timePickTarget = target;
        _overlayTimePicker.Time = TryParseTime(target.Text, out var t) ? t : TimeSpan.Zero;
        _timePickOverlay.IsVisible = true;
    }

    private void HideTimePickOverlay()
    {
        _timePickOverlay.IsVisible = false;
        _timePickTarget = null;
    }

    private static Entry CreateTimeEntry()
        => new()
        {
            Placeholder = "HH:mm",
            Keyboard = Keyboard.Plain,
            MaxLength = 5,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalOptions = LayoutOptions.Center
        };

    private static Button MakePrimaryBtn(string text)
    {
        var btn = new Button { Text = text };
        TryApplyStyle(btn, "DlgPrimaryBtn");
        return btn;
    }

    private static Button MakeNormalBtn(string text)
    {
        var btn = new Button { Text = text };
        TryApplyStyle(btn, "DlgNormalBtn");
        return btn;
    }

    private static Style? TryGetStyle(string key)
        => Application.Current?.Resources.TryGetValue(key, out var v) == true && v is Style s ? s : null;

    private static void TryApplyStyle(BindableObject target, string key)
    {
        var style = TryGetStyle(key);
        if (style is null) return;
        if (target is VisualElement ve)
            ve.Style = style;
    }

    private static T NamedCol<T>(T view, int column) where T : BindableObject
    {
        Grid.SetColumn(view, column);
        return view;
    }

    private static string FormatTime(TimeSpan t)
        => $"{t.Hours:00}:{t.Minutes:00}";

    private bool TryBuildRange(out DateTime from, out DateTime to, out string error)
    {
        from = default;
        to = default;
        error = "";
        if (_fromDate.Date is not DateTime fd || _toDate.Date is not DateTime td)
        {
            error = Loc.T("Log.DateInvalid");
            return false;
        }

        if (!TryParseTime(_fromTime.Text, out var ft) || !TryParseTime(_toTime.Text, out var tt))
        {
            error = Loc.T("Log.DateInvalid");
            return false;
        }

        from = fd.Date.Add(ft);
        to = td.Date.Add(tt);
        if (to < from)
        {
            error = Loc.T("Log.DateInvalid");
            return false;
        }
        return true;
    }

    private static bool TryParseTime(string? text, out TimeSpan time)
    {
        text = (text ?? "").Trim();
        if (TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out time))
            return true;
        if (TimeSpan.TryParseExact(text, @"h\:mm", CultureInfo.InvariantCulture, out time))
            return true;
        if (DateTime.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            || DateTime.TryParseExact(text, "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
        {
            time = dt.TimeOfDay;
            return true;
        }

        time = default;
        return false;
    }
}
