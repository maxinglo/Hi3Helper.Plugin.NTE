using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nte.LoginHost;

internal sealed class WelcomeToast : Window
{
    private readonly DispatcherTimer expiry = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly TextBlock text = new() { Foreground = Brushes.White, FontSize = 15, MaxWidth = 460, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly nint handle;
    internal bool Active { get; private set; }

    internal WelcomeToast()
    {
        Title = Localization.LoginText.Get("WelcomeTitle"); WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowActivated = false; ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight; FontFamily = new("Microsoft YaHei UI");
        Content = new Border { Background = new SolidColorBrush(Color.FromArgb(238, 30, 57, 71)), CornerRadius = new(12),
            Padding = new(22, 13, 22, 13), Child = text };
        handle = new WindowInteropHelper(this).EnsureHandle();
        WindowNative.SetWindowLongPtr(handle, -20, WindowNative.GetWindowLongPtr(handle, -20) | 0x080000A0);
        expiry.Tick += (_, _) => Dismiss();
    }
    internal void Present(string label, nint gameWindow, TimeSpan duration)
        => PresentMessage(Localization.LoginText.Get("WelcomePrefix") + label, gameWindow, duration);
    internal void PresentMessage(string message, nint gameWindow, TimeSpan duration)
    {
        text.Text = message; Active = true;
        expiry.Interval = duration;
        expiry.Stop(); expiry.Start(); Track(gameWindow);
    }
    internal void Track(nint gameWindow)
    {
        if (!Active) return;
        if (gameWindow == 0 || !WindowNative.IsWindow(gameWindow) || WindowNative.IsIconic(gameWindow) || WindowNative.GetForegroundWindow() != gameWindow)
        { Hide(); return; }
        WindowNative.Rect bounds = WindowNative.ScreenClient(gameWindow);
        double scale = WindowNative.GetDpiForWindow(handle) / 96d;
        text.MaxWidth = Math.Clamp(bounds.Width / Math.Max(1, scale) - 60, 100, 460);
        WindowNative.SetWindowLongPtr(handle, -8, gameWindow);
        if (!IsVisible) Show();
        UpdateLayout();
        int width = (int)Math.Ceiling(ActualWidth * scale), height = (int)Math.Ceiling(ActualHeight * scale);
        WindowNative.SetWindowPos(handle, 0, bounds.Left + (bounds.Width - width) / 2, bounds.Top + (int)(28 * scale), width, height, 0x214);
    }
    internal void Dismiss() { Active = false; expiry.Stop(); Hide(); }
    internal void Stop() { Dismiss(); Close(); }
}
