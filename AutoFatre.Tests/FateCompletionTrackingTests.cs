using System.Numerics;
using Dalamud.Game.ClientState.Fates;

namespace AutoFatre.Tests;

public sealed class FateCompletionTrackingTests
{
    private static FateSnapshot Fate(ushort id = 1, byte progress = 20, FateState state = FateState.Running, int start = 100) =>
        new(id, "test", state, progress, 300, 1, 1, new Vector3(1, 2, 3), 30, false, 1,
            FateSnapshot.DefeatEnemiesIconId, 0, 0, 0, [], [], "", start, 900);

    private static FateRewardObservation Reward(FateCompletionTracking tracker, ushort id = 1, byte flags = 1,
        byte medal = 0, bool active = true) => new(tracker.Session, tracker.Scope, active, 957, 1,
            DateTime.UtcNow, id, flags, medal);

    private static FateCompletionTracking Participating()
    {
        var tracker = new FateCompletionTracking();
        tracker.Start();
        Assert.Empty(tracker.Observe([Fate()], 957, 1, 1));
        return tracker;
    }

    [Theory]
    [InlineData(FateState.Ending, 20)]
    [InlineData(FateState.Ended, 20)]
    [InlineData(FateState.Running, 100)]
    public void CompletionTransitionCountsOnce(FateState state, byte progress)
    {
        var tracker = Participating();
        var fate = Fate(progress: progress, state: state);
        Assert.Single(tracker.Observe([fate], 957, 1, 1));
        for (int i = 0; i < 100; i++)
            Assert.Empty(tracker.Observe([fate], 957, 1, 1));
        Assert.True(tracker.WasNotified(957, 1, fate));
        Assert.Equal(1, tracker.StateTotal);
        Assert.Equal(0, tracker.RewardTotal);
    }

