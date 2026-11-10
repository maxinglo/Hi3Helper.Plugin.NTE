using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal sealed class NteDownloadJob
{
    internal NteDownloadJob(NteGameInstaller.DownloadTask file)
    {
        File = file;
        Space = NteDownloadSpace.Capture(file);
        TemporaryBytes = Space.TemporaryBytes;
    }

    internal NteGameInstaller.DownloadTask File { get; }
    internal NteDownloadSpace Space { get; set; }
    internal int Attempt { get; set; }
    internal long ReadyAt { get; set; }
    internal long TemporaryBytes;
    internal long ReportedBytes { get; set; }
    internal bool SourcePrepared { get; set; }
    internal bool PreparingSource { get; set; }
}

/// <summary>Source checks, network transfers and local completion overlap within a disk reservation budget.</summary>
internal sealed class NteDownloadScheduler
{
    internal const long ReserveBytes = 512L * 1024 * 1024;
    private readonly object sync = new();
    private readonly List<NteDownloadJob> pending;
    private readonly HashSet<NteDownloadJob> active = [];
    private readonly Func<long> freeSpace;
    private TaskCompletionSource changed = NewSignal();
    private int completed;
    private readonly int total;
    private bool prepareSources;

    internal NteDownloadScheduler(IReadOnlyList<NteDownloadJob> jobs, Func<long> freeSpace)
    {
        this.freeSpace = freeSpace;
        total = jobs.Count;
        pending = jobs.ToList();
        SortPending();
        long required = NteDownloadSpace.RequiredFreeBytes(jobs.Select(j => j.Space), ReserveBytes);
        long available = freeSpace();
        if (available < required)
            throw new IOException(
                $"Insufficient remaining disk space. Required {required} additional bytes including reserve; available {available} bytes.");
    }

