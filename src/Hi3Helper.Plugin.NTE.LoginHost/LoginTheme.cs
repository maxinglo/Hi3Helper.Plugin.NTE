namespace Nte.LoginHost;

internal static class LoginTheme
{
    internal static string ForLocalTime(DateTime localTime) => localTime.Hour switch
    {
        >= 6 and < 17 => "day",
        >= 17 and < 19 => "sunset",
        _ => "night"
    };
}
