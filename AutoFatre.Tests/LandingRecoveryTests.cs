using System.Numerics;
using FieldNavigation;

namespace AutoFatre.Tests;

public sealed class LandingRecoveryTests
{
    [Theory]
    [InlineData(0f, 50f)]
    [InlineData(48f, 50f)]
    [InlineData(9.7f, 10f)]
    public void OffsetCandidatesStayInsideFate(float distance, float radius)
    {
        Vector3 center = new(30, -5, 80);
        Vector3 origin = center + new Vector3(distance, 100, 0);
        Vector3[] candidates = LandingRecoveryPolicy.GetCandidates(origin, center, radius, 1).ToArray();

        Assert.Equal(8, candidates.Length);
        Assert.All(candidates, candidate =>
        {
            Assert.True(LandingRecoveryPolicy.HorizontalDistance(candidate, center) <= radius * 0.9f + 0.001f);
            Assert.Equal(origin.Y, candidate.Y);
        });
        Assert.Contains(candidates, candidate => LandingRecoveryPolicy.IsUsable(candidate, origin, center, radius));
    }

    [Fact]
    public void RetryChangesDirectionAndUsesModerateOffset()
    {
        Vector3 first = LandingRecoveryPolicy.GetCandidates(Vector3.Zero, Vector3.Zero, 50, 1).First();
        Vector3 next = LandingRecoveryPolicy.GetCandidates(Vector3.Zero, Vector3.Zero, 50, 2).First();

        Assert.Equal(10f, first.Length(), 4);
        Assert.NotEqual(first, next);
    }

    [Theory]
    [InlineData(0f, 0f, false)]
    [InlineData(5f, 0f, false)]
    [InlineData(6f, -100f, true)]
    [InlineData(10f, -100f, true)]
    [InlineData(16f, 0f, true)]
    [InlineData(16.1f, 0f, false)]
    [InlineData(float.NaN, 0f, false)]
    [InlineData(10f, float.NaN, false)]
    public void ResolvedGroundMustBeFiniteAndMeaningfullyOffset(float x, float y, bool expected)
    {
        Assert.Equal(expected, LandingRecoveryPolicy.IsUsable(new(x, y, 0), Vector3.Zero, Vector3.Zero, 50));
    }

    [Fact]
    public void ResolvedGroundOutsideSafeBoundaryIsRejected()
    {
        Assert.False(LandingRecoveryPolicy.IsUsable(new(48, 0, 0), new(38, 100, 0), Vector3.Zero, 50));
        Assert.True(LandingRecoveryPolicy.IsUsable(new(44, 0, 0), new(38, 100, 0), Vector3.Zero, 50));
    }

    [Fact]
    public void DescentTimesOutAt12SecondsAndReleasesMovement()
    {
        FakeNavigation backend = new();
        FakeDescent descent = new();
        FakeRecovery recovery = new();
        using NavigationTravelSession session = new(backend, descent, recovery);
        DateTime now = new(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc);
        Vector3 destination = new(20, 0, 0);
        session.Start(now, Vector3.Zero, destination, false,
            new NavigationTravelOptions { InitialIntent = NavigationTravelIntent.Ground });
        Assert.Equal(NavigationTravelState.Landing, session.Tick(Frame(now)).State);
        Assert.NotEqual(NavigationTravelOutcome.Failed, session.Tick(Frame(now.AddSeconds(11.99))).Outcome);

        NavigationTravelUpdate result = session.Tick(Frame(now.AddSeconds(12)));

        Assert.Equal(NavigationTravelOutcome.Failed, result.Outcome);
        Assert.Equal(NavigationFailureStage.LandingTimeout, result.FailureStage);
        Assert.False(descent.Descending);
        Assert.False(backend.IsMoveActive);
        Assert.Equal(0, recovery.Jumps);

        NavigationTravelFrame Frame(DateTime tick) => new(tick, Vector3.Zero, 20, false, false, new(true, false, false));
    }

    [Fact]
    public void ValidatedLandingPointDoesNotRunAnotherUnboundedFloorSearch()
    {
        FakeNavigation backend = new();
        FakeDescent descent = new();
        LandingSession landing = new(backend, descent);

        landing.BeginVerticalDescent();
        Assert.Equal(LandingStatus.Descending, landing.Update(DateTime.UtcNow, Vector3.Zero, new(true, false, false)));
        Assert.Equal(0, backend.GroundQueries);
        Assert.True(descent.Descending);
    }

