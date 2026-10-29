using Hi3Helper.Plugin.Core.UI.Settings;
using Hi3Helper.Plugin.NTE.Localization;
using Nte.Login;
using System;
using System.IO;
using System.Threading;

namespace Hi3Helper.Plugin.NTE.Management.Config;

internal sealed class NteServerSettings
{
    internal const string TargetKey = "server.switchTarget";
    private readonly Lock _gate = new();
    private string _pending = "none";

    internal GameSettingsSection GetSection()
    {
        using (_gate.EnterScope())
            return new(NteResourceProvider.GetString("ServerSettingsSection"),
                [
                    GameSettingEntry.Choice(TargetKey, NteResourceProvider.GetString("ServerSwitchTarget"), _pending,
                    [
                        new("none", NteResourceProvider.GetString("ServerSwitchNoChange")),
                        new("Global", NteResourceProvider.GetString("GlobalZoneName")),
                        new("Taiwan", NteResourceProvider.GetString("TaiwanZoneName"))
                    ])
                ])
                { Description = NteResourceProvider.GetString("ServerSwitchSettingsHint") };
    }

    internal void SetTarget(string value)
    {
        if (value is not ("none" or "Global" or "Taiwan"))
            throw new ArgumentException("Unsupported server switch target.");
        using (_gate.EnterScope()) _pending = value;
    }

    internal NteRegionSwitchOptions? TakeRequest(string? root, string language)
    {
        using (_gate.EnterScope())
        {
            if (_pending == "none") return null;
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new InvalidOperationException(NteResourceProvider.GetString("ServerSwitchSelectInstallation"));
            NteRegionSwitchOptions request = new(root, Enum.Parse<NteHostRegion>(_pending), language);
            _ = request.ToArguments();
            _pending = "none";
            return request;
        }
    }
}
