using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Hi3Helper.Plugin.NTE.Management.Game;

// Collapse checks status immediately after starting the sidecar. Until the
// sidecar creates the game, this lease covers that startup interval. Once an
// actual game is observed, only that game's process can keep it running.
internal sealed class NteGameLaunchSession : IDisposable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, NteGameLaunchSession> Sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly string executable;
    private readonly Process host;
    private readonly DateTime startTime;
    private bool gameObserved;

    private NteGameLaunchSession(string executable, Process host)
    {
        this.executable = Path.GetFullPath(executable);
        this.host = host;
        startTime = host.StartTime;
        lock (Gate) Sessions[this.executable] = this;
    }

    // The caller must keep its Process alive until this lease is disposed.
    internal static NteGameLaunchSession Track(string executable, Process host) => new(executable, host);

    internal static bool IsStarting(string executable, out DateTime startTime)
    {
        startTime = default;
        lock (Gate)
        {
            if (!Sessions.TryGetValue(Path.GetFullPath(executable), out NteGameLaunchSession? session) || session.gameObserved)
                return false;
            try
            {
                if (session.host.HasExited) return false;
                startTime = session.startTime;
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                return false;
            }
        }
    }

    internal static void ObserveGame(string executable)
    {
        lock (Gate)
            if (Sessions.TryGetValue(Path.GetFullPath(executable), out NteGameLaunchSession? session)) session.gameObserved = true;
    }

    public void Dispose()
    {
        lock (Gate)
            if (Sessions.TryGetValue(executable, out NteGameLaunchSession? session) && ReferenceEquals(session, this)) Sessions.Remove(executable);
    }
}
