using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nte.Login;
using Nte.LoginHost;
using Nte.LoginHost.Accounts;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class AccountDisplayStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "NteDisplay-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsAccountKeyProtector windows = new();
    private readonly GestureProtector hello = new();
    private CancellationToken Token => TestContext.Current.CancellationToken;

    private LocalAccountStore Vault => new(root, windows, helloProtector: hello,
        subjectKey: StoredGlobalIdentity.SubjectKey, credentialLimit: 12288);

    private AccountDisplayStore Displays => new(root, windows);

    private static readonly AccountDisplayProfile Profile = new(2, "person.one@example.com", "Fixture name",
        InitialLabel: "Original label");

    private static byte[] Credential(string uid = "fixture-display-user", string token = "fixture-old-token")
    {
        using JsonDocument response =
            JsonDocument.Parse(JsonSerializer.Serialize(new { code = 0, result = new { uid, token } }));
        using NteGlobalIdentity identity = NteGlobalIdentity.FromService(response.RootElement, 2);
        return StoredGlobalIdentity.Encode(identity);
    }

    private async Task<LocalAccount> SaveAsync(string uid = "fixture-display-user", bool useHello = true)
    {
        byte[] credential = Credential(uid);
        try
        {
            PendingAccountSave pending =
                await Vault.PrepareChannelSaveAsync("Original label", credential, 1, Token, useHello);
            pending = await Displays.AttachAsync(pending, Profile, Token);
            return await AccountSaveTransaction.SaveAndDeliverAsync(() => Task.FromResult(pending),
                () => Task.CompletedTask);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }

    [Fact]
    public async Task ListingProtectedAccountsReadsEncryptedDisplayDataWithoutAHelloGesture()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync();
        int gestures = hello.UnwrapCalls;
        hello.Reject = true;
        Assert.Equal(account, Assert.Single(Vault.List()));
        Assert.True(Vault.RequiresHello(account.Id));
        AccountDisplayProfile profile = Assert.IsType<AccountDisplayProfile>(await Displays.ReadAsync(account, Token));
        Assert.Equal(Profile.Email, profile.Email);
        Assert.Equal(Profile.Name, profile.Name);
        Assert.Equal(gestures, hello.UnwrapCalls);
        foreach (string path in Directory.GetFiles(root))
            Assert.DoesNotContain(Profile.Email!, Encoding.UTF8.GetString(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task RenamingAndARefreshWithoutProfileKeepTheEmailNameAndOriginalLabel()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync();
        LocalAccount renamed = await Vault.RenameAsync(account.Id, "My own label", 1, Token);
        PendingAccountSave refresh = await Vault.PrepareRefreshAsync(account.Id, 1,
            (_, _) => Task.FromResult(Credential(token: "fixture-fresh-token")), Token, true);
        refresh = await Displays.AttachAsync(refresh, new(2), Token);
        await AccountSaveTransaction.SaveAndDeliverAsync(() => Task.FromResult(refresh), () => Task.CompletedTask);
        AccountDisplayProfile profile = Assert.IsType<AccountDisplayProfile>(await Displays.ReadAsync(renamed, Token));
        Assert.Equal(Profile.Email, profile.Email);
        Assert.Equal(Profile.Name, profile.Name);
        Assert.Equal("Original label", profile.InitialLabel);
        Assert.Equal("My own label", Assert.Single(Vault.List()).Label);
    }

    [Fact]
    public async Task FailedGameDeliveryRollsBackCredentialsAndDisplayTogether()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync();
        string record = Path.Combine(root, account.Id.ToString("N") + ".json"),
            display = Path.ChangeExtension(record, ".display");
        byte[] oldRecord = File.ReadAllBytes(record), oldDisplay = File.ReadAllBytes(display);
        PendingAccountSave change = await Vault.PrepareRefreshAsync(account.Id, 1,
            (_, _) => Task.FromResult(Credential(token: "fixture-new-token")), Token, false);
        change = await Displays.AttachAsync(change, new(2, "changed@example.com", "Changed name"), Token);
        await Assert.ThrowsAsync<IOException>(() =>
            AccountSaveTransaction.SaveAndDeliverAsync(() => Task.FromResult(change),
                () => Task.FromException(new IOException("fixture delivery"))));
        Assert.True(Vault.RequiresHello(account.Id));
        Assert.Equal(oldRecord, File.ReadAllBytes(record));
        Assert.Equal(oldDisplay, File.ReadAllBytes(display));
    }

    [Fact]
    public async Task CancelledDisplayPreparationRestoresTheExistingCredentialAndLeavesNoPartialFiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync();
        string record = Path.Combine(root, account.Id.ToString("N") + ".json");
        byte[] original = File.ReadAllBytes(record);
        PendingAccountSave change = await Vault.PrepareRefreshAsync(account.Id, 1,
            (_, _) => Task.FromResult(Credential(token: "fixture-new-token")), Token, false);
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Displays.AttachAsync(change, Profile, cancelled.Token));
        Assert.Equal(original, File.ReadAllBytes(record));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        Assert.Equal(Profile.Email, (await Displays.ReadAsync(account, Token))!.Email);
    }

    [Fact]
    public async Task DisplayDataCannotBeCopiedToAnotherAccountAndMissingDetailsDoNotDeleteCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount first = await SaveAsync(), second = await SaveAsync("fixture-other-user");
        File.Copy(Path.Combine(root, first.Id.ToString("N") + ".display"),
            Path.Combine(root, second.Id.ToString("N") + ".display"), true);
        await Assert.ThrowsAsync<CryptographicException>(() => Displays.ReadAsync(second, Token));
        Assert.Equal(2, Vault.List().Count);
        Displays.Remove(second.Id);
        Assert.Null(await Displays.ReadAsync(second, Token));
        Assert.Equal(2, Vault.List().Count);
    }

    [Fact]
    public async Task ChangingProviderDoesNotCarryAnEmailFromThePreviousProvider()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccount account = await SaveAsync();
        PendingAccountSave change =
            await Vault.PrepareRefreshAsync(account.Id, 1, (_, _) => Task.FromResult(Credential()), Token, true);
        change = await Displays.AttachAsync(change, new(1, Name: "Facebook name"), Token);
        change.Commit();
        AccountDisplayProfile profile = Assert.IsType<AccountDisplayProfile>(await Displays.ReadAsync(account, Token));
        Assert.Equal(1, profile.Provider);
        Assert.Null(profile.Email);
        Assert.Equal("Facebook name", profile.Name);
    }

    private sealed class GestureProtector : IAccountKeyProtector
    {
        public string Protection => AccountProtection.Hello;
        internal int UnwrapCalls;
        internal bool Reject;

        public Task<byte[]> WrapAsync(Guid account, byte[] key, nint owner, CancellationToken token)
        {
            byte[] value = new byte[256];
            key.CopyTo(value, 0);
            return Task.FromResult(value);
        }

        public Task<byte[]> UnwrapAsync(Guid account, byte[] wrapped, nint owner, CancellationToken token)
        {
            UnwrapCalls++;
            if (Reject) throw new CryptographicException("Fixture gesture unavailable.");
            return Task.FromResult(wrapped.AsSpan(0, 32).ToArray());
        }

        public void Delete(Guid account)
        {
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
