using System.ComponentModel;
using System.Runtime.InteropServices;
using Nte.LoginHost;

internal sealed class CursorFixture : IDisposable
{
    private readonly nint handle;
    internal Cursor Cursor { get; }

    internal CursorFixture(int width, int height)
    {
        if (width is < 16 or > 256 || height is < 16 or > 256)
            throw new ArgumentOutOfRangeException(nameof(width), "Cursor fixture dimensions must be 16 to 256 pixels.");
        int stride = ((width + 15) / 16) * 2;
        byte[] mask = new byte[stride * height];
        byte[] shape = new byte[mask.Length];
        Array.Fill(mask, byte.MaxValue);
        // Monochrome cursor with a noncentral hotspot and a visible cross.
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            if ((x < 5 || x > 9) && (y < 7 || y > 11)) continue;
            int index = y * stride + x / 8;
            byte bit = (byte)(128 >> (x % 8));
            mask[index] &= (byte)~bit;
            shape[index] |= bit;
        }
        handle = CreateCursor(0, 7, 9, width, height, mask, shape);
        if (handle == 0) throw new Win32Exception();
        Cursor = new(handle);
    }

    internal void ApplyWithin(nint window)
    {
        if (WindowNative.GetCursorPos(out var point) &&
            WindowNative.GetAncestor(WindowNative.WindowFromPoint(point), 2) == window)
            SetCursor(handle);
    }

    internal static object Inspect()
    {
        CursorInfo cursor = new() { Size = (uint)Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor)) throw new Win32Exception();
        nint window = WindowNative.GetAncestor(WindowNative.WindowFromPoint(cursor.Position), 2);
        WindowNative.GetWindowThreadProcessId(window, out uint pid);
        if (cursor.Handle == 0) return new { visible = false, pid };
        if (!GetIconInfo(cursor.Handle, out IconInfo bitmap)) throw new Win32Exception();
        try
        {
            nint source = bitmap.Color != 0 ? bitmap.Color : bitmap.Mask;
            if (GetObjectW(source, Marshal.SizeOf<BitmapInfo>(), out BitmapInfo size) == 0)
                throw new Win32Exception();
            return new { handle = (long)cursor.Handle, visible = (cursor.Flags & 1) != 0,
                width = size.Width, height = bitmap.Color != 0 ? size.Height : size.Height / 2,
                hotspotX = bitmap.X, hotspotY = bitmap.Y, pid, dpi = WindowNative.GetDpiForWindow(window) };
        }
        finally
        {
            if (bitmap.Color != 0) DeleteObject(bitmap.Color);
            if (bitmap.Mask != 0) DeleteObject(bitmap.Mask);
        }
    }

    public void Dispose()
    {
        Cursor.Dispose();
        DestroyCursor(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo { public uint Size, Flags; public nint Handle; public WindowNative.Point Position; }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo { public int IsIcon; public uint X, Y; public nint Mask, Color; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo { public int Type, Width, Height, Stride; public ushort Planes, BitsPerPixel; public nint Bits; }
    [DllImport("user32", SetLastError = true)]
    private static extern nint CreateCursor(nint instance, int x, int y, int width, int height, byte[] mask, byte[] shape);
    [DllImport("user32")] private static extern bool DestroyCursor(nint cursor);
    [DllImport("user32")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32", SetLastError = true)] private static extern bool GetCursorInfo(ref CursorInfo cursor);
    [DllImport("user32", SetLastError = true)] private static extern bool GetIconInfo(nint cursor, out IconInfo info);
    [DllImport("gdi32", SetLastError = true)] private static extern int GetObjectW(nint bitmap, int size, out BitmapInfo info);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint bitmap);
}
