using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkSessionStoreTests
{
    [Fact]
    public void CurrentUserDpapiPersistsOnlyCiphertextAndCanRestoreTheChannel()
    {
        string directory = Path.Combine(Path.GetTempPath(), "nte-session-test-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "session.dpapi");
        using JsonDocument config = JsonDocument.Parse("""
            {"init":{"channel":{"channelAppId":"12345","channelAppKey":"0123456789abcdef9876543210fedcba"}},
             "meta":{"version":"1.0.0","packageName":"fixture","channelId":9,"platform":4}}
            """);
        try
        {
            ProbeSessionStore.Save(path, config.RootElement, "1.4.17", new string('a', 32), "fedcba9876543210"u8, "fixture-channel-uid"u8, "fixture-secret-channel-token"u8);
            byte[] encrypted = File.ReadAllBytes(path);
            Assert.True(encrypted.AsSpan().IndexOf("fixture-secret-channel-token"u8) < 0);
            Assert.True(encrypted.AsSpan().IndexOf("0123456789abcdef9876543210fedcba"u8) < 0);
            Assert.True(ProbeSessionStore.Read(path, root => root.GetProperty("installedVersion").GetString() == "1.4.17"));
            var restored = ProbeAuthenticationClient.RestoreSession(path, _ => { });
            using (restored.Client)
            using (restored.Channel)
            {
                Assert.True(restored.Channel.IdentityAvailable);
                Assert.Equal(ProbeIdentityStage.Channel, restored.Channel.Stage);
            }
            encrypted[^1] ^= 0x80; File.WriteAllBytes(path, encrypted);
            Assert.Throws<Win32Exception>(() => ProbeSessionStore.Read(path, _ => true));
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(directory)) Directory.Delete(directory); }
    }
}
