using Hi3Helper.Plugin.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal sealed class NtePatchFallbackException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>One release's content cache, shared transfers and leases. No game writes occur while planning.</summary>
internal sealed class NteUpdateContext(string root, string appId, NteFileVerificationCache verification)
{
    private readonly Dictionary<string, int> _references = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _initialReferences = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SemaphoreSlim> _transferLocks = new(StringComparer.OrdinalIgnoreCase);
    internal NteFileVerificationCache Verification => verification;
    internal string PathFor(string md5, long size) => NtePreloadCache.PathFor(root, appId, md5, size);
    internal bool Cached(string md5, long size, CancellationToken token)
    {
        try { return verification.Verify(PathFor(md5, size), size, md5, token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    internal static NteGameInstaller.DownloadTask CacheTask(string path, string md5, long size) => new()
        { OutputPath = path, ExpectedMd5 = md5, TotalSize = size };
    internal long Remaining(NteResListEntry resource, CancellationToken token)
        => Cached(resource.Md5, resource.Filesize, token) ? 0 :
            NteDownloadSpace.Capture(CacheTask(PathFor(resource.Md5, resource.Filesize), resource.Md5, resource.Filesize)).NetworkBytes;
    internal void Register(IEnumerable<NteUpdateOperation> operations)
    {
        foreach (NteUpdateOperation operation in operations)
            foreach (string path in operation.Leases)
                _references[path] = _references.GetValueOrDefault(path) + 1;
        foreach (var pair in _references)
        {
            _initialReferences.Add(pair.Key, pair.Value);
            _transferLocks.Add(pair.Key, new(1, 1));
        }
    }
    internal bool Shared(string path) => _initialReferences.GetValueOrDefault(path) > 1;
    internal SemaphoreSlim TransferLock(string path) => _transferLocks[path];
    internal void Release(NteUpdateOperation operation)
    {
        foreach (string path in operation.Leases)
        {
            int remaining = --_references[path];
            if (remaining == 0)
            {
                File.Delete(path);
                File.Delete(path + ".tmp.identity");
            }
        }
    }
}

/// <summary>Download work and patch scratch space are accounted separately from installed output bytes.</summary>
internal sealed class NteUpdateOperation
{
    private readonly NteUpdateContext _context;
    private readonly NtePatchRoute? _route;
    private readonly List<NteResListEntry> _resources;
    private readonly string? _sourceCache;
    private readonly HashSet<string> _leases = new(StringComparer.OrdinalIgnoreCase);
    private long _scratchBytes;
    private bool _full;
    internal NteGameInstaller.DownloadTask Target { get; }
    internal IEnumerable<string> Leases => _leases;
    internal bool IsDelta => !_full && _route?.Steps.Count > 0;
    internal int PatchStages => !_full ? _route?.Steps.Count ?? 0 : 0;
    internal long TransferSize => _full ? Target.TotalSize : _resources.Count == 0 ? Target.TotalSize : _resources.Sum(r => r.Filesize);
    internal long ScratchBytes => Volatile.Read(ref _scratchBytes);

    internal NteUpdateOperation(NteGameInstaller.DownloadTask target, NteUpdateContext context, NtePatchRoute? route)
    {
        Target = target;
        _context = context;
        _route = route;
        _full = route == null;
        _resources = route?.Steps.Select(p => p.Resource).DistinctBy(r => NtePatchList.Identity(r.Md5, r.Filesize)).ToList() ?? [];
        if (route?.SourceCached == true)
            _sourceCache = context.PathFor(route.SourceMd5, route.SourceSize);
        if (_sourceCache != null) _leases.Add(_sourceCache);
        foreach (NteResListEntry resource in _resources) _leases.Add(context.PathFor(resource.Md5, resource.Filesize));
        foreach (NtePatchEntry step in route?.Steps ?? []) _leases.Add(context.PathFor(step.NewMd5, step.NewSize));
    }

    internal NteGameInstaller.DownloadTask AsTask() => new()
    {
        Kind = Target.Kind, ResEntry = Target.ResEntry, PakEntry = Target.PakEntry, PakInfo = Target.PakInfo,
        OutputPath = Target.OutputPath, ExpectedMd5 = Target.ExpectedMd5, UpdateOperation = this
    };

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    private long ScratchPeak
    {
        get
        {
            if (_full) return 0;
            if (_resources.Count == 0) return _sourceCache != null && _context.Shared(_sourceCache) ? Target.TotalSize : 0;
            long previous = 0, retained = 0, peak = 0;
            foreach (NtePatchEntry step in _route!.Steps)
            {
                peak = Math.Max(peak, checked(retained + previous + step.NewSize));
                if (_context.Shared(_context.PathFor(step.NewMd5, step.NewSize)))
                { retained += step.NewSize; previous = 0; }
                else previous = step.NewSize;
            }
            if (_context.Shared(_context.PathFor(Target.ExpectedMd5, Target.TotalSize)))
                peak = Math.Max(peak, checked(retained + Target.TotalSize));
            return peak;
        }
    }

    internal NteDownloadSpace CaptureSpace()
    {
        if (_full)
        {
            NteDownloadSpace actual = NteDownloadSpace.Capture(Target);
            NteDownloadSpace cached = NteDownloadSpace.Capture(NteUpdateContext.CacheTask(
                _context.PathFor(Target.ExpectedMd5, Target.TotalSize), Target.ExpectedMd5, Target.TotalSize));
            return cached.ResumableBytes > actual.ResumableBytes
                ? actual with { TemporaryBytes = cached.TemporaryBytes, ResumableBytes = cached.ResumableBytes }
                : actual;
        }
        long old = Length(Target.OutputPath), oldTemporary = Length(Target.OutputPath + ".tmp");
        if (_resources.Count == 0)
            return new(TransferSize, TransferSize + ScratchBytes, old, TransferSize, ScratchPeak, old + oldTemporary);
        long temporary = 0, resumable = 0, invalidExisting = 0;
        foreach (NteResListEntry resource in _resources)
        {
            string path = _context.PathFor(resource.Md5, resource.Filesize);
            if (_context.Verification.IsVerified(path, resource.Filesize, resource.Md5))
            { temporary += resource.Filesize; resumable += resource.Filesize; }
            else
            {
                NteDownloadSpace partial = NteDownloadSpace.Capture(NteUpdateContext.CacheTask(path, resource.Md5, resource.Filesize));
                temporary += partial.TemporaryBytes;
                resumable += partial.ResumableBytes;
                invalidExisting += partial.ExistingBytes;
            }
        }
        long retained = _resources.Where(r => _context.Shared(_context.PathFor(r.Md5, r.Filesize))).Sum(r => r.Filesize);
        retained += _route!.Steps.Where(p => _context.Shared(_context.PathFor(p.NewMd5, p.NewSize))).Sum(p => p.NewSize);
        long sourceReleased = _sourceCache != null && !_context.Shared(_sourceCache) ? _route.SourceSize : 0;
        long reclaimed = checked(TransferSize + ScratchPeak - Target.TotalSize + old + oldTemporary + invalidExisting + sourceReleased - retained);
        return new(TransferSize, temporary + ScratchBytes, old, resumable, ScratchPeak, reclaimed);
    }

    internal async Task DownloadAsync(NteGameInstaller installer, string[] roots, string branch, CancellationToken token, Action<long> progress)
    {
        if (_full)
        {
            string cached = _context.PathFor(Target.ExpectedMd5, Target.TotalSize);
            var cache = NteDownloadSpace.Capture(NteUpdateContext.CacheTask(cached, Target.ExpectedMd5, Target.TotalSize));
            if (cache.ResumableBytes > NteDownloadSpace.Capture(Target).ResumableBytes)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Target.OutputPath)!);
                File.Move(cached + ".tmp", Target.OutputPath + ".tmp", true);
                File.Move(cached + ".tmp.identity", Target.OutputPath + ".tmp.identity", true);
            }
            await DownloadTargetAsync(installer, roots, branch, Target, token, progress).ConfigureAwait(false);
            return;
        }
        foreach (NteResListEntry resource in _resources)
        {
            string path = _context.PathFor(resource.Md5, resource.Filesize);
            SemaphoreSlim gate = _context.TransferLock(path);
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_context.Verification.IsVerified(path, resource.Filesize, resource.Md5))
                { progress(resource.Filesize); continue; }
                if (resource.HasBlocks)
                    await installer.DownloadBlockedResourceAsync(roots, branch, resource, path, token, progress, true).ConfigureAwait(false);
                else
                    await installer.DownloadResourceFileAsync(roots, branch, resource.Md5, resource.Filesize, path, token, progress, true).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
    }

