# Hi3Helper.Plugin.NTE

`Hi3Helper.Plugin.NTE` is the plugin project for **Neverness to Everness (NTE)** in Collapse Launcher.

## Architecture

- `Plugin.cs` - plugin metadata and preset registration
- `Exports.cs` - unmanaged export bridge (`TryGetApiExport`)
- `Management/Config` - immutable runtime/game config
- `Management/PresetConfig/NteGlobalPresetConfig.cs` - preset adapter
- `Localization/NteResourceProvider.cs` - resource access layer
- `Resources/Strings*.resx` - i18n resources
- `docs/reverse-api-checklist.md` - reverse-engineering task list

## I18N design(Seem doesn't work)

- User-facing text is served from `.resx` resources.
- Locale input is normalized by `NteLocaleResolver` from `SharedStatic.PluginLocaleCode`.
- Fallback chain: requested locale -> `en-US` -> key name.

