# LoginHost Native AOT migration

This experimental branch replaces WPF with Avalonia 11.2.7 while retaining the existing C# login flows, account formats, regional services, and Win32 game-window ownership. The login host publishes as a Windows x64 Native AOT executable; users do not need the .NET Desktop Runtime.

WPF is incompatible with the trimming required by Native AOT. Avalonia supports Native AOT and has a layout/control model close enough to reuse the current dynamic interface. See [Microsoft's trimming incompatibilities](https://learn.microsoft.com/dotnet/core/deploying/trimming/incompatibilities#wpf) and [Avalonia's Native AOT deployment guide](https://docs.avaloniaui.net/docs/deployment/native-aot).

## Interface and runtime

- Preserve the 368 DIP card width, typography, day/night/sunset colors, rounded controls, cached game backdrop, mascot, account rows, and separate account-management pages.
- Port the mainland SMS form, overseas email/password/code forms, Taiwan country selector, consent links, help/official-launcher confirmation, welcome toast, and region-switch progress window.
- Keep the game-owned, non-topmost Win32 cover, geometry notifications, copied game cursor, display-affinity capture exclusion, and existing Windows Hello ownership.
- Compile XAML and region-switch bindings. Overseas forms use typed event wiring and typed data templates, without reflection-based property paths.
- Use generated JSON metadata for encrypted account records, display sidecars, SDK wire maps, and diagnostics. The existing field names and account encryption remain compatible.
- Forward browser callbacks before initializing the UI renderer. Resolve the companion native plugin through `AppContext.BaseDirectory` instead of `Assembly.Location`.

The publish directory contains `Nte.LoginHost.exe`, Skia/HarfBuzz/ANGLE native libraries, image assets, translated resource catalogs, and the retained Collapse style license. It has no CoreCLR, hostfxr, WPF assemblies, managed host DLL, or runtime configuration. Resource-only satellite DLLs still carry translations; they do not require a CLR process.

## Build and verify

Build on Windows x64 with the pinned .NET SDK, Visual Studio's C++ desktop tools, and a Windows SDK:

```powershell
dotnet build src/Hi3Helper.Plugin.NTE.LoginHost/Hi3Helper.Plugin.NTE.LoginHost.csproj -c Release -p:Platform=x64
dotnet publish src/Hi3Helper.Plugin.NTE.LoginHost/Hi3Helper.Plugin.NTE.LoginHost.csproj -c Release -r win-x64 -p:Platform=x64 -p:CopyOutputSymbolsToPublishDirectory=false -o artifacts/login-host/native-aot
./scripts/Test-NteLoginHostResources.ps1 -HostDirectory artifacts/login-host/native-aot
./scripts/Test-NteNativeLoginUi.ps1 -HostDirectory artifacts/login-host/native-aot
```

The native UI verifier runs inside the published executable. It covers all nine languages and three regions, account navigation/rename/removal confirmation, password clearing, consent, code cooldown, scrolling, modal confirmation, translated templates, and compiled region-switch progress bindings. It exports fifteen synthetic PNGs and `native-ui-report.json` beneath its report directory. It also round-trips synthetic account/display records using current-user protection in a new fixture directory. It does not read the user's accounts, invoke Windows Hello, launch a real game, or send authentication requests.

The earlier reflection-based WPF inspection tool remains useful for the WPF baseline. Use `Test-NteNativeLoginUi.ps1` for this branch's executable. The existing `--preview-pid`, `--preview-global-pid`, `--preview-taiwan-pid`, and `--preview-welcome-pid` modes remain available for a separately owned synthetic game window.

Build a complete importable plugin package with:

```powershell
./scripts/Publish.ps1 -Profile ReleasePublish-O1 -OutputName LoginHost-NativeAOT -Package
```

Package verification checks the executable's x64 PE header and absence of a CLR header, verifies the required native rendering libraries and translations, and rejects bundled managed runtimes. Real game acceptance, browser-provider authorization, and Windows Hello interaction still require device testing of this build.
