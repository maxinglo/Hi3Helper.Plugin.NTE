using Hi3Helper.Plugin.Core.UI.Settings;
using Hi3Helper.Plugin.NTE.Localization;
using Nte.Login;
using System.Threading;

namespace Hi3Helper.Plugin.NTE.Management.Config;

internal sealed class NteLoginSettings(string path)
{
    internal const string HelloKey = "login.experimentalWindowsHello";
    private readonly Lock sync = new();
    private bool? pending;
    internal GameSettingsSection GetSection()
    {
        using (sync.EnterScope())
        {
            pending ??= NteLoginPreferences.ReadExperimentalHello(path);
            return new(NteResourceProvider.GetString("LoginSettingsSection"),
                [GameSettingEntry.Toggle(HelloKey, NteResourceProvider.GetString("LoginExperimentalHello"), pending.Value)])
                { Description = NteResourceProvider.GetString("LoginExperimentalHelloHint") };
        }
    }
    internal void SetHello(bool enabled) { using (sync.EnterScope()) pending = enabled; }
    internal void Apply()
    {
        using (sync.EnterScope())
        { if (pending is bool value) { NteLoginPreferences.WriteExperimentalHello(path, value); pending = null; } }
    }
}
