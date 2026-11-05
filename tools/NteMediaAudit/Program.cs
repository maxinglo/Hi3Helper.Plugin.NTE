using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text.Json;
using Hi3Helper.Plugin.Core;
using Hi3Helper.Plugin.Core.Management.Api;
using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Microsoft.Extensions.Logging;

if (args.Length is < 2 or > 5) throw new ArgumentException("Usage: NteMediaAudit <native-plugin-dll> <report-path> [preset-index] [--download-info|--preload-info|--region-paths|--update-info <game-directory>]");
IPlugin plugin = Bootstrap.Load(Path.GetFullPath(args[0]));
if (args.Length == 4 && args[3] == "--region-paths")
{
    string reportPath = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    var audit = Bootstrap.CheckRegionPaths(plugin, Path.GetDirectoryName(reportPath)!);
    string json = JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(reportPath, json);
    Console.WriteLine(json);
    if (!audit.Passed) Environment.ExitCode = 1;
    return;
}
int presetIndex = args.Length >= 3 ? int.Parse(args[2]) : 0;
plugin.GetPresetConfig(presetIndex, out var preset);
if (args.Length == 5 && args[3] == "--update-info")
{
    string path = Path.GetFullPath(args[1]);
    string game = Path.GetFullPath(args[4]);
    if (!Directory.Exists(game)) throw new DirectoryNotFoundException(game);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string before = InstallationFingerprint(game);
    preset.comGet_GameManager(out IGameManager? manager);
    preset.comGet_GameInstaller(out IGameInstaller? installer);
    if (manager == null || installer == null) throw new InvalidOperationException("Missing native update interfaces.");
    try
    {
        manager.SetGamePath(game);
        manager.LoadConfig();
        manager.GetCurrentGameVersion(out GameVersion installed);
        Guid token = Guid.NewGuid();
        installer.GetGameSizeAsync(GameInstallerKind.Update, in token, out nint sizeResult);
        long size = await sizeResult.AsTask<long>().WaitAsync(TimeSpan.FromMinutes(10));
        installer.GetGameDownloadedSizeAsync(GameInstallerKind.Update, in token, out nint downloadedResult);
        long downloaded = await downloadedResult.AsTask<long>().WaitAsync(TimeSpan.FromMinutes(10));
        manager.GetCurrentGameVersion(out GameVersion after);
        manager.GetApiGameVersion(out GameVersion release);
        bool unchanged = before == InstallationFingerprint(game);
        bool passed = size >= downloaded && downloaded >= 0 && release > installed && installed == after && unchanged;
        string json = JsonSerializer.Serialize(new { passed, nativePluginLoaded = true, presetIndex,
            installedVersion = installed.ToString(), releasedVersion = release.ToString(),
            plannedPayloadBytes = size, cachedOrResumableBytes = downloaded, remainingNetworkBytes = size - downloaded,
            installationFingerprintUnchanged = unchanged, gameDownloadStarted = false,
            installedGameModified = false, gameAcceptanceEvidence = "not-observed" }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        Console.WriteLine(json);
        if (!passed) Environment.ExitCode = 1;
    }
    finally { installer.Free(); manager.Free(); }
    return;
}
if (args.Length == 5) throw new ArgumentException("Unknown audit option.");
if (args.Length == 4 && args[3] == "--preload-info")
{
    string path = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    preset.comGet_GameManager(out IGameManager? manager);
    preset.comGet_GameInstaller(out IGameInstaller? installer);
    if (manager == null || installer == null) throw new InvalidOperationException("Missing native preload interfaces.");
    try
    {
        Guid token = Guid.NewGuid();
        Bootstrap.AsInitializable(manager).InitAsync(in token, out nint initialization);
        if (await initialization.AsTask<int>().WaitAsync(TimeSpan.FromMinutes(2)) != 0)
            throw new InvalidOperationException("Native game manager initialization failed.");
        manager.GetApiGameVersion(out GameVersion current);
        manager.GetApiPreloadGameVersion(out GameVersion upcoming);
        manager.IsGameHasPreload(out bool availableWithoutInstallation);
        string game = Path.Combine(Path.GetDirectoryName(path)!, "preload-fixture");
        if (Directory.Exists(game)) throw new InvalidOperationException("The audit requires a new fixture directory.");
        preset.comGet_GameExecutableName(out string executable);
        string fixtureExe = Path.Combine(game, executable);
        Directory.CreateDirectory(Path.GetDirectoryName(fixtureExe)!);
        await File.WriteAllBytesAsync(fixtureExe, []);
        manager.SetGamePath(game);
        manager.SetCurrentGameVersion(in current);
        manager.IsGameHasPreload(out bool available);
        installer.GetGameSizeAsync(GameInstallerKind.Preload, in token, out nint sizeResult);
        long size = await sizeResult.AsTask<long>().WaitAsync(TimeSpan.FromMinutes(2));
        installer.GetGameDownloadedSizeAsync(GameInstallerKind.Preload, in token, out nint downloadedResult);
        long downloaded = await downloadedResult.AsTask<long>().WaitAsync(TimeSpan.FromMinutes(2));
        manager.GetCurrentGameVersion(out GameVersion after);
        bool fixtureUnchanged = Directory.GetFiles(game, "*", SearchOption.AllDirectories).Length == 1 && new FileInfo(fixtureExe).Length == 0;
        bool passed = !availableWithoutInstallation && available && upcoming > current && size > 0 && downloaded == 0 && current == after && fixtureUnchanged;
        string json = JsonSerializer.Serialize(new { passed, fixture = true, nativePluginLoaded = true, presetIndex,
            activeVersion = current.ToString(), preloadVersion = upcoming.ToString(), availableWithoutInstallation, available,
            preloadBytesForMissingFixtureFiles = size, downloadedBytes = downloaded, fixtureUnchanged,
            gameDownloadStarted = false, installedGameModified = false, gameAcceptanceEvidence = "not-observed" },
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        Console.WriteLine(json);
        if (!passed) Environment.ExitCode = 1;
    }
    finally { installer.Free(); manager.Free(); }
    return;
}
if (args.Length == 4 && args[3] == "--news")
{
    preset.comGet_LauncherApiNews(out ILauncherApiNews? news);
    if (news == null) throw new InvalidOperationException("Missing native news API.");
    try
    {
        Guid request = Guid.NewGuid();
        Bootstrap.AsInitializable(news).InitAsync(in request, out nint pending);
        if (await pending.AsTask<int>() != 0) throw new InvalidOperationException("News initialization failed.");
        using var carousel = PluginDisposableMemoryExtension.ToManagedSpan<LauncherCarouselEntry>(news.GetCarouselEntries);
        using var entries = PluginDisposableMemoryExtension.ToManagedSpan<LauncherNewsEntry>(news.GetNewsEntries);
        using var socials = PluginDisposableMemoryExtension.ToManagedSpan<LauncherSocialMediaEntry>(news.GetSocialMediaEntries);
        bool iconsValid = true;
        for (int i = 0; i < socials.Length; i++)
        {
            using LauncherSocialMediaEntry entry = socials[i];
            iconsValid &= File.Exists(entry.IconPath) && File.Exists(entry.IconHoverPath) && entry.IconPath != entry.IconHoverPath;
        }
        bool passed = carousel.Length > 0 && entries.Length > 0 && socials.Length >= 6 && iconsValid;
        string json = JsonSerializer.Serialize(new { passed, presetIndex, carouselCount = carousel.Length, newsCount = entries.Length, socialCount = socials.Length, iconsValid, nativePluginLoaded = true, accountRequestsSent = false }, new JsonSerializerOptions { WriteIndented = true });
        string path = Path.GetFullPath(args[1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, json);
        Console.WriteLine(json); if (!passed) Environment.ExitCode = 1; return;
    }
    finally { news.Free(); }
}
if (args.Length == 4)
{
    if (args[3] != "--download-info") throw new ArgumentException("Unknown audit option.");
    string path = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    preset.comGet_GameManager(out IGameManager? manager);
    preset.comGet_GameInstaller(out IGameInstaller? installer);
    if (manager == null || installer == null) throw new InvalidOperationException("Missing native download interfaces.");
    try
    {
        // A non-existent isolated directory; the size query must not install game files.
        string gamePath = Path.Combine(Path.GetDirectoryName(path)!, "uninstalled");
        if (Directory.Exists(gamePath)) throw new InvalidOperationException("The audit expects an absent game directory.");
        manager.SetGamePath(gamePath);
        Guid token = Guid.NewGuid();
        installer.GetGameSizeAsync(GameInstallerKind.Install, in token, out nint sizeResult);
        long size = await sizeResult.AsTask<long>().WaitAsync(TimeSpan.FromMinutes(2));
        installer.GetGameDownloadedSizeAsync(GameInstallerKind.Install, in token, out nint downloadedResult);
        long downloaded = await downloadedResult.AsTask<long>().WaitAsync(TimeSpan.FromMinutes(2));
        manager.GetApiGameVersion(out GameVersion version);
        manager.IsGameInstalled(out bool installed);
        manager.GetGamePath(out string? actualPath);
        preset.comGet_ProfileName(out string profile);
        preset.comGet_ZoneName(out string zone);
        bool directoryCreated = Directory.Exists(gamePath);
        if (size <= 0 || downloaded != 0 || version == GameVersion.Empty || installed || directoryCreated || gamePath != actualPath)
            throw new InvalidOperationException("Native download information is inconsistent.");
        string json = JsonSerializer.Serialize(new { passed = true, fixture = true, presetIndex, profile, zone,
            version = version.ToString(), installBytes = size, downloadedBytes = downloaded, remainingBytes = size - downloaded,
            gamePath = actualPath, gameDirectoryCreated = directoryCreated,
            gameDownloadStarted = false }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json);
        Console.WriteLine(json);
    }
    finally { installer.Free(); manager.Free(); }
    return;
}
preset.comGet_LauncherApiMedia(out ILauncherApiMedia? media);
if (media == null) throw new InvalidOperationException("The plugin returned no media API.");
try
{
    Guid token = Guid.NewGuid();
    IInitializableTask initializer = Bootstrap.AsInitializable(media);
    initializer.InitAsync(in token, out nint result);
    int status = await result.AsTask<int>();
    if (status != 0) throw new InvalidOperationException($"Media initialization returned {status}.");
    media.GetBackgroundFlag(out LauncherBackgroundFlag backgroundFlag);
    media.GetLogoFlag(out LauncherBackgroundFlag overlayFlag);
    var report = new { utc = DateTimeOffset.UtcNow, backgroundFlag = backgroundFlag.ToString(), overlayFlag = overlayFlag.ToString(),
        backgrounds = Paths(media.GetBackgroundEntries), overlays = Paths(media.GetLogoOverlayEntries) };
    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    string path = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, json);
    Console.WriteLine(json);
}
finally { media.Free(); }

static object[] Paths(PluginDisposableMemoryExtension.MarshalToMemorySelectorDelegate getter)
{
    using PluginDisposableMemory<LauncherPathEntry> entries = PluginDisposableMemoryExtension.ToManagedSpan<LauncherPathEntry>(getter);
    List<object> paths = [];
    foreach (LauncherPathEntry entry in entries.AsSpan())
    {
        using (entry)
        {
            string? path = entry.Path;
            paths.Add(new { path, exists = File.Exists(path), bytes = File.Exists(path) ? new FileInfo(path!).Length : 0 });
        }
    }
    return [.. paths];
}

static string InstallationFingerprint(string root)
{
    var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => { FileInfo file = new(path); return new { path = Path.GetRelativePath(root, path), file.Length,
            written = file.LastWriteTimeUtc.Ticks, created = file.CreationTimeUtc.Ticks }; });
    return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(files)));
}

