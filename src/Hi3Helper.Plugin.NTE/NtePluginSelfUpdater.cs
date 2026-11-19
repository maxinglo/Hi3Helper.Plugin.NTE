using Hi3Helper.Plugin.Core.Update;
using Hi3Helper.Plugin.Core.Utility;
using System;
using System.Net.Http;
using System.Runtime.InteropServices.Marshalling;

namespace Hi3Helper.Plugin.NTE;

[GeneratedComClass]
internal sealed partial class NtePluginSelfUpdater : PluginSelfUpdateBase
{
    private static readonly string[] ReleaseSources =
    [
        "https://github.com/maxinglo/Hi3Helper.Plugin.NTE/releases/download/collapse-update",
        "https://gitee.com/maxing_long/hi3helper-plugin-nte/releases/download/collapse-update"
    ];

    private readonly Lazy<HttpClient> _updateHttpClient =
        new(() => new PluginHttpClientBuilder().AllowRedirections().Create());

    protected override ReadOnlySpan<string> BaseCdnUrlSpan => ReleaseSources;

    protected override HttpClient UpdateHttpClient => _updateHttpClient.Value;
}
