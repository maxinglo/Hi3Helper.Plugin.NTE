using System.Globalization;

namespace Nte.Login;

// Explicit trust and storage boundaries for the two Global-SDK publishers.
// Domain and application values are verified against each installed resource.
internal sealed class NteOverseasRegion
{
    internal static NteOverseasRegion Global { get; } = new(NteHostRegion.Global, 3000001, "NTEGlobal", "com.hottagames.nte",
        "https://mapi.pwsdk.com", "global-sg", "pwsdk.com", "https://i.pwsdk.com", "Global",
        "0153111179C82EE2AA984B2DDB33648C692824A97A80AD104D522C742CF4E49B",
        "B922E40052ED1B0F7306D909986A69A58C1FDD869233307162DD4572A63D5AB2");
    internal static NteOverseasRegion Taiwan { get; } = new(NteHostRegion.Taiwan, 2000013, "NTETW", "tw.com.iwplay.nte",
        "https://mapitw.playcomb.com", "iwplay", "playcomb.com", "https://i.playcomb.com", null,
        "E853AC6797F9D2AC901FB76E3209F3D5841A4CA642017BD7182E6B8D26028C6A",
        "999551DF1512D6EE453B1A2C730A45893E5431B164157567A44F9B3FE1A04335");

    internal NteHostRegion HostRegion { get; }
    internal string Name => HostRegion.ToString();
    internal int ApplicationId { get; }
    internal string AppId => ApplicationId.ToString(CultureInfo.InvariantCulture);
    internal string LauncherDirectory { get; }
    internal string PackageName { get; }
    internal Uri Service { get; }
    internal string Area { get; }
    internal string BrowserDomain { get; }
    internal Uri WebService { get; }
    internal string? SavedDirectorySuffix { get; }
    internal string ImageSha256 { get; }
    internal string BridgeSha256 { get; }
    internal Uri TermsUri => new(HostRegion == NteHostRegion.Taiwan
        ? "https://www.iwplay.com.tw/pdf/contract-ht.pdf"
        : "https://static.pwsdk.com/nte/contract/contract.html");
    internal Uri PrivacyUri => new(HostRegion == NteHostRegion.Taiwan
        ? "https://www.iwplay.com.tw/pdf/privacy-ht.pdf"
        : "https://static.pwsdk.com/nte/privacy/privacy.html");

    private NteOverseasRegion(NteHostRegion hostRegion, int applicationId, string launcher, string packageName,
        string service, string area, string browserDomain, string webService, string? savedDirectorySuffix,
        string imageSha256, string bridgeSha256)
    {
        HostRegion = hostRegion; ApplicationId = applicationId; LauncherDirectory = launcher; PackageName = packageName;
        Service = new(service); Area = area; BrowserDomain = browserDomain; WebService = new(webService);
        SavedDirectorySuffix = savedDirectorySuffix; ImageSha256 = imageSha256; BridgeSha256 = bridgeSha256;
    }

    internal static NteOverseasRegion From(NteHostRegion region) => region switch
    {
        NteHostRegion.Global => Global, NteHostRegion.Taiwan => Taiwan,
        _ => throw new ArgumentException("An overseas launch region is required.", nameof(region))
    };

    internal bool IsBrowserOrigin(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && (uri.Host.Equals(BrowserDomain, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + BrowserDomain, StringComparison.OrdinalIgnoreCase));
}
