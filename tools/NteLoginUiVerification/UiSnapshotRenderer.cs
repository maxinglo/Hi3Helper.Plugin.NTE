using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class UiSnapshotRenderer
{
    internal static object Capture(UiSession session, JsonElement request, string directory, string repository)
    {
        string name = request.TryGetProperty("name", out JsonElement label)
            ? label.GetString()!
            : "snapshot-" + Guid.NewGuid().ToString("N");
        if (!Regex.IsMatch(name, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\\z"))
            throw new InvalidDataException("invalid-capture-name");
        string theme = request.TryGetProperty("theme", out JsonElement mode) ? mode.GetString()! : "night";
        if (theme is not ("night" or "day" or "sunset")) throw new InvalidDataException("invalid-theme");
        string path = Path.Combine(directory, name + ".png");
        if (File.Exists(path)) throw new InvalidDataException("capture-already-exists");
        UiSession.Call(session.Window, "ApplyTheme", theme);
        FrameworkElement content = (FrameworkElement)session.Window.Content;
        session.Window.UpdateLayout();
        double width = content.ActualWidth, height = content.ActualHeight;
        if (width is < 100 or > 4096 || height is < 100 or > 4096) throw new InvalidDataException("capture-size-bound");
        BitmapImage background = new();
        background.BeginInit();
        background.CacheOption = BitmapCacheOption.OnLoad;
        background.UriSource =
            new Uri(Path.Combine(repository, "tests", "Fixtures", "UI", $"game-login-{theme}.png"));
        background.EndInit();
        background.Freeze();
        // Never include a desktop-derived backdrop in a verification artifact.
        // The exact target visual tree is composited over its synthetic fixture.
        System.Windows.Shapes.Rectangle backdrop =
            (System.Windows.Shapes.Rectangle)UiSession.Field(session.Window, "cachedBackdrop")!;
        Brush previous = backdrop.Fill;
        try
        {
            backdrop.Fill = Brushes.Transparent;
            // Flush retained drawing updates after changing theme and excluding
            // the backdrop, before sampling the connected visual tree.
            session.Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            DrawingVisual visual = new();
            using (DrawingContext drawing = visual.RenderOpen())
            {
                drawing.DrawImage(background, new Rect(0, 0, width, height));
                drawing.DrawRectangle(new VisualBrush(content)
                    {
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.Fill,
                        AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top
                    },
                    null, new Rect(0, 0, width, height));
            }

            RenderTargetBitmap bitmap = new((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream output = new(path, FileMode.CreateNew);
            encoder.Save(output);
        }
        finally
        {
            backdrop.Fill = previous;
        }

        return new
        {
            path, width = (int)Math.Ceiling(width), height = (int)Math.Ceiling(height), theme,
            renderer = "published-assembly-WPF-visual-tree-over-synthetic-background", desktopCaptured = false,
            liveBackdropFidelity = false, syntheticOnly = true, hostSha256 = session.HostSha256
        };
    }
}
