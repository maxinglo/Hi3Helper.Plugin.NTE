using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using Hi3Helper.Plugin.NTE.Management.PresetConfig;
using Microsoft.Extensions.Logging;
using Nte.Login;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Hi3Helper.Plugin.NTE;

public partial class Exports
{
    /// <inheritdoc/>
    protected override (bool IsSupported, Task<bool> Task) LaunchGameFromGameManagerCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context,
        string? startArgument,
        bool isRunBoosted,
        ProcessPriorityClass processPriority,
        CancellationToken token)
    {
        return (true, Impl());

        async Task<bool> Impl()
        {
            token.ThrowIfCancellationRequested();
            if (!TryGetGameExecutablePath(context, out string? executable))
            {
                return false;
            }
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executable)!, "../../../../.."));
            NteHostRegion region = NteHostRegion.CN;
            if (context.PresetConfig is NteCNPresetConfig preset)
            {
                region = preset.Region.IsGlobal ? NteHostRegion.Global : NteHostRegion.CN;
                startArgument = preset.LaunchSettings.BuildArguments(startArgument);
            }
            int hostPid = NteLoginHostLaunch.Start(new(root, startArgument, isRunBoosted, processPriority, region, SharedStatic.PluginLocaleCode ?? "en-US"));
            InstanceLogger.LogInformation("[NTE::LaunchGameFromGameManagerCoreAsync] Independent login host started: {HostPid}", hostPid);
            try
            {
                using Process host = Process.GetProcessById(hostPid);
                using NteGameLaunchSession session = NteGameLaunchSession.Track(executable, host);
                // Cancellation releases Collapse's wait only. The host owns the
                // game, SDK and UI, and is outside the launcher's job/handles.
                await host.WaitForExitAsync(token);
                return host.ExitCode == 0;
            }
            catch (ArgumentException) { return false; } // Host exited before it could be observed.
        }
    }

    /// <inheritdoc/>
    protected override bool IsGameRunningCore(
        GameManagerExtension.RunGameFromGameManagerContext context,
        out bool isGameRunning,
        out DateTime gameStartTime)
    {
        isGameRunning = false;
        gameStartTime = default;

        if (!TryGetGameExecutablePath(context, out string? gameExecutablePath))
        {
            return true;
        }

        using Process? process = FindExecutableProcess(gameExecutablePath);
        if (process != null)
        {
            NteGameLaunchSession.ObserveGame(gameExecutablePath);
            isGameRunning = true;
            gameStartTime = process.StartTime;
        }
        else
        {
            isGameRunning = NteGameLaunchSession.IsStarting(gameExecutablePath, out gameStartTime);
        }
        return true;
    }

    /// <inheritdoc/>
    protected override (bool IsSupported, Task<bool> Task) WaitRunningGameCoreAsync(
        GameManagerExtension.RunGameFromGameManagerContext context,
        CancellationToken token)
    {
        return (true, Impl());

        async Task<bool> Impl()
        {
            if (!TryGetGameExecutablePath(context, out string? gameExecutablePath))
            {
                return true;
            }

            while (true)
            {
                token.ThrowIfCancellationRequested();
                using Process? process = FindExecutableProcess(gameExecutablePath);
                if (process != null)
                {
                    NteGameLaunchSession.ObserveGame(gameExecutablePath);
                    try { await process.WaitForExitAsync(token); }
                    catch (Win32Exception ex) when (ex.NativeErrorCode == 5 && OperatingSystem.IsWindows())
                    { await NteProcessImage.WaitForExitWithLimitedAccessAsync(process.Id, gameExecutablePath, token); }
                    return true;
                }
                if (!NteGameLaunchSession.IsStarting(gameExecutablePath, out _)) return true;
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    protected override bool KillRunningGameCore(
        GameManagerExtension.RunGameFromGameManagerContext context,
        out bool wasGameRunning,
        out DateTime gameStartTime)
    {
        wasGameRunning = false;
        gameStartTime = default;

        if (!TryGetGameExecutablePath(context, out string? gameExecutablePath))
        {
            return true;
        }

        using Process? process = FindExecutableProcess(gameExecutablePath);
        if (process == null)
        {
            return true;
        }

        wasGameRunning = true;
        gameStartTime = process.StartTime;
        process.Kill();
        return true;
    }

    private static Process? FindExecutableProcess(string? executablePath)
    {
        if (executablePath == null)
        {
            return null;
        }

        string executableName = Path.GetFileNameWithoutExtension(executablePath);

        Process[] processes = Process.GetProcessesByName(executableName);
        Process? returnProcess = null;

        foreach (Process process in processes)
        {
            try
            {
                if (OperatingSystem.IsWindows() && NteProcessImage.Matches(process.Id, executablePath) && !process.HasExited)
                {
                    returnProcess = process;
                    break;
                }
            }
            catch
            {
                // Ignore inaccessible process modules.
            }
        }

        try
        {
            return returnProcess;
        }
        finally
        {
            foreach (Process process in processes)
            {
                if (process != returnProcess)
                {
                    process.Dispose();
                }
            }
        }
    }

    private static bool TryGetGameExecutablePath(
        GameManagerExtension.RunGameFromGameManagerContext context,
        [NotNullWhen(true)] out string? gameExecutablePath)
    {
        gameExecutablePath = null;
        if (context is not { GameManager: NteCNGameManager nteGameManager, PresetConfig: PluginPresetConfigBase presetConfig })
        {
            return false;
        }

        nteGameManager.GetGamePath(out string? gamePath);
        presetConfig.comGet_GameExecutableName(out string executablePath);

        gamePath?.NormalizePathInplace();
        executablePath.NormalizePathInplace();

        if (string.IsNullOrEmpty(gamePath) || !nteGameManager.IsGamePathCompatible(gamePath))
        {
            return false;
        }

        gameExecutablePath = Path.GetFullPath(Path.Combine(gamePath, executablePath));
        return File.Exists(gameExecutablePath);
    }

}

