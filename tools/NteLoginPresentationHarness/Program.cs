using System.Text.Json;
using System.Runtime.InteropServices;
using Nte.LoginHost;

// A stationary, non-authenticating owner plus an independent native Z-order
// oracle. Never load a game, login service, account store or product settings.
internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (arguments is ["inspect-cursor"])
        {
            Console.WriteLine(JsonSerializer.Serialize(CursorFixture.Inspect()));
            return 0;
        }
        if (arguments.Length == 3 && arguments[0] == "notify-login")
        {
            using JsonDocument descriptor = JsonDocument.Parse(File.ReadAllBytes(arguments[1]));
            JsonElement sdk = descriptor.RootElement;
            int expectedPid = int.Parse(arguments[2]);
            nint window = (nint)sdk.GetProperty("window").GetInt64();
            uint message = sdk.GetProperty("message").GetUInt32();
            WindowNative.GetWindowThreadProcessId(window, out uint pid);
            if (pid != expectedPid || sdk.GetProperty("hostPid").GetInt32() != expectedPid ||
                message is < 0xc000 or > 0xffff || !PostMessageW(window, message, 1, 0))
                throw new InvalidOperationException("Cannot notify the isolated login fixture.");
            return 0;
        }
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
            Console.WriteLine(JsonSerializer.Serialize(new { foreground = (long)WindowNative.GetForegroundWindow(), windows }));
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
        bool cursorFixture = arguments.Length == 4 && arguments[0] == "window-cursor";
        if (cursorFixture || arguments.Length is 2 or 3 && arguments[0] == "window")
        {
            Directory.CreateDirectory(arguments[1]);
            Application.EnableVisualStyles();
            using Form owner = new()
            {
                Text = "NTE stationary login presentation fixture", ClientSize = new(1180, 730),
                Location = new(40, 40), StartPosition = FormStartPosition.Manual,
                BackColor = Color.FromArgb(36, 50, 72)
            };
            using CursorFixture? customCursor = cursorFixture
                ? new(int.Parse(arguments[2]), int.Parse(arguments[3]))
                : null;
            using System.Windows.Forms.Timer cursorTimer = new() { Interval = 20 };
            if (customCursor != null)
            {
                owner.Text = "NTE cursor sizing fixture";
                owner.Cursor = customCursor.Cursor;
                owner.TopMost = true;
                if (WindowNative.GetCursorPos(out var position))
                {
                    Point pointer = new(position.X, position.Y);
                    Rectangle workArea = Screen.FromPoint(pointer).WorkingArea;
                    owner.Location = new Point(Math.Max(workArea.Left, pointer.X - owner.ClientSize.Width / 2),
                        Math.Max(workArea.Top, pointer.Y - owner.ClientSize.Height / 2));
                }
                owner.Activated += (_, _) => customCursor.ApplyWithin(owner.Handle);
                owner.Shown += (_, _) => customCursor.ApplyWithin(owner.Handle);
                cursorTimer.Tick += (_, _) => customCursor.ApplyWithin(owner.Handle);
                cursorTimer.Start();
            }
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
        throw new ArgumentException(
            "window <directory> [background], window-cursor <directory> <width> <height>, inspect <pid>, inspect-stack <pids>, inspect-cursor, or notify-login <descriptor> <pid>");
    }

    [DllImport("user32")]
    private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}
