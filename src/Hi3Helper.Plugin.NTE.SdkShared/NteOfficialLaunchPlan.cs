using System;
using System.Diagnostics;
using System.IO;

namespace Nte.Login;

internal sealed record NteOfficialLaunchPlan(string Executable, string GameExecutable)
{
    internal static NteOfficialLaunchPlan Create(string gameRoot, bool global)
    {
        string root = Path.GetFullPath(gameRoot);
        string executable = Path.Combine(root, global ? "NTEGlobal" : "NTELauncher",
            global ? "NTEGlobalGame.exe" : "NTEGame.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("The official launcher must be installed inside the game installation directory.", executable);
        return new(executable, Path.Combine(root, "Client", "WindowsNoEditor", "HT", "Binaries", "Win64", "HTGame.exe"));
    }

    internal ProcessStartInfo CreateStartInfo(string? additionalArguments)
    {
        ProcessStartInfo start = new(Executable)
        {
            WorkingDirectory = Path.GetDirectoryName(Executable)!, UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        // These are the vendor's direct/autoplay switches, used by both PC regions.
        // Game options are appended verbatim, never interpreted by a shell.
        start.Arguments = "/launcher /directly /autoplay" +
                          (string.IsNullOrWhiteSpace(additionalArguments) ? "" : " " + additionalArguments);
        return start;
    }
}
