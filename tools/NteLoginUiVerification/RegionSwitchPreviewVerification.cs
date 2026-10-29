using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class RegionSwitchPreviewVerification
{
    internal static int Run(string hostDirectory, string directory)
    {
        Application? app = null;
        Window? window = null;
        DispatcherTimer? timer = null;
        int exitCode = 1;
        try
        {
            string root = Program.RepositoryRoot();
            hostDirectory = Program.UnderArtifacts(root, hostDirectory);
            directory = Program.UnderArtifacts(root, directory);
            Directory.CreateDirectory(Path.Combine(directory, ".collapse-nte"));
            string journal = Path.Combine(directory, ".collapse-nte", "switch.json");
            File.WriteAllText(journal, "{}");
            app = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string candidate = Path.Combine(hostDirectory, name.Name + ".dll");
                return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
            };
            string host = Path.Combine(hostDirectory, "Nte.LoginHost.dll");
            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(host);
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/Nte.LoginHost;component/Styles/Collapse.Adapted.xaml") });
            Type text = assembly.GetType("Nte.LoginHost.Localization.LoginText", true)!;
            text.GetMethod("UseLanguage", UiSession.Members)!.Invoke(null, ["zh-CN"]);
            string Text(string key) => (string)text.GetMethod("Get", UiSession.Members)!.Invoke(null, [key, Array.Empty<object>()])!;
            Type regionType = assembly.GetType("Nte.Login.NteHostRegion", true)!;
            object options = Activator.CreateInstance(assembly.GetType("Nte.Login.NteRegionSwitchOptions", true)!, UiSession.Members, null,
                [directory, Enum.Parse(regionType, "Taiwan"), "zh-CN"], null)!;
            object client = Activator.CreateInstance(assembly.GetType("Nte.LoginHost.Services.NativeRegionSwitchClient", true)!, true)!;
            object model = Activator.CreateInstance(assembly.GetType("Nte.LoginHost.ViewModels.RegionSwitchViewModel", true)!, UiSession.Members, null,
                [client, options, (Func<string, string>)Text], null)!;
            object? Value(string name) => model.GetType().GetProperty(name, UiSession.Members)!.GetValue(model);
            window = (Window)Activator.CreateInstance(assembly.GetType("Nte.LoginHost.RegionSwitchWindow", true)!, UiSession.Members, null, [model], null)!;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000; window.Top = -20000;
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (_, _) =>
            {
                try
                {
                    if (Value("IsBusy") is true || string.IsNullOrEmpty(Value("Status") as string))
                    {
                        if (DateTime.UtcNow > deadline) throw new TimeoutException("Synthetic switch preview did not finish.");
                        return;
                    }
                    if ((string?)Value("Status") != Text("ServerSwitchReady") || (string?)Value("Summary") != Text("ServerSwitchRecovery"))
                        throw new InvalidOperationException("Native recovery preview failed: " + Value("Detail"));
                    if (File.ReadAllText(journal) != "{}" || File.Exists(Path.Combine(directory, ".collapse-nte", "state.json")))
                        throw new InvalidOperationException("The switch preview changed fixture state.");
                    window.UpdateLayout();
                    Button[] buttons = Walk(window).OfType<Button>().ToArray();
                    if (buttons.Length != 3 || !buttons[0].IsEnabled || !buttons[1].IsEnabled || buttons[2].IsEnabled)
                        throw new InvalidOperationException("Switch preview commands were not bound correctly.");
                    FrameworkElement content = (FrameworkElement)window.Content;
                    RenderTargetBitmap bitmap = new((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    DrawingVisual canvas = new();
                    using (DrawingContext drawing = canvas.RenderOpen())
                    {
                        Rect bounds = new(0, 0, content.ActualWidth, content.ActualHeight);
                        drawing.DrawRectangle(Brushes.White, null, bounds);
                        drawing.DrawRectangle(new VisualBrush(content), null, bounds);
                    }
                    bitmap.Render(canvas);
                    PngBitmapEncoder png = new(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    string capture = Path.Combine(directory, "switch-preview.png");
                    using (FileStream output = File.Create(capture)) png.Save(output);
                    object report = new { passed = true, construction = true, nativeRecoveryPreview = true, commandsBound = true,
                        journalUnchanged = true, syntheticOnly = true, authenticationRequests = 0, desktopCaptured = false,
                        hostSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(host))), capture };
                    string json = JsonSerializer.Serialize(report, Program.Json);
                    File.WriteAllText(Path.Combine(directory, "result.json"), json); Console.WriteLine(json);
                    exitCode = 0; timer.Stop(); window.Close(); app.Shutdown();
                }
                catch (Exception error) { timer.Stop(); ReportFailure(error, directory); window.Close(); app.Shutdown(); }
            };
            window.Show(); timer.Start(); app.Run();
            return exitCode;
        }
        catch (Exception error)
        {
            timer?.Stop(); window?.Close();
            ReportFailure(error, directory);
            return 1;
        }
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Walk(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void ReportFailure(Exception error, string directory)
    {
        Exception cause = error.GetBaseException();
        // This mode constructs only a synthetic switch view; it has no login or account inputs.
        string json = JsonSerializer.Serialize(new { passed = false, error = cause.GetType().Name,
            detail = cause.Message, stack = cause.StackTrace, syntheticOnly = true, authenticationRequests = 0 }, Program.Json);
        Console.WriteLine(json);
        if (Directory.Exists(directory)) File.WriteAllText(Path.Combine(directory, "result.json"), json);
    }
}
