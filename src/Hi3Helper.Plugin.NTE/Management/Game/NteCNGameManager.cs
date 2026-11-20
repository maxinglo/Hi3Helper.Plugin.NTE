using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Xml;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;
using Nte.Login;

namespace Hi3Helper.Plugin.NTE.Management.Game;

[GeneratedComClass]
internal partial class NteGameManager : GameManagerBase
{
    internal NteRegionConfig Region { get; }
    internal NteGameManager(HttpClient? httpClient = null, NteRegionConfig? region = null)
    {
        Region = region ?? NteConfigProvider.CN;
        GameResBaseUrls = Region.ResourceUrls;
        BranchName = Region.DefaultBranch;
        AppId = Region.AppId;
        if (httpClient != null) ApiResponseHttpClient = httpClient;
    }

    internal string? ReleaseConfigXml { get; private set; }
    internal NtePreloadRelease? PreloadRelease { get; private set; }
    private string? _lastSelectedPath;
    private GameVersion? _launcherOnlyVersion;

    internal bool IsGamePathCompatible(string gamePath)
    {
        NteRegionConfig? installedRegion = NteConfigProvider.DetectInstalledRegion(gamePath);
        return installedRegion == null || installedRegion.HostRegion == Region.HostRegion ||
            !installedRegion.IsOverseas && !Region.IsOverseas ||
            installedRegion.IsOverseas && Region.IsOverseas && NteSharedInstallation.Contains(gamePath, Region);
    }

    internal bool NeedsMainlandLauncher(string path)
    {
        if (Region.IsOverseas) return false;
        string directory = NteRegionDefinition.From(Region.HostRegion).LauncherDirectoryFor(path);
        return (Region.IsBilibili || NteRegionDefinition.IsStockBilibili(path)) &&
            !NteOfficialLauncherInstaller.IsReady(path, Region, directory);
    }

    internal bool TryGetLauncherOnlyPath([NotNullWhen(true)] out string? path)
    {
        path = CurrentGameInstallPath;
        return !string.IsNullOrEmpty(path) && !Region.IsOverseas && CurrentGameVersion != GameVersion.Empty &&
            IsGamePathCompatible(path) && File.Exists(Path.Combine(path, Region.GameExecutableName)) &&
            NeedsMainlandLauncher(path);
    }

    internal void PreserveVersionAfterLauncherInstall() => _launcherOnlyVersion = CurrentGameVersion;

    internal void RecordInstalledVersion(in GameVersion version)
    {
        _launcherOnlyVersion = null;
        SetCurrentGameVersion(in version);
    }

    private bool AcceptCurrentGamePath()
    {
        if (string.IsNullOrWhiteSpace(CurrentGameInstallPath)) return false;
        if (IsGamePathCompatible(CurrentGameInstallPath)) return true;
        SharedStatic.InstanceLogger.LogWarning("Ignoring a different NTE region's installation path for {Profile}: {Path}",
            Region.ProfileName, CurrentGameInstallPath);
        CurrentGameInstallPath = null;
        _lastSelectedPath = null;
        CurrentGameVersion = GameVersion.Empty;
        return false;
    }

    [field: AllowNull, MaybeNull]
    protected override HttpClient ApiResponseHttpClient
    {
        get => field ??= new PluginHttpClientBuilder()
            .AllowRedirections()
            .AllowUntrustedCert()
            .Create();
        set;
    }

    /// <summary>CDN 资源基础 URL 列表（从 PatcherConfig 或默认值）</summary>
    internal string[] GameResBaseUrls { get; private set; } = NteConfigProvider.GameResBaseUrls;

    /// <summary>分支名称（从 PatcherConfig 或默认值）</summary>
    internal string BranchName { get; private set; } = NteConfigProvider.DefaultBranchName;

    /// <summary>应用 ID（从 PatcherConfig 或默认值）</summary>
    internal string AppId { get; private set; } = NteConfigProvider.DefaultAppId;

    protected override bool HasPreload => IsInstalled && CurrentGameVersion == ApiGameVersion && !HasUpdate &&
        PreloadRelease != null && CurrentGameVersion < PreloadRelease.Version;

