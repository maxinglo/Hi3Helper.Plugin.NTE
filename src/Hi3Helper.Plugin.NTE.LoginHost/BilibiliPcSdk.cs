using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Nte.Login;

namespace Nte.LoginHost;

// The installed Bilibili library owns its authentication windows and the
// PCGamePlatform process. These callbacks borrow their buffers until return.
internal sealed class BilibiliPcSdk : IDisposable
{
    private const string SdkSha256 = "C75FB3D291AF28CB31B22613ECCDCBE12F7702CB634F96F72B786EA80F29F549";
    private const uint LoadLibrarySearchDllLoadDir = 0x100;
    private const uint LoadLibrarySearchDefaultDirs = 0x1000;
    private const int MaximumCallbackBytes = 65536;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void SdkCallback(nint buffer, int byteCount);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Initialize(nint json, nint owner, byte exclusive, byte multiple, SdkCallback callback);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Login(SdkCallback callback);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int Uninitialize();

    private readonly nint module;
    private readonly Initialize initialize;
    private readonly Login login;
    private readonly Uninitialize uninitialize;
    private readonly SdkCallback onInitialization;
    private readonly SdkCallback onLogin;
    private static BilibiliPcSdk? startedSdk;
    private TaskCompletionSource<int>? initialization;
    private TaskCompletionSource<byte[]>? loginResult;
    private bool started;
    private bool ready;
    private bool disposed;

    internal BilibiliPcSdk(string launcherDirectory)
    {
        string directory = Path.Combine(launcherDirectory, "channelSdk", "bilibili");
        string path = Path.Combine(directory, "PCGameSDK.dll");
        using FileStream sdkFile = File.OpenRead(path);
        if (!Convert.ToHexString(SHA256.HashData(sdkFile)).Equals(SdkSha256, StringComparison.Ordinal))
            throw new NteCnLaunchException("BilibiliSdkUnsupported");
        module = LoadLibraryExW(path, 0, LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
        if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            initialize = Bind<Initialize>("SDKInit");
            login = Bind<Login>("SDKLogin");
            uninitialize = Bind<Uninitialize>("SDKUnInit");
            onInitialization = OnInitialization;
            onLogin = OnLogin;
        }
        catch
        {
            NativeLibrary.Free(module);
            throw;
        }
    }

    internal static void RequireInstalledFiles(string launcherDirectory)
    {
        string directory = Path.Combine(launcherDirectory, "channelSdk", "bilibili");
        if (!File.Exists(Path.Combine(directory, "PCGameSDK.dll")) ||
            !File.Exists(Path.Combine(directory, "BLPlatform64", "PCGamePlatform.exe")))
            throw new NteCnLaunchException("BilibiliSdkMissing");
    }

    internal async Task InitializeAsync(byte[] configuration, nint owner, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ready) return;
        if (started || configuration.Length < 2 || configuration[^1] != 0 || owner == 0)
            throw new InvalidOperationException("Bilibili SDK cannot initialize in its current state.");
        token.ThrowIfCancellationRequested();
        TaskCompletionSource<int> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref initialization, result);
        try
        {
            // SDKUnInit does not guarantee the native callbacks have drained.
            startedSdk = this;
            started = true;
            if (Start(configuration, owner) != 0) throw new NteCnLaunchException("BilibiliSdkInitFailed");
            if (await result.Task.WaitAsync(token) != 0) throw new NteCnLaunchException("BilibiliSdkInitFailed");
            ready = true;
        }
        finally { Interlocked.Exchange(ref initialization, null); }
    }

    internal async Task<byte[]> LoginAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!ready) throw new InvalidOperationException("Bilibili SDK is not initialized.");
        token.ThrowIfCancellationRequested();
        TaskCompletionSource<byte[]> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref loginResult, result, null) != null)
            throw new InvalidOperationException("A Bilibili sign-in is already active.");
        try
        {
            if (login(onLogin) != 0) throw new NteCnLaunchException("BilibiliSdkLoginFailed");
            return await result.Task.WaitAsync(token);
        }
        catch
        {
            if (!result.TrySetCanceled(token) && result.Task.IsCompletedSuccessfully)
                CryptographicOperations.ZeroMemory(result.Task.Result);
            throw;
        }
        finally { Interlocked.Exchange(ref loginResult, null); }
    }

    private unsafe int Start(byte[] configuration, nint owner)
    {
        fixed (byte* pointer = configuration) return initialize((nint)pointer, owner, 0, 0, onInitialization);
    }

    private void OnInitialization(nint buffer, int byteCount)
    {
        TaskCompletionSource<int>? result = Volatile.Read(ref initialization);
        if (result == null) return;
        byte[] bytes = CopyCallback(buffer, byteCount);
        try
        {
            int code = ReadCode(bytes);
            if (code != 100) result.TrySetResult(code);
        }
        catch (JsonException) { result.TrySetResult(-1); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void OnLogin(nint buffer, int byteCount)
    {
        TaskCompletionSource<byte[]>? result = Volatile.Read(ref loginResult);
        if (result == null) return;
        byte[] bytes = CopyCallback(buffer, byteCount);
        try
        {
            // The SDK can report code 100 before its authentication dialog
            // completes. Only a terminal callback may end the login attempt.
            if (ReadCode(bytes) == 100)
            {
                CryptographicOperations.ZeroMemory(bytes);
                return;
            }
        }
        catch (JsonException) { /* The caller rejects the malformed result. */ }
        if (!result.TrySetResult(bytes)) CryptographicOperations.ZeroMemory(bytes);
    }

    internal static int ReadCode(ReadOnlyMemory<byte> bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out JsonElement code) &&
            code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int value) ? value : -1;
    }

    private static unsafe byte[] CopyCallback(nint buffer, int length) =>
        buffer == 0 || length is <= 0 or > MaximumCallbackBytes ? [] : new ReadOnlySpan<byte>((void*)buffer, length).ToArray();

    private T Bind<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (started)
        {
            _ = uninitialize();
            GC.KeepAlive(startedSdk);
        }
        else NativeLibrary.Free(module);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);
}
