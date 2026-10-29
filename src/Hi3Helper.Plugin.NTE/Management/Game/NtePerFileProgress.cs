using System;
using System.Collections.Generic;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>Track the first active download consistently while other parallel files finish. Caller serializes access.</summary>
internal sealed class NtePerFileProgress
{
    private readonly SortedDictionary<int, (long Done, long Total)> _active = [];
    internal void Begin(int order, long total) => _active.Add(order, (0, total));
    internal void Add(int order, long bytes)
    {
        var entry = _active[order];
        _active[order] = (Math.Clamp(entry.Done + bytes, 0, entry.Total), entry.Total);
    }
    internal void End(int order) => _active.Remove(order);
    internal (long Done, long Total)? Current
    {
        get
        {
            foreach (var item in _active) return item.Value;
            return null;
        }
    }
}
