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
            Localization.LoginText.UseLanguage(e.Args.Length == 4 && e.Args[0] is "--preview-global-pid" or "--preview-taiwan-pid"
                ? e.Args[3]
                : NteHostLaunchOptions.Parse(e.Args).Language);
        }
        catch (ArgumentException)
        {
            Localization.LoginText.UseLanguage("zh-CN");
        }

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher));
        if (e.Args.Length >= 1 && e.Args[0] == "--switch-region")
        {
            _ = RunRegionSwitchAsync(e.Args);
            return;
        }
        window = new LoginWindow(e.Args.Contains("--preview-pid") || e.Args.Contains("--preview-welcome-pid") ||
                                 e.Args.Contains("--preview-global-pid") || e.Args.Contains("--preview-taiwan-pid"));
        _ = RunAsync(e.Args);
    }

    private async Task RunRegionSwitchAsync(string[] arguments)
    {
        try
        {
            NteRegionSwitchOptions options = NteRegionSwitchOptions.Parse(arguments);
            Shutdown(await ShowRegionSwitchAsync(options, false) ? 0 : 1);
        }
        catch (Exception)
        {
            MessageBox.Show(Localization.LoginText.Get("ServerSwitchFailed"), Localization.LoginText.Get("ServerSwitchTitle"));
            Shutdown(1);
        }
    }

    private static async Task<bool> ShowRegionSwitchAsync(NteRegionSwitchOptions options, bool closeOnCompletion)
    {
        Localization.LoginText.UseLanguage(options.Language);
        ViewModels.RegionSwitchViewModel model = new(new Services.NativeRegionSwitchClient(), options, key => Localization.LoginText.Get(key));
        RegionSwitchWindow view = new(model);
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Closed += (_, _) => closed.TrySetResult();
        if (closeOnCompletion) model.Completed += () => view.Dispatcher.BeginInvoke(view.Close);
        view.Show();
        await closed.Task;
        return model.IsCompleted;
    }

    private static bool NeedsServerActivation(NteHostLaunchOptions options)
    {
        if (options.Region is not (NteHostRegion.Global or NteHostRegion.Taiwan)) return false;
        if (System.IO.File.Exists(System.IO.Path.Combine(options.GameRoot, ".collapse-nte", "switch.json"))) return true;
        string state = System.IO.Path.Combine(options.GameRoot, ".collapse-nte", "state.json");
        if (!System.IO.File.Exists(state)) return false;
        if (new System.IO.FileInfo(state).Length > 16 * 1024 * 1024) throw new System.IO.InvalidDataException("Invalid shared installation state.");
        using var document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllBytes(state));
        return document.RootElement.GetProperty("ActiveAppId").GetString() != (options.Region == NteHostRegion.Taiwan ? "2000013" : "3000001");
    }

    private async Task RunAsync(string[] arguments)
    {
        int exitCode = 0;
        bool officialLaunch = false;
        try
        {
            if (arguments.Length is 2 or 4 && arguments[0] is "--preview-global-pid" or "--preview-taiwan-pid" &&
                int.TryParse(arguments[1], out int globalPid))
            {
                if (arguments[0] == "--preview-taiwan-pid") await window!.PreviewTaiwanAsync(globalPid);
                else await window!.PreviewGlobalAsync(globalPid);
                return;
            }

            if (arguments.Length == 2 && arguments[0] is "--preview-pid" or "--preview-welcome-pid" &&
                int.TryParse(arguments[1], out int pid))
            {
                await window!.PreviewAsync(pid, arguments[0] == "--preview-welcome-pid");
                return;
            }

            NteHostLaunchOptions options = NteHostLaunchOptions.Parse(arguments);
            if (NeedsServerActivation(options) && !await ShowRegionSwitchAsync(new(options.GameRoot, options.Region, options.Language), true))
            { exitCode = 1; return; }
            window!.ConfigureLaunch(options);
            officialLaunch = NteLaunchPreferences.Read(NteLaunchPreferences.GetPath(options.Region)).OfficialLauncher;
            if (officialLaunch)
            {
                if (!await OfficialGameHost.RunAsync(options)) exitCode = 1;
                return;
            }
            if (options.Region is NteHostRegion.Global or NteHostRegion.Taiwan)
            {
                using OverseasGameLoginHost host = new(options, window!);
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
