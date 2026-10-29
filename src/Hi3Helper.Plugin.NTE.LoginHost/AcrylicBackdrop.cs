using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Nte.LoginHost;

// Capture the foreground game's client, excluding our own cover on scheduled
// refreshes. Drag/resize continue to reuse the cache. No image is written or
// uploaded; display affinity is restored immediately after each capture.
internal static class AcrylicBackdrop
{
    internal sealed record Snapshot(BitmapSource Image, double PreparationMs, int Bytes);

    internal static Snapshot? Capture(nint game, WindowNative.Rect client, nint visibleCover = 0)
    {
        bool HasForeground()
        {
            nint foreground = WindowNative.GetForegroundWindow();
            return foreground == game || (visibleCover != 0 && foreground == visibleCover);
        }
        if (game == 0 || !HasForeground()) return null;
        uint originalAffinity = 0;
        bool excluded = false;
        if (visibleCover != 0)
        {
            // Keep the card visible and focused without including it in its
            // own screenshot. Skip refresh when exclusion is unavailable.
            // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowdisplayaffinity
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) ||
                !GetWindowDisplayAffinity(visibleCover, out originalAffinity) ||
                !SetWindowDisplayAffinity(visibleCover, 0x11)) return null;
            excluded = true;
        }
        try
        {
            if (excluded && DwmFlush() < 0) return null;
            return CaptureCore(game, client, HasForeground);
        }
        finally
        {
            if (excluded) { SetWindowDisplayAffinity(visibleCover, originalAffinity); DwmFlush(); }
        }
    }

    private static Snapshot? CaptureCore(nint game, WindowNative.Rect client, Func<bool> hasForeground)
    {
        if (!hasForeground() ||
            client.Width is < 1 or > 16384 || client.Height is < 1 or > 16384) return null;
        Stopwatch timer = Stopwatch.StartNew();
        double reduction = Math.Min(1, Math.Min(960d / client.Width, 600d / client.Height));
        int width = Math.Max(1, (int)(client.Width * reduction)), height = Math.Max(1, (int)(client.Height * reduction));
        nint screen = WindowNative.GetDC(0), memory = 0, bitmap = 0, previous = 0;
        if (screen == 0) return null;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == 0) return null;
            BitmapInfo info = new() { Size = (uint)Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(screen, ref info, 0, out nint pixels, 0, 0);
            if (bitmap == 0 || pixels == 0) return null;
            previous = SelectObject(memory, bitmap);
            if (previous == 0 || previous == -1) return null;
            SetStretchBltMode(memory, 4); // HALFTONE. Downsample before filtering.
            SetBrushOrgEx(memory, 0, 0, 0);
            if (!StretchBlt(memory, 0, 0, width, height, screen, client.Left, client.Top, client.Width, client.Height, 0x00CC0020)) return null;
            // If focus moved during preparation, do not use someone else's UI.
            if (!hasForeground()) return null;
            byte[] data = new byte[width * height * 4];
            Marshal.Copy(pixels, data, 0, data.Length);
            int radius = Math.Clamp((int)(22 * reduction * Math.Max(1, WindowNative.GetDpiForWindow(game) / 96d)), 3, 24);
            CachedBackdropPixels.BlurAndGrain(data, width, height, radius);
            BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
            image.Freeze();
            Array.Clear(data);
            return new(image, timer.Elapsed.TotalMilliseconds, width * height * 4);
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or InvalidOperationException) { return null; }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            WindowNative.ReleaseDC(0, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        internal uint Size; internal int Width, Height; internal ushort Planes, BitCount;
        internal uint Compression, ImageBytes; internal int XResolution, YResolution; internal uint Colors, ImportantColors;
    }
    [DllImport("gdi32")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32")] private static extern bool SetBrushOrgEx(nint dc, int x, int y, nint previous);
    [DllImport("gdi32")] private static extern bool StretchBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32")] private static extern bool DeleteDC(nint dc);
    [DllImport("user32")] private static extern bool GetWindowDisplayAffinity(nint window, out uint affinity);
    [DllImport("user32", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint window, uint affinity);
    [DllImport("dwmapi")] private static extern int DwmFlush();
}
