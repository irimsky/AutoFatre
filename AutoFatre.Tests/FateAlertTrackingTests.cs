using System.Numerics;

namespace AutoFatre.Tests;

public sealed class FateAlertTrackingTests
{
    private static readonly FateOccurrence Fate = new(152, 1, 616, 100);

    [Fact]
    public void EntryWithExistingPlayersAlertsOnceAndNewArrivalsAlert()
    {
        FateAlertTracking tracking = new();
        Assert.True(tracking.ObservePlayers(Fate, [1, 2]));
        Assert.False(tracking.ObservePlayers(Fate, [1, 2]));
        Assert.True(tracking.ObservePlayers(Fate, [1, 2, 3]));
        Assert.False(tracking.ObservePlayers(Fate, [2, 3]));
        Assert.True(tracking.ObservePlayers(Fate, [1, 2, 3]));
    }

    [Fact]
    public void EmptyEntryIsSilentAndReplacementAtSameCountAlerts()
    {
        FateAlertTracking tracking = new();
        Assert.False(tracking.ObservePlayers(Fate, []));
        Assert.True(tracking.ObservePlayers(Fate, [1]));
        Assert.True(tracking.ObservePlayers(Fate, [2]));
        Assert.False(tracking.ObservePlayers(Fate, [2, 2]));
    }

    [Fact]
    public void LeavingAndReenteringOrChangingFateRearmsEntryAlert()
    {
        FateAlertTracking tracking = new();
        Assert.True(tracking.ObservePlayers(Fate, [1]));
        Assert.False(tracking.ObservePlayers(null, [1]));
        Assert.True(tracking.ObservePlayers(Fate, [1]));
        Assert.True(tracking.ObservePlayers(Fate with { StartTimeEpoch = 101 }, [1]));
        Assert.True(tracking.ObservePlayers(Fate with { Instance = 2 }, [1]));
        tracking.ResetPlayers();
        Assert.True(tracking.ObservePlayers(Fate, [1]));
    }

    [Fact]
    public void InvalidObjectIdsDoNotAlert()
    {
        FateAlertTracking tracking = new();
        Assert.False(tracking.ObservePlayers(Fate, [0, 0xE0000000]));
    }

    [Theory]
    [InlineData(1000u)]
    [InlineData(1500u)]
    public void GemstonesUseActualCapAndAlertOnceUntilSpent(uint cap)
    {
        FateAlertTracking tracking = new();
        Assert.False(tracking.ObserveGemstones(cap - 1, cap));
        Assert.True(tracking.ObserveGemstones(cap, cap));
        Assert.False(tracking.ObserveGemstones(cap, cap));
        tracking.ResetPlayers(); // A map/FATE transition must not repeat the full-wallet alert.
        Assert.False(tracking.ObserveGemstones(cap, cap));
        Assert.False(tracking.ObserveGemstones(cap - 1, cap));
        Assert.True(tracking.ObserveGemstones(cap, cap));
        tracking.Reset();
        Assert.True(tracking.ObserveGemstones(cap, cap)); // Already full when a run starts.
    }

    [Fact]
    public void MissingCurrencyDataDoesNotAlertOrRearm()
    {
        FateAlertTracking tracking = new();
        Assert.False(tracking.ObserveGemstones(0, 0));
        Assert.True(tracking.ObserveGemstones(1500, 1500));
        Assert.False(tracking.ObserveGemstones(0, 0));
        Assert.False(tracking.ObserveGemstones(1500, 1500));
    }

    [Theory]
    [InlineData(50, 0, 50, true)]
    [InlineData(51, 0, 50, false)]
    [InlineData(30, 40, 50, true)]
    [InlineData(30, 41, 50, false)]
    [InlineData(0, 0, 0, false)]
    [InlineData(0, 0, -1, false)]
    [InlineData(float.NaN, 0, 50, false)]
    public void PlayerRangeUsesHorizontalFateCircle(float x, float z, float radius, bool expected)
    {
        Assert.Equal(expected, FateAlertTracking.IsInRange(new(x, 100, z), Vector3.Zero, radius));
    }
}
