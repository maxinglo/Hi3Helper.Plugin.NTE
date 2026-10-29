using System.Net;
using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class DownloadSchedulerTests : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;
    private readonly string root = Path.Combine(Path.GetTempPath(), "NteDownloadSchedulerTests", Guid.NewGuid().ToString("N"));
    public DownloadSchedulerTests() => Directory.CreateDirectory(root);
    private NteDownloadJob Job(string name, long size, long temporary = 0, long existing = 0)
        => new(new() { OutputPath = Path.Combine(root, name), TotalSize = size, ExpectedMd5 = "hash" })
        { Space = new(size, temporary, existing, temporary), TemporaryBytes = temporary };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task LocalVerification_DoesNotOccupyTheOnlyNetworkSlot()
    {
        List<NteDownloadJob> jobs = Enumerable.Range(0, 6).Select(i => Job(i.ToString(), 1024)).ToList();
        TaskCompletionSource allTransferred = Signal();
        using ManualResetEventSlim release = new();
        int transferred = 0, verified = 0;
        bool localUsesPool = true;
        NteDownloadScheduler scheduler = new(jobs, () => 10 * GiB);
        Task running = scheduler.RunAsync(jobs, 1, (job, _) =>
        {
            Volatile.Write(ref job.TemporaryBytes, job.File.TotalSize);
            if (Interlocked.Increment(ref transferred) == jobs.Count) allTransferred.SetResult();
            return Task.CompletedTask;
        }, (_, ct) => { localUsesPool = Thread.CurrentThread.IsThreadPoolThread; release.Wait(ct); },
            _ => Interlocked.Increment(ref verified), (_, _) => { }, TestContext.Current.CancellationToken);
        try
        {
            await allTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(running.IsCompleted);
            Assert.Equal(6, transferred);
        }
        finally { release.Set(); await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        Assert.False(localUsesPool);
        Assert.Equal(6, verified);
    }

    [Fact]
    public async Task FreshHundredGiBVolume_AllowsFourNetworkTransfers()
    {
        long free = 100 * GiB;
        List<NteDownloadJob> jobs = new[] { 15, 13, 11, 9, 25, 25 }.Select((size, i) => Job(i.ToString(), size * GiB)).ToList();
        TaskCompletionSource fourStarted = Signal(), release = Signal();
        int active = 0, maximum = 0, started = 0;
        NteDownloadScheduler scheduler = new(jobs, () => Interlocked.Read(ref free));
        Task running = scheduler.RunAsync(jobs, 4, async (job, ct) =>
        {
            int count = Interlocked.Increment(ref active);
            int prior;
            do { prior = maximum; } while (count > prior && Interlocked.CompareExchange(ref maximum, count, prior) != prior);
            if (Interlocked.Increment(ref started) == 4) fourStarted.SetResult();
            try
            {
                await release.Task.WaitAsync(ct);
                Interlocked.Add(ref free, -job.Space.AdditionalBytes);
                Volatile.Write(ref job.TemporaryBytes, job.File.TotalSize);
            }
            finally { Interlocked.Decrement(ref active); }
        }, (_, _) => { }, _ => { }, (_, _) => { }, TestContext.Current.CancellationToken);
        try { await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); Assert.Equal(4, maximum); }
        finally { release.TrySetResult(); await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        Assert.Equal(2 * GiB, free);
    }

    [Fact]
    public async Task OneGiBRemaining_WithFiftyGiBFree_DoesNotRequireTheHundredGiBTargetAgain()
    {
        NteDownloadJob job = Job("resumed", 100 * GiB, 99 * GiB);
        long free = 50 * GiB;
        NteDownloadScheduler scheduler = new([job], () => free);
        await scheduler.RunAsync([job], 4, (current, _) =>
        {
            free -= current.Space.NetworkBytes;
            Volatile.Write(ref current.TemporaryBytes, current.File.TotalSize);
            return Task.CompletedTask;
        }, (_, _) => { }, _ => { }, (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.Equal(49 * GiB, free);
    }

    [Fact]
    public async Task Replacement_ReservesTemporarySpaceUntilTheOldFileCanBeReleased()
    {
        long free = 50 * GiB;
        List<NteDownloadJob> jobs = [Job("a", 40 * GiB, existing: 40 * GiB), Job("b", 40 * GiB, existing: 40 * GiB)];
        TaskCompletionSource firstTransferred = Signal();
        using ManualResetEventSlim release = new();
        int transferred = 0;
        NteDownloadScheduler scheduler = new(jobs, () => Interlocked.Read(ref free));
        Task running = scheduler.RunAsync(jobs, 4, (job, _) =>
        {
            Interlocked.Add(ref free, -job.Space.AdditionalBytes); Volatile.Write(ref job.TemporaryBytes, job.File.TotalSize);
            if (Interlocked.Increment(ref transferred) == 1) firstTransferred.SetResult();
            return Task.CompletedTask;
        }, (job, ct) => { release.Wait(ct); Interlocked.Add(ref free, job.Space.ExistingBytes); }, _ => { }, (_, _) => { }, TestContext.Current.CancellationToken);
        try
        {
            await firstTransferred.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(1, transferred);
        }
        finally { release.Set(); await running.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
        Assert.Equal(2, transferred);
        Assert.Equal(50 * GiB, free);
    }

    [Fact]
    public async Task SpaceConstrainedUpdate_DoesNotSpendSpaceOutOfOrder()
    {
        long free = 10 * GiB + NteDownloadScheduler.ReserveBytes;
        List<NteDownloadJob> jobs = [Job("small-loss", 5 * GiB), Job("large-return", 10 * GiB, existing: 9 * GiB), Job("gain", 5 * GiB, existing: 5 * GiB)];
        List<string> transfers = [];
        NteDownloadScheduler scheduler = new(jobs, () => Interlocked.Read(ref free));
        await scheduler.RunAsync(jobs, 4, (job, _) =>
        {
            lock (transfers) transfers.Add(Path.GetFileName(job.File.OutputPath));
            Interlocked.Add(ref free, -job.Space.AdditionalBytes);
            Volatile.Write(ref job.TemporaryBytes, job.File.TotalSize);
            return Task.CompletedTask;
        }, (job, _) => Interlocked.Add(ref free, job.Space.ReclaimedBytes), _ => { }, (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "gain", "large-return", "small-loss" }, transfers);
    }

    [Fact]
    public async Task CompleteTemporaryFiles_UseOnlyTheLocalWorkerEvenWithNoFreeSpace()
    {
        List<NteDownloadJob> jobs = Enumerable.Range(0, 4).Select(i => Job(i.ToString(), GiB, GiB)).ToList();
        int networkCalls = 0, verified = 0;
        NteDownloadScheduler scheduler = new(jobs, () => 0);
        await scheduler.RunAsync(jobs, 4, (_, _) => { networkCalls++; return Task.CompletedTask; },
            (_, _) => Assert.False(Thread.CurrentThread.IsThreadPoolThread), _ => verified++, (_, _) => { }, TestContext.Current.CancellationToken);
        Assert.Equal(0, networkCalls); Assert.Equal(4, verified);
    }

    [Fact]
    public async Task LocalIoFailure_IsReturnedWithoutAnotherNetworkDownload()
    {
        NteDownloadJob job = Job("failed", 1);
        int calls = 0, retries = 0;
        NteDownloadScheduler scheduler = new([job], () => GiB);
        await Assert.ThrowsAsync<IOException>(() => scheduler.RunAsync([job], 4,
            (current, _) => { calls++; Volatile.Write(ref current.TemporaryBytes, 1); return Task.CompletedTask; },
            (_, _) => throw new IOException("local disk failure"), _ => { }, (_, _) => retries++, TestContext.Current.CancellationToken));
        Assert.Equal(1, calls); Assert.Equal(0, retries);
    }

    [Fact]
    public async Task TransientNetworkFailure_RetriesWithoutHoldingANetworkThreadDuringBackoff()
    {
        NteDownloadJob job = Job("retry", 1);
        int attempts = 0, retries = 0;
        NteDownloadScheduler scheduler = new([job], () => GiB);
        await scheduler.RunAsync([job], 1, (current, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new HttpRequestException("transient", null, HttpStatusCode.ServiceUnavailable);
            Volatile.Write(ref current.TemporaryBytes, 1); return Task.CompletedTask;
        }, (_, _) => { }, _ => { }, (_, _) => retries++, TestContext.Current.CancellationToken);
        Assert.Equal(2, attempts); Assert.Equal(1, retries);
    }

    public void Dispose() => Directory.Delete(root, true);
}
