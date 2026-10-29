using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.UI.Settings;
using Hi3Helper.Plugin.NTE.Localization;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Hi3Helper.Plugin.NTE.Management.Config;

internal sealed class NteAppearanceStore(string path)
{
    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CollapseLauncher", "Plugins", "NTE", "appearance.json");
    private readonly Lock _sync = new();
    private NteAppearanceOptions? _pending;

    internal NteAppearanceOptions Load()
    {
        using (_sync.EnterScope())
        {
            try
            {
                if (!File.Exists(path))
                    return new NteAppearanceOptions();
                NteAppearanceOptions options = JsonSerializer.Deserialize(File.ReadAllText(path),
                    NteAppearanceJsonContext.Default.NteAppearanceOptions)
                    ?? throw new InvalidDataException("Appearance settings are empty.");
                options.Validate();
                return options;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
            {
                SharedStatic.InstanceLogger.LogWarning(ex, "Cannot read NTE appearance settings; using defaults.");
                return new NteAppearanceOptions();
            }
        }
    }

    internal GameSettingsPage GetPage()
    {
        using (_sync.EnterScope())
        {
            NteAppearanceOptions options = _pending ??= Load();
            List<GameSettingsSection> sections =
            [
                new(Text("AppearanceBackgroundSection"),
                [
                    GameSettingEntry.Toggle("dynamicBackground", Text("AppearanceDynamicBackground"), options.DynamicBackground),
                    GameSettingEntry.Toggle("overlayEnabled", Text("AppearanceOverlayEnabled"), options.OverlayEnabled),
                    GameSettingEntry.Number("canvasWidth", Text("AppearanceCanvasWidth"), options.CanvasWidth, 64, 4096),
                    GameSettingEntry.Number("canvasHeight", Text("AppearanceCanvasHeight"), options.CanvasHeight, 64, 4096)
                ]) { Description = Text("AppearanceRestartHint") }
            ];
            foreach (NteOverlayElement element in options.Elements)
            {
                string title = element.Id switch
                {
                    "slogan" => Text("AppearanceSlogan"),
                    "logo" => Text("AppearanceLogo"),
                    _ => element.Id
                };
                string prefix = "element." + element.Id + ".";
                sections.Add(new GameSettingsSection(title,
                [
                    GameSettingEntry.Toggle(prefix + "enabled", Text("AppearanceElementEnabled"), element.Enabled),
                    GameSettingEntry.Text(prefix + "source", Text("AppearanceSource"), element.Source,
                        Text("AppearanceSourceHint")),
                    GameSettingEntry.Number(prefix + "x", Text("AppearanceX"), element.X, -8192, 8192),
                    GameSettingEntry.Number(prefix + "y", Text("AppearanceY"), element.Y, -8192, 8192),
                    GameSettingEntry.Number(prefix + "width", Text("AppearanceWidth"), element.Width, 1, 8192),
                    GameSettingEntry.Number(prefix + "height", Text("AppearanceHeight"), element.Height, 1, 8192),
                    GameSettingEntry.Slider(prefix + "opacity", Text("AppearanceOpacity"), element.Opacity, 0, 1, 0.05)
                ]));
            }
            return new GameSettingsPage(sections) { Title = Text("AppearanceTitle") };
        }
    }

    internal void SetValue(string key, string value)
    {
        using (_sync.EnterScope())
        {
            NteAppearanceOptions options = _pending ??= Load();
            switch (key)
            {
                case "dynamicBackground": options.DynamicBackground = bool.Parse(value); return;
                case "overlayEnabled": options.OverlayEnabled = bool.Parse(value); return;
                case "canvasWidth": options.CanvasWidth = int.Parse(value, CultureInfo.InvariantCulture); return;
                case "canvasHeight": options.CanvasHeight = int.Parse(value, CultureInfo.InvariantCulture); return;
            }
            string[] parts = key.Split('.');
            if (parts.Length != 3 || parts[0] != "element")
                throw new ArgumentException("Unknown appearance setting.", nameof(key));
            NteOverlayElement element = options.Elements.FirstOrDefault(item => item.Id == parts[1])
                ?? throw new ArgumentException("Unknown decorative element.", nameof(key));
            switch (parts[2])
            {
                case "enabled": element.Enabled = bool.Parse(value); break;
                case "source": element.Source = value; break;
                case "x": element.X = ParseNumber(value); break;
                case "y": element.Y = ParseNumber(value); break;
                case "width": element.Width = ParseNumber(value); break;
                case "height": element.Height = ParseNumber(value); break;
                case "opacity": element.Opacity = ParseNumber(value); break;
                default: throw new ArgumentException("Unknown element setting.", nameof(key));
            }
        }
    }

    internal void Apply()
    {
        using (_sync.EnterScope())
        {
            NteAppearanceOptions options = _pending ?? Load();
            options.Validate();
            string fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(options,
                    NteAppearanceJsonContext.Default.NteAppearanceOptions));
                File.Move(temporaryPath, fullPath, overwrite: true);
                _pending = null;
            }
            finally { File.Delete(temporaryPath); }
        }
    }

    private static double ParseNumber(string value)
    {
        double number = double.Parse(value, CultureInfo.InvariantCulture);
        return double.IsFinite(number) ? number : throw new ArgumentException("Value must be finite.", nameof(value));
    }

    private static string Text(string key) => NteResourceProvider.GetString(key);
}
