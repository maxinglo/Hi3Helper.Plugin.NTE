using System.Collections.Generic;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.NTE.Management.Game;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.UI.Settings;
using Hi3Helper.Plugin.NTE.Management.PresetConfig;
using Hi3Helper.Plugin.NTE.Management.Config;
using Hi3Helper.Plugin.NTE.Localization;
using System;

namespace Hi3Helper.Plugin.NTE;

public partial class Exports
{
    protected override GameSettingsPage? GetGameSettingsPageCore(IPluginPresetConfig presetConfig)
    {
        if (presetConfig is not NteCNPresetConfig nte) return null;
        GameSettingsPage appearance = nte.Appearance.GetPage();
        List<GameSettingsSection> sections = [nte.LaunchSettings.GetSection()];
        sections.Add(nte.LoginSettings.GetSection());
        if (nte.Region.IsOverseas) sections.Add(nte.ServerSettings.GetSection());
        sections.AddRange(appearance.Sections);
        return new(sections) { Title = NteResourceProvider.GetString("GameSettingsTitle") };
    }

    protected override void SetGameSettingValueCore(IPluginPresetConfig presetConfig, string key, string value)
    {
        if (presetConfig is NteCNPresetConfig nte)
        {
            if (key == NteServerSettings.TargetKey && nte.Region.IsOverseas) nte.ServerSettings.SetTarget(value);
            else if (key == NteLaunchSettings.GraphicsKey) nte.LaunchSettings.SetValue(value);
            else if (key == NteLaunchSettings.OfficialLauncherKey) nte.LaunchSettings.SetOfficialLauncher(bool.Parse(value));
            else if (key == NteLoginSettings.HelloKey) nte.LoginSettings.SetHello(bool.Parse(value));
            else nte.Appearance.SetValue(key, value);
        }
    }

    protected override void ApplyGameSettingsCore(IPluginPresetConfig presetConfig)
    {
        if (presetConfig is NteCNPresetConfig nte)
        {
            nte.Appearance.Apply();
            nte.LaunchSettings.Apply();
            nte.LoginSettings.Apply();
            if (nte.Region.IsOverseas)
            {
                string? selected = null;
                if (nte.GameManager is { } manager) { manager.GetGamePath(out selected); }
                selected ??= NteSharedInstallation.FindRoot(nte.Region);
                if (nte.ServerSettings.TakeRequest(selected, SharedStatic.PluginLocaleCode ?? "en-US") is { } request)
                    NteLoginHostLaunch.StartSwitch(request);
            }
        }
    }
}
