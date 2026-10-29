using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nte.Login;
using Nte.LoginHost;
using Nte.LoginHost.Accounts;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

// A deterministic protector verifies policy/transaction behavior. It does not
// claim that a synthetic key or callback validates real Passport hardware.
public sealed class GlobalHelloStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NteGlobalHello-" + Guid.NewGuid().ToString("N"));
    private readonly GestureProtector hello = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private GlobalAccountStore Store => new(root, new WindowsAccountKeyProtector(), hello);

    private static NteGlobalIdentity Identity(string token = "fixture-old-token")
    {
        using JsonDocument response = JsonDocument.Parse(JsonSerializer.Serialize(new
        { code = 0, result = new { uid = "fixture-account", token, loginType = 2 } }));
        return NteGlobalIdentity.FromService(response.RootElement, 2);
    }

    private async Task<LocalAccount> SaveAsync(bool protectedByHello)
    {
        using NteGlobalIdentity identity = Identity();
        return Assert.IsType<LocalAccount>(await Store.SaveAndDeliverAsync(identity, "Fixture label", true, 1,
            (_, _) => Task.CompletedTask, Token, protectedByHello));
    }

    [Fact]
    public async Task DefaultGlobalSaveAndRestoreNeverRequestsHello()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync(false);
        using var api = new Api();
        int delivered = 0;
        await Store.RestoreAndDeliverAsync(account.Id, api.Client, true, 1, (identity, _) =>
        {
            Assert.Equal("fixture-fresh-token", identity.Result.GetProperty("token").GetString());
            delivered++; return Task.CompletedTask;
        }, Token);
        Assert.False(Store.RequiresHello(account.Id));
        Assert.Equal(0, hello.UnwrapCalls);
        Assert.Equal(1, delivered);
        Assert.Equal(account.Id, (await Store.LastAsync(Token))!.Id);
    }

    [Fact]
    public async Task ProtectedRestoreRefreshesAndDeliversWithOneGesture()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync(true);
        hello.UnwrapCalls = 0;
        using var api = new Api();
        await Store.RestoreAndDeliverAsync(account.Id, api.Client, true, 1, (identity, _) =>
        {
            Assert.Equal("fixture-fresh-token", identity.Result.GetProperty("token").GetString());
            return Task.CompletedTask;
        }, Token, true);
        Assert.Equal(1, hello.UnwrapCalls);
        Assert.True(Store.RequiresHello(account.Id));
        Assert.Equal(account, Assert.Single(Store.List()));
        using var checkApi = new Api();
        using NteGlobalIdentity saved = await Store.RestoreAsync(account.Id, checkApi.Client, 1, Token);
        Assert.Equal("fixture-fresh-token", checkApi.Handler.ReceivedToken);
    }

    [Fact]
    public async Task ExistingGlobalAccountConvertsInBothDirectionsWithoutLosingItsSelection()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync(false);
        using var enable = new Api();
        await Store.RestoreAndDeliverAsync(account.Id, enable.Client, true, 1, (_, _) => Task.CompletedTask, Token, true);
        Assert.True(Store.RequiresHello(account.Id));
        Assert.Equal(1, hello.UnwrapCalls);
        hello.UnwrapCalls = 0;
        using var disable = new Api();
        await Store.RestoreAndDeliverAsync(account.Id, disable.Client, true, 1, (_, _) => Task.CompletedTask, Token, false);
        Assert.Equal(1, hello.UnwrapCalls);
        Assert.False(Store.RequiresHello(account.Id));
        Assert.Equal(account, Assert.Single(Store.List()));
        Assert.Equal(account.Id, (await Store.LastAsync(Token))!.Id);
        using var next = new Api();
        await Store.RestoreAndDeliverAsync(account.Id, next.Client, true, 1, (_, _) => Task.CompletedTask, Token, false);
        Assert.Equal(1, hello.UnwrapCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrRejectedGestureDoesNotContactTheAccountServiceOrRewriteTheRecord(bool reject)
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync(true);
        byte[] original = File.ReadAllBytes(Path.Combine(root, account.Id.ToString("N") + ".json"));
        hello.Cancel = !reject; hello.Reject = reject;
        using var api = new Api();
        int delivered = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => Store.RestoreAndDeliverAsync(account.Id, api.Client, true, 1,
            (_, _) => { delivered++; return Task.CompletedTask; }, Token, false));
        Assert.Equal(0, api.Handler.Requests);
        Assert.Equal(0, delivered);
        Assert.True(Store.RequiresHello(account.Id));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, account.Id.ToString("N") + ".json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSdkDeliveryRollsBackTheRefreshedTokenAndProtectionConversion(bool initiallyProtected)
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync(initiallyProtected);
        byte[] original = File.ReadAllBytes(Path.Combine(root, account.Id.ToString("N") + ".json"));
        using var api = new Api();
        await Assert.ThrowsAsync<IOException>(() => Store.RestoreAndDeliverAsync(account.Id, api.Client, true, 1,
            (_, _) => Task.FromException(new IOException("fixture delivery")), Token, !initiallyProtected));
        Assert.Equal(initiallyProtected, Store.RequiresHello(account.Id));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, account.Id.ToString("N") + ".json")));
        Assert.Equal(account.Id, (await Store.LastAsync(Token))!.Id);
    }

    [Fact]
    public async Task RestoringCannotReplaceTheSelectedIdentityAndDoesNotRememberAnUnselectedAccount()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync(true);
        byte[] original = File.ReadAllBytes(Path.Combine(root, account.Id.ToString("N") + ".json"));
        using var wrong = new Api("fixture-other-account");
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.RestoreAndDeliverAsync(account.Id, wrong.Client, true,
            1, (_, _) => Task.CompletedTask, Token, true));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(root, account.Id.ToString("N") + ".json")));
        using var correct = new Api();
        await Store.RestoreAndDeliverAsync(account.Id, correct.Client, false, 1, (_, _) => Task.CompletedTask, Token, true);
        Assert.Null(await Store.LastAsync(Token));
        Assert.Single(Store.List());
    }

    private sealed class GestureProtector : IAccountKeyProtector
    {
        public string Protection => AccountProtection.Hello;
        internal int UnwrapCalls;
        internal bool Cancel, Reject;
        public Task<byte[]> WrapAsync(Guid account, byte[] key, nint owner, CancellationToken token)
        {
            byte[] wrapped = new byte[256]; key.CopyTo(wrapped, 0); return Task.FromResult(wrapped);
        }
        public Task<byte[]> UnwrapAsync(Guid account, byte[] wrapped, nint owner, CancellationToken token)
        {
            UnwrapCalls++;
            if (Cancel) throw new OperationCanceledException();
            if (Reject) throw new CryptographicException("Fixture gesture rejected.");
            return Task.FromResult(wrapped.AsSpan(0, 32).ToArray());
        }
        public void Delete(Guid account) { }
    }

    private sealed class RestoreHandler(string uid) : HttpMessageHandler
    {
        internal int Requests;
        internal string? ReceivedToken;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            string data;
            if (request.RequestUri!.AbsolutePath == "/c/api/config/getV2") data = "{\"code\":0,\"result\":{}}";
            else
            {
                string fields = await request.Content!.ReadAsStringAsync(token);
                ReceivedToken = Uri.UnescapeDataString(fields.Split('&').Single(field => field.StartsWith("token=", StringComparison.Ordinal))[6..]);
                data = JsonSerializer.Serialize(new { code = 0, result = new { uid, token = "fixture-fresh-token", loginType = 2 } });
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(data) };
        }
    }

    private sealed class Api : IDisposable
    {
        private readonly NteGlobalApplicationConfiguration configuration;
        private readonly HttpClient http;
        internal RestoreHandler Handler { get; }
        internal NteGlobalAuthentication Client { get; }
        internal Api(string uid = "fixture-account")
        {
            configuration = new(Encoding.UTF8.GetBytes("test-application-key-material-length-40!!"), "com.hottagames.nte",
                new("https://mapi.pwsdk.com"), "global-sg");
            Handler = new(uid); http = new(Handler); Client = new(configuration, "1.4.10", http);
        }
        public void Dispose() { Client.Dispose(); http.Dispose(); Handler.Dispose(); configuration.Dispose(); }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
