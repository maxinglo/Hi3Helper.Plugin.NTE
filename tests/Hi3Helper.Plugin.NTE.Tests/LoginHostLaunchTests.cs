using System.Diagnostics;
using Hi3Helper.Plugin.NTE.Management.Game;
using Nte.Login;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class LoginHostLaunchTests
{
    [Fact]
    public void LaunchOptionsPreserveCustomArgumentsAndGamePriorityWithoutAShell()
    {
        NteHostLaunchOptions request = new(Path.GetFullPath("fixture game"), "-windowed -some-value=\"a b\\\"", true, ProcessPriorityClass.AboveNormal);
        NteHostLaunchOptions restored = NteHostLaunchOptions.Parse(request.ToArguments());
        Assert.Equal(request, restored);
        Assert.Equal(NteHostLaunchOptions.RequiredGameArguments + " " + request.AdditionalArguments, restored.GameArguments);
    }

    [Fact]
    public void UnknownDuplicateAndInvalidLaunchOptionsAreRejected()
    {
        string root = Path.GetFullPath("fixture game");
        Assert.Throws<ArgumentException>(() => NteHostLaunchOptions.Parse(["--game-root", root, "--unknown", "x"]));
        Assert.Throws<ArgumentException>(() => NteHostLaunchOptions.Parse(["--game-root", root, "--game-root", root]));
        Assert.Throws<ArgumentException>(() => NteHostLaunchOptions.Parse(["--game-root", root, "--priority", "1234"]));
        Assert.Throws<ArgumentException>(() => new NteHostLaunchOptions("relative/path").ToArguments());
        Assert.Throws<ArgumentException>(() => new NteHostLaunchOptions(root, "bad\0argument").ToArguments());
    }

    [Fact]
    public void HostResolutionUsesTheSuppliedPluginFolderAndRejectsIncompletePackages()
    {
        string root = Path.Combine(Path.GetTempPath(), "NtePluginFolder-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.Throws<FileNotFoundException>(() => NteLoginHostLaunch.ResolveHostPath(root));
            string host = Path.Combine(root, "LoginHost"); Directory.CreateDirectory(Path.Combine(host, "Assets"));
            foreach (string file in new[] { "Nte.LoginHost.exe", "Nte.LoginHost.dll", "Nte.LoginHost.runtimeconfig.json", "Assets/question-mascot.png" })
                File.WriteAllText(Path.Combine(host, file), "fixture");
            Assert.Equal(Path.Combine(host, "Nte.LoginHost.exe"), NteLoginHostLaunch.ResolveHostPath(root));
            File.Delete(Path.Combine(host, "Nte.LoginHost.dll"));
            Assert.Throws<FileNotFoundException>(() => NteLoginHostLaunch.ResolveHostPath(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LimitedImageQueryMatchesOnlyTheCurrentProcessFullPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        string executable = Environment.ProcessPath!;
        Assert.True(NteProcessImage.Matches(Environment.ProcessId, executable));
        Assert.False(NteProcessImage.Matches(Environment.ProcessId, executable + ".other"));
        Assert.False(NteProcessImage.Matches(0, executable));
    }

    [Fact]
    public void StartupSessionEndsWhenTheActualGameHasBeenObserved()
    {
        string executable = Path.Combine(Path.GetTempPath(), "NteStartup-" + Guid.NewGuid().ToString("N"), "HTGame.exe");
        using Process host = Process.GetCurrentProcess();
        using (NteGameLaunchSession session = NteGameLaunchSession.Track(executable, host))
        {
            Assert.True(NteGameLaunchSession.IsStarting(executable, out DateTime started));
            Assert.Equal(host.StartTime, started);
            Assert.False(NteGameLaunchSession.IsStarting(executable + ".other", out _));
            NteGameLaunchSession.ObserveGame(executable);
            Assert.False(NteGameLaunchSession.IsStarting(executable, out _));
            Assert.False(host.HasExited);
        }
        Assert.False(NteGameLaunchSession.IsStarting(executable, out _));
    }

    [Fact]
    public void DisposingAnEarlierStartupCannotClearTheCurrentSession()
    {
        string executable = Path.Combine(Path.GetTempPath(), "NteStartup-" + Guid.NewGuid().ToString("N"), "HTGame.exe");
        using Process host = Process.GetCurrentProcess();
        using NteGameLaunchSession earlier = NteGameLaunchSession.Track(executable, host);
        using (NteGameLaunchSession current = NteGameLaunchSession.Track(executable, host))
        {
            earlier.Dispose();
            Assert.True(NteGameLaunchSession.IsStarting(executable, out _));
        }
        Assert.False(NteGameLaunchSession.IsStarting(executable, out _));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task LimitedWaitForCurrentProcessHonorsCancellationAndRejectsWrongPath()
    {
        if (!OperatingSystem.IsWindows()) return;
        string executable = Environment.ProcessPath!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => NteProcessImage.WaitForExitWithLimitedAccessAsync(Environment.ProcessId, executable + ".other", TestContext.Current.CancellationToken));
        using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NteProcessImage.WaitForExitWithLimitedAccessAsync(Environment.ProcessId, executable, cancel.Token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(259)]
    public async Task LimitedWaitCompletesWhenAnOwnedFixtureProcessExits(int exitCode)
    {
        if (!OperatingSystem.IsWindows()) return;
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"Start-Sleep -Seconds 1; exit {exitCode}");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Fixture process did not start.");
        string expectedGame = Path.Combine(Path.GetTempPath(), "NteStartup-" + Guid.NewGuid().ToString("N"), "HTGame.exe");
        using NteGameLaunchSession startup = NteGameLaunchSession.Track(expectedGame, process);
        Assert.True(NteGameLaunchSession.IsStarting(expectedGame, out _));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        Task wait = NteProcessImage.WaitForExitWithLimitedAccessAsync(process.Id, executable, deadline.Token);
        Assert.False(wait.IsCompleted);
        await wait;
        await process.WaitForExitAsync(deadline.Token);
        Assert.Equal(exitCode, process.ExitCode);
        Assert.False(NteGameLaunchSession.IsStarting(expectedGame, out _));
    }
}
