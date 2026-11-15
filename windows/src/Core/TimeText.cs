using System.Globalization;

namespace SuperiorIptv;

/// <summary>How clock times and dates are written everywhere in the app (12-hour by default).</summary>
public static class TimeText
{
    /// <summary>False: 4:36 PM. True: 16:36. Set from Settings when the app starts.</summary>
    public static bool TwentyFourHour;

    // One fixed culture so that "PM" and day names never depend on the Windows region settings.
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <summary>4:36 PM</summary>
    public static string Time(DateTime local) => local.ToString(TwentyFourHour ? "HH:mm" : "h:mm tt", English);

    /// <summary>Sat 4:36 PM</summary>
    public static string DayTime(DateTime local) => local.ToString("ddd ", English) + Time(local);

    /// <summary>Sat, Nov 14</summary>
    public static string ShortDate(DateTime local) => local.ToString("ddd, MMM d", English);

    /// <summary>Saturday, November 14</summary>
    public static string LongDate(DateTime local) => local.ToString("dddd, MMMM d", English);

    /// <summary>Sat, Nov 14, 4:36 PM</summary>
    public static string DateAndTime(DateTime local) => ShortDate(local) + ", " + Time(local);
}
