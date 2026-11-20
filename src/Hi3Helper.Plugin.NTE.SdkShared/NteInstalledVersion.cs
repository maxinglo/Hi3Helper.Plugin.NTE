using System.Xml;

namespace Nte.Login;

internal static class NteInstalledVersion
{
    internal static string Resolve(string gameRoot, NteRegionDefinition region, string officialVersion)
    {
        if (Valid(officialVersion, out string version)) return version;

        string installed = Path.Combine(gameRoot, ".collapse-nte", "installed");
        string[] profiles = region.IsOverseas ? [region.AppId] : [region.AppId, NteRegionDefinition.BilibiliProfileName];
        foreach (string profile in profiles)
        try
        {
            string manifest = Path.Combine(installed, profile, "ResList.xml");
            if (File.Exists(manifest))
            {
                using XmlReader reader = XmlReader.Create(manifest, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                    MaxCharactersInDocument = 32 * 1024 * 1024
                });
                if (reader.MoveToContent() == XmlNodeType.Element && reader.Name == "ResList" &&
                    Valid(reader.GetAttribute("version"), out version)) return version;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        {
        }

        string marker = Path.Combine(gameRoot, ".collapse_nte_config");
        if (File.Exists(marker) && new FileInfo(marker).Length <= 64 * 1024)
        {
            string? recordedVersion = null, recordedAppId = null, recordedRegion = null;
            foreach (string line in File.ReadLines(marker))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                string key = line[..separator].Trim(), value = line[(separator + 1)..].Trim();
                if (key == "GameVersion") recordedVersion = value;
                else if (key == "AppId") recordedAppId = value;
                else if (key == "Region") recordedRegion = value;
            }

            // Mainland channels share the same Client and installed version.
            bool matches = region.IsOverseas
                ? recordedRegion == null && recordedAppId == region.AppId
                : recordedRegion == null && (recordedAppId == null || recordedAppId == region.AppId) ||
                    recordedRegion == nameof(NteHostRegion.Bilibili) && recordedAppId == region.AppId;
            if (matches && Valid(recordedVersion, out version)) return version;
        }

        throw new InvalidDataException("Official public application metadata is missing.");
    }

    private static bool Valid(string? text, out string value)
    {
        value = "";
        if (!Version.TryParse(text?.Trim(), out Version? parsed) ||
            parsed.Major == 0 && parsed.Minor == 0 && parsed.Build <= 0 && parsed.Revision <= 0) return false;
        value = parsed.ToString();
        return true;
    }
}
