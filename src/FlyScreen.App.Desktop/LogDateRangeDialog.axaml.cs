using System.Globalization;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using FlyScreen.Helpers.Localization;

namespace FlyScreen.DesktopUi;

/// <summary>日志页：选择起始～结束时间（日期/时间均可选可输）。</summary>
public partial class LogDateRangeDialog : Window
{
    public enum ResultKind { Cancel, Apply, Clear }

    public ResultKind Result { get; private set; } = ResultKind.Cancel;
    public DateTime? From { get; private set; }
    public DateTime? To { get; private set; }

    private bool _timeComboReady;

    public LogDateRangeDialog()
    {
        InitializeComponent();
        FillTimeCombos(FromHour, FromMinute);
        FillTimeCombos(ToHour, ToMinute);
        _timeComboReady = true;
    }

    public static async Task<(ResultKind kind, DateTime? from, DateTime? to)> ShowAsync(
        Window? owner,
        DateTime? currentFrom,
        DateTime? currentTo)
    {
        var dlg = new LogDateRangeDialog();
        dlg.Title = Loc.T("Log.DateTitle");
        dlg.TitleText.Text = Loc.T("Log.DateTitle");
        dlg.FromLabel.Text = Loc.T("Log.DateFrom");
        dlg.ToLabel.Text = Loc.T("Log.DateTo");
        dlg.ClearBtn.Content = Loc.T("Log.DateClear");
        dlg.CancelBtn.Content = Loc.T("Dlg.Cancel");
        dlg.OkBtn.Content = Loc.T("Log.DateApply");

        var now = DateTime.Now;
        var from = currentFrom ?? now.Date;
        var to = currentTo ?? now;
        dlg.FromDate.SelectedDate = from.Date;
        dlg.ToDate.SelectedDate = to.Date;
        dlg.FromTime.Text = from.ToString("HH:mm");
        dlg.ToTime.Text = to.ToString("HH:mm");

        if (owner != null)
            await dlg.ShowDialog(owner).ConfigureAwait(true);
        else
            dlg.Show();

        return (dlg.Result, dlg.From, dlg.To);
    }

    private static void FillTimeCombos(ComboBox hour, ComboBox minute)
    {
        hour.Items.Clear();
        minute.Items.Clear();
        for (var h = 0; h < 24; h++)
            hour.Items.Add(h.ToString("00"));
        for (var m = 0; m < 60; m++)
            minute.Items.Add(m.ToString("00"));
    }

    private void FromTimePick_Click(object? sender, RoutedEventArgs e)
        => OpenTimePopup(FromTime, FromHour, FromMinute, FromTimePopup);

    private void ToTimePick_Click(object? sender, RoutedEventArgs e)
        => OpenTimePopup(ToTime, ToHour, ToMinute, ToTimePopup);

    private void OpenTimePopup(TextBox box, ComboBox hour, ComboBox minute, Popup popup)
    {
        var t = TryParseTime(box.Text, out var parsed) ? parsed : TimeSpan.Zero;
        _timeComboReady = false;
        hour.SelectedIndex = Math.Clamp(t.Hours, 0, 23);
        minute.SelectedIndex = Math.Clamp(t.Minutes, 0, 59);
        _timeComboReady = true;
        popup.PlacementTarget = box;
        popup.IsOpen = true;
    }

    private void FromTimeCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_timeComboReady || !FromTimePopup.IsOpen) return;
        ApplyComboToBox(FromHour, FromMinute, FromTime);
    }

    private void ToTimeCombo_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_timeComboReady || !ToTimePopup.IsOpen) return;
        ApplyComboToBox(ToHour, ToMinute, ToTime);
    }

    private static void ApplyComboToBox(ComboBox hour, ComboBox minute, TextBox box)
    {
        if (hour.SelectedItem is not string h || minute.SelectedItem is not string m) return;
        box.Text = $"{h}:{m}";
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryBuildRange(out var from, out var to, out var error))
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }

        Result = ResultKind.Apply;
        From = from;
        To = to;
        Close();
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        Result = ResultKind.Clear;
        From = null;
        To = null;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = ResultKind.Cancel;
        Close();
    }

    private bool TryBuildRange(out DateTime from, out DateTime to, out string error)
    {
        from = default;
        to = default;
        error = "";
        if (!TryGetDate(FromDate, out var fd) || !TryGetDate(ToDate, out var td))
        {
            error = Loc.T("Log.DateInvalid");
            return false;
        }

        if (!TryParseTime(FromTime.Text, out var ft) || !TryParseTime(ToTime.Text, out var tt))
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

    private static bool TryGetDate(CalendarDatePicker picker, out DateTime date)
    {
        if (picker.SelectedDate is DateTime selected)
        {
            date = selected.Date;
            return true;
        }

        var text = picker.Text?.Trim();
        if (!string.IsNullOrEmpty(text)
            && DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed.Date;
            return true;
        }

        date = default;
        return false;
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
