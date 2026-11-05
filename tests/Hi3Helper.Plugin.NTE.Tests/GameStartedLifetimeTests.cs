using Nte.LoginHost;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class GameStartedLifetimeTests
{
    [Theory]
    [InlineData("attach")]
    [InlineData("transport")]
    public async Task FailureIsReportedButHostAndCleanupWaitForGameExit(string phase)
    {
        TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cleaned = false;
        Task<bool> host = GameStartedLifetime.RunAsync(exit.Task, () => throw new InvalidOperationException(phase),
            ex => { Assert.Equal(phase, ex.Message); reported.SetResult(); return Task.CompletedTask; },
            () => false, () => { cleaned = true; return Task.CompletedTask; });
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(host.IsCompleted); Assert.False(cleaned);
        exit.SetResult(); Assert.False(await host.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)); Assert.True(cleaned);
    }

    [Fact]
    public async Task LoginTaskFailureIsPresentedWhileTheSdkConnectionIsStillWaiting()
    {
        TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource connection = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource loginFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cleaned = false;
        Task<bool> host = GameStartedLifetime.RunAsync(exit.Task, () => connection.Task,
            ex => { Assert.IsType<InvalidDataException>(ex); reported.SetResult(); return Task.CompletedTask; },
            () => false, () => { cleaned = true; connection.TrySetCanceled(); return Task.CompletedTask; }, loginFailure.Task);
        loginFailure.SetException(new InvalidDataException("synthetic UI setup failure"));
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.False(connection.Task.IsCompleted);
        Assert.False(host.IsCompleted);
        Assert.False(cleaned);
        exit.SetResult();
        Assert.False(await host.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.True(cleaned);
    }

    [Fact]
    public async Task FailureToDisplayWarningStillKeepsHostUntilGameExit()
    {
        TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously); bool cleaned = false;
        Task<bool> host = GameStartedLifetime.RunAsync(exit.Task, () => throw new IOException("fixture"),
            _ => throw new InvalidOperationException("display fixture"), () => false,
            () => { cleaned = true; return Task.CompletedTask; });
        Assert.False(host.IsCompleted); Assert.False(cleaned);
        exit.SetResult(); Assert.False(await host.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)); Assert.True(cleaned);
    }

    [Fact]
    public async Task GameExitCancellationDoesNotShowFailureAndRunsCleanup()
    {
        TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously); bool reported = false, cleaned = false;
        Task<bool> host = GameStartedLifetime.RunAsync(exit.Task, async () =>
        { await exit.Task; throw new OperationCanceledException(); },
            _ => { reported = true; return Task.CompletedTask; }, () => true,
            () => { cleaned = true; return Task.CompletedTask; });
        exit.SetResult(); Assert.True(await host.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.False(reported); Assert.True(cleaned);
    }
}
