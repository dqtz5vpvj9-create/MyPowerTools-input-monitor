using System.Globalization;

namespace InputMonitor.Surface.ViewModels;

/// <summary>Calendar periods used by history navigation and by the statistics request's day anchor.</summary>
public static class HistoryPeriod
{
    public static DateTime Start(DateTime date, string grain) => grain switch
    {
        "month" => new(date.Year, date.Month, 1),
        "quarter" => new(date.Year, ((date.Month - 1) / 3) * 3 + 1, 1),
        "year" => new(date.Year, 1, 1),
        _ => date.Date
    };

    public static DateTime Shift(DateTime date, string grain, int delta) => grain switch
    {
        "month" => Start(date, grain).AddMonths(delta),
        "quarter" => Start(date, grain).AddMonths(3 * delta),
        "year" => Start(date, grain).AddYears(delta),
        _ => date.Date.AddDays(delta)
    };

    public static string Label(DateTime date, string grain) => grain switch
    {
        "month" => date.ToString("yyyy年M月", CultureInfo.CurrentCulture),
        "quarter" => $"{date.Year}年第{(date.Month - 1) / 3 + 1}季度",
        "year" => date.ToString("yyyy年", CultureInfo.CurrentCulture),
        _ => date.ToString("yyyy/M/d", CultureInfo.CurrentCulture)
    };
}
