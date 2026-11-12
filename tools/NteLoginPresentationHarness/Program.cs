using System.Text.Json;
using Nte.LoginHost;

// A stationary, non-authenticating owner plus an independent native Z-order
// oracle. Never load a game, login service, account store or product settings.
internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (arguments.Length >= 2 && arguments[0] == "inspect-stack")
        {
            int[] processes = arguments.Skip(1).Select(int.Parse).ToArray();
            List<object> windows = [];
            int order = 0;
            WindowNative.EnumWindows((window, _) =>
            {
                WindowNative.GetWindowThreadProcessId(window, out uint pid);
                int index = order++;
                if (processes.Contains((int)pid))
                    windows.Add(new { pid, window = (long)window, order = index });
                return true;
            }, 0);
            Console.WriteLine(JsonSerializer.Serialize(new { windows }));
            return 0;
        }
        if (arguments.Length == 2 && arguments[0] == "inspect")
        {
            int processId = int.Parse(arguments[1]);
            nint selected = 0;
            long largest = 0;
            WindowNative.EnumWindows((window, _) =>
            {
                WindowNative.GetWindowThreadProcessId(window, out uint pid);
                if (pid != processId || !WindowNative.GetClientRect(window, out var size) ||
                    size.Width < 200 || size.Height < 120) return true;
                long score = (long)size.Width * size.Height +
                             (WindowNative.IsWindowVisible(window) ? 1_000_000_000_000L : 0);
                if (score > largest) { selected = window; largest = score; }
                return true;
            }, 0);
            WindowNative.Rect bounds = WindowNative.ScreenClient(selected);
            long style = WindowNative.GetWindowLongPtr(selected, -16).ToInt64();
            long extended = WindowNative.GetWindowLongPtr(selected, -20).ToInt64();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                window = (long)selected, visible = selected != 0 && WindowNative.IsWindowVisible(selected),
                nativeChild = (style & 0x40000000) != 0, topmost = (extended & 8) != 0,
                ownerWindow = (long)WindowNative.GetWindow(selected, 4),
                left = bounds.Left, top = bounds.Top, width = bounds.Width, height = bounds.Height
            }));
            return 0;
        }
        if (arguments.Length is 2 or 3 && arguments[0] == "window")
        {
            Directory.CreateDirectory(arguments[1]);
            Application.EnableVisualStyles();
            using Form owner = new()
            {
                Text = "NTE stationary login presentation fixture", ClientSize = new(1180, 730),
                Location = new(40, 40), StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(36, 50, 72)
            };
            if (arguments.Length == 3 && File.Exists(arguments[2]))
            {
                owner.BackgroundImage = Image.FromFile(arguments[2]);
                owner.BackgroundImageLayout = ImageLayout.Stretch;
            }
            owner.Shown += (_, _) => File.WriteAllText(Path.Combine(arguments[1], "ready.json"),
                JsonSerializer.Serialize(new { pid = Environment.ProcessId }));
            Application.Run(owner);
            owner.BackgroundImage?.Dispose();
            return 0;
        }
        throw new ArgumentException("window <directory> [background], inspect <pid>, or inspect-stack <pids>");
    }
}
