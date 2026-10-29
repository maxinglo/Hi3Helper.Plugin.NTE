using Hi3Helper.Plugin.NTE.Management.Game;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class PerFileProgressTests
{
    [Fact]
    public void ParallelDownloads_KeepTheFirstActiveFileInsteadOfTheLastCompletedFile()
    {
        NtePerFileProgress progress = new();
        progress.Begin(0, 1000);
        progress.Add(0, 200);
        progress.Begin(1, 100);
        progress.Add(1, 100);
        Assert.Equal((200L, 1000L), progress.Current);
        progress.End(1);
        Assert.Equal((200L, 1000L), progress.Current);
        progress.Add(0, -100);
        Assert.Equal((100L, 1000L), progress.Current);
        progress.Add(0, 900);
        Assert.Equal((1000L, 1000L), progress.Current);
        progress.End(0);
        Assert.Null(progress.Current);
    }

    [Fact]
    public void CompletedFile_SwitchesToNextActiveFileAndResetsItsOwnProgress()
    {
        NtePerFileProgress progress = new();
        progress.Begin(0, 100);
        progress.Add(0, 100);
        progress.Begin(1, 1000);
        progress.Add(1, 150);
        progress.End(0);
        Assert.Equal((150L, 1000L), progress.Current);
    }
}