    [Theory]
    [InlineData(FateState.Ending, 20)]
    [InlineData(FateState.Ended, 100)]
    [InlineData(FateState.Running, 100)]
    public void ArrivingAtCompletedFateNeverCounts(FateState state, byte progress)
    {
        var tracker = new FateCompletionTracking();
        tracker.Start();
        for (int i = 0; i < 3; i++)
            Assert.Empty(tracker.Observe([Fate(progress: progress, state: state)], 957, 1, 1));
        Assert.Equal(0, tracker.StateTotal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void FailureNeverCounts(byte progress)
    {
        var tracker = Participating();
        Assert.Empty(tracker.Observe([Fate(progress: progress, state: FateState.Failed)], 957, 1, 1));
        Assert.True(tracker.ObserveReward(Reward(tracker, flags: 0)).Accepted);
        Assert.Equal((0, 0), tracker.Counts(1));
    }

    [Fact]
    public void DisappearanceAndReselectionCannotDuplicate()
    {
        var tracker = Participating();
        var complete = Fate(progress: 100);
        Assert.Single(tracker.Observe([complete], 957, 1, 1));
        Assert.Empty(tracker.Observe([], 957, 1, 0));
        Assert.Empty(tracker.Observe([complete], 957, 1, 1));
        // Even a temporary regression of the same occurrence cannot re-notify.
        Assert.Empty(tracker.Observe([Fate()], 957, 1, 1));
        Assert.Empty(tracker.Observe([complete], 957, 1, 1));
        Assert.Equal(1, tracker.StateTotal);
    }

    [Fact]
    public void ParticipationAndRemainingTimeDoNotMakeDisappearanceSuccess()
    {
        var tracker = Participating();
        Assert.Empty(tracker.Observe([], 957, 1, 0));
        Assert.False(tracker.WasNotified(957, 1, Fate()));
        Assert.Equal(0, tracker.StateTotal);
    }

    [Fact]
    public void LastCurrentFateStillNotifiesAfterLeavingRadius()
    {
        var tracker = Participating();
        Assert.Empty(tracker.Observe([Fate()], 957, 1, 0));
        Assert.Single(tracker.Observe([Fate(progress: 100)], 957, 1, 0));
    }

    [Fact]
    public void MapWideSuccessWhileTravellingIsNotParticipation()
    {
        var tracker = new FateCompletionTracking();
        tracker.Start();
        tracker.Observe([Fate()], 957, 1, 0);
        Assert.Empty(tracker.Observe([Fate(progress: 100)], 957, 1, 0));
        Assert.Equal(0, tracker.StateTotal);
    }

    [Theory]
    [InlineData(957, 1, 200)]
    [InlineData(958, 1, 100)]
    [InlineData(957, 2, 100)]
    public void NewOccurrenceOfSameIdCanCount(uint territory, uint instance, int start)
    {
        var tracker = Participating();
        tracker.Observe([Fate(progress: 100)], 957, 1, 1);
        tracker.Observe([Fate(start: start)], territory, instance, 1);
        Assert.Single(tracker.Observe([Fate(progress: 100, start: start)], territory, instance, 1));
        Assert.Equal(2, tracker.StateTotal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothEventOrdersKeepIndependentTotals(bool packetFirst)
    {
        var tracker = Participating();
        if (packetFirst)
        {
            tracker.ObserveReward(Reward(tracker));
            Assert.Equal(0, tracker.StateTotal);
            Assert.Equal(1, tracker.RewardTotal);
            var entry = Assert.Single(tracker.Observe([Fate(progress: 100)], 957, 1, 1));
            Assert.Equal("Gold(金牌)", entry.Reward?.MedalDescription);
        }
        else
        {
            tracker.Observe([Fate(progress: 100)], 957, 1, 1);
            Assert.Equal(0, tracker.RewardTotal);
            tracker.ObserveReward(Reward(tracker));
        }
        Assert.Equal((1, 1), tracker.Counts(1));
    }

    [Fact]
    public void PacketWithoutStateNotificationStillCounts()
    {
        var tracker = new FateCompletionTracking();
        tracker.Start();
        var result = tracker.ObserveReward(Reward(tracker));
        Assert.True(result.Accepted);
        Assert.Null(result.Entry);
        Assert.Equal(0, tracker.StateTotal);
        Assert.Equal(1, tracker.RewardTotal);
    }

    [Fact]
    public void ActualHandlerInvocationsAreNotSuppressedByStateDeduplication()
    {
        var tracker = Participating();
        tracker.Observe([Fate(progress: 100)], 957, 1, 1);
        Assert.True(tracker.ObserveReward(Reward(tracker)).Accepted);
        Assert.True(tracker.ObserveReward(Reward(tracker)).Accepted);
        Assert.Equal(1, tracker.StateTotal);
        Assert.Equal(2, tracker.RewardTotal);
    }

    [Theory]
    [InlineData(0, "Gold(金牌)")]
    [InlineData(1, "Silver(银牌)")]
    [InlineData(2, "Bronze(铜牌)")]
    [InlineData(3, "CriticalEngagement")]
    [InlineData(255, "Unknown(255)")]
    public void MedalIsRecordedWithoutFilteringSuccess(byte medal, string expected)
    {
        var tracker = Participating();
        var reward = Reward(tracker, medal: medal);
        Assert.Equal(expected, reward.MedalDescription);
        var result = tracker.ObserveReward(reward);
        Assert.Equal(medal, result.Entry?.Reward?.Medal);
        Assert.Equal(1, tracker.RewardTotal);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(9, true)]
    [InlineData(17, true)]
    [InlineData(0, false)]
    [InlineData(8, false)]
    [InlineData(16, false)]
    public void SuccessIsBitFlagNotMedalOrPopup(byte flags, bool success)
    {
        var tracker = Participating();
        var reward = Reward(tracker, flags: flags);
        Assert.Equal(success, reward.Success);
        Assert.True(tracker.ObserveReward(reward).Accepted);
        Assert.Equal(success ? 1 : 0, tracker.RewardTotal);
    }

    [Fact]
    public void DelayedPacketAfterScopeSwitchBelongsToOldStep()
    {
        var tracker = Participating();
        tracker.Observe([Fate(progress: 100)], 957, 1, 1);
        tracker.AdvanceScope();
        var result = tracker.ObserveReward(Reward(tracker));
        Assert.Equal(1, result.Scope);
        Assert.Equal((1, 1), tracker.Counts(1));
        Assert.Equal((0, 0), tracker.Counts(2));
    }

    [Fact]
    public void NewRunRejectsQueuedOldRunPacket()
    {
        var tracker = Participating();
        var old = Reward(tracker);
        tracker.Start();
        Assert.False(tracker.ObserveReward(old).Accepted);
        Assert.Equal(0, tracker.StateTotal);
        Assert.Equal(0, tracker.RewardTotal);
        Assert.False(tracker.WasNotified(957, 1, Fate()));
    }

    [Fact]
    public void StopKeepsKnownDelayedRewardsButDoesNotObserveNewStates()
    {
        var tracker = Participating();
        tracker.Stop();
        Assert.Empty(tracker.Observe([Fate(progress: 100)], 957, 1, 1));
        Assert.True(tracker.ObserveReward(Reward(tracker, active: false)).Accepted);
        Assert.False(tracker.ObserveReward(Reward(tracker, id: 2, active: false)).Accepted);
        Assert.Equal(1, tracker.RewardTotal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void InvalidRunOrIdIsIgnored(long session, ushort id)
    {
        var tracker = Participating();
        var packet = Reward(tracker, id: id) with { Session = session };
        Assert.False(tracker.ObserveReward(packet).Accepted);
    }

    [Fact]
    public void ObservationWithoutStartingDoesNothing()
    {
        var tracker = new FateCompletionTracking();
        Assert.Empty(tracker.Observe([Fate()], 957, 1, 1));
        Assert.Empty(tracker.Observe([Fate(progress: 100)], 957, 1, 1));
    }

    [Fact]
    public void PreviousTerritoryTableCannotCompleteAnotherInstance()
    {
        var tracker = Participating();
        Assert.Empty(tracker.Observe([Fate(progress: 100)], 958, 1, 1));
        Assert.Equal(0, tracker.StateTotal);
    }

    [Fact]
    public void LaterSameIdRewardDoesNotAttachToOldFailedParticipation()
    {
        var tracker = Participating();
        tracker.Observe([Fate(state: FateState.Failed)], 957, 1, 1);
        tracker.Observe([Fate(start: 200)], 957, 1, 1);
        tracker.Observe([Fate(progress: 100, start: 200)], 957, 1, 1);
        var result = tracker.ObserveReward(Reward(tracker));
        Assert.Equal(200, result.Entry?.Occurrence.StartTimeEpoch);
        Assert.Equal((1, 1), tracker.Counts(1));
    }
}
