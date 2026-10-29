using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Separate opt-in developer surface. It has no control pipe and no account,
// browser or game-service code. Only a human click starts a real Hello request.
internal static class HelloDeviceVerification
{
    internal static int Run(string hostDirectory, string directory, string language, bool dryRun)
    {
        string root = Program.RepositoryRoot();
        hostDirectory = Program.UnderArtifacts(root, hostDirectory); directory = Program.UnderArtifacts(root, directory);
        Directory.CreateDirectory(directory);
        string dll = Path.Combine(hostDirectory, "Nte.LoginHost.dll");
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll)));
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string candidate = Path.Combine(hostDirectory, name.Name + ".dll");
            return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
        };
        Assembly host = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        UiSession.Call(host.GetType("Nte.LoginHost.Localization.LoginText", true)!, "UseLanguage", language);
        Type protectorType = host.GetType("Nte.LoginHost.Accounts.HelloAccountKeyProtector", true)!;
        Type scopeType = host.GetType("Nte.LoginHost.Accounts.HelloPromptWindowScope", true)!;
        object protector = Activator.CreateInstance(protectorType, true)!;
        MethodInfo wrap = protectorType.GetMethod("WrapAsync", UiSession.Members)!;
        MethodInfo unwrap = protectorType.GetMethod("UnwrapAsync", UiSession.Members)!;
        MethodInfo delete = protectorType.GetMethod("Delete", UiSession.Members)!;
        if (wrap == null || unwrap == null || delete == null) throw new MissingMethodException("Hello verification adapter changed.");

        Application application = new() { ShutdownMode = ShutdownMode.OnMainWindowClose };
        StackPanel panel = new() { Margin = new(24) };
        panel.Children.Add(new TextBlock { Text = "Windows Hello · 开发验证程序", FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 16) });
        panel.Children.Add(new TextBlock
        {
            Text = "本程序只用随机测试数据验证插件内的 Windows Hello 保护与临时置顶。不会读取游戏账号、修改登录设置或连接登录服务。\n\n点击开始后，请完成系统要求的每次验证：创建测试保护、第一次解锁、第二次解锁。请保持本窗口打开，直到下方明确显示“验证成功”。完成一次系统提示后，流程可能仍在继续。测试密钥随后删除。",
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 18)
        });
        Button begin = new() { Content = "开始验证（会请求 Windows Hello）", MinHeight = 40, FontSize = 14 };
        Button close = new() { Content = "关闭", MinHeight = 32, Margin = new(0, 12, 0, 0) };
        TextBlock status = new() { Text = "已准备。等待本人点击开始。", FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new(0, 16, 0, 0) };
        panel.Children.Add(begin); panel.Children.Add(status); panel.Children.Add(close);
        Window window = new()
        {
            Title = "NTE 开发验证程序 · Windows Hello", Content = panel, Width = 560, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, FontFamily = new("Microsoft YaHei UI"), WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        if (dryRun) { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = window.Top = -16000; window.ShowActivated = false; }
        bool busy = false;
        string state = "prepared", phase = "waiting-user"; int calls = 0; string? error = null;
        bool promoted = false, restored = false, cleaned = true, gestureRequested = false, success = false;
        bool wrappedPassed = false, firstPassed = false, secondPassed = false, ownerCancellationRequested = false;
        string? errorPhase = null;
        CancellationTokenSource? cancellation = null;
        nint owner = 0;

        void Save()
        {
            var report = new
            {
                state, phase, hostSha256 = sha, hostDirectory, dryRun, userInitiated = gestureRequested,
                securityGestureRequested = gestureRequested, unwrapRequests = calls, ownerPromoted = promoted, ownerRestored = restored,
                keyCleanupPassed = cleaned, roundTripPassed = success, nativeError = error,
                protectionPassed = wrappedPassed, firstUnwrapPassed = firstPassed, secondUnwrapPassed = secondPassed,
                ownerCancellationRequested, errorPhase,
                syntheticCredentialsOnly = true, realAccountRead = false, settingsChanged = false, authenticationRequests = 0,
                processId = Environment.ProcessId, observedUtc = DateTimeOffset.UtcNow
            };
            string temporary = Path.Combine(directory, "state.tmp");
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, Program.Json));
            File.Move(temporary, Path.Combine(directory, "state.json"), true);
        }
        void Capture()
        {
            window.UpdateLayout();
            RenderTargetBitmap bitmap = new((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using FileStream file = File.Create(Path.Combine(directory, "verification-surface.png")); encoder.Save(file);
        }

        window.Loaded += (_, _) =>
        {
            owner = new WindowInteropHelper(window).Handle; Save(); Capture();
            if (dryRun) window.Dispatcher.BeginInvoke(() => window.Close());
        };
        close.Click += (_, _) =>
        {
            if (busy) { ownerCancellationRequested = true; cancellation?.Cancel(); status.Text = "已请求取消。请在系统 Windows Hello 窗口中选择取消，等待操作结束。"; Save(); }
            else window.Close();
        };
        window.Closing += (_, args) =>
        {
            if (busy) { args.Cancel = true; ownerCancellationRequested = true; cancellation?.Cancel(); Save(); }
        };
        begin.Click += async (_, _) =>
        {
            if (dryRun || busy) return;
            busy = true; begin.IsEnabled = false; close.Content = "取消验证"; cancellation = new(); gestureRequested = true; state = "running";
            Guid fixture = Guid.NewGuid(); byte[] key = RandomNumberGenerator.GetBytes(32), wrapped = [], first = [], second = [];
            bool original = Topmost(owner); IDisposable? scope = null;
            try
            {
                scope = (IDisposable)Activator.CreateInstance(scopeType, UiSession.Members, null, [owner, true], null)!;
                promoted = Topmost(owner); phase = "protecting-fixture"; status.Text = "步骤 1/3：请完成系统验证，创建测试保护。后面还有两次解锁，请勿关闭本窗口。"; Save();
                wrapped = await (Task<byte[]>)wrap.Invoke(protector, [fixture, key, owner, cancellation.Token])!;
                wrappedPassed = true; phase = "unlocking-first"; status.Text = "步骤 2/3：正在进行第一次解锁，请继续完成系统验证。"; Save();
                calls++; first = await (Task<byte[]>)unwrap.Invoke(protector, [fixture, wrapped, owner, cancellation.Token])!;
                if (!CryptographicOperations.FixedTimeEquals(key, first)) throw new CryptographicException("Fixture verification failed.");
                firstPassed = true; phase = "unlocking-again"; status.Text = "步骤 3/3：正在进行第二次解锁，请继续完成系统验证。"; Save();
                calls++; second = await (Task<byte[]>)unwrap.Invoke(protector, [fixture, wrapped, owner, cancellation.Token])!;
                success = CryptographicOperations.FixedTimeEquals(key, second);
                if (!success) throw new CryptographicException("Fixture verification failed.");
                secondPassed = true; state = "succeeded"; phase = "complete"; status.Text = "验证成功：随机测试数据保护、两次解锁均已完成。可以关闭本窗口，并确认系统弹窗是否始终可见。";
            }
            catch (Exception failure)
            {
                Exception actual = failure is TargetInvocationException { InnerException: { } inner } ? inner : failure;
                state = actual is OperationCanceledException ? "cancelled" : "failed";
                error = $"0x{actual.GetBaseException().HResult:X8}"; errorPhase = phase; phase = "complete";
                status.Text = state == "cancelled" ? "验证已取消。真实账号和登录设置未更改。" : "设备验证未完成（" + error + "）。真实账号和登录设置未更改。";
            }
            finally
            {
                try { delete.Invoke(protector, [fixture]); }
                catch { cleaned = false; }
                try { scope?.Dispose(); restored = Topmost(owner) == original; }
                catch { restored = false; }
                CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(wrapped);
                CryptographicOperations.ZeroMemory(first); CryptographicOperations.ZeroMemory(second);
                busy = false; close.Content = "关闭"; cancellation.Dispose(); cancellation = null; Save(); Capture();
            }
        };
        application.Run(window);
        return 0;
    }

    private static bool Topmost(nint owner) => (GetWindowLongPtr(owner, -20).ToInt64() & 8) != 0;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint owner, int index);
}
