using Xunit;
namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class SdkLoginRequestTests
{
    [Fact]
    public async Task DuplicateNotificationsCoalesceAndACompletedRequestCanBeFollowedByAnother()
    {
        ProbeLoginRequests requests = new();
        requests.Notify(); requests.Notify();
        await requests.WaitAsync(TestContext.Current.CancellationToken);
        using CancellationTokenSource deadline = new(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests.WaitAsync(deadline.Token));
        requests.Complete(); requests.Notify();
        await requests.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ACancelledWaitDoesNotConsumeTheNextRealRequest()
    {
        ProbeLoginRequests requests = new();
        using CancellationTokenSource cancelled = new(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requests.WaitAsync(cancelled.Token));
        requests.Notify();
        await requests.WaitAsync(TestContext.Current.CancellationToken);
    }
}
