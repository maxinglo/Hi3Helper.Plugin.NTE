using Nte.Login;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Nte.LoginHost.Services;

internal sealed class NativeRegionSwitchClient : INteRegionSwitchClient
{
    // NativeAOT libraries are retained for the host process lifetime. This also
    // keeps the verified-file cache between preview and activation.
    private static readonly Lazy<nint> Module = new(() =>
    {
        string hostDirectory = Path.GetDirectoryName(typeof(NativeRegionSwitchClient).Assembly.Location)!;
        string plugin = Path.GetFullPath(Path.Combine(hostDirectory, "..", "NTE.dll"));
        if (!File.Exists(plugin))
            throw new FileNotFoundException("Start server switching from the installed NTE plugin.", plugin);
        return NativeLibrary.Load(plugin);
    });

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int GetExport(char* name, void** value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int SwitchExport(char* root, int rootLength, char* target, int targetLength, char* report,
        int reportLength, nint callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int ProgressCallback(int phase, long processed, long total, int files, int count,
        char* path, int pathLength);

    public Task<NteSwitchSummary> PlanAsync(NteRegionSwitchOptions options, IProgress<NteSwitchProgress> progress,
        CancellationToken token)
        => Task.Run(() => Invoke("PlanNteRegionSwitch", options, progress, token), token);

    public Task<NteSwitchSummary> SwitchAsync(NteRegionSwitchOptions options, IProgress<NteSwitchProgress> progress,
        CancellationToken token)
        => Task.Run(() => Invoke("SwitchNteRegion", options, progress, token), token);

    private static unsafe NteSwitchSummary Invoke(string export, NteRegionSwitchOptions options,
        IProgress<NteSwitchProgress> progress, CancellationToken token)
    {
        string directory = Path.Combine(Path.GetTempPath(), "Collapse.NTE.RegionSwitch");
        Directory.CreateDirectory(directory);
        string report = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
        nint module = Module.Value;
        ProgressCallback callback = (phase, done, total, files, count, path, length) =>
        {
            try
            {
                progress.Report(new(phase, done, total, files, count, length > 0 ? new(path, 0, length) : ""));
            }
            catch (Exception)
            {
                return 1;
            }

            return token.IsCancellationRequested ? 1 : 0;
        };
        try
        {
            GetExport get =
                Marshal.GetDelegateForFunctionPointer<GetExport>(NativeLibrary.GetExport(module, "TryGetApiExport"));
            void* pointer = null;
            fixed (char* name = export)
                if (get(name, &pointer) != 0 || pointer == null)
                    throw new MissingMethodException("The NTE plugin does not provide server switching.");
            SwitchExport run = Marshal.GetDelegateForFunctionPointer<SwitchExport>((nint)pointer);
            string target = options.TargetAppId;
            int result;
            fixed (char* root = options.GameRoot, appId = target, output = report)
                result = run(root, options.GameRoot.Length, appId, target.Length, output, report.Length,
                    Marshal.GetFunctionPointerForDelegate(callback));
            GC.KeepAlive(callback);
            if (!File.Exists(report))
            {
                Marshal.ThrowExceptionForHR(result);
                throw new IOException("The region switch produced no result.");
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(report));
            JsonElement value = document.RootElement;
            string? error = value.TryGetProperty("Error", out var failure) && failure.ValueKind == JsonValueKind.String
                ? failure.GetString()
                : null;
            if (result == unchecked((int)0x800704C7)) throw new OperationCanceledException(token);
            if (result != 0 || error != null) throw new IOException(error ?? "The region switch failed.");
            string Text(string key) => value.GetProperty(key).GetString() ?? "";
            return new(Text("SourceAppId"), Text("TargetAppId"), Text("TargetVersion"),
                value.GetProperty("ReusedFiles").GetInt32(),
                value.GetProperty("CachedFiles").GetInt32(), value.GetProperty("DownloadFiles").GetInt32(),
                value.GetProperty("DownloadBytes").GetInt64(),
                value.GetProperty("Completed").GetBoolean(),
                value.TryGetProperty("RecoveryRequired", out var recovery) && recovery.GetBoolean());
        }
        finally
        {
            GC.KeepAlive(callback);
            if (File.Exists(report)) File.Delete(report);
        }
    }
}
