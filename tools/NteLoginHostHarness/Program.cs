using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Nte.LoginHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        if (args.Length >= 2 && args[0] == "inspect")
        {
            int pid = int.Parse(args[1]); nint window = FindOwnedWindow(pid);
            WindowNative.Rect rect = WindowNative.ScreenClient(window);
            nint root = WindowNative.GetAncestor(window, 2), parent = WindowNative.GetParent(window);
            bool nativeChild = (WindowNative.GetWindowLongPtr(window, -16).ToInt64() & 0x40000000) != 0;
            long exStyle = WindowNative.GetWindowLongPtr(window, -20).ToInt64();
            Console.WriteLine(JsonSerializer.Serialize(new { pid, window = (long)window,
                visible = window != 0 && WindowNative.IsWindowVisible(window) && !WindowNative.IsIconic(root),
                minimized = WindowNative.IsIconic(root), nativeChild, parentWindow = (long)parent, rootWindow = (long)root,
                ownerWindow = (long)WindowNative.GetWindow(window, 4), topmost = (exStyle & 8) != 0, layered = (exStyle & 0x80000) != 0,
                left = rect.Left, top = rect.Top, width = rect.Width, height = rect.Height }));
            return 0;
        }
        if (args.Length == 5 && args[0] == "job-launch")
        {
            nint job = CreateJobObjectW(0, null);
            if (job == 0) throw new System.ComponentModel.Win32Exception();
            Limits limits = new(); limits.Basic.Flags = 0x2000 | 0x800; // KILL_ON_JOB_CLOSE | BREAKAWAY_OK
            try
            {
                if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Limits>())) throw new System.ComponentModel.Win32Exception();
                Startup startup = new() { Size = (uint)Marshal.SizeOf<Startup>() };
                string quote(string x) => "\"" + x.Replace("\"", "\\\"") + "\"";
                StringBuilder command = new(quote(args[1]) + " " + quote(args[2]) + " --preview-pid " + quote(args[3]) + " " + quote(args[4]));
                if (!CreateProcessW(args[1], command, 0, 0, false, 0x08000004, 0, Path.GetDirectoryName(args[1]), ref startup, out Info process))
                    throw new System.ComponentModel.Win32Exception();
                try
                {
                    if (!AssignProcessToJobObject(job, process.Process)) throw new System.ComponentModel.Win32Exception();
                    ResumeThread(process.Thread);
                    if (WaitForSingleObject(process.Process, 20000) != 0) throw new TimeoutException("Fixture launcher did not exit.");
                    GetExitCodeProcess(process.Process, out uint code);
                    if (code != 0) throw new InvalidOperationException("Detached launcher failed.");
                }
                finally { CloseHandle(process.Thread); CloseHandle(process.Process); }
            }
            finally { CloseHandle(job); }
            Console.WriteLine("{\"launcherExited\":true,\"killOnCloseJobClosed\":true}");
            return 0;
        }
        if (args.Length != 3 || args[0] is not ("window" or "window-offscreen")) throw new ArgumentException("window|window-offscreen <control-dir> <background>, inspect <pid>, or job-launch <launcher> <host> <game-pid> <result-file>");
        Application.EnableVisualStyles();
        Directory.CreateDirectory(args[1]);
        using Fixture fixture = new(args[1], args[2], args[0] == "window-offscreen"); Application.Run(fixture); return 0;
    }

    private static nint FindOwnedWindow(int processId)
    {
        nint best = 0; long score = 0;
        bool Visit(nint window, nint _)
        {
            WindowNative.GetWindowThreadProcessId(window, out uint pid);
            if (pid == processId && WindowNative.GetClientRect(window, out var rect) && rect.Width >= 200 && rect.Height >= 120)
            {
                // WPF has hidden composition/owner helper HWNDs that can be
                // larger than the actual cover. Prefer its layered surface.
                long style = WindowNative.GetWindowLongPtr(window, -20).ToInt64();
                long candidate = (long)rect.Width * rect.Height + ((style & 0x80000) != 0 ? 1_000_000_000_000L : 0) +
                    (WindowNative.IsWindowVisible(window) ? 10_000_000_000L : 0);
                if (candidate > score) { best = window; score = candidate; }
            }
            return true;
        }
        WindowNative.EnumWindows((window, _) =>
        {
            Visit(window, 0); WindowNative.EnumChildWindows(window, Visit, 0); return true;
        }, 0);
        return best;
    }

    private sealed class Fixture : Form
    {
        private readonly string directory;
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
        private readonly System.Windows.Forms.Timer dragTimer = new() { Interval = 16 };
        private readonly System.Windows.Forms.Timer cursorTimer = new() { Interval = 16 };
        private readonly Stopwatch cursorClock = new();
        private int cursorSamples, cursorMismatches;
        private int gameCursorMessages;
        private string? expectedCursor;
        private Rectangle dragOrigin;
        private nint dragChild;
        private int samples, maxOffsetError, invalidSamples;
        private readonly List<double> alignmentTimes = [];
        private bool moving;
        private readonly Stopwatch dragClock = new();
        private readonly bool offscreen;
        private string seen = "";
        protected override bool ShowWithoutActivation => offscreen || base.ShowWithoutActivation;
        internal Fixture(string directory, string background, bool offscreen = false)
        {
            this.directory = directory; this.offscreen = offscreen; Text = "异环 · 登录界面预览"; ClientSize = new(1180, 730); Location = offscreen ? new(-16000, -16000) : new(40, 40); StartPosition = FormStartPosition.Manual;
            BackgroundImage = Image.FromFile(background); BackgroundImageLayout = ImageLayout.Stretch;
            timer.Tick += (_, _) =>
            {
                string path = Path.Combine(directory, "command.json");
                if (!File.Exists(path)) return;
                string content;
                try { content = File.ReadAllText(path); } catch (IOException) { return; }
                if (content == seen) return;
                try
                {
                    using JsonDocument json = JsonDocument.Parse(content); var command = json.RootElement; seen = content;
                    switch (command.GetProperty("action").GetString())
                    {
                        case "minimize": WindowState = FormWindowState.Minimized; break;
                        case "restore": WindowState = FormWindowState.Normal; Activate(); break;
                        case "move": Bounds = new(150, 100, 1080, 700); Activate(); break;
                        case "resize":
                            int width = command.GetProperty("width").GetInt32(), height = command.GetProperty("height").GetInt32();
                            if (width is < 640 or > 3840 || height is < 360 or > 2160) throw new InvalidDataException("Fixture size bound.");
                            ClientSize = new(width, height); break;
                        case "recreate": RecreateHandle(); Activate(); break;
                        case "drag-check": BeginDragCheck(command.GetProperty("childPid").GetInt32()); break;
                        case "background":
                            Image old = BackgroundImage!; BackgroundImage = Image.FromFile(command.GetProperty("path").GetString()!); old.Dispose(); Activate(); break;
                        case "color":
                            BackgroundImage?.Dispose(); BackgroundImage = null;
                            BackColor = ColorTranslator.FromHtml(command.GetProperty("value").GetString()!); Invalidate(); Activate(); break;
                        case "cursor-watch":
                            Cursor = Cursors.Cross; expectedCursor = CursorShape(Cursor.Handle);
                            cursorSamples = cursorMismatches = 0; cursorClock.Restart(); cursorTimer.Start(); break;
                        case "cursor-stop": cursorTimer.Stop(); WriteCursorReport(); break;
                        case "close": Close(); break;
                    }
                }
                catch (JsonException) { }
            };
            dragTimer.Tick += async (_, _) => await DragStepAsync();
            cursorTimer.Tick += (_, _) =>
            {
                nint foreground = WindowNative.GetForegroundWindow();
                CursorState current = new() { Size = (uint)Marshal.SizeOf<CursorState>() };
                if ((foreground != Handle && WindowNative.GetWindow(foreground, 4) != Handle) || !GetCursorInfo(ref current) || !ClientRectangle.Contains(PointToClient(new Point(current.Position.X, current.Position.Y)))) return;
                cursorSamples++;
                if (CursorShape(current.Cursor) != expectedCursor) cursorMismatches++;
                // Model a game that continues setting its own cursor each frame.
                SetCursor(Cursor.Handle);
                if (cursorSamples % 30 == 0) WriteCursorReport();
            };
            Shown += (_, _) => { timer.Start(); File.WriteAllText(Path.Combine(directory, "ready.json"), JsonSerializer.Serialize(new { pid = Environment.ProcessId })); };
        }
        private void WriteCursorReport() => File.WriteAllText(Path.Combine(directory, "cursor-watch.json"), JsonSerializer.Serialize(new
        { fixture = true, cursorSamples, cursorMismatches, gameCursorMessages, seconds = cursorClock.Elapsed.TotalSeconds, shapeOnly = true, accountRequests = 0 }));
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x20 && expectedCursor != null)
            {
                gameCursorMessages++; SetCursor(Cursor.Handle); message.Result = 1; return;
            }
            base.WndProc(ref message);
        }
        private static string CursorShape(nint cursor)
        {
            using Bitmap bitmap = new(32, 32);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                nint dc = graphics.GetHdc();
                try { if (!DrawIconEx(dc, 0, 0, cursor, 32, 32, 0, 0, 3)) return "unavailable"; }
                finally { graphics.ReleaseHdc(dc); }
            }
            using MemoryStream data = new(); bitmap.Save(data, System.Drawing.Imaging.ImageFormat.Png);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data.ToArray()));
        }
        private void BeginDragCheck(int childPid)
        {
            if (dragTimer.Enabled) throw new InvalidOperationException("Movement check already active.");
            dragChild = FindOwnedWindow(childPid);
            if (dragChild == 0 || WindowNative.GetWindow(dragChild, 4) != Handle)
                throw new InvalidOperationException("Login cover is not owned by this fixture.");
            dragOrigin = Bounds; alignmentTimes.Clear();
            samples = maxOffsetError = invalidSamples = 0;
            dragClock.Restart(); dragTimer.Start();
        }
        private async Task DragStepAsync()
        {
            if (moving) return; moving = true;
            const int steps = 120;
            int x = dragOrigin.X + (int)(110 * Math.Sin(samples * Math.PI / 30));
            int y = dragOrigin.Y + (int)(55 * Math.Sin(samples * Math.PI / 20));
            Stopwatch alignment = Stopwatch.StartNew();
            if (!WindowNative.SetWindowPos(Handle, 0, x, y, 0, 0, 0x15)) invalidSamples++;
            var parent = WindowNative.ScreenClient(Handle); var child = WindowNative.ScreenClient(dragChild);
            if (!WindowNative.IsWindow(dragChild) || WindowNative.GetWindow(dragChild, 4) != Handle) invalidSamples++;
            maxOffsetError = Math.Max(maxOffsetError, Math.Max(Math.Abs(child.Left - parent.Left), Math.Abs(child.Top - parent.Top)));
            // Measure asynchronous event convergence rather than pretending an
            // owned popup moves synchronously with its owner. This is geometry,
            // not a measurement of presented pixels or an actual mouse drag.
            bool aligned = await Task.Run(() =>
            {
                while (alignment.ElapsedMilliseconds < 150)
                {
                    var actual = WindowNative.ScreenClient(dragChild);
                    if (actual.Left == parent.Left && actual.Top == parent.Top && actual.Width == parent.Width && actual.Height == parent.Height) return true;
                    Thread.Sleep(1);
                }
                return false;
            });
            if (!aligned) invalidSamples++;
            alignmentTimes.Add(alignment.Elapsed.TotalMilliseconds); moving = false;
            if (++samples < steps) return;
            dragTimer.Stop(); dragClock.Stop();
            WindowNative.SetWindowPos(Handle, 0, dragOrigin.X, dragOrigin.Y, 0, 0, 0x15);
            File.WriteAllText(Path.Combine(directory, "movement.json"), JsonSerializer.Serialize(new
            {
                fixture = true, check = "event-driven-full-client-cover-alignment", samples,
                elapsedMs = dragClock.ElapsedMilliseconds, maxOffsetErrorPx = maxOffsetError, invalidSamples,
                coverWindow = (long)dragChild, ownerWindow = (long)Handle,
                p50AlignmentObservedMs = alignmentTimes.Order().ElementAt(steps / 2),
                p95AlignmentObservedMs = alignmentTimes.Order().ElementAt((int)(steps * .95)), maxAlignmentObservedMs = alignmentTimes.Max(),
                programmaticMovement = true, displayLatencyMeasured = false,
                passed = invalidSamples == 0 && alignmentTimes.Max() < 150
            }));
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (BackgroundImage == null) { base.OnPaintBackground(e); return; }
            // The night reference contains an OS title bar. Display its client
            // artwork so the fixture does not show two stacked window frames.
            int top = BackgroundImage.Width == 1275 && BackgroundImage.Height == 742 ? 42 : 0;
            e.Graphics.DrawImage(BackgroundImage, ClientRectangle, new Rectangle(0, top, BackgroundImage.Width, BackgroundImage.Height - top), GraphicsUnit.Pixel);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { timer.Dispose(); dragTimer.Dispose(); cursorTimer.Dispose(); BackgroundImage?.Dispose(); }
            base.Dispose(disposing);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct CursorState { public uint Size, Flags; public nint Cursor; public WindowNative.Point Position; }
    [DllImport("user32")] private static extern bool GetCursorInfo(ref CursorState info);
    [DllImport("user32")] private static extern nint SetCursor(nint cursor);
    [DllImport("user32")] private static extern bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public nuint MinSet, MaxSet; public uint Active; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct Limits { public BasicLimits Basic; public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [StructLayout(LayoutKind.Sequential)] private struct Startup { public uint Size; public nint Reserved, Desktop, Title; public uint X, Y, Width, Height, XCount, YCount, Fill, Flags; public ushort Show, ReservedSize; public nint ReservedBytes, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct Info { public nint Process, Thread; public uint Id, ThreadId; }
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern nint CreateJobObjectW(nint attributes, string? name);
    [DllImport("kernel32", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int kind, ref Limits limits, uint size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder command, nint processAttributes, nint threadAttributes, bool inheritHandles, uint flags, nint environment, string? directory, ref Startup startup, out Info information);
    [DllImport("kernel32")] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32")] private static extern uint WaitForSingleObject(nint handle, uint timeout);
    [DllImport("kernel32")] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32")] private static extern bool CloseHandle(nint handle);
}
