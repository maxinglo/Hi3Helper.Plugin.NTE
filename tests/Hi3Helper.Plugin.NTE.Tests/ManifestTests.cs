using System.IO;
using System.Xml;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class ManifestTests
{
    [Fact]
    public void FileListUrl_PreservesQueryStringAndResolvesRelativePath()
    {
        Assert.Equal("https://example.com/launcher/1/AllFiles.xml?token=a=b",
            NteLauncherManifest.ParseFileListUrl("; comment\nFileListURL = 1/AllFiles.xml?token=a=b",
                "https://example.com/launcher/Version.ini"));
    }

    [Fact]
    public void ManifestPaths_StayUnderVersionDirectory()
    {
        NteLauncherManifest manifest = NteLauncherManifest.Parse(
            """<All_Files><File Path="\ResFilesM\1289\bgimgs\bg_0.jpg" /></All_Files>""",
            "https://example.com/launcher/1/AllFiles.xml");
        Assert.True(manifest.TryFindPathBySuffix("/bgimgs/bg_0.jpg", out string path));
        Assert.Equal("https://example.com/launcher/1/ResFilesM/1289/bgimgs/bg_0.jpg.zip", manifest.GetArchiveUrl(path));
    }

    [Fact]
    public void MissingFileListUrl_FailsClearly()
        => Assert.Throws<InvalidDataException>(() => NteLauncherManifest.ParseFileListUrl("[Version]\nFileListURL=",
            "https://example.com/Version.ini"));

    [Fact]
    public void ExternalEntities_AreRejected()
        => Assert.Throws<XmlException>(() => NteLauncherManifest.Parse(
            """<!DOCTYPE All_Files [<!ENTITY external SYSTEM "file:///C:/Windows/win.ini">]><All_Files>&external;</All_Files>""",
            "https://example.com/AllFiles.xml"));
}
