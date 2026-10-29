using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nte.Login;
using Nte.LoginHost;
using Nte.LoginHost.Accounts;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GlobalAccountStoreTests : IDisposable
{
    private readonly string
        root = Path.Combine(Path.GetTempPath(), "NteGlobalAccounts-" + Guid.NewGuid().ToString("N"));

    private CancellationToken Token => TestContext.Current.CancellationToken;
    private GlobalAccountStore Store() => new(root, new WindowsAccountKeyProtector());

    private static NteGlobalIdentity Identity(string uid, string token = "fixture-global-token", int provider = 2)
    {
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            code = 0,
            result = new
            {
                uid, token, loginType = 2, email = "never-save-email@example.invalid",
                thirdAuthToken = "never-save-provider-token"
            }
        }));
        return NteGlobalIdentity.FromService(document.RootElement, provider);
    }

    [Fact]
    public void SavedPayloadKeepsOnlyThePcIdentityAndHasAnExplicitGlobalScope()
    {
        using NteGlobalIdentity identity = Identity("fixture-user");
        byte[] payload = StoredGlobalIdentity.Encode(identity);
        try
        {
            string text = Encoding.UTF8.GetString(payload);
            Assert.DoesNotContain("never-save-email", text);
            Assert.DoesNotContain("never-save-provider-token", text);
            Assert.Throws<InvalidDataException>(() =>
                StoredGlobalIdentity.Decode(Encoding.UTF8.GetBytes(text.Replace("3000001", "1289"))));
            Assert.Throws<InvalidDataException>(() =>
                StoredGlobalIdentity.Decode(Encoding.UTF8.GetBytes(text.Replace("global-sg", "cn"))));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    [Fact]
    public async Task ReauthenticationUpdatesTheSameAccountAndPreservesItsRemark()
    {
        if (!OperatingSystem.IsWindows()) return;
        GlobalAccountStore store = Store();
        using NteGlobalIdentity original = Identity("fixture-user", "fixture-original-token");
        LocalAccount first = Assert.IsType<LocalAccount>(await store.SaveAndDeliverAsync(original, "Google", true, 0,
            (_, _) => Task.CompletedTask, Token));
        await store.RenameAsync(first.Id, "常用国际服", 0, Token);
        using NteGlobalIdentity refreshed = Identity("fixture-user", "fixture-refreshed-token", 1);
        LocalAccount second = Assert.IsType<LocalAccount>(await store.SaveAndDeliverAsync(refreshed, "Facebook", true,
            0,
            (_, _) => Task.CompletedTask, Token));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("常用国际服", Assert.Single(store.List()).Label);
        Assert.Equal(first.Id, (await Store().LastAsync(Token))!.Id);
        foreach (string path in Directory.GetFiles(root))
        {
            string disk = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain("fixture-user", disk);
            Assert.DoesNotContain("fixture-refreshed-token", disk);
        }
    }

    [Fact]
    public async Task FailedDeliveryRollsBackANewAccountAndCannotRememberIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        GlobalAccountStore store = Store();
        using NteGlobalIdentity identity = Identity("fixture-user");
        await Assert.ThrowsAsync<IOException>(() => store.SaveAndDeliverAsync(identity, "Google", true, 0,
            (_, _) => Task.FromException(new IOException("fixture")), Token));
        Assert.Empty(store.List());
        Assert.Null(await store.LastAsync(Token));
    }

    [Fact]
    public async Task ExplicitLogoutClearsAutomaticSelectionWithoutDeletingTheRecord()
    {
        if (!OperatingSystem.IsWindows()) return;
        GlobalAccountStore store = Store();
        using NteGlobalIdentity identity = Identity("fixture-user");
        await store.SaveAndDeliverAsync(identity, "Google", true, 0, (_, _) => Task.CompletedTask, Token);
        await store.ClearRememberedAsync(Token);
        Assert.Null(await Store().LastAsync(Token));
        Assert.Single(store.List());
    }

    [Fact]
    public async Task DeviceContextSurvivesHostRestartButRemainsProtectedOnDisk()
    {
        if (!OperatingSystem.IsWindows()) return;
        string device = await Store().DeviceIdAsync(Token);
        Assert.Equal(device, await Store().DeviceIdAsync(Token));
        Assert.DoesNotContain(device, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, "device.bin"))));
    }

    [Fact]
    public async Task RestoringARecordObtainsAFreshOfficialIdentityBeforeDelivery()
    {
        if (!OperatingSystem.IsWindows()) return;
        GlobalAccountStore store = Store();
        using NteGlobalIdentity original = Identity("fixture-user", "fixture-old-token");
        LocalAccount saved = Assert.IsType<LocalAccount>(await store.SaveAndDeliverAsync(original, "Google", true, 0,
            (_, _) => Task.CompletedTask, Token));
        using var configuration = new NteGlobalApplicationConfiguration(
            Encoding.UTF8.GetBytes("test-application-key-material-length-40!!"),
            "com.hottagames.nte", new("https://mapi.pwsdk.com"), "global-sg");
        using var handler = new RestoreHandler();
        using var http = new HttpClient(handler);
        using var api =
            new NteGlobalAuthentication(configuration, "1.4.10", http, deviceId: await store.DeviceIdAsync(Token));
        using NteGlobalIdentity restored = await Store().RestoreAsync(saved.Id, api, 0, Token);
        Assert.Equal("fixture-new-token", restored.Result.GetProperty("token").GetString());
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task RemovingOneAccountKeepsTheOtherRecordAndDoesNotAutoSelectIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        GlobalAccountStore store = Store();
        using NteGlobalIdentity first = Identity("fixture-first");
        using NteGlobalIdentity second = Identity("fixture-second");
        LocalAccount a =
            Assert.IsType<LocalAccount>(await store.SaveAndDeliverAsync(first, "Google", true, 0,
                (_, _) => Task.CompletedTask, Token));
        LocalAccount b =
            Assert.IsType<LocalAccount>(await store.SaveAndDeliverAsync(second, "Google", true, 0,
                (_, _) => Task.CompletedTask, Token));
        await store.RemoveAsync(b.Id, Token);
        Assert.Equal(a.Id, Assert.Single(store.List()).Id);
        Assert.Null(await store.LastAsync(Token));
    }

    [Fact]
    public async Task UncheckedRememberDoesNotCreateANewAccountRecord()
    {
        if (!OperatingSystem.IsWindows()) return;
        GlobalAccountStore store = Store();
        using NteGlobalIdentity identity = Identity("fixture-user");
        int delivered = 0;
        Assert.Null(await store.SaveAndDeliverAsync(identity, "Google", false, 0,
            (_, _) =>
            {
                delivered++;
                return Task.CompletedTask;
            }, Token));
        Assert.Equal(1, delivered);
        Assert.Empty(store.List());
        Assert.Null(await store.LastAsync(Token));
    }

    [Fact]
    public async Task ARestoredDifferentUserCannotReplaceTheSelectedSavedAccount()
    {
        if (!OperatingSystem.IsWindows()) return;
        GlobalAccountStore store = Store();
        using NteGlobalIdentity original = Identity("fixture-user");
        LocalAccount saved = Assert.IsType<LocalAccount>(
            await store.SaveAndDeliverAsync(original, "Google", true, 0, (_, _) => Task.CompletedTask, Token));
        using var configuration = new NteGlobalApplicationConfiguration(
            Encoding.UTF8.GetBytes("test-application-key-material-length-40!!"),
            "com.hottagames.nte", new("https://mapi.pwsdk.com"), "global-sg");
        using var handler = new RestoreHandler("fixture-other-user");
        using var http = new HttpClient(handler);
        using var api = new NteGlobalAuthentication(configuration, "1.4.10", http);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreAsync(saved.Id, api, 0, Token));
        Assert.Equal(saved.Id, Assert.Single(store.List()).Id);
    }

    [Fact]
    public void AccessibleAccountTextContainsOnlyTheUserFacingLabel()
    {
        LocalAccount account = new(Guid.NewGuid(), "Fixture label", DateTimeOffset.UtcNow, null,
            "fixture-private-index");
        Assert.Equal("Fixture label", account.ToString());
    }

    private sealed class RestoreHandler(string uid = "fixture-user") : HttpMessageHandler
    {
        internal int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            string result = request.RequestUri!.AbsolutePath == "/c/api/config/getV2"
                ? "{\"code\":0,\"result\":{}}"
                : JsonSerializer.Serialize(new
                    { code = 0, result = new { uid, token = "fixture-new-token", loginType = 2 } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(result) });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