    private static Task DownloadTargetAsync(NteGameInstaller installer, string[] roots, string branch,
        NteGameInstaller.DownloadTask target, CancellationToken token, Action<long> progress)
        => target.Kind == NteGameInstaller.DownloadTaskKind.PakEntry
            ? installer.DownloadPakEntryAsync(roots, branch, target.PakInfo!, target.PakEntry!, target.OutputPath, token, progress, true)
            : target.ResEntry!.HasBlocks
                ? installer.DownloadBlockedResourceAsync(roots, branch, target.ResEntry, target.OutputPath, token, progress, true)
                : installer.DownloadResourceFileAsync(roots, branch, target.ExpectedMd5, target.TotalSize, target.OutputPath, token, progress, true);

    internal void Complete(CancellationToken token, Action<long, long> progress, Action<long> diskProgress)
    {
        if (_full)
        {
            NteGameInstaller.CompleteDownloadedFile(Target.OutputPath + ".tmp", Target.OutputPath, Target.TotalSize, Target.ExpectedMd5, token);
            _context.Verification.RememberVerified(Target.OutputPath, Target.TotalSize, Target.ExpectedMd5);
            _context.Release(this);
            return;
        }
        // Transfer verification uses the existing dedicated local worker, before any patch source is opened.
        foreach (NteResListEntry resource in _resources)
        {
            string path = _context.PathFor(resource.Md5, resource.Filesize);
            if (!_context.Verification.IsVerified(path, resource.Filesize, resource.Md5))
            {
                NteGameInstaller.CompleteDownloadedFile(path + ".tmp", path, resource.Filesize, resource.Md5, token);
                _context.Verification.RememberVerified(path, resource.Filesize, resource.Md5);
            }
        }
        string source = _sourceCache ?? Target.OutputPath;
        if (!_context.Verification.Verify(source, _route!.SourceSize, _route.SourceMd5, token, done => progress(done, _route.SourceSize)))
            throw new NtePatchFallbackException("The selected delta source no longer matches its official checksum.");
        List<(string Path, long Size)> produced = [];
        foreach (NtePatchEntry step in _route.Steps)
        {
            token.ThrowIfCancellationRequested();
            string target = _context.PathFor(step.NewMd5, step.NewSize);
            if (!_context.Verification.IsVerified(target, step.NewSize, step.NewMd5))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string temporary = target + ".tmp";
                NteGameInstaller.PrepareTemporaryFile(temporary, step.NewMd5, step.NewSize);
                long priorScratch = produced.Sum(p => p.Size);
                try
                {
                    NteHdiffPatch.Apply(source, _context.PathFor(step.Resource.Md5, step.Resource.Filesize), temporary,
                        step.NewSize, token, (done, total) =>
                        {
                            Volatile.Write(ref _scratchBytes, priorScratch + done);
                            diskProgress(TransferSize + ScratchBytes);
                            progress(done, total);
                        });
                    NteGameInstaller.CompleteDownloadedFile(temporary, target, step.NewSize, step.NewMd5, token);
                    _context.Verification.RememberVerified(target, step.NewSize, step.NewMd5);
                    produced.Add((target, step.NewSize));
                }
                catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException)
                {
                    File.Delete(temporary);
                    File.Delete(temporary + ".identity");
                    throw new NtePatchFallbackException("The selected delta format or output is invalid; using the full resource.", ex);
                }
            }
            int prior = produced.FindIndex(p => p.Path.Equals(source, StringComparison.OrdinalIgnoreCase));
            if (prior >= 0 && !_context.Shared(source))
            {
                File.Delete(source);
                produced.RemoveAt(prior);
            }
            Volatile.Write(ref _scratchBytes, produced.Sum(p => p.Size));
            diskProgress(TransferSize + ScratchBytes);
            source = target;
        }
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(Target.OutputPath)!);
        string finalTemporary = Target.OutputPath + ".tmp";
        NteGameInstaller.PrepareTemporaryFile(finalTemporary, Target.ExpectedMd5, Target.TotalSize);
        if (_context.Shared(source))
        {
            using FileStream input = File.OpenRead(source);
            using FileStream output = new(finalTemporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
            byte[] buffer = new byte[1024 * 1024];
            int read; long copied = 0;
            while ((read = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read); copied += read;
                Volatile.Write(ref _scratchBytes, produced.Sum(p => p.Size) + copied);
                diskProgress(TransferSize + ScratchBytes); progress(copied, Target.TotalSize);
            }
        }
        else File.Move(source, finalTemporary, true);
        NteGameInstaller.CompleteDownloadedFile(finalTemporary, Target.OutputPath, Target.TotalSize, Target.ExpectedMd5, token);
        _context.Verification.RememberVerified(Target.OutputPath, Target.TotalSize, Target.ExpectedMd5);
        _context.Release(this);
    }

    internal bool UseFullDownload()
    {
        if (_full) return false;
        _full = true;
        SharedStatic.InstanceLogger.LogWarning("[NTE Update] Falling back to full resource: {File}", Path.GetFileName(Target.OutputPath));
        return true;
    }
}
