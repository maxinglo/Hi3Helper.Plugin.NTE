using System.Text.Json;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GlobalDeviceInfoTests
{
    private const string Device = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void AllSdkFieldsUseTheSameSessionAndKeepMissingMeasurementsExplicit()
    {
        NteGlobalDeviceInfo device = new(Device, new Dictionary<string, string>
        { ["cpu"] = "Fixture CPU", ["res"] = "1920x1080" }, DateTimeOffset.FromUnixTimeMilliseconds(1234567890000));
        using JsonDocument document = JsonDocument.Parse(device.Json);
        JsonElement root = document.RootElement;
        Assert.Equal(67, root.EnumerateObject().Count());
        foreach (string key in new[] { "did", "udid", "ndid" }) Assert.Equal(Device, root.GetProperty(key).GetString());
        Assert.Equal("Fixture CPU", root.GetProperty("cpu").GetString());
        Assert.Equal("1920x1080", root.GetProperty("res").GetString());
        Assert.Equal("NULL", root.GetProperty("gyro").GetString());
        Assert.Equal("4.186.0", root.GetProperty("sdk").GetString());
        Assert.Equal(1234567890000, root.GetProperty("dt").GetInt64());
        Assert.DoesNotContain(root.EnumerateObject(), item => item.Name is "code" or "result" or "token");
    }

    [Theory]
    [InlineData("token", "fixture")]
    [InlineData("udid", "different-session")]
    [InlineData("sdk", "unverified")]
    [InlineData("cpu", "embedded\0value")]
    public void UnrelatedFieldsAndIdentityOverridesCannotEnterDeviceInfo(string name, string value) =>
        Assert.Throws<ArgumentException>(() => new NteGlobalDeviceInfo(Device, new Dictionary<string, string> { [name] = value }, DateTimeOffset.UnixEpoch));
}
