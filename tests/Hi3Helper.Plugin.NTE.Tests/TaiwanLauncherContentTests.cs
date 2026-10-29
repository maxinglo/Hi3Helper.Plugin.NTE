using Hi3Helper.Plugin.Core.Management;
using Hi3Helper.Plugin.NTE.Management.Api.Media;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class TaiwanLauncherContentTests
{
    [Fact]
    public void CarouselUsesTaiwanScheduleAndRejectsExpiredFutureMalformedAndUnsafeEntries()
    {
        const string html="""
        <div class="carousel-item" on="2026/10/30 00:00:00" off="2026/10/31 23:59:59"><a href="/event/current.html"><img src="/CmsBanner/current.jpg" alt="Current &amp; official"></a></div>
        <div class="carousel-item" on="2026/09/30 00:00:00" off="2026/10/29 23:59:59"><a href="/old"><img src="/old.jpg"></a></div>
        <div class="carousel-item" on="2026/10/31 00:00:00" off="2999/12/31 23:59:59"><a href="/future"><img src="/future.jpg"></a></div>
        <div class="carousel-item" on="malformed" off="2999/12/31 23:59:59"><a href="/bad"><img src="/bad.jpg"></a></div>
        <div class="carousel-item" on="2026/10/30 00:00:00" off="2999/12/31 23:59:59"><a href="javascript:alert(1)"><img src="/unsafe.jpg"></a></div>
        """;
        var item=Assert.Single(NteTaiwanLauncherContent.ParseCarousel(html,new DateTimeOffset(2026,10,29,16,0,0,TimeSpan.Zero)));
        Assert.Equal("https://nte.iwplay.com.tw/event/current.html",item.link); Assert.Equal("https://nte.iwplay.com.tw/CmsBanner/current.jpg",item.img); Assert.Equal("Current & official",item.title);
    }

    [Fact]
    public void NewsReadsDynamicFragmentTitlesDatesAndTypesWithoutImageOrDescriptionDuplicates()
    {
        const string html="""
        <a class="systemurl" href="/news/view/20261030/test.html" title="Image copy"><img src="/image.png"></a>
        <div class="system-single"><a class="systemurl intelSlideTit" href="/news/view/20261030/test.html" title="Title &amp; Event">Title &amp; Event</a><a class="systemurl intelSlideDes" href="/news/view/20261030/test.html">Description</a><div class="abs intelDate">2026-10-30</div></div>
        <a class="intelSlideTit" href="https://foreign.example/news/view/20261030/wrong.html" title="Foreign">Foreign</a>
        """;
        var item=Assert.Single(NteTaiwanLauncherContent.ParseNews(html,LauncherNewsEntryType.Event));
        Assert.Equal("Title & Event",item.title); Assert.Equal("2026-10-30",item.date); Assert.Equal(LauncherNewsEntryType.Event,item.type);
    }

    [Fact]
    public void SocialPseudoElementsPreserveBothOfficialImagesAndLinkTitles()
    {
        const string html="""<ul class="ewm-list"><li class="icon-discord"><a title="Discord" href="https://discord.gg/fixture"></a></li></ul>""";
        const string css=".icon-discord::before{background-image:url(../images/normal.png)} .icon-discord::after{background-image:url(../images/hover.png)}";
        var icon=Assert.Single(NteLauncherHtmlAssets.ParseSocials(html,css,"https://nte.iwplay.com.tw/platform/index.html","https://images1.iwplay.com.tw/style/launcher.css"));
        Assert.Equal("Discord",icon.Description); Assert.Equal("https://images1.iwplay.com.tw/images/normal.png",icon.IconUrl); Assert.Equal("https://images1.iwplay.com.tw/images/hover.png",icon.HoverIconUrl);
    }
}
