using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using Nte.Login;

namespace Nte.LoginHost;

// The launcher is deliberately absent from this lifetime graph: this process
// owns the game, SDK objects and login window. Only the game exit cancels it.
internal sealed class GameLoginHost(NteHostLaunchOptions options, LoginWindow window) : IDisposable
{
    private readonly string root = Path.GetFullPath(options.GameRoot);
    private readonly CancellationTokenSource lifetime = new();
    private ProbeAuthenticationClient? authentication;
    private ProbePreLoginClient? preLogin;
    private ProbeLoginAdmission? admission;
    private ProbeAntifatigueQueries? queries;
    private ProbeCallbackTransport? callback;
    private Task loginTask = Task.CompletedTask;
    private ProbeEnvironment? context;
    private bool disposed;
    private readonly SynchronizationContext ui = SynchronizationContext.Current ?? throw new InvalidOperationException("UI context required.");

    internal async Task<bool> RunAsync()
    {
        string executable = Path.Combine(root, "Client/WindowsNoEditor/HT/Binaries/Win64/HTGame.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Installed client missing.");
        foreach (string name in new[] { "HTGame", "NTEGame", "NTEBrowser", "NTEUpdate", "NTELauncher" })
        {
            Process[] running = Process.GetProcessesByName(name);
            try { if (running.Length != 0) throw new InvalidOperationException("A game or official platform is already running."); }
            finally { foreach (Process process in running) process.Dispose(); }
        }
        string launcherImage = Path.Combine(root, "NTELauncher/NTEGame.exe");
        if (await HashAsync(launcherImage) != "3CF58ECBE4CD33A83E741C12437CA9BB67678D166548C5972579C1266F250D9F")
            throw new InvalidDataException("Unverified authentication configuration image.");
        string sdkPath = Path.GetFullPath(File.ReadAllText(Path.Combine(Path.GetDirectoryName(executable)!, "GameLauncher.config")).Trim());
        if (!sdkPath.EndsWith("WPGameClientSDK_64.dll", StringComparison.OrdinalIgnoreCase) ||
            await HashAsync(sdkPath) != "4101B035406BECDA3EACBF0B2DABD7F43D9DF3F9294906706FC504623E7CD13B")
            throw new InvalidDataException("Unverified SDK callback image.");
        using Mutex instance = new(false, @"Local\Collapse.NTE.LoginHost.1289", out bool created);
        if (!created) throw new InvalidOperationException("An independent login host is already running.");
        context = new(root, true, useOwnExecutable: true, hostExecutable: Environment.ProcessPath!);
        context.GameWindowChanged += window.NotifySdkGeometryChanged;
        context.LogoutRequested += () => ui.Post(_ => window.OnSdkLogout(), null);
        string pipeName = "Collapse.NTE.Login." + Guid.NewGuid().ToString("N");
        using NamedPipeServerStream pipe = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        ProcessStartInfo start = new(executable, options.GameArguments)
        {
            WorkingDirectory = Path.Combine(root, "Client"), UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["ArcGameP"] = pipeName;
        start.Environment["ArcGameSDKSharePath"] = sdkPath[..^"_64.dll".Length];
        foreach (string absent in new[] { "ArcGameAppId", "GameVersion", "_PATCHER_RES_VERSION_" })
            start.Environment.Remove(absent);
        start.Environment["_PATCHER_RES_PATH_"] = root;
        start.Environment["_PATCHER_SDK_PATH_"] = Path.Combine(root, "NTELauncher/UserData/Patcher/PatcherSDK");
        // Only use paths from a matching installed configuration; fall back to
        // the previous two-path launch when it is absent or unrecognized.
        PatcherLaunchEnvironment.TryApply(start.Environment, root, context.ApplicationId);
        foreach (string flag in new[] { "wmgplaunch", "ArcGameMobileSdkType", "ArcGameMsgSkipFlag" }) start.Environment[flag] = "1";
        start.Environment["GamePlatformPath"] = Path.Combine(root, "NTELauncher");
        // Native SDK logging is isolated and removed after client exit. Raw
        // lines and account payloads never reach host diagnostics.
        string logRoot = Path.Combine(Path.GetTempPath(), "Collapse.NTE.LoginHost", Guid.NewGuid().ToString("N"));
        System.Security.AccessControl.DirectorySecurity acl = new();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            new System.Security.Principal.SecurityIdentifier("S-1-5-18") })
            acl.AddAccessRule(new(sid, System.Security.AccessControl.FileSystemRights.FullControl, inheritance,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        System.IO.FileSystemAclExtensions.Create(new DirectoryInfo(logRoot), acl);
        start.Environment["sdkLogPath"] = logRoot + Path.DirectorySeparatorChar;
        Process? createdGame;
        try { createdGame = Process.Start(start) ?? throw new InvalidOperationException("Cannot start game."); }
        catch
        {
            // A failed CreateProcess has no game lifetime to wait for.
            try { ProbeSdkLog.CollectAndRemove(logRoot); }
            catch (Exception) { /* Keep the original process-start failure. */ }
            throw;
        }
        using Process game = createdGame;
        Task output = Task.CompletedTask, errors = Task.CompletedTask;
        Task exit = WatchExitAsync(game);
        return await GameStartedLifetime.RunAsync(exit, async () =>
        {
            // Start draining redirected handles before attaching the UI: even a
            // failed hook must not leave a live game blocked on a full pipe.
            output = DiscardAsync(game.StandardOutput, lifetime.Token);
            errors = DiscardAsync(game.StandardError, lifetime.Token);
            try { game.PriorityBoostEnabled = options.PriorityBoost; game.PriorityClass = options.Priority; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* Best effort. */ }
            window.Attach(game.Id);
            await ServeAsync(pipe, game.Id);
        }, async _ =>
        {
            // Stop any pending login attempt before presenting the visible
            // failure. Dismissing it does not terminate the game or this host.
            window.CancelForGameExit();
            Task dismissed = window.ShowFailureAsync("登录组件与游戏的通信或窗口初始化失败。游戏仍在运行，请退出游戏后重试。");
            await Task.WhenAny(dismissed, exit);
            if (dismissed.IsCompleted && !exit.IsCompleted) window.Hide();
        }, () => lifetime.IsCancellationRequested, async () =>
        {
            lifetime.Cancel();
            try { await loginTask; }
            catch (Exception) { /* A failed login task must not skip log cleanup. */ }
            try { await Task.WhenAll(output, errors); }
            finally { ProbeSdkLog.CollectAndRemove(logRoot); }
        });
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, int clientId)
    {
        string? application = null;
        while (!lifetime.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(lifetime.Token);
            try
            {
                if (!ProbeNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != clientId)
                    continue; // Reject unrelated same-user peers without ending the game.
                while (pipe.IsConnected && !lifetime.IsCancellationRequested)
                {
                    byte[] buffer = new byte[65536];
                    try
                    {
                        int count = 0, read;
                        do
                        {
                            read = await pipe.ReadAsync(buffer.AsMemory(count), lifetime.Token);
                            count += read;
                            if (count == buffer.Length && !pipe.IsMessageComplete) throw new InvalidDataException("SDK frame size bound.");
                        } while (read != 0 && !pipe.IsMessageComplete);
                        if (count == 0) break;
                        if (count < 12 || BinaryPrimitives.ReadUInt32LittleEndian(buffer) != 0x6B647331 ||
                            BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8)) != count)
                            throw new InvalidDataException("SDK frame header mismatch.");
                        uint operation = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4));
                        if (operation == 1000 && count == 796 && BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12)) == clientId)
                        {
                            application = System.Text.Encoding.Unicode.GetString(buffer, 18, 64).Split('\0')[0];
                            byte[] response = context!.CreateAcknowledgement(buffer.AsSpan(0, count));
                            await pipe.WriteAsync(response, lifetime.Token);
                            await pipe.FlushAsync(lifetime.Token);
                        }
                        if (operation == 0 && count == 1244 && authentication == null)
                        {
                            callback = ProbeCallbackTransport.FromRegistration(buffer.AsSpan(0, count), clientId, context!.WindowHandle);
                            _ = ProbeBootConfig.Inspect(buffer.AsSpan(0, count), clientId, application, root,
                                inspectWithApplicationKey: (configuration, key) =>
                                {
                                    authentication = new(configuration, _ => { }, installedVersion: context.InstalledVersion, applicationKeyTail: key.Span);
                                    return new { captured = true };
                                });
                            if (authentication == null) throw new InvalidDataException("Authentication context unavailable.");
                            preLogin = authentication.CreatePreLoginClient(buffer.AsSpan(0, count), clientId);
                            admission = new(preLogin, _ => { });
                            queries = new(admission, clientId, (response, request, token) => Task.Run(() => callback.Send(response, request, token), token), _ => { });
                            loginTask = RunLoginsAsync();
                        }
                        if (operation == 40 && queries != null)
                        {
                            try { await queries.HandleAsync(buffer.AsMemory(0, count), lifetime.Token); }
                            catch (ProbeAuthenticationException ex) { window.ReportServiceFailure(ex.LocalExplanation ?? "服务端未通过登录前检查。"); }
                            catch (InvalidOperationException) { /* Identity is not established yet: no allow response. */ }
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(buffer); }
                }
            }
            catch (IOException) when (!lifetime.IsCancellationRequested) { /* A separate SDK connection may follow. */ }
            finally { if (pipe.IsConnected) pipe.Disconnect(); }
        }
    }

    private async Task RunLoginsAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await context!.WaitForNextLoginRequestAsync(lifetime.Token);
                await admission!.ResetAsync(lifetime.Token);
                bool consumed = false;
                try
                {
                    if (!await window.EnsureNoticeAsync(false, lifetime.Token)) continue;
                    await window.RequestAsync(authentication!, async (game, channel, token) =>
                    {
                        await admission.AuthorizeAndDeliverAsync(game, async () =>
                        {
                            using ProbeLoginSequence sequence = ProbeLoginSequence.EncodeCurrentBridge(game, channel, authentication!.Platform, authentication.ChannelId, Environment.ProcessId);
                            context.CompleteLoginRequest(); consumed = true;
                            await Task.Run(() => callback!.Send(sequence, token), token);
                        }, token);
                    }, lifetime.Token, token => admission.ResetAsync(token));
                }
                finally { if (!consumed) context.CompleteLoginRequest(); }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task WatchExitAsync(Process game)
    {
        await game.WaitForExitAsync(); lifetime.Cancel();
        ui.Post(_ => window.CancelForGameExit(), null);
    }
    private static async Task DiscardAsync(StreamReader reader, CancellationToken token)
    {
        char[] buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer.AsMemory(), token) != 0) Array.Clear(buffer); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) when (token.IsCancellationRequested) { }
        finally { Array.Clear(buffer); }
    }
    private static async Task<string> HashAsync(string path)
    {
        await using FileStream file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file));
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        lifetime.Cancel(); admission?.Dispose(); preLogin?.Dispose(); authentication?.Dispose(); context?.Dispose(); lifetime.Dispose();
    }
}