    [Fact]
    public void LandingRelocationCanMoveWhileAlreadyAirborneInNoFlyArea()
    {
        FakeNavigation backend = new();
        using NavigationTravelSession session = new(backend, new FakeDescent());
        DateTime now = DateTime.UtcNow;
        session.Start(now, Vector3.Zero, new(10, 0, 0), true, new NavigationTravelOptions
        {
            InitialIntent = NavigationTravelIntent.Fly,
            LandingApproach = true,
        });

        NavigationTravelUpdate result = session.Tick(new(now, Vector3.Zero, 10, false, true, new(true, false, false)));

        Assert.Equal(NavigationRequestResult.Accepted, result.RequestResult);
        Assert.Equal(NavigationTravelMode.Fly, result.Mode);
        Assert.True(backend.LastMoveWasFlight);
    }

    [Fact]
    public void OrdinaryNoFlyTravelStillLandsAndCancellationStopsDescent()
    {
        FakeNavigation backend = new();
        FakeDescent descent = new();
        using NavigationTravelSession session = new(backend, descent);
        DateTime now = DateTime.UtcNow;
        session.Start(now, Vector3.Zero, new(20, 0, 0), true,
            new NavigationTravelOptions { InitialIntent = NavigationTravelIntent.Fly });

        NavigationTravelUpdate result = session.Tick(new(now, Vector3.Zero, 20, false, true, new(true, false, false)));

        Assert.Equal(NavigationTravelState.Landing, result.State);
        Assert.True(descent.Descending);
        session.Cancel();
        Assert.False(descent.Descending);
        Assert.False(backend.IsMoveActive);
        Assert.Null(session.Landing.Destination);
    }

    [Fact]
    public void SameMapTeleportMustActuallyLoadBeforeCompletion()
    {
        TeleportArrivalGate gate = new();
        gate.Requested();
        Assert.False(gate.Complete(true, false, false));
        gate.Observe(true);
        Assert.False(gate.Complete(true, true, false));
        Assert.False(gate.Complete(true, false, true));
        Assert.True(gate.Complete(true, false, false));
        Assert.False(gate.Pending);
    }

    [Fact]
    public void AethernetReentryWaitsForFinalMapAndNewLoadingCycle()
    {
        TeleportArrivalGate gate = new();
        gate.Requested();
        gate.Observe(true);
        Assert.False(gate.Complete(false, false, false)); // Idyllshire is only the first hop.
        gate.Requested(); // The aethernet hop owns a new checkpoint.
        Assert.False(gate.Complete(true, false, false));
        gate.Observe(true);
        Assert.True(gate.Complete(true, false, false));
    }

    private sealed class FakeNavigation : INavigationBackend
    {
        public bool IsAvailable => true;
        public bool IsMoveInProgress => false;
        public bool IsPathRunning { get; private set; }
        public bool IsMoveActive => this.IsPathRunning;
        public bool LastMoveWasFlight { get; private set; }
        public int GroundQueries { get; private set; }
        public bool MoveTo(Vector3 destination, bool fly)
        {
            this.LastMoveWasFlight = fly;
            this.IsPathRunning = true;
            return true;
        }
        public void Stop() => this.IsPathRunning = false;
        public Vector3? FindNearestReachablePoint(Vector3 position, float halfExtentXZ = 20f, float halfExtentY = 100f)
        {
            this.GroundQueries++;
            return position;
        }
        public bool TryResolveTravelGroundDestination(Vector3 requested, bool mapWaypoint, out Vector3 destination, out string resolution)
        {
            destination = requested;
            resolution = "test floor";
            return true;
        }
    }

    private sealed class FakeDescent : IDescentControl
    {
        public bool Descending { get; private set; }
        public void BeginDescending() => this.Descending = true;
        public void StopDescending() => this.Descending = false;
    }

    private sealed class FakeRecovery : ITravelRecoveryControl
    {
        public int Jumps { get; private set; }
        public bool TryJump() { this.Jumps++; return true; }
    }
}
