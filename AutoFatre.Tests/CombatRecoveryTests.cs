using System.Numerics;

namespace AutoFatre.Tests;

public sealed class CombatRecoveryTests
{
    [Theory]
    [InlineData(46.99f, false)]
    [InlineData(47f, true)]
    [InlineData(47.5f, true)]
    [InlineData(49f, true)]
    [InlineData(50.2f, true)]
    public void AreaReturn_TriggersImmediatelyAtRadiusMinusThree(float distance, bool expected)
    {
        Assert.Equal(expected, FateAreaReturnSession.IsNearBoundary(new(distance, 100, 0), Vector3.Zero, 50));
    }

    [Theory]
    [InlineData(55f, 51.99f, false)]
    [InlineData(55f, 52f, true)]
    [InlineData(100f, 96.99f, false)]
    [InlineData(100f, 97f, true)]
    [InlineData(20f, 16.99f, false)]
    [InlineData(20f, 17f, true)]
    [InlineData(4f, 0.99f, false)]
    [InlineData(4f, 1f, true)]
    public void AreaReturn_UsesFixedThreeYalmInsetAcrossFateSizes(float radius, float distance, bool expected)
    {
        Vector3 center = new(10, 2, 20);
        Assert.Equal(expected, FateAreaReturnSession.IsNearBoundary(center + new Vector3(distance, 100, 0), center, radius));
    }

    [Fact]
    public void AreaReturn_BoundaryUsesHorizontalDistanceOnDiagonals()
    {
        Assert.True(FateAreaReturnSession.IsNearBoundary(new(28.2f, 100, 37.6f), Vector3.Zero, 50));
        Assert.False(FateAreaReturnSession.IsNearBoundary(new(28, 100, 37.5f), Vector3.Zero, 50));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void AreaReturn_InvalidRadiusCannotTriggerOrComplete(float radius)
    {
        Assert.False(FateAreaReturnSession.IsNearBoundary(new(100, 0, 0), Vector3.Zero, radius));
        Assert.False(FateAreaReturnSession.IsAtDestination(Vector3.Zero, Vector3.Zero, Vector3.Zero, radius));
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(3f)]
    [InlineData(4f)]
    [InlineData(5f)]
    public void AreaReturn_SmallRadiusStillHasSafeCenterArrival(float radius)
    {
        Assert.False(FateAreaReturnSession.IsNearBoundary(Vector3.Zero, Vector3.Zero, radius));
        Assert.True(FateAreaReturnSession.IsAtDestination(Vector3.Zero, Vector3.Zero, Vector3.Zero, radius));
        Assert.True(FateAreaReturnSession.IsNearBoundary(new(radius, 0, 0), Vector3.Zero, radius));
    }

    [Theory]
    [InlineData(AutomationState.PullingTargets, false)]
    [InlineData(AutomationState.Fighting, false)]
    [InlineData(AutomationState.BossFighting, false)]
    [InlineData(AutomationState.CleaningBossNonFate, false)]
    [InlineData(AutomationState.CollectingFateItems, true)]
    [InlineData(AutomationState.TurningInCollectionFate, true)]
    [InlineData(AutomationState.WaitingForLevelSync, true)]
    public void AreaReturn_CoversEveryFateCombatEntry(AutomationState state, bool inCombat)
    {
        Assert.True(FateAreaReturnSession.ShouldMonitor(state, AutomationState.Stopped, inCombat));
    }

    [Fact]
    public void AreaReturn_CoversCompanionCheckpointInterruptedCombat()
    {
        Assert.True(FateAreaReturnSession.ShouldMonitor(AutomationState.CheckingCompanions, AutomationState.Fighting, false));
        Assert.True(FateAreaReturnSession.ShouldMonitor(AutomationState.CheckingCompanions, AutomationState.BossFighting, false));
        Assert.False(FateAreaReturnSession.ShouldMonitor(AutomationState.CheckingCompanions, AutomationState.ScanningFates, false));
    }

    [Theory]
    [InlineData(AutomationState.CleaningUpCombat)]
    [InlineData(AutomationState.ReturningToFateArea)]
    [InlineData(AutomationState.Paused)]
    [InlineData(AutomationState.Stopped)]
    [InlineData(AutomationState.DeadReturning)]
    [InlineData(AutomationState.NavigatingToFate)]
    public void AreaReturn_DoesNotInterruptNonFateCombatStates(AutomationState state)
    {
        Assert.False(FateAreaReturnSession.ShouldMonitor(state, AutomationState.Fighting, true));
    }

    [Fact]
    public void AreaReturn_RequiresArrivalAtResolvedWaypointAndInsideBoundary()
    {
        Assert.False(FateAreaReturnSession.IsAtDestination(new(32.5f, 0, 0), Vector3.Zero, Vector3.Zero, 50));
        Assert.True(FateAreaReturnSession.IsAtDestination(new(2, 0, 0), Vector3.Zero, Vector3.Zero, 50));
        Assert.False(FateAreaReturnSession.IsAtDestination(new(0, 10, 0), Vector3.Zero, Vector3.Zero, 50));
        Assert.True(FateAreaReturnSession.IsAtDestination(new(3, 0, 0), Vector3.Zero, Vector3.Zero, 50));
        Assert.False(FateAreaReturnSession.IsAtDestination(new(3.01f, 0, 0), Vector3.Zero, Vector3.Zero, 50));
        Assert.False(FateAreaReturnSession.IsAtDestination(new(49, 0, 0), new(47, 0, 0), Vector3.Zero, 50));
    }

    [Theory]
    [InlineData(50f, true)]
    [InlineData(55f, true)]
    [InlineData(55.1f, false)]
    public void OrdinaryPull_AllowsFiveUnitsOutsideFateRadius(float distance, bool expected)
    {
        Assert.Equal(expected, FateAreaReturnSession.IsInsidePullRange(new(distance, 0, 0), Vector3.Zero, 50));
    }

    [Theory]
    [InlineData(0, 40f, false)]
    [InlineData(1, 40f, false)]
    [InlineData(2, 24.9f, false)]
    [InlineData(2, 25f, true)]
    [InlineData(2, 25.1f, true)]
    [InlineData(3, 40f, true)]
    public void OrdinaryPull_SealsOnlyWithTwoEnemiesAndNearestAtLeast25(int count, float nearestDistance, bool expected)
    {
        GeneralPullBatchSession session = new();

        Assert.Equal(expected, session.TrySealForDistantTarget(count, nearestDistance * nearestDistance, false));
        Assert.Equal(expected, session.MustDrain);
    }

    [Fact]
    public void OrdinaryPull_ProtectionThreatBypassesDistanceRule()
    {
        GeneralPullBatchSession session = new();

        Assert.False(session.TrySealForDistantTarget(3, 100f * 100f, isProtectionThreat: true));
        Assert.False(session.MustDrain);
    }

    [Fact]
    public void OrdinaryPull_SealedBatchStaysClosedUntilReset()
    {
        GeneralPullBatchSession session = new();
        Assert.True(session.TrySealForDistantTarget(2, 30f * 30f, false));

        Assert.False(session.TrySealForDistantTarget(1, 10f * 10f, false));
        Assert.True(session.MustDrain);
        session.Reset();
        Assert.False(session.MustDrain);
        Assert.False(session.TrySealForDistantTarget(2, 10f * 10f, false));
    }
}
