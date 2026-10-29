using System.Text.Json;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GlobalSessionTests
{
    [Fact]
    public void SessionOwnsItsIdentityAfterTheLoginFormDisposesItsResult()
    {
        using NteGlobalSession session = new();
        using (NteGlobalIdentity identity = Identity()) session.Remember(identity, session.Token);
        using NteGlobalIdentity saved = Assert.IsType<NteGlobalIdentity>(session.Capture());
        Assert.Equal("42", NteGlobalIdentity.ReadUserId(saved.Result.GetProperty("uid")));
        Assert.Equal(2, saved.Provider);
    }
    [Fact]
    public void LogoutCancelsPendingResultsAndDoesNotRestoreAnOldIdentity()
    {
        using NteGlobalSession session = new(); using NteGlobalIdentity identity = Identity();
        CancellationToken originalRequest = session.Token; session.Remember(identity, originalRequest);
        session.Clear(); Assert.Null(session.Capture()); Assert.True(originalRequest.IsCancellationRequested);
        Assert.Throws<OperationCanceledException>(() => session.Remember(identity, originalRequest));
        Assert.Null(session.Capture()); Assert.False(session.Token.IsCancellationRequested);
    }
    private static NteGlobalIdentity Identity()
    {
        using JsonDocument response = JsonDocument.Parse("{\"code\":0,\"result\":{\"uid\":42,\"token\":\"fixture-session-token\"}}");
        return NteGlobalIdentity.FromService(response.RootElement, 2);
    }
    [Fact]
    public void UnrelatedLifetimeTokenCannotRecreateALoggedOutSession()
    {
        using NteGlobalSession session = new(); using NteGlobalIdentity identity = Identity();
        using CancellationTokenSource unrelated = new();
        Assert.Throws<InvalidOperationException>(() => session.Remember(identity, unrelated.Token));
        Assert.Null(session.Capture());
    }
}
