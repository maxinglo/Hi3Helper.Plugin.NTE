using Hi3Helper.Plugin.Core.UI.Settings;
using Hi3Helper.Plugin.NTE.Localization;
using Nte.Login;
using System.Threading;

namespace Hi3Helper.Plugin.NTE.Management.Config;

internal sealed class NteLoginSettings(string path)
{
    internal const string HelloKey = "login.experimentalWindowsHello";
    private readonly Lock _sync = new();
    private bool? _pending;

    internal GameSettingsSection GetSection()
    {
        using (_sync.EnterScope())
        {
            _pending ??= NteLoginPreferences.ReadExperimentalHello(path);
            return new(NteResourceProvider.GetString("LoginSettingsSection"),
            [
                GameSettingEntry.Toggle(HelloKey, NteResourceProvider.GetString("LoginExperimentalHello"),
                    _pending.Value,
                    NteResourceProvider.GetString("LoginExperimentalHelloHint"))
            ]);
        }
    }

    internal void SetHello(bool enabled)
    {
        using (_sync.EnterScope()) _pending = enabled;
    }

    internal void Apply()
    {
        using (_sync.EnterScope())
        {
            if (_pending is bool value)
            {
                NteLoginPreferences.WriteExperimentalHello(path, value);
                _pending = null;
            }
        }
    }
}
