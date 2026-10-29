using Hi3Helper.Plugin.NTE.Management.Game;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.NTE.Management.Config;

namespace Hi3Helper.Plugin.NTE;

public partial class Exports
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int CheckNteIntegrityDelegate(char* gamePath, int gamePathLength,
        char* manifestPath, int manifestPathLength, char* reportPath, int reportPathLength, nint progressCallback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int NteIntegrityProgressCallback(int phase, long processed, long total, int checkedFiles,
        int totalFiles, char* path, int pathLength);

    private static unsafe void RegisterIntegrityExports()
    {
        TryRegisterApiExport<CheckNteIntegrityDelegate>("CheckNteGameIntegrity", CheckNteGameIntegrity);
        TryRegisterApiExport<CheckNteIntegrityDelegate>("RepairNteGame", RepairNteGame);
    }

    // Optional NTE export; the upstream Core/launcher contract stays unchanged.
    private static unsafe int CheckNteGameIntegrity(char* gamePath, int gamePathLength,
        char* manifestPath, int manifestPathLength, char* reportPath, int reportPathLength, nint progressCallback)
        => RunNteMaintenance(gamePath, gamePathLength, manifestPath, manifestPathLength, reportPath, reportPathLength, progressCallback, false);

    private static unsafe int RepairNteGame(char* gamePath, int gamePathLength,
        char* manifestPath, int manifestPathLength, char* reportPath, int reportPathLength, nint progressCallback)
        => RunNteMaintenance(gamePath, gamePathLength, manifestPath, manifestPathLength, reportPath, reportPathLength, progressCallback, true);

    private static unsafe int RunNteMaintenance(char* gamePath, int gamePathLength,
        char* manifestPath, int manifestPathLength, char* reportPath, int reportPathLength, nint progressCallback, bool repair)
    {
        try
        {
            string root = new(gamePath, 0, gamePathLength);
            string report = new(reportPath, 0, reportPathLength);
            string? manifest = manifestPathLength > 0 ? new(manifestPath, 0, manifestPathLength) : null;
            NteIntegrityProgressCallback? callback = progressCallback == 0 ? null : Marshal.GetDelegateForFunctionPointer<NteIntegrityProgressCallback>(progressCallback);
            using CancellationTokenSource cancel = new();
            void Progress(int phase, long processed, long total, int checkedFiles, int totalFiles, string path)
            {
                if (callback != null)
                {
                    fixed (char* pointer = path)
                        if (callback(phase, processed, total, checkedFiles, totalFiles, pointer, path.Length) != 0) cancel.Cancel();
                }
                cancel.Token.ThrowIfCancellationRequested();
            }
            return RunIntegrityAsync(root, manifest, report, Progress, repair, cancel.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { return unchecked((int)0x800704C7); }
        catch (Exception ex) { return Marshal.GetHRForException(ex); }
    }

    private static async Task<int> RunIntegrityAsync(string root, string? manifestPath, string reportPath,
        Action<int, long, long, int, int, string> progress, bool repair, CancellationToken token)
    {
        using FileStream operation = NteGameOperation.Acquire(root);
        NteGameManager manager = new(region: NteConfigProvider.DetectInstalledRegion(root));
        manager.SetGamePath(root);
        manager.LoadConfig();
        NteGameInstaller installer = new(manager);
        try
        {
            NteResListParser manifest;
            if (manifestPath != null && !repair)
            {
                byte[] bytes = await File.ReadAllBytesAsync(manifestPath, token).ConfigureAwait(false);
                manifest = NteResListParser.Parse(NteResListDecryptor.Decrypt(bytes, manager.AppId));
            }
            else
            {
                manifest = await installer.GetIntegrityManifestAsync(token).ConfigureAwait(false);
                if (manifestPath != null) throw new ArgumentException("Repair always uses the active official manifest; omit -ManifestPath.");
            }
            NteIntegrityReport report = await NteGameIntegrity.CheckAsync(manifest, root,
                (done, total, count, files, path) => progress(1, done, total, count, files, path), token).ConfigureAwait(false);
            NteGameIntegrity.SaveReport(report, reportPath);
            if (repair && report.Completed)
            {
                try
                {
                    await installer.RepairFilesAsync(report,
                        (in InstallProgress value) => progress(2, value.DownloadedBytes, value.TotalBytesToDownload,
                            value.DownloadedCount, value.TotalCountToDownload, ""), null, token).ConfigureAwait(false);
                    foreach (NteIntegrityIssue issue in report.Issues) report.RepairedFiles.Add(issue.Path);
                    report.Issues.Clear();
                }
                catch (Exception ex)
                {
                    report.Completed = false;
                    report.RepairError = ex is OperationCanceledException ? "cancelled" : ex.Message;
                    throw;
                }
                finally { report.FinishedUtc = DateTimeOffset.UtcNow; NteGameIntegrity.SaveReport(report, reportPath); }
            }
            return !report.Completed ? unchecked((int)0x800704C7) : report.Healthy ? 0 : 1;
        }
        finally { installer.Dispose(); manager.Dispose(); }
    }
}
