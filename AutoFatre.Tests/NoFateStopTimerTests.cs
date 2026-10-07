namespace AutoFatre.Tests;

public sealed class NoFateStopTimerTests
{
    private static readonly DateTime Start = new(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(10)]
    public void RequiresFullContinuousTimeout(int seconds)
    {
        NoFateStopTimer timer = new();
        timer.Observe(Start, 0, 152, 1, true, false);
        timer.Observe(Start.AddSeconds(seconds).AddMilliseconds(-1), 0, 152, 1, true, false);
        Assert.Equal(seconds - 1, timer.ElapsedSeconds);
        timer.Observe(Start.AddSeconds(seconds), 0, 152, 1, true, false);
        Assert.Equal(seconds, timer.ElapsedSeconds);
    }

    [Theory]
    [InlineData(true, true)] // Any observed FATE, including blacklisted/unsupported/preparing ones.
    [InlineData(false, false)] // Pause, death, loading, wrong map, recovery or unavailable FATE data.
    public void FateAppearanceOrUnavailableObservationRestartsTimer(bool canObserve, bool hasFates)
    {
        NoFateStopTimer timer = new();
        timer.Observe(Start, 0, 152, 1, true, false);
        timer.Observe(Start.AddSeconds(4), 0, 152, 1, canObserve, hasFates);
        Assert.Equal(0, timer.ElapsedSeconds);
        timer.Observe(Start.AddSeconds(100), 0, 152, 1, true, false);
        Assert.Equal(0, timer.ElapsedSeconds);
        timer.Observe(Start.AddSeconds(103), 0, 152, 1, true, false);
        Assert.Equal(3, timer.ElapsedSeconds);
    }

    [Theory]
    [InlineData(1, 152u, 1u)]
    [InlineData(0, 153u, 1u)]
    [InlineData(0, 152u, 2u)]
    public void DifferentPresetMapOrInstanceStartsNewTimer(int preset, uint territory, uint instance)
    {
        NoFateStopTimer timer = new();
        timer.Observe(Start, 0, 152, 1, true, false);
        timer.Observe(Start.AddSeconds(10), preset, territory, instance, true, false);
        Assert.Equal(0, timer.ElapsedSeconds);
    }

    [Fact]
    public void ExplicitResetClearsProgressAndLongWaitIsCapped()
    {
        NoFateStopTimer timer = new();
        timer.Observe(Start, 0, 152, 1, true, false);
        timer.Observe(Start.AddHours(1), 0, 152, 1, true, false);
        Assert.Equal(10, timer.ElapsedSeconds);
        timer.Reset();
        Assert.Equal(0, timer.ElapsedSeconds);
        timer.Observe(Start.AddHours(2), 0, 152, 1, true, false);
        Assert.Equal(0, timer.ElapsedSeconds);
    }
}
