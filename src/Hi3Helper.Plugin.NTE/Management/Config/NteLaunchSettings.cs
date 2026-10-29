using Hi3Helper.Plugin.Core.UI.Settings;
using Hi3Helper.Plugin.NTE.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Nte.Login;

namespace Hi3Helper.Plugin.NTE.Management.Config;

internal sealed class NteLaunchSettings(string path, bool officialOnly = false)
{
    internal const string GraphicsKey = "launch.graphicsApi";
    internal const string OfficialLauncherKey = "launch.officialLauncher";
    private readonly Lock sync = new();
    private string? pending;
    private bool? pendingOfficial;

    internal static string GetPath(string profile) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CollapseLauncher", "Plugins", "NTE",
        profile, "launch.json");

    internal string Load() => NteLaunchPreferences.Read(path).GraphicsApi;
    internal bool UseOfficialLauncher => officialOnly || NteLaunchPreferences.Read(path).OfficialLauncher;

    internal GameSettingsSection GetSection()
    {
        using (sync.EnterScope())
            return new(NteResourceProvider.GetString("LaunchSettingsSection"),
            [
                GameSettingEntry.Choice(GraphicsKey, NteResourceProvider.GetString("LaunchGraphicsApi"),
                    pending ??= Load(),
                    [
                        new("default", NteResourceProvider.GetString("LaunchGraphicsDefault")),
                        new("dx11", "DirectX 11"), new("dx12", "DirectX 12")
                    ]),
                .. (officialOnly ? Array.Empty<GameSettingEntry>() : new[] { GameSettingEntry.Toggle(OfficialLauncherKey, NteResourceProvider.GetString("LaunchOfficialLauncher"),
                    pendingOfficial ?? UseOfficialLauncher, NteResourceProvider.GetString("LaunchOfficialLauncherHint")) })
            ]);
    }

    internal void SetValue(string value)
    {
        Validate(value);
        using (sync.EnterScope()) pending = value;
    }

    internal void SetOfficialLauncher(bool enabled)
    {
        using (sync.EnterScope()) pendingOfficial = enabled;
    }

    internal void Apply()
    {
        using (sync.EnterScope())
        {
            if (pending == null && pendingOfficial == null) return;
            NteLaunchPreferences.Update(path, pending, pendingOfficial);
            pending = null;
            pendingOfficial = null;
        }
    }

    internal string? BuildArguments(string? additional) => ApplyGraphicsApi(additional, Load());

    internal static string? ApplyGraphicsApi(string? additional, string graphicsApi)
    {
        Validate(graphicsApi);
        if (graphicsApi == "default") return additional;
        List<string> arguments = [];
        foreach (Match match in Regex.Matches(additional ?? "", "(?:[^\\s\"]|\"[^\"]*\")+"))
        {
            string value = match.Value;
            if (value.Trim('"').ToLowerInvariant() is "-dx11" or "-dx12" or "-d3d11" or "-d3d12") continue;
            arguments.Add(value);
        }

        arguments.Add("-" + graphicsApi);
        return string.Join(' ', arguments);
    }

    private static void Validate(string value)
    {
        if (value is not ("default" or "dx11" or "dx12"))
            throw new ArgumentException("Unknown graphics API.", nameof(value));
    }
}
