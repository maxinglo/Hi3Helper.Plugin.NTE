using System.Diagnostics;
using Hi3Helper.Plugin.NTE.Management.Game;
using Nte.Login;

namespace Nte.LoginHost;

internal static class OfficialGameHost
{
    internal static async Task<bool> RunAsync(NteHostLaunchOptions options)
    {
        NteOfficialLaunchPlan plan = NteOfficialLaunchPlan.Create(options.GameRoot, options.Region == NteHostRegion.Global);
        using Process platform = Process.Start(plan.CreateStartInfo(options.AdditionalArguments))
                                 ?? throw new InvalidOperationException("Official launcher could not start.");
        // The official platform owns authentication and the game. Observe the
        // selected installation only; never read or import its saved accounts.
        Stopwatch startup = Stopwatch.StartNew();
        while (startup.Elapsed < TimeSpan.FromMinutes(3))
        {
            foreach (Process candidate in Process.GetProcessesByName("HTGame"))
            {
                using (candidate)
                {
                    if (!NteProcessImage.Matches(candidate.Id, plan.GameExecutable)) continue;
                    await NteProcessImage.WaitForExitWithLimitedAccessAsync(candidate.Id, plan.GameExecutable, CancellationToken.None);
                    return true;
                }
            }
            if (platform.HasExited && platform.ExitCode != 0) return false;
            await Task.Delay(500);
        }
        return false;
    }
}
