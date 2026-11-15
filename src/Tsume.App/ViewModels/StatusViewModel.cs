using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Tsume.App.Language;
using Tsume.App.Mvvm;
using Tsume.App.Platform;

namespace Tsume.App.ViewModels;

/// <summary>The header: what time it is and how the battery is doing.</summary>
public sealed class StatusViewModel : Observable
{
    /// <summary>Hours and minutes, in Windows' 12- or 24-hour style, e.g. "3:45" or "15:45".</summary>
    public string Time { get; private set => Set(ref field, value); } = "";

    /// <summary>"AM" / "PM" (in the user's language) on a 12-hour clock; empty on a 24-hour one.</summary>
    public string Meridiem { get; private set => Set(ref field, value); } = "";

    public string Date { get; private set => Set(ref field, value); } = "";

    // The user's regional settings, re-read when they change them in Windows Settings.
    private CultureInfo? _culture;

    public StatusViewModel()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.Locale) _culture = null;
        };
    }

    private CultureInfo Culture()
    {
        if (_culture is { } culture) return culture;
        CultureInfo.CurrentCulture.ClearCachedData();
        return _culture = new CultureInfo(CultureInfo.CurrentCulture.Name, useUserOverride: true);
    }

    public int BatteryPercent { get; private set => Set(ref field, value); }
    public bool IsPluggedIn { get; private set => Set(ref field, value); }
    public bool IsBatteryLow { get; private set => Set(ref field, value); }

    public void Refresh()
    {
        var now = DateTime.Now;
        var culture = Culture();

        // Windows' short-time pattern carries the 12/24-hour choice: "h:mm tt" or "HH:mm".
        // The designator is pulled out so the header can draw it smaller beside the digits.
        var pattern = culture.DateTimeFormat.ShortTimePattern;
        var twelveHour = pattern.Contains('t');
        var digits = Regex.Replace(pattern, @"\s*t+\s*", "").Trim();
        if (digits.Length == 0) digits = twelveHour ? "h:mm" : "HH:mm";

        Time = now.ToString(digits, culture);
        Meridiem = twelveHour ? now.ToString("tt", culture) : "";
        Date = now.ToString(Strings.HeaderDate, culture);

        if (Battery.Read() is not { } battery) return;

        BatteryPercent = battery.Percent;
        IsPluggedIn = battery.PluggedIn;
        IsBatteryLow = battery.Percent <= 20 && !battery.PluggedIn;
    }
}