    internal async Task RunAsync(IReadOnlyList<NteDownloadJob> jobs, int networkWorkers,
        Func<NteDownloadJob, CancellationToken, Task> transfer,
        Action<NteDownloadJob, CancellationToken> verify,
        Action<NteDownloadJob> verified, Action<NteDownloadJob, Exception> retry,
        CancellationToken token, Func<NteDownloadJob, CancellationToken, Task>? prepare = null)
    {
        prepareSources = prepare != null;
        using CancellationTokenSource cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        using BlockingCollection<NteDownloadJob>
            verification = new(); // Metadata only; file handles/buffers are closed before enqueueing.
        Task localWorker = Task.Factory.StartNew(() =>
        {
            try
            {
                foreach (NteDownloadJob job in verification.GetConsumingEnumerable(cancel.Token))
                {
                    try
                    {
                        verify(job, cancel.Token);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or NtePatchFallbackException)
                    {
                        if (!TryRetry(job, ex, retry)) throw;
                        continue;
                    }

                    lock (sync)
                    {
                        active.Remove(job);
                        completed++;
                        Pulse();
                    }

                    verified(job);
                }
            }
            catch
            {
                cancel.Cancel();
                throw;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        async Task PrepareWorker()
        {
            try
            {
                while (await TakePreparationAsync(cancel.Token).ConfigureAwait(false) is { } job)
                {
                    try
                    {
                        await prepare!(job, cancel.Token).ConfigureAwait(false);
                    }
                    catch (NtePatchFallbackException ex)
                    {
                        if (!TryRetry(job, ex, retry)) throw;
                        continue;
                    }

                    lock (sync)
                    {
                        job.SourcePrepared = true;
                        job.PreparingSource = false;
                        Pulse();
                    }
                }
            }
            catch
            {
                cancel.Cancel();
                throw;
            }
        }

        async Task NetworkWorker()
        {
            try
            {
                while (await TakeAsync(cancel.Token).ConfigureAwait(false) is { } job)
                {
                    try
                    {
                        if (job.Space.NetworkBytes > 0)
                            await transfer(job, cancel.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsTransient(ex))
                    {
                        if (!TryRetry(job, ex, retry)) throw;
                        continue;
                    }

                    verification.Add(job, cancel.Token);
                }
            }
            catch
            {
                cancel.Cancel();
                throw;
            }
        }

        Exception? error = null;
        Task preparationWorker = prepareSources ? PrepareWorker() : Task.CompletedTask;
        try
        {
            await Task.WhenAll(Enumerable.Range(0, Math.Max(1, networkWorkers)).Select(_ => NetworkWorker()))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
            cancel.Cancel();
        }
        finally
        {
            verification.CompleteAdding();
        }

        try
        {
            await preparationWorker.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (error == null || error is OperationCanceledException && !token.IsCancellationRequested) error = ex;
        }

        try
        {
            await localWorker.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (error == null || error is OperationCanceledException && !token.IsCancellationRequested) error = ex;
        }

        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        token.ThrowIfCancellationRequested();
    }

    private bool NeedsPreparation(NteDownloadJob job)
        => prepareSources && !job.SourcePrepared && job.Space.NetworkBytes > 0 &&
           job.File.UpdateOperation?.IsDelta == true;

    private async Task<NteDownloadJob?> TakePreparationAsync(CancellationToken token)
    {
        while (true)
        {
            Task signal;
            long delay = -1;
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (completed == total) return null;
                long now = Environment.TickCount64;
                foreach (NteDownloadJob job in pending)
                {
                    if (!NeedsPreparation(job)) continue;
                    if (job.ReadyAt <= now)
                    {
                        job.PreparingSource = true;
                        return job;
                    }
                    delay = delay < 0 ? job.ReadyAt - now : Math.Min(delay, job.ReadyAt - now);
                }

                signal = changed.Task;
            }

            await WaitForChangeAsync(signal, delay, token).ConfigureAwait(false);
        }
    }

    private async Task<NteDownloadJob?> TakeAsync(CancellationToken token)
    {
        while (true)
        {
            Task signal;
            long delay = -1;
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (completed == total) return null;
                // Free space already reflects bytes written. Reserve only the unwritten
                // suffix of active temporary files, not their complete target size again.
                long available = freeSpace();
                foreach (NteDownloadJob job in active)
                    available -= Math.Max(0,
                        checked(job.File.TotalSize + job.Space.WorkBytes) - Volatile.Read(ref job.TemporaryBytes));
                long now = Environment.TickCount64;
                bool spaceReleasingPending = false;
                for (int i = 0; i < pending.Count; i++)
                {
                    NteDownloadJob job = pending[i];
                    bool releasesSpace = job.Space.ReclaimedBytes >= job.Space.AdditionalBytes;
                    if (!releasesSpace && spaceReleasingPending) break;
                    spaceReleasingPending |= releasesSpace;
                    if (job.PreparingSource || NeedsPreparation(job))
                    {
                        if (!releasesSpace) break;
                        continue;
                    }
                    if (job.ReadyAt > now)
                    {
                        delay = delay < 0 ? job.ReadyAt - now : Math.Min(delay, job.ReadyAt - now);
                        if (!releasesSpace) break;
                        continue;
                    }

                    if (job.Space.AdditionalBytes > 0 && available - ReserveBytes < job.Space.AdditionalBytes)
                    {
                        if (!releasesSpace) break;
                        continue;
                    }

                    pending.RemoveAt(i);
                    active.Add(job);
                    return job;
                }

                // Bypass a source check only when the remaining plan still fits. Waiting for a
                // large source must not idle the network or spend space needed by its patch.
                bool sourcePending = pending.Any(job => job.PreparingSource || NeedsPreparation(job));
                if (sourcePending)
                {
                    foreach (NteDownloadJob job in pending)
                    {
                        if (job.PreparingSource || NeedsPreparation(job) || job.ReadyAt > now) continue;
                        if (job.Space.AdditionalBytes > 0 && available - ReserveBytes < job.Space.AdditionalBytes)
                            continue;
                        long remaining = NteDownloadSpace.RequiredFreeBytes(
                            pending.Where(other => other != job).Select(other => other.Space), ReserveBytes);
                        if (available - job.Space.AdditionalBytes < remaining) break;
                        pending.Remove(job);
                        active.Add(job);
                        return job;
                    }
                }

                if (active.Count == 0 && pending.Count > 0 && delay < 0 && !sourcePending)
                    throw new IOException(
                        $"Insufficient remaining disk space for the next file. Available {available} bytes.");
                signal = changed.Task;
            }

            await WaitForChangeAsync(signal, delay, token).ConfigureAwait(false);
        }
    }

    private static async Task WaitForChangeAsync(Task signal, long delay, CancellationToken token)
    {
        if (delay < 0)
        {
            await signal.WaitAsync(token).ConfigureAwait(false);
            return;
        }

        using CancellationTokenSource wake = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task timer = Task.Delay(TimeSpan.FromMilliseconds(delay), wake.Token);
        await Task.WhenAny(signal.WaitAsync(wake.Token), timer).ConfigureAwait(false);
        wake.Cancel();
        token.ThrowIfCancellationRequested();
    }

    private bool TryRetry(NteDownloadJob job, Exception error, Action<NteDownloadJob, Exception> retry)
    {
        if (error is NtePatchFallbackException && job.File.UpdateOperation?.UseFullDownload() == true)
            job.Attempt = -1;
        if (job.Attempt >= 2) return false;
        NteDownloadSpace current = NteDownloadSpace.Capture(job.File);
        lock (sync)
        {
            job.Space = current;
            Volatile.Write(ref job.TemporaryBytes, current.TemporaryBytes);
            job.SourcePrepared = false;
            job.Attempt++;
            job.ReadyAt = Environment.TickCount64 +
                          (error is InvalidDataException or NtePatchFallbackException ? 0 : 500L << (job.Attempt - 1));
        }

        retry(job, error);
        lock (sync)
        {
            active.Remove(job);
            pending.Remove(job);
            job.PreparingSource = false;
            pending.Add(job);
            SortPending();
            Pulse();
        }

        return true;
    }

    private static bool IsTransient(Exception error) => error switch
    {
        OperationCanceledException => false,
        HttpRequestException http => http.StatusCode == null ||
                                     http.StatusCode is HttpStatusCode.RequestTimeout
                                         or HttpStatusCode.TooManyRequests || (int)http.StatusCode >= 500,
        HttpIOException => true,
        NtePatchFallbackException => true,
        InvalidDataException => true,
        IOException io => io.InnerException is SocketException or HttpRequestException or HttpIOException,
        _ => false
    };

    private void Pulse()
    {
        TaskCompletionSource previous = changed;
        changed = NewSignal();
        previous.TrySetResult();
    }

    private void SortPending()
    {
        NteDownloadJob[] sorted = pending
            .OrderByDescending(job => job.Space.ReclaimedBytes >= job.Space.AdditionalBytes)
            .ThenBy(job =>
                job.Space.ReclaimedBytes >= job.Space.AdditionalBytes
                    ? job.Space.AdditionalBytes
                    : -job.Space.ReclaimedBytes)
            .ThenByDescending(job => job.Space.NetworkBytes).ToArray();
        pending.Clear();
        pending.AddRange(sorted);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
