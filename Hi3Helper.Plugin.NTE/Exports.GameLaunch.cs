using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Management.Game;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
            if (!TryGetGameProcessFromContext(context, startArgument, out Process? process))
            {
                return false;
            }

            using (process)
            {
                process.Start();

                try
                {
                    process.PriorityBoostEnabled = isRunBoosted;
                    process.PriorityClass = processPriority;
                }
                catch (Exception ex)
                {
                    InstanceLogger.LogError(ex,
                        "[NTE::LaunchGameFromGameManagerCoreAsync] Failed to set process priority. Ignoring.");
                }

                await process.WaitForExitAsync(token);
                return true;
            }
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
        isGameRunning = process != null;
        gameStartTime = process?.StartTime ?? default;
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

            using Process? process = FindExecutableProcess(gameExecutablePath);
            if (process != null)
            {
                await process.WaitForExitAsync(token);
            }

            return true;
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

        ReadOnlySpan<char> executableDirPath = Path.GetDirectoryName(executablePath.AsSpan());
        string executableName = Path.GetFileNameWithoutExtension(executablePath);

        Process[] processes = Process.GetProcessesByName(executableName);
        Process? returnProcess = null;

        foreach (Process process in processes)
        {
            try
            {
                if (process.MainModule?.FileName != null &&
                    process.MainModule.FileName.StartsWith(executableDirPath, StringComparison.OrdinalIgnoreCase))
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

        if (string.IsNullOrEmpty(gamePath))
        {
            return false;
        }

        gameExecutablePath = Path.Combine(gamePath, executablePath);
        return File.Exists(gameExecutablePath);
    }

    private static bool TryGetGameProcessFromContext(
        GameManagerExtension.RunGameFromGameManagerContext context,
        string? startArgument,
        [NotNullWhen(true)] out Process? process)
    {
        process = null;
        if (!TryGetGameExecutablePath(context, out string? gameExecutablePath))
        {
            SharedStatic.InstanceLogger.LogError(
                "[NTE::TryGetGameProcessFromContext] Failed to resolve game executable path.");
            return false;
        }

        string mergedArguments = string.IsNullOrWhiteSpace(startArgument)
            ? NteConfigProvider.RequiredStartArgument
            : $"{NteConfigProvider.RequiredStartArgument} {startArgument}";

        SharedStatic.InstanceLogger.LogInformation(
            "[NTE::TryGetGameProcessFromContext] Launching executable: {Path} | Arguments: {Args}",
            gameExecutablePath, mergedArguments);

        ProcessStartInfo startInfo = new ProcessStartInfo(gameExecutablePath, mergedArguments)
        {
            WorkingDirectory = Path.GetDirectoryName(gameExecutablePath) ?? string.Empty
        };

        process = new Process
        {
            StartInfo = startInfo
        };
        return true;
    }
}

