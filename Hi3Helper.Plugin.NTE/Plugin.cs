using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.PresetConfig;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Localization;
using Hi3Helper.Plugin.NTE.Management.PresetConfig;
using System;
using System.Runtime.InteropServices.Marshalling;

namespace Hi3Helper.Plugin.NTE;

[GeneratedComClass]
public partial class NtePlugin : PluginBase
{
    private static readonly IPluginPresetConfig[] PresetConfigInstances = [new NteCNPresetConfig()];
    private static DateTime _pluginCreationDate = new(2026, 04, 21, 00, 00, 00, DateTimeKind.Utc);

    public override void GetPluginName(out string result) => result = NteResourceProvider.GetString("PluginName");

    public override void GetPluginDescription(out string result) => result = NteResourceProvider.GetString("PluginDescription");

    public override void GetPluginAuthor(out string result) => result = "Maxing";

    public override unsafe void GetPluginCreationDate(out DateTime* result) => result = _pluginCreationDate.AsPointer();

    public override void GetPresetConfigCount(out int count) => count = PresetConfigInstances.Length;

    public override void GetPresetConfig(int index, out IPluginPresetConfig presetConfig)
    {
        if (index < 0 || index >= PresetConfigInstances.Length)
        {
            presetConfig = null!;
            return;
        }

        presetConfig = PresetConfigInstances[index];
    }
}

