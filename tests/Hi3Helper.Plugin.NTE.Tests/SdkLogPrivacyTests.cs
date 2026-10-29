using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkLogPrivacyTests
{
    [Theory]
    [InlineData("00000000", false)]
    [InlineData("ABCDEF10", true)]
    public void LoginCallbackDiagnosticsKeepOnlyPresenceAndCompletion(string pointer, bool expected)
    {
        string report = JsonSerializer.Serialize(ProbeSdkLog.Summarize(
            $"loginSuccess Begin, {pointer}, uid:private-account-uid\nloginSuccess End\nprivate-token=fixture-token"));
        using JsonDocument json = JsonDocument.Parse(report);
        Assert.Equal(2, json.RootElement.GetArrayLength());
        Assert.Equal(expected, json.RootElement[0].GetProperty("callbackRegistered").GetBoolean());
        Assert.Equal("login-result-handled", json.RootElement[1].GetProperty("category").GetString());
        foreach (string secret in new[] { pointer, "private-account-uid", "fixture-token" }) Assert.DoesNotContain(secret, report);
    }
}
