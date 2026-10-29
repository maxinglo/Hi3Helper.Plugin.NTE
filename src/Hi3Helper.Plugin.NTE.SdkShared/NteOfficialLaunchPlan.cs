using System;
using System.Diagnostics;
using System.IO;

namespace Nte.Login;

internal sealed record NteOfficialLaunchPlan(string Executable, string GameExecutable)
{
    internal static NteOfficialLaunchPlan Create(string gameRoot, bool global)
        => Create(gameRoot, global ? NteHostRegion.Global : NteHostRegion.CN);

    internal static NteOfficialLaunchPlan Create(string gameRoot, NteHostRegion region)
    {
        string root = Path.GetFullPath(gameRoot);
        (string directory, string file) = region switch
        {
            NteHostRegion.Global => ("NTEGlobal", "NTEGlobalGame.exe"),
            NteHostRegion.Taiwan => ("NTETW", "NTETWGame.exe"),
            NteHostRegion.CN => ("NTELauncher", "NTEGame.exe"),
            _ => throw new ArgumentException("Unknown official launch region.", nameof(region))
        };
        string executable = Path.Combine(root, directory, file);
        if (!File.Exists(executable))
            throw new FileNotFoundException(
                "The official launcher must be installed inside the game installation directory.", executable);
        return new(executable,
            Path.Combine(root, "Client", "WindowsNoEditor", "HT", "Binaries", "Win64", "HTGame.exe"));
    }

    internal ProcessStartInfo CreateStartInfo(string? additionalArguments, bool requestElevation = false)
    {
        ProcessStartInfo start = new(Executable)
        {
            WorkingDirectory = Path.GetDirectoryName(Executable)!, UseShellExecute = requestElevation,
            Verb = requestElevation ? "runas" : "",
            CreateNoWindow = !requestElevation,
            WindowStyle = requestElevation ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
        };
        // ShellExecute is used only for the Windows elevation verb. The exact
        // executable and arguments are passed directly; no command shell runs.
        start.Arguments = "/launcher /directly /autoplay" +
                          (string.IsNullOrWhiteSpace(additionalArguments) ? "" : " " + additionalArguments);
        return start;
    }
}
