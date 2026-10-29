using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

internal static class Program
{
    internal static readonly JsonSerializerOptions Json = new()
        { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 3 && args[0] == "switch-preview")
                return RegionSwitchPreviewVerification.Run(args[1], args[2]);
            if (args.Length is 4 or 5 && args[0] == "hello" && (args.Length == 4 || args[4] == "--dry-run"))
                return HelloDeviceVerification.Run(args[1], args[2], args[3], args.Length == 5);
            if (args.Length == 3 && args[0] == "call") return CallAsync(args[1], args[2]).GetAwaiter().GetResult();
            if (args.Length != 6 || args[0] != "serve")
                throw new ArgumentException(
                    "serve <published-host-dir> <session-dir> <fixture-pid> <CN|Global|Taiwan> <language>, or call <session-dir> <request-json>");
            string root = RepositoryRoot();
            string hostDirectory = UnderArtifacts(root, args[1]);
            string sessionDirectory = UnderArtifacts(root, args[2]);
            if (args[4] is not ("CN" or "Global" or "Taiwan")) throw new ArgumentException("Unknown preview region.");
            using Process fixture = Process.GetProcessById(int.Parse(args[3]));
            string expectedFixture = Path.Combine(root, "tools", "NteLoginHostHarness", "bin", "Release",
                "net10.0-windows", "NteLoginHostHarness.exe");
            if (!string.Equals(fixture.MainModule?.FileName, expectedFixture, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only this repository's synthetic window fixture is supported.");
            Directory.CreateDirectory(sessionDirectory);
            if (File.Exists(Path.Combine(sessionDirectory, "endpoint.json")))
                throw new InvalidOperationException("Choose a new verification session.");
            Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            using UiSession session = new(hostDirectory, sessionDirectory, fixture.Id, args[4], args[5], root);
            string pipe = "Collapse.NTE.UiVerification." + Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(sessionDirectory, "endpoint.json"), JsonSerializer.Serialize(new
            {
                pipe, processId = Environment.ProcessId, syntheticOnly = true, region = args[4], language = args[5],
                hostSha256 = session.HostSha256
            }, Json));
            Task server = ServeAsync(session, pipe);
            Dispatcher.Run();
            if (server.IsFaulted) throw new InvalidOperationException("Verification server failed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    ok = false, error = error.GetType().Name,
                    detail = "Verification setup or request failed; no authentication was attempted."
                }, Json));
            return 1;
        }
    }

    private static async Task ServeAsync(UiSession session, string pipeName)
    {
        Dispatcher dispatcher = session.Window.Dispatcher;
        try
        {
            while (true)
            {
                using NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                bool close = false;
                try
                {
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
                    byte[] requestBytes = await ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
                    using JsonDocument request = JsonDocument.Parse(requestBytes);
                    JsonElement command = request.RootElement;
                    Exception? rejected = null;
                    try
                    {
                        await dispatcher.InvokeAsync(() => session.Apply(command), DispatcherPriority.Normal);
                    }
                    catch (Exception error)
                    {
                        rejected = error;
                    }

                    await dispatcher.InvokeAsync(() => session.Window.UpdateLayout(),
                        DispatcherPriority.ApplicationIdle);
                    object response = await dispatcher.InvokeAsync(() => session.Response(command, rejected),
                        DispatcherPriority.ApplicationIdle);
                    await WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(response, Json), timeout.Token)
                        .ConfigureAwait(false);
                    close = rejected == null && command.GetProperty("action").GetString() == "close";
                }
                catch (Exception error) when (error is IOException or JsonException or OperationCanceledException
                                                  or InvalidDataException)
                {
                    // Malformed or disconnected clients cannot affect the owned preview.
                }

                if (!close) continue;
                await dispatcher.InvokeAsync(session.Close);
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                return;
            }
        }
        catch
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            throw;
        }
    }

    private static async Task<int> CallAsync(string sessionDirectory, string requestPath)
    {
        string root = RepositoryRoot();
        sessionDirectory = UnderArtifacts(root, sessionDirectory);
        if (new FileInfo(requestPath).Length is < 2 or > 65536) throw new InvalidDataException("Request size bound.");
        using JsonDocument endpoint =
            JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(sessionDirectory, "endpoint.json")));
        if (!endpoint.RootElement.GetProperty("syntheticOnly").GetBoolean())
            throw new InvalidDataException("Not a verification endpoint.");
        string pipeName = endpoint.RootElement.GetProperty("pipe").GetString()!;
        if (!pipeName.StartsWith("Collapse.NTE.UiVerification.", StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected endpoint.");
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await WriteAsync(pipe, await File.ReadAllBytesAsync(requestPath, timeout.Token), timeout.Token);
        byte[] response = await ReadAsync(pipe, timeout.Token);
        using JsonDocument json = JsonDocument.Parse(response);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(response));
        return json.RootElement.GetProperty("ok").GetBoolean() ? 0 : 2;
    }

    private static async Task<byte[]> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 2 or > 1024 * 1024) throw new InvalidDataException("Message size bound.");
        byte[] bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken token)
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    internal static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(Environment.CurrentDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Hi3Helper.Plugin.NTE.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Run from this repository.");
    }

    internal static string UnderArtifacts(string root, string path)
    {
        string resolved = Path.GetFullPath(path);
        if (!resolved.StartsWith(Path.Combine(root, "artifacts") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Verification inputs and outputs must stay under this workspace's artifacts.");
        return resolved;
    }
}
