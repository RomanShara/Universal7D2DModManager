using U7D2D.Core.Infrastructure;

namespace U7D2D.Core.Tests;

public sealed class ProgressThrottleTests
{
    [Fact]
    public void Only_the_first_rapid_tick_reaches_the_ui()
    {
        var seen = new List<string>();
        var throttle = new ThrottledProgress<string>(
            new DirectProgress<string>(seen.Add), intervalMilliseconds: 500, keySelector: value => value);

        for (var i = 0; i < 100; i++) throttle.Report("reading");

        Assert.Single(seen);
    }

    [Fact]
    public void A_stage_change_is_forwarded_immediately()
    {
        var seen = new List<string>();
        var throttle = new ThrottledProgress<string>(
            new DirectProgress<string>(seen.Add), intervalMilliseconds: 500, keySelector: value => value);

        throttle.Report("index");
        throttle.Report("scan");
        throttle.Report("analyze");

        Assert.Equal(new[] { "index", "scan", "analyze" }, seen);
    }

    [Fact]
    public void A_tick_after_the_interval_is_forwarded()
    {
        var seen = new List<string>();
        var throttle = new ThrottledProgress<string>(
            new DirectProgress<string>(seen.Add), intervalMilliseconds: 20, keySelector: value => value);

        throttle.Report("reading");
        Thread.Sleep(60);
        throttle.Report("reading");

        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void Direct_progress_runs_on_the_calling_thread()
    {
        var called = 0;
        var progress = new DirectProgress<int>(_ => called++);

        progress.Report(1);
        progress.Report(2);

        Assert.Equal(2, called);
    }
}
