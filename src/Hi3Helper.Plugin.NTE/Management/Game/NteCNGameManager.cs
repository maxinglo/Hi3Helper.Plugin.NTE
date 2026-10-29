using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Xml;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;

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
    private string? _lastSelectedPath;

    internal bool IsGamePathCompatible(string gamePath)
    {
        NteRegionConfig? installedRegion = NteConfigProvider.DetectInstalledRegion(gamePath);
        return installedRegion == null || installedRegion.AppId == Region.AppId ||
            installedRegion.IsOverseas && Region.IsOverseas && NteSharedInstallation.Contains(gamePath, Region);
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

    protected override bool HasPreload => false;

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

            return File.Exists(exePath) && IsGamePathCompatible(CurrentGameInstallPath);
        }
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
    {
        // 版本在 SaveConfig 时持久化
    }

    protected override void SetGamePathInner(string gamePath)
    {
        string? normalized = string.IsNullOrWhiteSpace(gamePath) ? null : Path.GetFullPath(gamePath);
        if (!string.Equals(_lastSelectedPath, normalized, StringComparison.OrdinalIgnoreCase))
            CurrentGameVersion = GameVersion.Empty;
        _lastSelectedPath = normalized;
        AcceptCurrentGamePath();
    }

    public override void LoadConfig()
    {
        CurrentGameVersion = GameVersion.Empty;
        if (!AcceptCurrentGamePath() || string.IsNullOrEmpty(CurrentGameInstallPath))
            return;

        NteSharedInstallationState? shared = NteSharedInstallation.Load(CurrentGameInstallPath);
        if (shared?.Profiles.TryGetValue(Region.AppId, out NteSharedProfile? profile) == true &&
            GameVersion.TryParse(profile.Version, out GameVersion sharedVersion))
        {
            CurrentGameVersion = sharedVersion;
            return;
        }
        string officialConfigPath = Path.Combine(CurrentGameInstallPath, Region.SdkDirectory, "config.xml");
        if (File.Exists(officialConfigPath))
        {
            try
            {
                CurrentGameVersion = ParseReleaseVersion(File.ReadAllText(officialConfigPath));
                SharedStatic.InstanceLogger.LogInformation("[NteCNGameManager::LoadConfig] Official installed version={V}", CurrentGameVersion);
                return;
            }
            catch (Exception ex) when (ex is IOException or XmlException or InvalidDataException)
            {
                SharedStatic.InstanceLogger.LogWarning(ex, "[NteCNGameManager::LoadConfig] Cannot read official version");
            }
        }

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
            Directory.CreateDirectory(CurrentGameInstallPath);
            File.WriteAllText(configPath, $"GameVersion={CurrentGameVersion}\n" + (Region.IsOverseas ? $"AppId={Region.AppId}\n" : ""));

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
            SharedStatic.InstanceLogger.LogInformation(
                "[NteCNGameManager::InitAsyncInner] Active branch={Branch}, API version={Version}", BranchName, version);
            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
