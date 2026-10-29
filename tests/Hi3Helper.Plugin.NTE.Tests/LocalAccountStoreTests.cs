using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nte.LoginHost.Accounts;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LocalAccountStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "NteAccountTest-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProtector protector = new();
    private readonly FakeProtector hello = new(AccountProtection.Hello);
    private readonly TestClock clock = new();
    private LocalAccountStore Store => new(directory, protector, clock, hello);
    private static readonly byte[] Credential = "fixture-credential-not-an-account"u8.ToArray();

    private static byte[] ChannelCredential(string uid, string secret, char device = 'a')
    {
        byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { code = 0, result = new { userId = uid, token = secret } });
        using ProbeIdentityEnvelope channel = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Channel, 200, json);
        return StoredChannelIdentity.Encode(channel, new string(device, 32), 4, 9);
    }

    [Fact]
    public async Task ReauthenticationUpdatesTheExistingAccountAndPreservesItsRemark()
    {
        byte[] first = ChannelCredential("fixture-user", "first-token");
        LocalAccount saved = await AccountSaveTransaction.SaveAndDeliverAsync(
            () => Store.PrepareChannelSaveAsync("138****5678", first, 1, TestContext.Current.CancellationToken), () => Task.CompletedTask);
        await Store.RenameAsync(saved.Id, "我的主号", 1, TestContext.Current.CancellationToken);
        byte[] renewed = ChannelCredential("fixture-user", "new-token", 'b');
        LocalAccount updated = await AccountSaveTransaction.SaveAndDeliverAsync(
            () => Store.PrepareChannelSaveAsync("138****5678", renewed, 1, TestContext.Current.CancellationToken), () => Task.CompletedTask);
        Assert.Equal(saved.Id, updated.Id); Assert.Equal("我的主号", updated.Label); Assert.Equal(saved.SavedUtc, updated.SavedUtc);
        Assert.Single(Store.List()); Assert.Equal(renewed, await Store.UnlockAsync(saved.Id, 1, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("fixture-user", File.ReadAllText(Path.Combine(directory, saved.Id.ToString("N") + ".json")));
    }

    [Fact]
    public async Task LegacyDuplicatesMergeOnlyAfterDeliveryAndKeepTheCustomRemark()
    {
        LocalAccount first = await Store.SaveAsync("138****5678", ChannelCredential("same-user", "old-token"), 1, TestContext.Current.CancellationToken);
        LocalAccount named = await Store.SaveAsync("常用角色", ChannelCredential("same-user", "newer-token"), 1, TestContext.Current.CancellationToken);
        byte[] fresh = ChannelCredential("same-user", "latest-token");
        LocalAccount result = await AccountSaveTransaction.SaveAndDeliverAsync(
            () => Store.PrepareChannelSaveAsync("138****5678", fresh, 1, TestContext.Current.CancellationToken),
            () => { Assert.Equal(2, Store.List().Count); return Task.CompletedTask; });
        Assert.Equal(named.Id, result.Id); Assert.Equal("常用角色", result.Label); Assert.NotEqual(first.Id, result.Id);
        Assert.Single(Store.List()); Assert.Equal(fresh, await Store.UnlockAsync(named.Id, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedReauthenticationRestoresTheOldCredentialAndRemark()
    {
        byte[] original = ChannelCredential("same-user", "old-token");
        LocalAccount saved = await Store.SaveAsync("我的备注", original, 1, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AccountSaveTransaction.SaveAndDeliverAsync(
            () => Store.PrepareChannelSaveAsync("138****5678", ChannelCredential("same-user", "new-token"), 1, TestContext.Current.CancellationToken),
            () => Task.FromException(new InvalidOperationException("fixture delivery failed"))));
        Assert.Equal("我的备注", Assert.Single(Store.List()).Label);
        Assert.Equal(original, await Store.UnlockAsync(saved.Id, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MatchingMaskedPhoneDoesNotMergeDifferentIdentities()
    {
        foreach (string uid in new[] { "first-user", "second-user" })
            await AccountSaveTransaction.SaveAndDeliverAsync(
                () => Store.PrepareChannelSaveAsync("138****5678", ChannelCredential(uid, "token"), 1, TestContext.Current.CancellationToken), () => Task.CompletedTask);
        Assert.Equal(2, Store.List().Count);
    }

    [Fact]
    public async Task UncheckedRememberUpdatesAnExistingAccountButDoesNotAddANewOne()
    {
        Assert.Null(await Store.TryPrepareChannelSaveAsync("138****5678", ChannelCredential("new-user", "token"), 1, TestContext.Current.CancellationToken, false, false));
        Assert.Empty(Store.List());
        LocalAccount saved = await Store.SaveAsync("我的备注", ChannelCredential("known-user", "old-token"), 1, TestContext.Current.CancellationToken);
        using PendingAccountSave? update = await Store.TryPrepareChannelSaveAsync("138****5678", ChannelCredential("known-user", "new-token"), 1, TestContext.Current.CancellationToken, false, false);
        Assert.NotNull(update); Assert.Equal(saved.Id, update.Account.Id); Assert.Equal("我的备注", update.Account.Label); update.Commit();
        Assert.Single(Store.List());
    }

    [Fact]
    public async Task RememberedAccountPersistsAcrossInstancesAndExplicitLogoutClearsIt()
    {
        string path = Path.Combine(directory, "last-login.bin");
        RememberedLogin state = new(path, protector); Assert.False(state.HasState);
        Assert.Null(await state.ReadAsync(TestContext.Current.CancellationToken));
        Guid selected = Guid.NewGuid(); await state.SetAsync(selected, TestContext.Current.CancellationToken);
        Assert.Equal(selected, await new RememberedLogin(path, protector).ReadAsync(TestContext.Current.CancellationToken));
        await state.SetAsync(null, TestContext.Current.CancellationToken);
        Assert.True(state.HasState); Assert.Null(await new RememberedLogin(path, protector).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AutomaticResumeHonorsExplicitLogoutAndDoesNotGuessAmongLegacyAccounts()
    {
        LocalAccount a = new(Guid.NewGuid(), "主号", DateTimeOffset.UtcNow, null), b = new(Guid.NewGuid(), "小号", DateTimeOffset.UtcNow, null);
        Assert.Equal(a, RememberedLogin.Resolve(null, false, [a]));
        Assert.Null(RememberedLogin.Resolve(null, true, [a]));
        Assert.Null(RememberedLogin.Resolve(null, false, [a, b]));
        Assert.Equal(b, RememberedLogin.Resolve(b.Id, true, [a, b]));
        Assert.Null(RememberedLogin.Resolve(Guid.NewGuid(), true, [a, b]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAdmissionOrCancelledDeliveryRemovesOnlyTheNewAccount(bool canceled)
    {
        LocalAccount existing = await Store.SaveAsync("existing", Credential, 1, TestContext.Current.CancellationToken);
        async Task Deliver() { await Task.Yield(); if (canceled) throw new OperationCanceledException(); throw new InvalidOperationException("fixture admission rejected"); }
        await Assert.ThrowsAnyAsync<Exception>(() => AccountSaveTransaction.SaveAndDeliverAsync(
            () => Store.SaveAsync("new", Credential, 1, TestContext.Current.CancellationToken), Deliver, Store.Remove));
        Assert.Equal(existing.Id, Assert.Single(Store.List()).Id);
        Assert.Equal(Credential, await Store.UnlockAsync(existing.Id, 1, TestContext.Current.CancellationToken));
        Assert.Single(protector.Deleted); Assert.NotEqual(existing.Id, protector.Deleted[0]);
    }

    [Fact]
    public async Task SuccessfulDeliveryKeepsTheNewAccount()
    {
        LocalAccount saved = await AccountSaveTransaction.SaveAndDeliverAsync(
            () => Store.SaveAsync("new", Credential, 1, TestContext.Current.CancellationToken), () => Task.CompletedTask, Store.Remove);
        Assert.Equal(saved.Id, Assert.Single(Store.List()).Id); Assert.Empty(protector.Deleted);
    }

    [Fact]
    public async Task RollbackFailureIsReportedRatherThanHidden()
    {
        LocalAccount account = new(Guid.NewGuid(), "fixture", DateTimeOffset.UtcNow, null);
        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => AccountSaveTransaction.SaveAndDeliverAsync(
            () => Task.FromResult(account), () => Task.FromException(new InvalidOperationException("delivery")), _ => throw new IOException("rollback")));
        Assert.Equal(2, failure.InnerExceptions.Count);
    }

    [Fact]
    public async Task SavingVerifiesProtectedDecryptionAndStoresNoPlainCredential()
    {
        LocalAccount account = await Store.SaveAsync("我的账号", Credential, 1, TestContext.Current.CancellationToken);
        Assert.Equal(1, protector.UnwrapCalls);
        string file = File.ReadAllText(Path.Combine(directory, account.Id.ToString("N") + ".json"));
        Assert.DoesNotContain(Encoding.UTF8.GetString(Credential), file);
        Assert.Equal("我的账号", Assert.Single(Store.List()).Label);
        Assert.Equal(Credential, await Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledHelloReleasesNoCredentialAndKeepsTheSavedRecord()
    {
        LocalAccount account = await Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken);
        protector.Reject = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken));
        Assert.Single(Store.List());
    }

    [Fact]
    public async Task EnrollmentCancellationCommitsNothingAndRemovesOnlyItsOwnKey()
    {
        protector.Reject = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken));
        Assert.Empty(Store.List()); Assert.Single(protector.Deleted);
    }

    [Fact]
    public async Task NewRecordRemainsUsableAfterThirtyDaysUntilExplicitRemoval()
    {
        LocalAccount account = await Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken);
        Assert.Null(account.ExpiresUtc);
        clock.Now = clock.Now.AddDays(31);
        Assert.Equal(Credential, await Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken));
        Store.Remove(account.Id); Assert.Empty(Store.List());
    }

    [Theory]
    [InlineData("Label")]
    [InlineData("Ciphertext")]
    [InlineData("WrappedKey")]
    public async Task ChangingBoundMetadataOrCiphertextCannotUnlock(string field)
    {
        LocalAccount account = await Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken);
        string path = Path.Combine(directory, account.Id.ToString("N") + ".json");
        JsonNode json = JsonNode.Parse(File.ReadAllText(path))!;
        if (field == "Label") json["Account"]!["Label"] = "B";
        else
        {
            byte[] value = Convert.FromBase64String(json[field]!.GetValue<string>()); value[0] ^= 0x80;
            json[field] = Convert.ToBase64String(value);
        }
        File.WriteAllText(path, json.ToJsonString());
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovingOneIndexedAccountKeepsTheOtherIdentityRemarkAndRememberedChoice()
    {
        using PendingAccountSave first = await Store.PrepareChannelSaveAsync("主号备注", ChannelCredential("first-account", "first-token"), 1, TestContext.Current.CancellationToken);
        first.Commit();
        using PendingAccountSave second = await Store.PrepareChannelSaveAsync("小号备注", ChannelCredential("second-account", "second-token"), 1, TestContext.Current.CancellationToken);
        second.Commit();
        RememberedLogin remembered = new(Path.Combine(directory, "last-login.bin"), protector);
        await remembered.SetAsync(first.Account.Id, TestContext.Current.CancellationToken);
        Store.Remove(second.Account.Id);
        LocalAccount remaining = Assert.Single(Store.List());
        Assert.Equal(first.Account.Id, remaining.Id); Assert.Equal("主号备注", remaining.Label);
        Assert.Equal(first.Account.Id, await remembered.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ChannelCredential("first-account", "first-token"), await Store.UnlockAsync(first.Account.Id, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovingOneAccountDoesNotTouchTheOther()
    {
        LocalAccount a = await Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken);
        LocalAccount b = await Store.SaveAsync("B", "other-fixture"u8.ToArray(), 1, TestContext.Current.CancellationToken);
        Store.Remove(a.Id);
        Assert.Equal(b.Id, Assert.Single(Store.List()).Id);
        Assert.Equal(new[] { a.Id }, protector.Deleted);
        Assert.Equal("other-fixture"u8.ToArray(), await Store.UnlockAsync(b.Id, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AResultArrivingAfterCancellationIsClearedAndNeverReleased()
    {
        LocalAccount account = await Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellation = new();
        protector.BeforeReturn = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.UnlockAsync(account.Id, 1, cancellation.Token));
        Assert.All(protector.LastUnwrapped!, value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void StoredChannelRoundTripsWithoutSavingAConfigurationOrPassword()
    {
        using ProbeIdentityEnvelope channel = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Channel, 200,
            "{\"code\":0,\"result\":{\"userId\":\"fixture-user\",\"token\":\"fixture-token\"}}"u8);
        byte[] encoded = StoredChannelIdentity.Encode(channel, new string('a', 32), 4, 9);
        using SavedChannelCredential restored = StoredChannelIdentity.Decode(encoded);
        Assert.Equal(new string('a', 32), restored.DeviceId); Assert.Equal(4, restored.Platform); Assert.Equal(9, restored.ChannelId);
        var expected = channel.CopyIdentity(); var actual = restored.Identity.CopyIdentity();
        try { Assert.Equal(expected.Uid, actual.Uid); Assert.Equal(expected.Token, actual.Token); }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded); CryptographicOperations.ZeroMemory(expected.Uid); CryptographicOperations.ZeroMemory(expected.Token);
            CryptographicOperations.ZeroMemory(actual.Uid); CryptographicOperations.ZeroMemory(actual.Token);
        }
    }

    [Fact]
    public void StoredChannelRejectsMalformedLengths() => Assert.Throws<InvalidDataException>(() => StoredChannelIdentity.Decode(new byte[20]));

    [Fact]
    public void StoredChannelRejectsOtherChannelAndUnknownVersion()
    {
        using ProbeIdentityEnvelope channel = ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Channel, 200,
            "{\"code\":0,\"result\":{\"userId\":\"fixture-user\",\"token\":\"fixture-token\"}}"u8);
        Assert.Throws<InvalidDataException>(() => StoredChannelIdentity.Encode(channel, new string('a', 32), 4, 8));
        byte[] encoded = StoredChannelIdentity.Encode(channel, new string('a', 32), 4, 9);
        try { encoded[4] = 3; Assert.Throws<InvalidDataException>(() => StoredChannelIdentity.Decode(encoded)); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    [Fact]
    public async Task LegacyLeaseIsNotExtendedAndExpiredRecordIsKept()
    {
        LocalAccount account = await Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken);
        string path = Path.Combine(directory, account.Id.ToString("N") + ".json");
        JsonNode json = JsonNode.Parse(File.ReadAllText(path))!;
        json["Format"] = 1; json["Account"]!["ExpiresUtc"] = account.SavedUtc.AddHours(24);
        json["Protection"] = AccountProtection.Hello;
        File.WriteAllText(path, json.ToJsonString());
        clock.Now = clock.Now.AddHours(24); int before = protector.UnwrapCalls;
        await Assert.ThrowsAsync<InvalidDataException>(() => Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken));
        Assert.Equal(before, protector.UnwrapCalls); Assert.Single(Store.List());
    }

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public async Task DefaultProtectionNeverUsesHelloAndTheDefaultNameMasksThePhone()
    {
        string label = AccountLabels.FromPhone("13812345678"); Assert.Equal("138****5678", label);
        LocalAccount account = await Store.SaveAsync(label, Credential, 1, TestContext.Current.CancellationToken);
        Assert.False(Store.RequiresHello(account.Id)); Assert.Equal(0, hello.UnwrapCalls);
        await Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken, false);
        Assert.Equal(0, hello.UnwrapCalls);
        string file = File.ReadAllText(Path.Combine(directory, account.Id.ToString("N") + ".json"));
        Assert.DoesNotContain("13812345678", file);
    }

    [Fact]
    public async Task EnablingHelloMigratesOnlyAfterSuccessfulProtectedDecryption()
    {
        LocalAccount account = await Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken);
        Assert.Equal(Credential, await Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken, true));
        Assert.True(Store.RequiresHello(account.Id)); Assert.Equal(1, hello.UnwrapCalls);
        Assert.Equal(Credential, await Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken, true));
        Assert.Equal(2, hello.UnwrapCalls);
    }

    [Fact]
    public async Task DisablingHelloConvertsAfterOneUnlockAndSubsequentRestoresDoNotUseHello()
    {
        LocalAccount account = await Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken, true);
        int initial = hello.UnwrapCalls;
        Assert.Equal(Credential, await Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken, false));
        Assert.False(Store.RequiresHello(account.Id)); Assert.Equal(initial + 1, hello.UnwrapCalls);
        Assert.Equal(new[] { account.Id }, hello.Deleted);
        Assert.Equal(Credential, await Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken, false));
        Assert.Equal(initial + 1, hello.UnwrapCalls);
    }

    [Fact]
    public async Task CancellingHelloDuringConversionDoesNotRewriteOrFallback()
    {
        LocalAccount account = await Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken);
        string path = Path.Combine(directory, account.Id.ToString("N") + ".json"); byte[] before = File.ReadAllBytes(path);
        hello.Reject = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken, true));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.False(Store.RequiresHello(account.Id));
    }

    [Fact]
    public async Task FailedHelloOnExistingRecordNeverFallsBackToCurrentUserProtection()
    {
        LocalAccount account = await Store.SaveAsync("A", Credential, 1, TestContext.Current.CancellationToken, true);
        hello.Reject = true; int before = protector.UnwrapCalls;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken, false));
        Assert.Equal(before, protector.UnwrapCalls); Assert.True(Store.RequiresHello(account.Id));
    }

    [Fact]
    public async Task RenamingRebindsAuthenticatedMetadataWithoutChangingTheCredentialOrRetention()
    {
        LocalAccount account = await Store.SaveAsync("138****5678", Credential, 1, TestContext.Current.CancellationToken);
        LocalAccount renamed = await Store.RenameAsync(account.Id, "常用账号", 1, TestContext.Current.CancellationToken);
        Assert.Equal(account.Id, renamed.Id); Assert.Equal(account.SavedUtc, renamed.SavedUtc); Assert.Null(renamed.ExpiresUtc);
        Assert.Equal("常用账号", Assert.Single(Store.List()).Label);
        Assert.Equal(Credential, await Store.UnlockAsync(account.Id, 1, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task RealWindowsCurrentUserProtectionRoundTripsFixtureWithoutHello()
    {
        if (!OperatingSystem.IsWindows()) return;
        LocalAccountStore real = new(directory, new WindowsAccountKeyProtector(), clock);
        LocalAccount account = await real.SaveAsync("138****5678", Credential, 0, TestContext.Current.CancellationToken);
        Assert.Equal(Credential, await real.UnlockAsync(account.Id, 0, TestContext.Current.CancellationToken, false));
        real.Remove(account.Id); Assert.Empty(real.List());
    }

    private sealed class FakeProtector(string protection = AccountProtection.CurrentUser) : IAccountKeyProtector
    {
        public string Protection => protection;
        internal int UnwrapCalls;
        internal bool Reject;
        internal Action? BeforeReturn;
        internal byte[]? LastUnwrapped;
        internal List<Guid> Deleted = [];
        public Task<byte[]> WrapAsync(Guid account, byte[] key, nint owner, CancellationToken token)
        { byte[] wrapped = new byte[256]; key.CopyTo(wrapped, 0); return Task.FromResult(wrapped); }
        public Task<byte[]> UnwrapAsync(Guid account, byte[] wrappedKey, nint owner, CancellationToken token)
        {
            UnwrapCalls++;
            if (Reject) throw new OperationCanceledException();
            LastUnwrapped = wrappedKey.AsSpan(0, 32).ToArray(); BeforeReturn?.Invoke(); return Task.FromResult(LastUnwrapped);
        }
        public void Delete(Guid account) => Deleted.Add(account);
    }
    public void Dispose()
    { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
