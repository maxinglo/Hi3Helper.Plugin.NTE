using System;
using System.Collections.Generic;

namespace Hi3Helper.Plugin.NTE.Management.Game;

internal enum NteFileProgressStage
{
    Download,
    Completion,
    SourceVerification
}

/// <summary>Publish one active work item's progress; background work cannot overwrite it. Caller serializes access.</summary>
internal sealed class NtePerFileProgress
{
    private readonly SortedDictionary<(NteFileProgressStage Stage, int Order), (long Done, long Total)> _active = [];
    private (NteFileProgressStage Stage, int Order)? _current;
    private (long Done, long Total) _last = (0, 1);

    internal void Begin(int order, long total) => _active.Add((NteFileProgressStage.Download, order), (0, total));
    internal void Add(int order, long bytes)
    {
        var key = (NteFileProgressStage.Download, order);
        var entry = _active[key];
        _active[key] = (Math.Clamp(entry.Done + bytes, 0, entry.Total), entry.Total);
    }

    internal void Set(int order, long done, long total, NteFileProgressStage stage)
    {
        total = Math.Max(0, total);
        _active[(stage, order)] = (Math.Clamp(done, 0, total), total);
    }

    internal void End(int order, NteFileProgressStage stage = NteFileProgressStage.Download)
    {
        var key = (stage, order);
        _active.Remove(key);
        if (_current == key) _current = null;
    }

    internal (long Done, long Total)? Current
    {
        get
        {
            foreach (var item in _active)
            {
                // Downloads take precedence. Source checks and local completion share one
                // background owner so they cannot take turns replacing the visible value.
                if (_current is { } selected &&
                    (selected.Stage == NteFileProgressStage.Download) ==
                    (item.Key.Stage == NteFileProgressStage.Download) &&
                    _active.TryGetValue(selected, out var current)) return current;
                _current = item.Key;
                return item.Value;
            }

            return null;
        }
    }

    internal (long Done, long Total) Snapshot(bool completed = false)
    {
        if (completed) return _last = (1, 1);
        if (Current is { } current) _last = (current.Done, Math.Max(1, current.Total));
        // Keep the last value between stages instead of injecting a synthetic zero.
        return _last;
    }
}
