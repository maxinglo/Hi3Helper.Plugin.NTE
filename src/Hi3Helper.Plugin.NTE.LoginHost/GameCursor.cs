using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Nte.LoginHost;

// Query the game asynchronously, then own a copy of its native cursor. This
// preserves native cursor images without WPF or runtime COM interop.
internal sealed class GameCursor : IDisposable
{
    private nint cursor, source;
    private bool observed, hidden;
    private readonly DispatcherTimer pending = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private nint pendingGame, pendingCover;
    private Action? changed;
    internal bool HasCursor => observed;
    internal int Synchronizations { get; private set; }

    internal GameCursor()
    {
        pending.Tick += (_, _) =>
        {
            pending.Stop();
            Action? update = changed;
            changed = null;
            if (TryObserve(pendingGame, pendingCover)) update?.Invoke();
        };
    }

    internal bool RequestSynchronize(nint game, nint cover, Action update)
    {
        if (!CanObserve(game, cover)) return false;
        Apply();
        if (pending.IsEnabled) return true;
        if (!PostMessageW(game, 0x20, game, 0x02000001)) return false;
        pendingGame = game;
        pendingCover = cover;
        changed = update;
        pending.Start();
        return true;
    }

    private static bool CanObserve(nint game, nint cover)
    {
        nint foreground = WindowNative.GetForegroundWindow();
        return game != 0 && WindowNative.IsWindow(game) && (foreground == game || foreground == cover);
    }

    internal bool TryObserve(nint game, nint cover)
    {
        if (!CanObserve(game, cover)) return false;
        CursorInfo info = new() { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info)) return false;
        nint selected = GetCursor();
        bool nextHidden = (info.Flags & 1) == 0 || selected == 0;
        if (observed && source == selected && hidden == nextHidden) return true;
        nint replacement = nextHidden ? 0 : CopyImage(selected, 2, 0, 0, 0);
        if (!nextHidden && replacement == 0) return false;
        nint previous = cursor;
        cursor = replacement;
        source = selected;
        hidden = nextHidden;
        observed = true;
        Synchronizations++;
        if (previous != 0) DestroyCursor(previous);
        return true;
    }

    internal void Apply()
    {
        if (observed) SetCursor(cursor);
    }

    public void Dispose()
    {
        pending.Stop();
        changed = null;
        if (cursor != 0) DestroyCursor(cursor);
        cursor = source = 0;
        observed = false;
    }

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

    [DllImport("user32")]
    private static extern nint CopyImage(nint image, uint type, int width, int height, uint flags);

    [DllImport("user32")]
    private static extern bool DestroyCursor(nint cursor);

    [DllImport("user32", SetLastError = true)]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
