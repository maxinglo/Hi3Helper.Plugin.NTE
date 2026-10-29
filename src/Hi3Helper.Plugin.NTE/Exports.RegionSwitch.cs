using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace Hi3Helper.Plugin.NTE;

public partial class Exports
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NteRegionSwitchDelegate(char* gameRoot, int gameRootLength,
        char* targetAppId, int targetAppIdLength, char* reportPath, int reportPathLength, nint progressCallback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NteRegionSwitchProgressDelegate(int phase, long processed, long total, int files,
        int count, char* path, int pathLength);

    private static unsafe void RegisterRegionSwitchExports()
    {
        TryRegisterApiExport<NteRegionSwitchDelegate>("PlanNteRegionSwitch", PlanNteRegionSwitch);
        TryRegisterApiExport<NteRegionSwitchDelegate>("SwitchNteRegion", SwitchNteRegion);
    }

    private static unsafe int PlanNteRegionSwitch(char* root, int rootLength, char* target, int targetLength,
        char* report, int reportLength, nint callback) => RunRegionSwitch(root, rootLength, target, targetLength,
        report, reportLength, callback, false);

    private static unsafe int SwitchNteRegion(char* root, int rootLength, char* target, int targetLength,
        char* report, int reportLength, nint callback) => RunRegionSwitch(root, rootLength, target, targetLength,
        report, reportLength, callback, true);

    private static unsafe int RunRegionSwitch(char* rootPointer, int rootLength, char* targetPointer, int targetLength,
        char* reportPointer, int reportLength, nint callbackPointer, bool apply)
    {
        string? reportPath = null;
        try
        {
            if (rootPointer == null || targetPointer == null || reportPointer == null || rootLength is < 1 or > 32767 ||
                targetLength is < 1 or > 16 ||
                reportLength is < 1 or > 32767) throw new ArgumentException("Invalid region switch arguments.");
            string root = new(rootPointer, 0, rootLength);
            reportPath = new(reportPointer, 0, reportLength);
            if (!Path.IsPathFullyQualified(root) || !Path.IsPathFullyQualified(reportPath))
                throw new ArgumentException("Region switch paths must be absolute.");
            NteRegionConfig target = NteConfigProvider.GetRegion(new(targetPointer, 0, targetLength));
            NteRegionSwitchProgressDelegate? callback = callbackPointer == 0
                ? null
                : Marshal.GetDelegateForFunctionPointer<NteRegionSwitchProgressDelegate>(callbackPointer);
            using CancellationTokenSource cancellation = new();

            void Progress(int phase, long done, long total, int files, int count, string path)
            {
                if (callback != null)
                    fixed (char* value = path)
                        if (callback(phase, done, total, files, count, value, path.Length) != 0)
                            cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }

            using var http = new PluginHttpClientBuilder().AllowRedirections().Create();
            NteRegionSwitchService service = new(http);
            NteRegionSwitchPlan plan = (apply
                ? service.SwitchAsync(root, target, Progress, cancellation.Token)
                : service.PlanAsync(root, target, Progress, cancellation.Token)).GetAwaiter().GetResult();
            NteSharedInstallation.WriteAtomic(reportPath,
                JsonSerializer.SerializeToUtf8Bytes(plan, NteRegionSwitchPlanJsonContext.Default.NteRegionSwitchPlan));
            return 0;
        }
        catch (Exception error)
        {
            if (reportPath != null)
                try
                {
                    NteSharedInstallation.WriteAtomic(reportPath, JsonSerializer.SerializeToUtf8Bytes(
                        new NteRegionSwitchPlan
                            { Error = error is OperationCanceledException ? "cancelled" : error.Message },
                        NteRegionSwitchPlanJsonContext.Default.NteRegionSwitchPlan));
                }
                catch (Exception)
                {
                    /* A diagnostic write must not escape the native ABI. */
                }

            return error is OperationCanceledException ? unchecked((int)0x800704C7) : Marshal.GetHRForException(error);
        }
    }
}
