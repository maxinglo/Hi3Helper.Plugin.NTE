using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Nte.LoginHost;

// Borrow the cursor Windows is displaying for the game. Its handle retains
// the original size, hotspot, and system cursor behavior.
internal sealed class GameCursor : IDisposable
{
    private nint cursor;
    private readonly DispatcherTimer pending = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private nint pendingGame, pendingCover;
    internal bool HasCursor { get; private set; }
    internal int Synchronizations { get; private set; }

    internal GameCursor()
    {
        pending.Tick += (_, _) =>
        {
            pending.Stop();
            if (WindowNative.IsWindow(pendingGame) && ObserveAt(pendingCover)) Apply();
        };
    }

    internal void ObserveGame(nint game)
    {
        ObserveAt(game);
    }

    internal bool Synchronize(nint game, nint cover)
    {
        if (!WindowNative.IsWindow(game) || !WindowNative.GetCursorPos(out var point) ||
            !MatchesWindowAt(point, cover)) return false;
        bool applied = Apply();
        if (!pending.IsEnabled && PostMessageW(game, 0x20, game, 0x02000001))
        {
            pendingGame = game;
            pendingCover = cover;
            pending.Start();
        }
        return applied;
    }

    private static bool MatchesWindowAt(WindowNative.Point point, nint window)
        => window != 0 && WindowNative.GetAncestor(WindowNative.WindowFromPoint(point), 2) == window;

    private bool ObserveAt(nint window)
    {
        CursorInfo info = new() { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        // Touch or pen can temporarily suppress a hardware cursor. That
        // should not overwrite the last cursor the game selected.
        if (!GetCursorInfo(ref info) || (info.Flags & 2) != 0 || !MatchesWindowAt(info.Position, window))
            return false;
        nint selected = (info.Flags & 1) != 0 ? info.Cursor : 0;
        if (!HasCursor || cursor != selected)
        {
            cursor = selected;
            HasCursor = true;
            Synchronizations++;
        }
        return true;
    }

    private bool Apply()
    {
        if (!HasCursor) return false;
        SetCursor(cursor);
        // The source may have destroyed a custom cursor after we observed it.
        return GetCursor() == cursor;
    }

    internal void Reset()
    {
        pending.Stop();
        cursor = pendingGame = pendingCover = 0;
        HasCursor = false;
    }

    public void Dispose() => Reset();

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        internal uint Size, Flags;
        internal nint Cursor;
        internal WindowNative.Point Position;
    }

    [DllImport("user32")]
    private static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32")]
    private static extern nint GetCursor();

    [DllImport("user32")]
    private static extern nint SetCursor(nint cursor);

    [DllImport("user32", SetLastError = true)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
