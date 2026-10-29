using Nte.LoginHost;
using Xunit;

namespace Hi3Helper.Plugin.NTE.Tests;

public sealed class BackdropRefreshScheduleTests
{
    [Fact]
    public void RefreshesAtAbsoluteDeadlinesThenStops()
    {
        BackdropRefreshSchedule schedule = new();
        TimeSpan start = TimeSpan.FromSeconds(123);
        schedule.Start(start);
        foreach (int second in new[] { 5, 15, 60 })
        {
            TimeSpan deadline = start + TimeSpan.FromSeconds(second);
            Assert.False(schedule.TryConsumeDue(deadline - TimeSpan.FromMilliseconds(1)));
            Assert.True(schedule.TryConsumeDue(deadline));
            Assert.False(schedule.TryConsumeDue(deadline));
        }
        Assert.Null(schedule.DelayUntilNext(start + TimeSpan.FromHours(1)));
        Assert.False(schedule.TryConsumeDue(start + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void MissedDeadlinesCoalesceAndANewLoginRestartsTheSchedule()
    {
        BackdropRefreshSchedule schedule = new();
        schedule.Start(TimeSpan.Zero);
        Assert.True(schedule.TryConsumeDue(TimeSpan.FromSeconds(20)));
        Assert.False(schedule.TryConsumeDue(TimeSpan.FromSeconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(40), schedule.DelayUntilNext(TimeSpan.FromSeconds(20)));
        schedule.Stop();
        Assert.False(schedule.TryConsumeDue(TimeSpan.FromSeconds(80)));
        schedule.Start(TimeSpan.FromSeconds(100));
        Assert.False(schedule.TryConsumeDue(TimeSpan.FromSeconds(104)));
        Assert.True(schedule.TryConsumeDue(TimeSpan.FromSeconds(105)));
    }
}
