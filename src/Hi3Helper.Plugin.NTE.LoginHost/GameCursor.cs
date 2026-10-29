using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Nte.LoginHost;

// Ask the game asynchronously: owner-related windows share input state, so
// synchronous sends can block each other's mouse/activation processing.
// WPF uses an owned copy, without replacing global settings or hooking game code.
internal sealed class GameCursor : IDisposable
{
    private Cursor? cursor;
    private nint source;
    private bool hidden;
    private readonly DispatcherTimer pending = new() { Interval = TimeSpan.FromMilliseconds(20) };
    private nint pendingGame, pendingCover;
    private Action? changed;
    internal Cursor? Value => cursor;
    internal int Synchronizations { get; private set; }

    internal GameCursor()
    {
        pending.Tick += (_, _) =>
        {
            pending.Stop(); Action? update = changed; changed = null;
            if (TryObserve(pendingGame, pendingCover)) update?.Invoke();
        };
    }

    internal bool RequestSynchronize(nint game, nint cover, Action update)
    {
        if (!CanObserve(game, cover)) return false;
        if (pending.IsEnabled) return true;
        if (!PostMessageW(game, 0x20, game, 0x02000001)) return false;
        pendingGame = game; pendingCover = cover; changed = update; pending.Start();
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
        // Read input-thread state after the posted message, rather than the
        // compositor's potentially stale global cursor display snapshot.
        nint selected = GetCursor();
        bool nextHidden = (info.Flags & 1) == 0 || selected == 0;
        if (cursor != null && source == selected && hidden == nextHidden) return true;
        Cursor replacement;
        if (nextHidden) replacement = Cursors.None;
        else
        {
            nint copy = CopyImage(selected, 2, 0, 0, 0); // IMAGE_CURSOR, owned.
            if (copy == 0) return false;
            SafeCursor owned = new(copy);
            try { replacement = CursorInteropHelper.Create(owned); }
            catch { owned.Dispose(); return false; }
        }
        Cursor? previous = cursor;
        cursor = replacement; source = selected; hidden = nextHidden; Synchronizations++;
        if (previous != null && previous != Cursors.None) previous.Dispose();
        return true;
    }

    public void Dispose()
    {
        pending.Stop(); changed = null;
        if (cursor != null && cursor != Cursors.None) cursor.Dispose();
        cursor = null; source = 0;
    }

    private sealed class SafeCursor : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeCursor(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => DestroyCursor(handle);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { internal uint Size, Flags; internal nint Cursor; internal WindowNative.Point Position; }
    [DllImport("user32")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32")] private static extern nint GetCursor();
    [DllImport("user32")] private static extern nint CopyImage(nint image, uint type, int width, int height, uint flags);
    [DllImport("user32")] private static extern bool DestroyCursor(nint cursor);
    [DllImport("user32", SetLastError = true)] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
