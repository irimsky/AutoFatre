using System.Numerics;
using Dalamud.Utility;

namespace AutoFatre.Tests;

public sealed class ValleyApothecaryPreSyncTests
{
    [Theory]
    [InlineData(616, 152, 1, true)]
    [InlineData(616, 152, 2, true)]
    [InlineData(616, 152, 3, false)]
    [InlineData(616, 152, 4, false)]
    [InlineData(616, 152, 0, false)]
    [InlineData(616, 153, 1, false)]
    [InlineData(617, 152, 2, false)]
    public void OnlyThisFateTerritoryAndMeleeRolesApply(ushort fateId, uint territoryId, byte role, bool expected)
        => Assert.Equal(expected, ValleyApothecaryPreSync.AppliesTo(fateId, territoryId, role));

    [Theory]
    [InlineData(100, 0, 0)]
    [InlineData(200, 0, 0)]
    [InlineData(75, 150, -90)]
    public void MapCoordinatesRoundTripThroughCurrentDalamud(uint scale, int offsetX, int offsetY)
    {
        Vector3 point = ValleyApothecaryPreSync.GetWaypoint(scale, offsetX, offsetY);
        Assert.Equal(32f, MapUtil.ConvertWorldCoordXZToMapCoord(point.X, scale, offsetX), 4);
        Assert.Equal(14f, MapUtil.ConvertWorldCoordXZToMapCoord(point.Z, scale, offsetY), 4);
        Assert.Equal(0f, point.Y);
    }

    [Fact]
    public void Map100WithoutOffsetsConvertsToExpectedWorldXZ()
    {
        Vector3 point = ValleyApothecaryPreSync.GetWaypoint(100, 0, 0);
        Assert.Equal(526f, point.X, 3);
        Assert.Equal(-374f, point.Z, 3);
    }

    [Fact]
    public void ZeroMapScaleIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ValleyApothecaryPreSync.GetWaypoint(0, 0, 0));

    [Theory]
    [InlineData(0f, true)]
    [InlineData(3f, true)]
    [InlineData(3.01f, false)]
    public void GroundResolutionCannotSubstituteADistantPoint(float offset, bool expected) =>
        Assert.Equal(expected, ValleyApothecaryPreSync.IsResolvedPointValid(new(10, 0, 20), new(10 + offset, -22, 20)));

    [Fact]
    public void InvalidGroundResolutionIsRejected()
    {
        Assert.False(ValleyApothecaryPreSync.IsResolvedPointValid(Vector3.Zero, new(float.NaN, 0, 0)));
        Assert.False(ValleyApothecaryPreSync.IsResolvedPointValid(Vector3.Zero, new(0, float.PositiveInfinity, 0)));
        Assert.False(ValleyApothecaryPreSync.IsResolvedPointValid(Vector3.Zero, new(0, 0, float.NaN)));
    }

    [Fact]
    public void GroundQueryUsesMapConvertedXZAndPlayerHeightNotTopDownProbe()
    {
        Vector3 waypoint = ValleyApothecaryPreSync.GetWaypoint(100, 0, 0);
        Vector3 expectedGround = new(waypoint.X, -21.207073f, waypoint.Z);
        int calls = 0;
        bool found = ValleyApothecaryPreSync.TryResolveGround(waypoint, -20.547503f,
            (probe, radius, height) =>
            {
                calls++;
                Assert.Equal(waypoint.X, probe.X);
                Assert.Equal(waypoint.Z, probe.Z);
                Assert.Equal(-20.547503f, probe.Y);
                Assert.Equal(3f, radius);
                Assert.Equal(100f, height);
                return expectedGround;
            }, out Vector3? ground);
        Assert.True(found);
        Assert.Equal(1, calls);
        Assert.Equal(expectedGround, ground);
    }

    [Fact]
    public void OldHighestFloorBesideWaypointIsNotTheRequiredStagingPoint()
    {
        Vector3 waypoint = ValleyApothecaryPreSync.GetWaypoint(100, 0, 0);
        // Reproduced from both local East Shroud mesh caches: old PointOnFloor picks
        // this higher polygon 7.29 yalms away despite valid ground at the requested X/Z.
        Assert.False(ValleyApothecaryPreSync.IsResolvedPointValid(waypoint, new(519.75f, -19.75f, -377.75f)));
        Assert.True(ValleyApothecaryPreSync.IsResolvedPointValid(waypoint, new(waypoint.X, -21.207073f, waypoint.Z)));
    }

    [Fact]
    public void MissingReachableGroundDoesNotAuthorizeSync()
    {
        Assert.False(ValleyApothecaryPreSync.TryResolveGround(Vector3.Zero, -21,
            (_, _, _) => null, out Vector3? ground));
        Assert.Null(ground);
    }

    [Fact]
    public void QueryReturningDistantGroundDoesNotAuthorizeSync()
    {
        Vector3 far = new(7, -21, 0);
        Assert.False(ValleyApothecaryPreSync.TryResolveGround(Vector3.Zero, -21,
            (_, _, _) => far, out Vector3? ground));
        Assert.Equal(far, ground);
    }

    [Fact]
    public void QueryReturningNonFiniteGroundDoesNotAuthorizeSync() =>
        Assert.False(ValleyApothecaryPreSync.TryResolveGround(Vector3.Zero, -21,
            (_, _, _) => new Vector3(0, float.NaN, 0), out _));

    [Theory]
    [InlineData(3f, 0f, true)]
    [InlineData(3.01f, 0f, false)]
    [InlineData(0f, 4f, false)]
    [InlineData(2f, 2f, true)]
    [InlineData(2.5f, 2f, false)]
    public void ArrivalRequiresActualPositionIncludingGroundHeight(float horizontal, float height, bool expected)
    {
        Vector3 ground = new(526, -22, -374);
        Assert.Equal(expected, ValleyApothecaryPreSync.HasArrived(ground + new Vector3(horizontal, height, 0), ground));
    }

    [Fact]
    public void WalkingAwayRequiresReturningBeforeAnotherSyncRequest()
    {
        Vector3 ground = new(526, -22, -374);
        Assert.True(ValleyApothecaryPreSync.HasArrived(ground, ground));
        Assert.False(ValleyApothecaryPreSync.HasArrived(ground + new Vector3(10, 0, 0), ground));
        Assert.True(ValleyApothecaryPreSync.HasArrived(ground, ground));
    }
}
