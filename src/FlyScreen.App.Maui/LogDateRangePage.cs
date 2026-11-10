using System.Globalization;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.AppMaui;

/// <summary>
/// 日志页时间范围：半透明遮罩 + 居中卡片（对齐 ConfirmPage）。
/// 日期可选；时间可直接输入，也可点时钟打开 TimePicker 选择。
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

    private readonly Grid _root = new() { Padding = 24 };
    private readonly Border _card = new();
    private readonly Border _timePickOverlay = new() { IsVisible = false };
    private readonly TimePicker _overlayTimePicker = new() { HorizontalOptions = LayoutOptions.Center };
    private Entry? _timePickTarget;

    public ResultKind Result { get; private set; } = ResultKind.Cancel;
    public DateTime? From { get; private set; }
    public DateTime? To { get; private set; }

    public LogDateRangePage(DateTime? currentFrom, DateTime? currentTo)
    {
        Title = Loc.T("Log.DateTitle");
        NavigationPage.SetHasNavigationBar(this, false);
        BackgroundColor = Color.FromArgb("#80000000");

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
            Result = ResultKind.Apply;
            From = f;
            To = t;
            await Navigation.PopModalAsync().ConfigureAwait(true);
        };

        var clear = MakeNormalBtn(Loc.T("Log.DateClear"));
        clear.Clicked += async (_, _) =>
        {
            Result = ResultKind.Clear;
            From = null;
            To = null;
            await Navigation.PopModalAsync().ConfigureAwait(true);
        };

        var cancel = MakeNormalBtn(Loc.T("Dlg.Cancel"));
        cancel.Clicked += async (_, _) =>
        {
            Result = ResultKind.Cancel;
            await Navigation.PopModalAsync().ConfigureAwait(true);
        };

        if (Application.Current?.Resources.TryGetValue("Card", out var cardStyle) == true
            && cardStyle is Style style)
            _card.Style = style;
        _card.StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 };
        _card.StrokeThickness = 1;
        _card.MaximumWidthRequest = 440;
        _card.MinimumWidthRequest = 300;
        _card.VerticalOptions = LayoutOptions.Center;
        _card.HorizontalOptions = LayoutOptions.Center;
        _card.Padding = 20;
        _card.BackgroundColor = Color.FromArgb("#F7F8FA");
        _card.Content = new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                new Label
                {
                    Text = Loc.T("Log.DateTitle"),
                    FontSize = 15,
                    FontAttributes = FontAttributes.Bold
                },
                BuildRow(Loc.T("Log.DateFrom"), _fromDate, _fromTime),
                BuildRow(Loc.T("Log.DateTo"), _toDate, _toTime),
                _error,
                new HorizontalStackLayout
                {
                    Spacing = 8,
                    HorizontalOptions = LayoutOptions.End,
                    Children = { clear, cancel, ok }
                }
            }
        };

        BuildTimePickOverlay();

        _root.Children.Add(_card);
        _root.Children.Add(_timePickOverlay);
        Content = _root;

        // 点遮罩空白处等同取消
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (_timePickOverlay.IsVisible)
            {
                HideTimePickOverlay();
                return;
            }
            Result = ResultKind.Cancel;
            try { await Navigation.PopModalAsync().ConfigureAwait(true); }
            catch { /* ignore */ }
        };
        _root.GestureRecognizers.Add(tap);
        // 卡片本身不冒泡取消
        _card.GestureRecognizers.Add(new TapGestureRecognizer());
        _timePickOverlay.GestureRecognizers.Add(new TapGestureRecognizer());
    }

    private void BuildTimePickOverlay()
    {
        var title = new Label
        {
            Text = Loc.T("Log.Date"),
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            HorizontalOptions = LayoutOptions.Center
        };
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

        var inner = new Border
        {
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            StrokeThickness = 1,
            Padding = 16,
            BackgroundColor = Colors.White,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center,
            MinimumWidthRequest = 260,
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    title,
                    _overlayTimePicker,
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        HorizontalOptions = LayoutOptions.End,
                        Children = { dismiss, done }
                    }
                }
            }
        };

        _timePickOverlay.BackgroundColor = Color.FromArgb("#66000000");
        _timePickOverlay.Content = inner;
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
            Children = { time, Named(pickBtn, 1) }
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
                Named(date, 1),
                Named(timeBox, 2)
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
        => new()
        {
            Text = text,
            BackgroundColor = Color.FromArgb("#2563EB"),
            TextColor = Colors.White,
            FontAttributes = FontAttributes.Bold,
            HeightRequest = 36,
            MinimumWidthRequest = 88,
            CornerRadius = 8
        };

    private static Button MakeNormalBtn(string text)
        => new()
        {
            Text = text,
            HeightRequest = 36,
            MinimumWidthRequest = 88,
            CornerRadius = 8
        };

    private static T Named<T>(T view, int column) where T : BindableObject
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
