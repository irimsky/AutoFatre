using System.Numerics;

namespace AutoFatre.Tests;

public sealed class SelectionAndRecoveryTests
{
    [Fact]
    public void IdentityCatalog_ContainsVerifiedDestroyObjective()
    {
        Assert.True(FateObjectiveIdentityCatalog.TryGetDestroyObjectives(840, out IReadOnlyList<FateObjectiveIdentity> identities));
        Assert.Contains(identities, identity => identity.BaseId == 5027 && identity.NameId == 4010);
        Assert.False(FateObjectiveIdentityCatalog.TryGetDestroyObjectives(9999, out _));
    }

    [Theory]
    [InlineData(1000f, 100f, true)]
    [InlineData(200f, 100f, false)]
    public void AetherytePlanner_UsesTeleportCostThreshold(
        float directDistance,
        float crystalDistance,
        bool expected)
    {
        Vector3 current = new(directDistance, 500, 0);
        Vector3 target = Vector3.Zero;
        Vector3 crystal = new(crystalDistance, -300, 0);

        Assert.Equal(expected, AetheryteTravelPlanner.ShouldTeleport(current, target, crystal));
    }

    [Fact]
    public void AetherytePlanner_HorizontalDistanceIgnoresAltitude()
    {
        Assert.Equal(5f, AetheryteTravelPlanner.HorizontalDistance(new Vector3(0, 100, 0), new Vector3(3, -200, 4)));
    }

    [Fact]
    public void DestroySession_LocksObjectiveThenCleansEngagedBatch()
    {
        DestroyObjectiveSession session = new();
        CombatCandidate objective = new(10, Hp: 100, DistanceSquared: 25);

        DestroyDecision first = session.Choose([objective], []);
        Assert.Equal(DestroyAction.Destroy, first.Action);
        Assert.Equal((ulong)10, first.TargetId);

        CombatCandidate[] engaged =
        [
            new(20, Hp: 50, DistanceSquared: 9),
            new(21, Hp: 10, DistanceSquared: 16),
            new(22, Hp: 30, DistanceSquared: 4),
            new(23, Hp: 40, DistanceSquared: 1),
            new(24, Hp: 60, DistanceSquared: 2),
        ];
        DestroyDecision cleanup = session.Choose([], engaged);

        Assert.Equal(DestroyAction.Cleanup, cleanup.Action);
        Assert.Equal((ulong)21, cleanup.TargetId);
        Assert.True(session.ClearingAggro);
    }

    [Fact]
    public void DestroySession_ReturnsFallbackWithoutCandidates()
    {
        DestroyObjectiveSession session = new();

        DestroyDecision decision = session.Choose([], []);

        Assert.Equal(DestroyAction.Fallback, decision.Action);
        Assert.Null(decision.TargetId);
    }
}
