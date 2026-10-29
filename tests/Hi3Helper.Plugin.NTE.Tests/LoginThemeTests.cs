using Nte.LoginHost;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LoginThemeTests
{
    [Theory]
    [InlineData(0, 0, 0, "night")]
    [InlineData(5, 59, 59, "night")]
    [InlineData(6, 0, 0, "day")]
    [InlineData(12, 0, 0, "day")]
    [InlineData(16, 59, 59, "day")]
    [InlineData(17, 0, 0, "sunset")]
    [InlineData(18, 59, 59, "sunset")]
    [InlineData(19, 0, 0, "night")]
    [InlineData(23, 59, 59, "night")]
    public void ThemeFollowsLocalTimeAtEveryBoundary(int hour, int minute, int second, string expected)
    {
        DateTime time = new(2026, 10, 27, hour, minute, second, DateTimeKind.Local);
        Assert.Equal(expected, LoginTheme.ForLocalTime(time));
    }
}
