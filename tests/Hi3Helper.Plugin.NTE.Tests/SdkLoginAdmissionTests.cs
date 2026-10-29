using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkLoginAdmissionTests
{
    [Theory]
    [InlineData(0, 0, 0, null, 0, 0, "real-name-required")]
    [InlineData(0, 1, 1, null, 0, 0, "real-name-restricted")]
    [InlineData(0, 0, 1, 1, 0, 0, "real-name-restricted")]
    [InlineData(0, 0, 1, 5, 0, 0, "real-name-restricted")] // No native limited-session exception.
    [InlineData(10005, null, null, null, null, null, "prelogin-service-rejected")]
    [InlineData(0, 2, 1, 0, 0, 0, "prelogin-state-unavailable")]
    [InlineData(0, 0, 2, 0, 0, 0, "prelogin-state-unavailable")]
    [InlineData(0, 0, 1, -1, 0, 0, "prelogin-state-unavailable")]
    [InlineData(0, null, 1, 0, 0, 0, "invalid-response")]
    [InlineData(0, 0, null, 0, 0, 0, "invalid-response")]
    public async Task RejectedOrUncertainStateNeverDeliversSuccessfulLoginOrPreLogin(int code, int? status, int? realUser,
        int? bannedType, int? accountType, int? civicType, string reason)
    {
        List<object> events = [];
        using Service handler = new(code, status, realUser, bannedType, accountType, civicType);
        using HttpClient http = new(handler);
        using ProbePreLoginClient client = Client(http, events.Add);
        using ProbeLoginAdmission admission = new(client, events.Add);
        using ProbeIdentityEnvelope identity = Identity("fixture-user");
        int login = 0, reply = 0;
        if (reason == "invalid-response")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                admission.AuthorizeAndDeliverAsync(identity, () => { login++; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
            Assert.Equal(0, login); Assert.Equal(1, handler.Count); return;
        }
        ProbeAuthenticationException failure = await Assert.ThrowsAsync<ProbeAuthenticationException>(() =>
            admission.AuthorizeAndDeliverAsync(identity, () => { login++; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
        Assert.Equal(reason, failure.Phase);
        Assert.Contains("登录已阻止", failure.LocalExplanation);
        await Assert.ThrowsAsync<ProbeAuthenticationException>(() =>
            admission.ReplyAsync(_ => { reply++; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
        Assert.Equal(0, login); Assert.Equal(0, reply); Assert.Equal(1, handler.Count);
        string report = JsonSerializer.Serialize(events);
        Assert.Contains("login-admission-blocked", report);
        Assert.DoesNotContain("fixture-user", report); Assert.DoesNotContain("fixture-token", report);
        Assert.DoesNotContain("fixture-service-message", report);
    }

    [Theory]
    [InlineData(null, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(null, 2, 0)]
    [InlineData(null, 1, 1)] // Native cache branch, not a login restriction.
    [InlineData(null, 0, 2)]
    public async Task AllowedResultIsCheckedBeforeIdentityCallbackAndReusedWithFreshClientContexts(int? bannedType, int accountType, int civicType)
    {
        List<object> events = [];
        using Service handler = new(0, 0, 1, bannedType, accountType, civicType);
        using HttpClient http = new(handler);
        using ProbePreLoginClient client = Client(http, events.Add);
        using ProbeLoginAdmission admission = new(client, events.Add);
        using ProbeIdentityEnvelope identity = Identity("fixture-user");
        int login = 0;
        await admission.AuthorizeAndDeliverAsync(identity, () =>
        {
            Assert.Equal(1, handler.Count);
            Assert.Contains("login-admission-allowed", JsonSerializer.Serialize(events));
            login++; return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        List<int> received = [];
        ProbeAntifatigueQueries queries = new(admission, 1234, (response, request, _) =>
        {
            byte[] packet = response.Encode(request, 256); // XOR byte 0 makes context directly inspectable.
            received.Add(BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(24)));
            return Task.CompletedTask;
        }, events.Add);
        await queries.HandleAsync(Request(1, 17), TestContext.Current.CancellationToken);
        await queries.HandleAsync(Request(1, 31), TestContext.Current.CancellationToken);
        Assert.Equal(1, login); Assert.Equal(1, handler.Count);
        Assert.Equal(new[] { 17, 31 }, received);
    }

    [Fact]
    public async Task ExpiredAdmissionRefreshCanBlockLaterPreLoginWithoutReusingPriorAllowedState()
    {
        using Service handler = new(0, 0, 1, null, 0, 0);
        using HttpClient http = new(handler);
        using ProbePreLoginClient client = Client(http, _ => { });
        Clock clock = new(); using ProbeLoginAdmission admission = new(client, _ => { }, clock);
        using ProbeIdentityEnvelope identity = Identity("fixture-user");
        await admission.AuthorizeAndDeliverAsync(identity, () => Task.CompletedTask, TestContext.Current.CancellationToken);
        handler.Status = 1; clock.Advance(TimeSpan.FromMinutes(1));
        bool delivered = false;
        ProbeAuthenticationException failure = await Assert.ThrowsAsync<ProbeAuthenticationException>(() =>
            admission.ReplyAsync(_ => { delivered = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
        Assert.Equal("real-name-restricted", failure.Phase); Assert.False(delivered); Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task ANewIdentityCannotInheritAnotherAccountsAdmission()
    {
        using Service handler = new(0, 0, 1, null, 0, 0);
        using HttpClient http = new(handler);
        using ProbePreLoginClient client = Client(http, _ => { });
        using ProbeLoginAdmission admission = new(client, _ => { });
        using ProbeIdentityEnvelope first = Identity("fixture-user"), second = Identity("fixture-other");
        await admission.AuthorizeAndDeliverAsync(first, () => Task.CompletedTask, TestContext.Current.CancellationToken);
        handler.RealUser = 0;
        bool delivered = false;
        await Assert.ThrowsAsync<ProbeAuthenticationException>(() => admission.AuthorizeAndDeliverAsync(second,
            () => { delivered = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
        Assert.False(delivered); Assert.Equal(2, handler.Count); Assert.Equal("fixture-other", handler.LastUser);
    }

    [Fact]
    public async Task ResetForAnotherLoginClearsBothCachedAdmissionAndThePreviousIdentity()
    {
        using Service handler = new(0, 0, 1, null, 0, 0);
        using HttpClient http = new(handler);
        using ProbePreLoginClient client = Client(http, _ => { });
        using ProbeLoginAdmission admission = new(client, _ => { });
        using ProbeIdentityEnvelope first = Identity("fixture-user"), second = Identity("fixture-other");
        await admission.AuthorizeAndDeliverAsync(first, () => Task.CompletedTask, TestContext.Current.CancellationToken);
        await admission.ResetAsync(TestContext.Current.CancellationToken);
        bool delivered = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => admission.ReplyAsync(_ =>
            { delivered = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
        Assert.False(delivered); Assert.Equal(1, handler.Count);
        await admission.AuthorizeAndDeliverAsync(second, () => Task.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Count); Assert.Equal("fixture-other", handler.LastUser);
    }

    [Fact]
    public async Task FailedRefreshAndCancellationNeverUseCachedSuccess()
    {
        using Service handler = new(0, 0, 1, null, 0, 0);
        using HttpClient http = new(handler);
        using ProbePreLoginClient client = Client(http, _ => { });
        Clock clock = new(); using ProbeLoginAdmission admission = new(client, _ => { }, clock);
        using ProbeIdentityEnvelope identity = Identity("fixture-user");
        await admission.AuthorizeAndDeliverAsync(identity, () => Task.CompletedTask, TestContext.Current.CancellationToken);
        handler.HttpStatus = HttpStatusCode.ServiceUnavailable; clock.Advance(TimeSpan.FromMinutes(1));
        bool delivered = false;
        await Assert.ThrowsAsync<HttpRequestException>(() => admission.ReplyAsync(_ =>
            { delivered = true; return Task.CompletedTask; }, TestContext.Current.CancellationToken));
        Assert.False(delivered); Assert.Equal(2, handler.Count);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => admission.AuthorizeAndDeliverAsync(identity,
            () => { delivered = true; return Task.CompletedTask; }, canceled.Token));
        Assert.False(delivered); Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task TypeTwoSubscriptionNeitherQueriesServiceNorImmediatelyRepliesBeforeOrAfterAdmission()
    {
        List<object> events = [];
        using Service handler = new(0, 0, 1, null, 0, 0);
        using HttpClient http = new(handler);
        using ProbePreLoginClient client = Client(http, events.Add);
        using ProbeLoginAdmission admission = new(client, events.Add);
        int delivered = 0;
        ProbeAntifatigueQueries queries = new(admission, 1234, (_, _, _) => { delivered++; return Task.CompletedTask; }, events.Add);
        await queries.HandleAsync(Request(2, -1), TestContext.Current.CancellationToken);
        Assert.Equal(0, handler.Count); Assert.Equal(0, delivered);
        using ProbeIdentityEnvelope identity = Identity("fixture-user");
        await admission.AuthorizeAndDeliverAsync(identity, () => Task.CompletedTask, TestContext.Current.CancellationToken);
        for (int i = 0; i < 3; i++) await queries.HandleAsync(Request(2, -1), TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Count); Assert.Equal(0, delivered);
        string report = JsonSerializer.Serialize(events);
        Assert.Contains("antifatigue-info-subscribed", report); Assert.DoesNotContain("sdk-query-unimplemented", report);
        await Assert.ThrowsAsync<InvalidDataException>(() => queries.HandleAsync(Request(2, 1), TestContext.Current.CancellationToken));
        byte[] invalid = Request(2, -1); invalid[20] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => queries.HandleAsync(invalid, TestContext.Current.CancellationToken));
        await queries.HandleAsync(Request(0, 7), TestContext.Current.CancellationToken);
        Assert.Contains("sdk-query-unimplemented", JsonSerializer.Serialize(events)); Assert.Equal(0, delivered);
    }

    private static ProbePreLoginClient Client(HttpClient http, Action<object> observe) =>
        new("abcdefghijklmnop"u8, new string('a', 32), "1.4.17", observe, http);
    private static ProbeIdentityEnvelope Identity(string user) => ProbeIdentityEnvelope.Parse(ProbeIdentityStage.Game, 200,
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { code = 0, uid = user, token = "fixture-token" })));

    private static byte[] Request(int type, int context)
    {
        byte[] frame = new byte[0x214];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, 0x6B647331);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), frame.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), type);
        "{}"u8.CopyTo(frame.AsSpan(16));
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0x210), context);
        for (int i = 12; i < frame.Length; i++) frame[i] ^= unchecked((byte)1234);
        return frame;
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now += duration;
    }
    private sealed class Service(int code, int? status, int? realUser, int? bannedType, int? accountType, int? civicType) : HttpMessageHandler
    {
        internal int Count;
        internal int? Status = status, RealUser = realUser;
        internal HttpStatusCode HttpStatus = HttpStatusCode.OK;
        internal string? LastUser;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            string form = await request.Content!.ReadAsStringAsync(cancellationToken);
            LastUser = form.Split('&').Select(pair => pair.Split('=', 2)).Where(pair => pair[0] == "userId")
                .Select(pair => WebUtility.UrlDecode(pair[1])).Single();
            return new(HttpStatus) { Content = new StringContent(JsonSerializer.Serialize(new { code, message = "fixture-service-message",
                result = new { appId = 1289, userId = LastUser, status = Status, realUser = RealUser, bannedType, accountType, civicType } })) };
        }
    }
}
