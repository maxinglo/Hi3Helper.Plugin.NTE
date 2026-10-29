using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkLoginContractTests
{
    [Fact]
    public void ComparisonContainsNoAccountFieldsAndUsesOnlyPublicPcDifferences()
    {
        using JsonDocument config = JsonDocument.Parse("""
            {"init":{"channel":{"channelAppId":"12345","channelAppKey":"0123456789abcdef9876543210fedcba"}},
             "meta":{"version":"1.0.0","packageName":"fixture","channelId":9,"platform":4}}
            """);
        var previous = ProbeLoginContract.BuildParameters(config.RootElement, "1.4.17", false, new string('a', 32));
        var pc = ProbeLoginContract.BuildParameters(config.RootElement, "1.4.17", true, new string('a', 32));
        foreach (var values in new[] { previous, pc })
        {
            foreach (string name in new[] { "cellphone", "captcha", "password", "username", "uid", "token", "channelUid", "channelToken", "appKey" })
                Assert.False(values.ContainsKey(name));
            Assert.Equal(32, values["sign"].Length);
            Assert.Equal(13, values["t"].Length);
        }
        Assert.Equal("WindowsPc", pc["deviceType"]);
        Assert.Equal("1.4.17", pc["version"]);
        Assert.Equal("", previous["version"]);
        Assert.NotEqual(previous["sign"], pc["sign"]);
    }

    [Fact]
    public void ExplanationRedactsNumericAccountAndLongTokenEchoes()
    {
        string text = ProbeLoginContract.SafeExplanation("参数错误 13800000000 123456 token=abcdefghijklmnopqrstuvwxyz012345 https://example.invalid/?token=secret")!;
        Assert.DoesNotContain("13800000000", text);
        Assert.DoesNotContain("123456", text);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz012345", text);
        Assert.DoesNotContain("example.invalid", text);
        Assert.Contains("参数错误", text);
    }
}
