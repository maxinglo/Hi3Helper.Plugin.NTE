using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.Core.Utility;
using Hi3Helper.Plugin.NTE.Management.Config;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Hi3Helper.Plugin.Core;

namespace Hi3Helper.Plugin.NTE.Management.Game;

[GeneratedComClass]
internal partial class NteCNGameManager : GameManagerBase
{
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
        CurrentGameVersion != ApiGameVersion;

    protected override bool IsInstalled
    {
        get
        {
            if (string.IsNullOrEmpty(CurrentGameInstallPath))
                return false;

            if (CurrentGameVersion == GameVersion.Empty)
                return false;

            // 检查游戏主可执行文件是否存在
            string exePath = Path.Combine(CurrentGameInstallPath, NteConfigProvider.CN.GameExecutableName);

            return File.Exists(exePath);
        }
    }

    protected override void SetCurrentGameVersionInner(in GameVersion gameVersion)
    {
        // 版本在 SaveConfig 时持久化
    }

    protected override void SetGamePathInner(string gamePath)
    {
        // 路径在 SaveConfig 时持久化
    }

    public override void LoadConfig()
    {
        if (string.IsNullOrEmpty(CurrentGameInstallPath))
            return;

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
        if (string.IsNullOrEmpty(CurrentGameInstallPath))
            return;

        try
        {
            string configPath = Path.Combine(CurrentGameInstallPath, ".collapse_nte_config");
            Directory.CreateDirectory(CurrentGameInstallPath);
            File.WriteAllText(configPath, $"GameVersion={CurrentGameVersion}\n");

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
        // 尝试从注册表查找已有安装路径（仅 Windows）
        if (OperatingSystem.IsWindows())
        {
            try
            {
                string? installPath = FindInstallPathFromRegistry();
                if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
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
    private static string? FindInstallPathFromRegistry()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            NteConfigProvider.CN.GameRegistryKeyName);
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

        foreach (string url in NteConfigProvider.ConfigXmlUrls)
        {
            try
            {
                using HttpResponseMessage response = await ApiResponseHttpClient.GetAsync(url, token);
                response.EnsureSuccessStatusCode();

                string xmlContent = await response.Content.ReadAsStringAsync(token);

                var match = Regex.Match(xmlContent, @"<ResVersion>(.*?)</ResVersion>");
                if (match.Success && GameVersion.TryParse(match.Groups[1].Value, out GameVersion version))
                {
                    ApiGameVersion = version;
                    SharedStatic.InstanceLogger.LogInformation(
                        "[NteCNGameManager::InitAsyncInner] Successfully fetched ApiGameVersion: {V} from {Url}",
                        version, url);
                    return 0;
                }
                else
                {
                    SharedStatic.InstanceLogger.LogWarning(
                        "[NteCNGameManager::InitAsyncInner] Failed to parse ResVersion from {Url}", url);
                }
            }
            catch (Exception ex)
            {
                SharedStatic.InstanceLogger.LogWarning(ex,
                    "[NteCNGameManager::InitAsyncInner] Failed to fetch config from {Url}", url);
            }
        }

        SharedStatic.InstanceLogger.LogError(
            "[NteCNGameManager::InitAsyncInner] Exhausted all CDNs and failed to fetch API Game Version.");
        return -1;
    }
}