    protected override bool HasUpdate =>
        IsInstalled &&
        CurrentGameVersion != GameVersion.Empty &&
        ApiGameVersion != GameVersion.Empty &&
        (CurrentGameVersion < ApiGameVersion || !string.IsNullOrEmpty(CurrentGameInstallPath) && NteSharedInstallation.NeedsActivation(CurrentGameInstallPath, Region));

    protected override bool IsInstalled
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentGameInstallPath))
                return false;

            if (CurrentGameVersion == GameVersion.Empty)
                return false;

            // 检查游戏主可执行文件是否存在
            string exePath = Path.Combine(CurrentGameInstallPath, Region.GameExecutableName);

            return File.Exists(exePath) && IsGamePathCompatible(CurrentGameInstallPath) &&
                !NeedsMainlandLauncher(CurrentGameInstallPath);
        }
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
    {
        // Collapse also applies the API version after installation. Adding a
        // launcher leaves the shared Client at its previously installed version.
        if (_launcherOnlyVersion is { } installed) CurrentGameVersion = installed;
    }

    protected override void SetGamePathInner(string gamePath)
    {
        string? normalized = string.IsNullOrWhiteSpace(gamePath) ? null : Path.GetFullPath(gamePath);
        if (!string.Equals(_lastSelectedPath, normalized, StringComparison.OrdinalIgnoreCase))
        {
            CurrentGameVersion = GameVersion.Empty;
            _launcherOnlyVersion = null;
        }
        CurrentGameInstallPath = normalized;
        _lastSelectedPath = normalized;
        AcceptCurrentGamePath();
    }

    public override void LoadConfig()
    {
        CurrentGameVersion = GameVersion.Empty;
        if (!AcceptCurrentGamePath() || string.IsNullOrEmpty(CurrentGameInstallPath))
            return;

        NteSharedInstallationState? shared = Region.IsOverseas ? NteSharedInstallation.Load(CurrentGameInstallPath) : null;
        if (shared?.Profiles.TryGetValue(Region.AppId, out NteSharedProfile? profile) == true &&
            GameVersion.TryParse(profile.Version, out GameVersion sharedVersion))
        {
            CurrentGameVersion = sharedVersion;
            return;
        }
        foreach (string directory in Region.PatcherDirectoriesFor(CurrentGameInstallPath))
        {
            string officialConfigPath = Path.Combine(CurrentGameInstallPath, directory, "config.xml");
            if (!File.Exists(officialConfigPath)) continue;
            try
            {
                GameVersion version = ParseReleaseVersion(File.ReadAllText(officialConfigPath));
                if (version > CurrentGameVersion) CurrentGameVersion = version;
            }
            catch (Exception ex) when (ex is IOException or XmlException or InvalidDataException)
            {
                SharedStatic.InstanceLogger.LogWarning(ex, "[NteCNGameManager::LoadConfig] Cannot read official version");
            }
        }
        if (CurrentGameVersion != GameVersion.Empty) return;

        string configPath = Path.Combine(CurrentGameInstallPath, ".collapse_nte_config");
        if (!File.Exists(configPath))
            return;

        try
        {
            string[] lines = File.ReadAllLines(configPath);
            foreach (string line in lines)
            {
                int eqIdx = line.IndexOf('=');
                if (eqIdx <= 0) continue;

                string key = line[..eqIdx].Trim();
                string value = line[(eqIdx + 1)..].Trim();

                switch (key)
                {
                    case "GameVersion":
                        if (GameVersion.TryParse(value, out GameVersion ver))
                            CurrentGameVersion = ver;
                        break;
                }
            }

            SharedStatic.InstanceLogger.LogInformation(
                "[NteCNGameManager::LoadConfig] Loaded config: Version={V}",
                CurrentGameVersion);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(ex,
                "[NteCNGameManager::LoadConfig] Failed to load config");
        }
    }

    public override void SaveConfig()
    {
        if (!AcceptCurrentGamePath() || string.IsNullOrEmpty(CurrentGameInstallPath))
            return;

        try
        {
            if (NteSharedInstallation.HasPendingSwitch(CurrentGameInstallPath)) return;
            if (NteSharedInstallation.Load(CurrentGameInstallPath) is { } shared && shared.ActiveAppId != Region.AppId) return;
            string configPath = Path.Combine(CurrentGameInstallPath, ".collapse_nte_config");
            string identity = !Region.IsOverseas && NteRegionDefinition.IsStockBilibili(CurrentGameInstallPath)
                ? $"AppId={Region.AppId}\nRegion={NteHostRegion.Bilibili}\n" :
                Region.IsOverseas ? $"AppId={Region.AppId}\n" : "";
            NteSharedInstallation.WriteAtomic(configPath, Encoding.UTF8.GetBytes($"GameVersion={CurrentGameVersion}\n" + identity));

            SharedStatic.InstanceLogger.LogInformation(
                "[NteCNGameManager::SaveConfig] Saved config: Version={V}",
                CurrentGameVersion);
        }
        catch (Exception ex)
        {
            SharedStatic.InstanceLogger.LogWarning(ex,
                "[NteCNGameManager::SaveConfig] Failed to save config");
        }
    }

    protected override Task<string?> FindExistingInstallPathAsyncInner(CancellationToken token)
    {
        string? sharedRoot = NteSharedInstallation.FindRoot(Region);
        if (sharedRoot != null) return Task.FromResult<string?>(sharedRoot);
        // 尝试从注册表查找已有安装路径（仅 Windows）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                string? installPath = FindInstallPathFromRegistry();
                if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath) &&
                    IsGamePathCompatible(installPath) && File.Exists(Path.Combine(installPath, Region.GameExecutableName)))
                {
                    SharedStatic.InstanceLogger.LogInformation(
                        "[NteCNGameManager::FindExistingInstallPathAsyncInner] Found install path from registry: {Path}",
                        installPath);
                    return Task.FromResult<string?>(installPath);
                }
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogDebug(ex,
                    "[NteCNGameManager::FindExistingInstallPathAsyncInner] Registry lookup failed");
            }
        }

        return Task.FromResult<string?>(null);
    }

    [SupportedOSPlatform("windows")]
    private string? FindInstallPathFromRegistry()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            Region.GameRegistryKeyName);
        return key?.GetValue("InstallPath") as string;
    }

    protected override async Task<int> InitAsync(CancellationToken token)
    {
        return await InitAsyncInner(false, token).ConfigureAwait(false);
    }

    /// <summary>
    /// 内部初始化方法，可被 Installer 调用以强制刷新。
    /// 从 config.xml 获取游戏版本。
    /// </summary>
    internal async Task<int> InitAsyncInner(bool forceRefresh, CancellationToken token)
    {
        if (!forceRefresh && ApiGameVersion != GameVersion.Empty)
            return 0;

        PreloadRelease = null;
        ApiPreloadGameVersion = GameVersion.Empty;
        try
        {
            NtePatcherConfig patcher = await NtePatcherConfig.DiscoverAsync(ApiResponseHttpClient, token, Region).ConfigureAwait(false);
            string xml = await patcher.ReadReleaseConfigAsync(ApiResponseHttpClient, token).ConfigureAwait(false);
            GameVersion version = ParseReleaseVersion(xml);
            GameResBaseUrls = patcher.ResourceUrls;
            BranchName = patcher.BranchName;
            AppId = patcher.AppId;
            ReleaseConfigXml = xml;
            ApiGameVersion = version;
            PreloadRelease = NtePreloadRelease.Parse(xml, version);
            ApiPreloadGameVersion = PreloadRelease?.Version ?? GameVersion.Empty;
            SharedStatic.InstanceLogger.LogInformation(
                "[NteCNGameManager::InitAsyncInner] Active branch={Branch}, API version={Version}", BranchName, version);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PreloadRelease = null;
            ApiPreloadGameVersion = GameVersion.Empty;
            SharedStatic.InstanceLogger.LogError(ex, "[NteCNGameManager::InitAsyncInner] Cannot resolve the active official game release");
            return -1;
        }
    }

    internal static GameVersion ParseReleaseVersion(string xml)
    {
        using StringReader input = new(xml);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        XmlDocument document = new() { XmlResolver = null };
        document.Load(reader);
        string? value = document.DocumentElement?["ResVersion"]?.InnerText;
        if (!GameVersion.TryParse(value, out GameVersion version) || version == GameVersion.Empty)
            throw new InvalidDataException("Official config has no valid ResVersion.");
        return version;
    }
}
