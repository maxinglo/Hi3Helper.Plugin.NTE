using System.Windows;
using System.Windows.Threading;
using Nte.Login;

namespace Nte.LoginHost;

public partial class App : Application
{
    private LoginWindow? window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length == 2 && e.Args[0] == "--global-web-callback")
        {
            _ = ForwardGlobalBrowserAsync(e.Args[1]);
            return;
        }

        try
        {
            Localization.LoginText.UseLanguage(e.Args.Length == 4 && e.Args[0] == "--preview-global-pid"
                ? e.Args[3]
                : NteHostLaunchOptions.Parse(e.Args).Language);
        }
        catch (ArgumentException)
        {
            Localization.LoginText.UseLanguage("zh-CN");
        }

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
        window = new LoginWindow(e.Args.Contains("--preview-pid") || e.Args.Contains("--preview-welcome-pid") ||
                                 e.Args.Contains("--preview-global-pid"));
        _ = RunAsync(e.Args);
    }

    private async Task RunAsync(string[] arguments)
    {
        int exitCode = 0;
        bool officialLaunch = false;
        try
        {
            if (arguments.Length is 2 or 4 && arguments[0] == "--preview-global-pid" &&
                int.TryParse(arguments[1], out int globalPid))
            {
                await window!.PreviewGlobalAsync(globalPid);
                return;
            }

            if (arguments.Length == 2 && arguments[0] is "--preview-pid" or "--preview-welcome-pid" &&
                int.TryParse(arguments[1], out int pid))
            {
                await window!.PreviewAsync(pid, arguments[0] == "--preview-welcome-pid");
                return;
            }

            NteHostLaunchOptions options = NteHostLaunchOptions.Parse(arguments);
            window!.ConfigureLaunch(options);
            officialLaunch = NteLaunchPreferences.Read(NteLaunchPreferences.GetPath(options.Region == NteHostRegion.Global)).OfficialLauncher;
            if (officialLaunch)
            {
                if (!await OfficialGameHost.RunAsync(options)) exitCode = 1;
                return;
            }
            if (options.Region == NteHostRegion.Global)
            {
                using GlobalGameLoginHost host = new(options, window!);
                if (!await host.RunAsync()) exitCode = 1;
            }
            else
            {
                using GameLoginHost host = new(options, window!);
                if (!await host.RunAsync()) exitCode = 1;
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            if (arguments.Length == 2 && arguments[0] is "--preview-pid" or "--preview-welcome-pid")
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(AppContext.BaseDirectory, "preview-startup-error.txt"), ex.ToString());
            await window!.ShowFailureAsync(officialLaunch && ex is System.IO.FileNotFoundException
                ? Localization.LoginText.Get("OfficialLauncherMissing")
                : ex is NteGlobalLaunchException global
                ? Localization.LoginText.Get(global.Key)
                : ex is System.ComponentModel.Win32Exception native && native.NativeErrorCode == 740
                    ? Localization.LoginText.Get("ElevationRequired")
                    : Localization.LoginText.Get("StartupFailed"));
        }
        finally
        {
            window!.Shutdown();
            Shutdown(exitCode);
        }
    }

    private async Task ForwardGlobalBrowserAsync(string uri)
    {
        bool forwarded = false;
        try
        {
            forwarded = await GlobalBrowserLogin.ForwardAsync(uri);
        }
        catch (Exception)
        {
            /* Never show or log a callback URI. */
        }

        Shutdown(forwarded ? 0 : 1);
    }
}