internal static unsafe class Bootstrap
{
    internal sealed record RegionAudit(bool Passed, bool Fixture, bool ChinaRecognized, bool ForeignChinaRejected,
        bool ForeignPathCleared, bool ForeignMarkerPreserved, bool GlobalRecognized, bool ChangedPathClearedVersion);

    internal static RegionAudit CheckRegionPaths(IPlugin plugin, string output)
    {
        string china = Path.Combine(output, "china"), global = Path.Combine(output, "global"), incomplete = Path.Combine(output, "incomplete");
        foreach (string path in new[] { china, global, incomplete })
        {
            string exe = Path.Combine(path, "Client/WindowsNoEditor/HT/Binaries/Win64/HTGame.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "fixture; never executed");
        }
        string chinaMarker = Path.Combine(china, ".collapse_nte_config");
        const string chinaState = "GameVersion=1.4.17\n";
        File.WriteAllText(chinaMarker, chinaState);
        File.WriteAllText(Path.Combine(global, ".collapse_nte_config"), "GameVersion=1.4.10\nAppId=3000001\n");
        plugin.GetPresetConfig(0, out var cnPreset); cnPreset.comGet_GameManager(out IGameManager? cn);
        plugin.GetPresetConfig(1, out var globalPreset); globalPreset.comGet_GameManager(out IGameManager? intl);
        if (cn == null || intl == null) throw new InvalidOperationException("Missing native regional managers.");
        try
        {
            cn.SetGamePath(china); cn.LoadConfig(); cn.IsGameInstalled(out bool chinaRecognized);
            intl.SetGamePath(china); intl.LoadConfig(); intl.IsGameInstalled(out bool foreignInstalled);
            intl.GetGamePath(out string? foreignPath); intl.SaveConfig();
            bool preserved = File.ReadAllText(chinaMarker) == chinaState;
            intl.SetGamePath(global); intl.LoadConfig(); intl.IsGameInstalled(out bool globalRecognized);
            intl.SetGamePath(incomplete); intl.LoadConfig(); intl.IsGameInstalled(out bool incompleteInstalled);
            intl.GetCurrentGameVersion(out GameVersion changedVersion);
            bool changedCleared = !incompleteInstalled && changedVersion == GameVersion.Empty;
            bool passed = chinaRecognized && !foreignInstalled && foreignPath == null && preserved && globalRecognized && changedCleared;
            return new(passed, true, chinaRecognized, !foreignInstalled, foreignPath == null, preserved, globalRecognized, changedCleared);
        }
        finally { cn.Free(); intl.Free(); }
    }

    internal static IInitializableTask AsInitializable(ILauncherApiMedia media)
    {
        void* pointer = ComInterfaceMarshaller<ILauncherApiMedia>.ConvertToUnmanaged(media);
        try { return ComInterfaceMarshaller<IInitializableTask>.ConvertToManaged(pointer) ?? throw new InvalidOperationException("No initializable interface."); }
        finally { ComInterfaceMarshaller<ILauncherApiMedia>.Free(pointer); }
    }
    internal static IInitializableTask AsInitializable(ILauncherApiNews news)
    {
        void* pointer = ComInterfaceMarshaller<ILauncherApiNews>.ConvertToUnmanaged(news);
        try { return ComInterfaceMarshaller<IInitializableTask>.ConvertToManaged(pointer) ?? throw new InvalidOperationException("No initializable interface."); }
        finally { ComInterfaceMarshaller<ILauncherApiNews>.Free(pointer); }
    }
    internal static IInitializableTask AsInitializable(IGameManager manager)
    {
        void* pointer = ComInterfaceMarshaller<IGameManager>.ConvertToUnmanaged(manager);
        try { return ComInterfaceMarshaller<IInitializableTask>.ConvertToManaged(pointer) ?? throw new InvalidOperationException("No initializable interface."); }
        finally { ComInterfaceMarshaller<IGameManager>.Free(pointer); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Logger(LogLevel* level, void* eventId, char* message, int length);
    private static readonly Logger Callback = (level, eventId, message, length) => Console.WriteLine(new string(message, 0, length));
    internal static IPlugin Load(string path)
    {
        nint module = NativeLibrary.Load(path); // NativeAOT libraries remain loaded until this command exits.
        nint resolver = NativeLibrary.GetExport(module, "TryGetApiExport");
        nint Find(string name)
        {
            void* pointer = null;
            fixed (char* namePointer = name)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Cdecl]<char*, void**, int>)resolver)(namePointer, &pointer));
            if (pointer == null) throw new EntryPointNotFoundException(name);
            return (nint)pointer;
        }
        ((delegate* unmanaged[Cdecl]<nint, void>)Find("SetLoggerCallback"))(Marshal.GetFunctionPointerForDelegate(Callback));
        nint unknown = (nint)((delegate* unmanaged[Cdecl]<void*>)Find("GetPlugin"))();
        try { return ComInterfaceMarshaller<IPlugin>.ConvertToManaged((void*)unknown) ?? throw new InvalidOperationException("No plugin COM interface."); }
        finally { Marshal.Release(unknown); }
    }
}
