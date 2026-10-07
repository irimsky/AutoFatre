using System.Numerics;

namespace AutoFatre.Tests;

public sealed class CombatMovementTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
    private static AutoFatreConfiguration Config() => new()
    {
        PullApproachDistance = 3,
        AggroConfirmationTimeoutSeconds = 8,
        CombatEscapeDistance = 40,
        CombatEscapeTimeoutSeconds = 15,
    };

    [Theory]
    [InlineData(1, 133, 100, 100, true)]
    [InlineData(2, 4868, 100, 100, false)]
    [InlineData(8, 1, 100, 100, false)]
    [InlineData(13, 1, 100, 100, false)]
    [InlineData(5, 1, 100, 100, false)]
    [InlineData(0, 133, 100, 100, false)]
    [InlineData(1, 0, 100, 100, false)]
    [InlineData(1, 133, 200, 100, false)]
    [InlineData(1, 133, 0, 100, false)]
    [InlineData(1, 133, 3758096384, 100, false)]
    [InlineData(1, 133, 0, 0, false)]
    [InlineData(1, 133, 3758096384, 3758096384, false)]
    public void OnlyActionCastDirectedAtThisTargetEndsApproach(byte type, uint id, ulong castTarget, ulong target, bool expected) =>
        Assert.Equal(expected, CombatMovement.IsAttackCast(type, id, castTarget, target));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonAttackCastOnlyPausesUnfinishedApproach(bool pulling)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.BeginTargetSelection(1);
        CombatMovement.ApproachAction Tick(bool cast, double seconds) => pulling
            ? movement.UpdatePullApproach(10, cast, Now.AddSeconds(seconds))
            : movement.UpdateTargetApproach(10, cast, Now.AddSeconds(seconds));
        Assert.Equal(CombatMovement.ApproachAction.Approach, Tick(false, 0));
        Assert.Equal(CombatMovement.ApproachAction.StopForCast, Tick(true, .1));
        Assert.Equal(CombatMovement.ApproachAction.StopForCast, Tick(true, .4));
        Assert.False(movement.BeginTargetSelection(1));
        Assert.Equal(CombatMovement.ApproachAction.WaitForCast, Tick(false, 1.399));
        Assert.Equal(CombatMovement.ApproachAction.Approach, Tick(false, 1.401));
        Assert.Equal(CombatMovement.ApproachAction.Approach, Tick(false, 10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstSelectionDuringCompanionCastStillApproachesAfterSettlement(bool pulling)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.BeginTargetSelection(1);
        Assert.Equal(CombatMovement.ApproachAction.StopForCast, pulling
            ? movement.UpdatePullApproach(10, true, Now)
            : movement.UpdateTargetApproach(10, true, Now));
        movement.CompleteTargetSelection();
        Assert.Equal(CombatMovement.ApproachAction.Approach, pulling
            ? movement.UpdatePullApproach(10, false, Now.AddSeconds(2))
            : movement.UpdateTargetApproach(10, false, Now.AddSeconds(2)));
        if (pulling)
        {
            Assert.False(movement.IsPullAttemptTimedOut(Now.AddSeconds(8), false));
            Assert.Equal(CombatMovement.ApproachAction.Approach, movement.UpdateTargetApproach(10, false, Now.AddSeconds(2)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonAttackCastCannotReopenACompletedOrEngagedApproach(bool pulling)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.BeginTargetSelection(1);
        movement.CompleteApproach(Now, pulling);
        Assert.Equal(CombatMovement.ApproachAction.StopForCast, pulling
            ? movement.UpdatePullApproach(20, true, Now)
            : movement.UpdateTargetApproach(20, true, Now));
        Assert.Equal(CombatMovement.ApproachAction.Stay, pulling
            ? movement.UpdatePullApproach(20, false, Now.AddSeconds(2))
            : movement.UpdateTargetApproach(20, false, Now.AddSeconds(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AttackObservedAfterUnknownCastMetadataStillCompletesApproach(bool pulling)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.BeginTargetSelection(1);
        if (pulling)
        {
            movement.UpdatePullApproach(20, true, Now);
            movement.UpdatePullApproach(20, true, Now.AddMilliseconds(100), attackingCast: true);
            Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdatePullApproach(20, false, Now.AddSeconds(2)));
        }
        else
        {
            movement.UpdateTargetApproach(20, true, Now);
            movement.UpdateTargetApproach(20, true, Now.AddMilliseconds(100), attackingCast: true);
            Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(20, false, Now.AddSeconds(2)));
        }
    }

    [Fact]
    public void NonAttackCastDoesNotEraseAttackCompletion()
    {
        CombatMovement movement = new(Config());
        movement.BeginTargetSelection(1);
        movement.UpdateTargetApproach(20, true, Now, attackingCast: true);
        movement.UpdateTargetApproach(20, true, Now.AddSeconds(2));
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(20, false, Now.AddSeconds(4)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EngagedTarget_CompletesApproachBeforeSelectionAndDoesNotReopen(bool pulling)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.BeginTargetSelection(1);
        movement.CompleteApproach(Now, pulling);
        Assert.Equal(CombatMovement.SelectionAction.Wait, movement.TargetSelectionFailed(Now));
        movement.TargetSelectionFailed(Now.AddSeconds(2));
        Assert.Equal(CombatMovement.ApproachAction.Stay,
            movement.UpdateTargetApproach(100, false, Now.AddSeconds(2)));
        if (pulling)
            Assert.Equal(CombatMovement.ApproachAction.Stay,
                movement.UpdatePullApproach(100, false, Now.AddSeconds(2)));
        movement.CompleteTargetSelection();
        Assert.False(movement.BeginTargetSelection(1));
        Assert.Equal(CombatMovement.ApproachAction.Stay,
            movement.UpdateTargetApproach(100, false, Now.AddSeconds(3)));
        movement.BeginTargetSelection(2);
        Assert.Equal(CombatMovement.ApproachAction.Approach,
            movement.UpdateTargetApproach(100, false, Now.AddSeconds(4)));
    }

    [Theory]
    [InlineData(2.9f, CombatMovement.ApproachAction.ReachedRange)]
    [InlineData(3f, CombatMovement.ApproachAction.ReachedRange)]
    [InlineData(3.01f, CombatMovement.ApproachAction.Approach)]
    public void Pull_StopsAtConfiguredRange(float distance, CombatMovement.ApproachAction expected)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        Assert.Equal(expected, movement.UpdatePullApproach(distance, false, Now));
    }

    [Theory]
    [InlineData(6f)]
    [InlineData(6.01f)]
    [InlineData(25f)]
    public void Pull_DoesNotResumeAfterReachingRange(float distance)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        Assert.Equal(CombatMovement.ApproachAction.ReachedRange, movement.UpdatePullApproach(3, false, Now));
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdatePullApproach(distance, false, Now.AddSeconds(1)));
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdatePullApproach(distance, false, Now.AddSeconds(2)));
    }

    [Fact]
    public void Pull_AttackCastingEndsApproachAndExtendsSettlementProtection()
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        Assert.Equal(CombatMovement.ApproachAction.StopForCast, movement.UpdatePullApproach(30, true, Now, attackingCast: true));
        Assert.Equal(CombatMovement.ApproachAction.StopForCast, movement.UpdatePullApproach(30, true, Now.AddSeconds(2), attackingCast: true));
        Assert.Equal(CombatMovement.ApproachAction.WaitForCast, movement.UpdatePullApproach(3, false, Now.AddSeconds(2.999)));
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdatePullApproach(30, false, Now.AddSeconds(3)));
        Assert.False(movement.IsPullAttemptTimedOut(Now.AddSeconds(7.999), false));
        Assert.True(movement.IsPullAttemptTimedOut(Now.AddSeconds(8), false));
    }

    [Theory]
    [InlineData(29.999, false)]
    [InlineData(30, true)]
    public void Pull_TotalAttemptTimeoutIs30Seconds(double seconds, bool timedOut)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        Assert.Equal(timedOut, movement.IsPullAttemptTimedOut(Now.AddSeconds(seconds), false));
    }

    [Theory]
    [InlineData(9.999, false)]
    [InlineData(10, true)]
    public void Pull_AggroTimeoutStartsOnRangeEntry(double seconds, bool timedOut)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.UpdatePullApproach(3, false, Now.AddSeconds(2));
        Assert.Equal(timedOut, movement.IsPullAttemptTimedOut(Now.AddSeconds(seconds), false));
    }

    [Fact]
    public void Pull_TimeoutSuppressedWhileCastingAndDuringSettlement()
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        Assert.False(movement.IsPullAttemptTimedOut(Now.AddSeconds(35), true));
        movement.UpdatePullApproach(20, true, Now.AddSeconds(35), attackingCast: true);
        Assert.False(movement.IsPullAttemptTimedOut(Now.AddSeconds(35.999), false));
        Assert.True(movement.IsPullAttemptTimedOut(Now.AddSeconds(36), false));
    }

    [Fact]
    public void Pull_NavigationRejectionUsesExistingTimeoutPathAndNextPullRestarts()
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.RejectPullNavigation(Now.AddSeconds(1));
        Assert.True(movement.IsPullAttemptTimedOut(Now.AddSeconds(1), false));
        movement.BeginPull(Now.AddSeconds(2));
        Assert.False(movement.IsPullAttemptTimedOut(Now.AddSeconds(2), false));
    }

    [Fact]
    public void Pull_ResetClearsPhaseAndCastProtection()
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.UpdatePullApproach(3, false, Now);
        movement.UpdatePullApproach(3, true, Now);
        movement.ResetPull();
        Assert.Equal(CombatMovement.ApproachAction.Approach, movement.UpdatePullApproach(4, false, Now));
        movement.BeginPull(Now);
        Assert.False(movement.IsPullAttemptTimedOut(Now, false));
    }

    [Fact]
    public void Pull_UsesLiveConfigurationRatherThanCopiedThresholds()
    {
        AutoFatreConfiguration config = Config();
        CombatMovement movement = new(config);
        movement.BeginPull(Now);
        config.PullApproachDistance = 10;
        Assert.Equal(CombatMovement.ApproachAction.ReachedRange, movement.UpdatePullApproach(10, false, Now));
        config.AggroConfirmationTimeoutSeconds = 12;
        Assert.False(movement.IsPullAttemptTimedOut(Now.AddSeconds(8), false));
        Assert.True(movement.IsPullAttemptTimedOut(Now.AddSeconds(12), false));
    }

    [Theory]
    [InlineData(2.99f, CombatMovement.ApproachAction.ReachedRange)]
    [InlineData(3f, CombatMovement.ApproachAction.ReachedRange)]
    [InlineData(3.01f, CombatMovement.ApproachAction.Approach)]
    [InlineData(13.99f, CombatMovement.ApproachAction.Approach)]
    [InlineData(14f, CombatMovement.ApproachAction.Approach)]
    public void CombatTarget_UsesConfiguredRangeIncludingFormerDestroyRange(float distance, CombatMovement.ApproachAction expected)
    {
        CombatMovement movement = new(Config());
        movement.BeginTargetSelection(1);
        Assert.Equal(expected, movement.UpdateTargetApproach(distance, false, Now));
    }

    [Fact]
    public void CombatTarget_DoesNotResumeWhenPlayerDodgesOrTargetMoves()
    {
        CombatMovement movement = new(Config());
        movement.BeginTargetSelection(1);
        Assert.Equal(CombatMovement.ApproachAction.ReachedRange, movement.UpdateTargetApproach(3, false, Now));
        Assert.False(movement.BeginTargetSelection(1));
        Assert.False(movement.CompleteTargetSelection());
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(4, false, Now.AddSeconds(1)));
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(6, false, Now.AddSeconds(2)));
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(6.01f, false, Now.AddSeconds(3)));
        for (int tick = 4; tick <= 100; tick++)
            Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(25, false, Now.AddSeconds(tick)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombatTarget_PullCompletionCarriesIntoKillWithoutRepeatingApproach(bool casting)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.BeginTargetSelection(1);
        movement.UpdatePullApproach(casting ? 25 : 3, casting, Now, attackingCast: casting);
        movement.ResetPull();
        Assert.False(movement.BeginTargetSelection(1));
        movement.CompleteTargetSelection();
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(25, false, Now.AddSeconds(2)));
        Assert.True(movement.BeginTargetSelection(2));
        Assert.Equal(CombatMovement.ApproachAction.Approach, movement.UpdateTargetApproach(25, false, Now.AddSeconds(3)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombatTarget_OriginalNavigationArrivalCompletesEvenWhenTargetMoved(bool pulling)
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.BeginTargetSelection(1);
        movement.CompleteApproach(Now.AddSeconds(2), pulling);
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(25, false, Now.AddSeconds(3)));
        if (pulling)
        {
            Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdatePullApproach(25, false, Now.AddSeconds(3)));
            movement.CompleteApproach(Now.AddSeconds(5), pulling: true);
            Assert.False(movement.IsPullAttemptTimedOut(Now.AddSeconds(9.999), false));
            Assert.True(movement.IsPullAttemptTimedOut(Now.AddSeconds(10), false));
        }
        else
            Assert.Equal(CombatMovement.ApproachAction.Approach, movement.UpdatePullApproach(25, false, Now.AddSeconds(3)));
    }

    [Fact]
    public void CombatTarget_ConfigChangesAndSelectionFailureDoNotRestartCompletedApproach()
    {
        AutoFatreConfiguration config = Config();
        CombatMovement movement = new(config);
        movement.BeginTargetSelection(1);
        movement.UpdateTargetApproach(3, false, Now);
        config.PullApproachDistance = 1;
        movement.TargetSelectionFailed(Now.AddSeconds(1));
        movement.TargetSelectionFailed(Now.AddSeconds(2));
        Assert.Equal(CombatMovement.ApproachAction.Stay,
            movement.UpdateTargetApproach(float.PositiveInfinity, false, Now.AddSeconds(2)));
        Assert.True(movement.CompleteTargetSelection());
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(25, false, Now.AddSeconds(3)));
    }

    [Fact]
    public void CombatTarget_ChangingSelectedTargetStopsOldNavigationEvenWithoutSelectionFailure()
    {
        CombatMovement movement = new(Config());
        Assert.False(movement.BeginTargetSelection(1));
        Assert.Equal(CombatMovement.ApproachAction.ReachedRange, movement.UpdateTargetApproach(3, false, Now));
        Assert.True(movement.BeginTargetSelection(2));
        Assert.Equal(CombatMovement.ApproachAction.Approach, movement.UpdateTargetApproach(4, false, Now));
    }

    [Fact]
    public void CombatTarget_RangedCastingEndsApproachWithoutResumingAfterSettlement()
    {
        CombatMovement movement = new(Config());
        movement.BeginTargetSelection(1);
        Assert.Equal(CombatMovement.ApproachAction.StopForCast, movement.UpdateTargetApproach(30, true, Now, attackingCast: true));
        Assert.Equal(CombatMovement.ApproachAction.StopForCast, movement.UpdateTargetApproach(30, true, Now.AddSeconds(2), attackingCast: true));
        Assert.False(movement.BeginTargetSelection(1));
        movement.CompleteTargetSelection();
        Assert.Equal(CombatMovement.ApproachAction.WaitForCast, movement.UpdateTargetApproach(30, false, Now.AddSeconds(2.999)));
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(30, false, Now.AddSeconds(3)));
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdateTargetApproach(30, false, Now.AddSeconds(10)));
    }

    [Fact]
    public void CombatTarget_MovementDoesNotAlterPullAggroTimerAndPullResetDoesNotLoseTargetCastProtection()
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.UpdatePullApproach(3, false, Now);
        movement.BeginTargetSelection(1);
        movement.UpdateTargetApproach(3, false, Now.AddSeconds(7));
        Assert.True(movement.IsPullAttemptTimedOut(Now.AddSeconds(8), false));
        movement.UpdateTargetApproach(30, true, Now.AddSeconds(9));
        movement.ResetPull();
        Assert.Equal(CombatMovement.ApproachAction.WaitForCast, movement.UpdateTargetApproach(30, false, Now.AddSeconds(9.999)));
    }

    [Fact]
    public void CombatTarget_ResetClearsTargetPhaseAndCastProtectionButNotPullPhase()
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.UpdatePullApproach(3, false, Now);
        movement.BeginTargetSelection(1);
        movement.UpdateTargetApproach(3, false, Now);
        movement.UpdateTargetApproach(30, true, Now);
        movement.ResetTargetSelection();
        Assert.False(movement.BeginTargetSelection(2));
        Assert.Equal(CombatMovement.ApproachAction.Approach, movement.UpdateTargetApproach(4, false, Now));
        Assert.Equal(CombatMovement.ApproachAction.Stay, movement.UpdatePullApproach(4, false, Now));
    }

    [Fact]
    public void CombatTarget_UsesLiveConfigurationAndThreeDimensionalDistance()
    {
        AutoFatreConfiguration config = Config();
        CombatMovement movement = new(config);
        movement.BeginTargetSelection(1);
        Assert.Equal(CombatMovement.ApproachAction.Approach, movement.UpdateTargetApproach(Vector3.Distance(Vector3.Zero, new(0, 4, 0)), false, Now));
        config.PullApproachDistance = 10;
        Assert.Equal(CombatMovement.ApproachAction.ReachedRange, movement.UpdateTargetApproach(10, false, Now));
    }

    [Fact]
    public void Selection_StartsMovingOnlyAfterOneSecondAndStopsOnSuccess()
    {
        CombatMovement movement = new(Config());
        Assert.False(movement.BeginTargetSelection(1));
        Assert.Equal(CombatMovement.SelectionAction.Wait, movement.TargetSelectionFailed(Now));
        Assert.Equal(CombatMovement.SelectionAction.Wait, movement.TargetSelectionFailed(Now.AddSeconds(0.999)));
        Assert.Equal(CombatMovement.SelectionAction.BeginApproach, movement.TargetSelectionFailed(Now.AddSeconds(1)));
        Assert.Equal(CombatMovement.SelectionAction.ContinueApproach, movement.TargetSelectionFailed(Now.AddSeconds(2)));
        Assert.True(movement.CompleteTargetSelection());
        Assert.False(movement.CompleteTargetSelection());
        Assert.Equal(CombatMovement.SelectionAction.Wait, movement.TargetSelectionFailed(Now.AddSeconds(3)));
    }

    [Fact]
    public void Selection_SameTargetPreservesRecoveryButChangingTargetStopsAndRestartsGrace()
    {
        CombatMovement movement = new(Config());
        movement.BeginTargetSelection(1);
        movement.TargetSelectionFailed(Now);
        Assert.False(movement.BeginTargetSelection(1));
        Assert.Equal(CombatMovement.SelectionAction.BeginApproach, movement.TargetSelectionFailed(Now.AddSeconds(1)));
        Assert.False(movement.BeginTargetSelection(1));
        Assert.True(movement.BeginTargetSelection(2));
        Assert.Equal(CombatMovement.SelectionAction.Wait, movement.TargetSelectionFailed(Now.AddSeconds(1)));
        Assert.Equal(CombatMovement.SelectionAction.BeginApproach, movement.TargetSelectionFailed(Now.AddSeconds(2)));
    }

    [Fact]
    public void Selection_ResetDoesNotTouchPullTimers()
    {
        CombatMovement movement = new(Config());
        movement.BeginPull(Now);
        movement.BeginTargetSelection(1);
        movement.TargetSelectionFailed(Now);
        movement.TargetSelectionFailed(Now.AddSeconds(1));
        movement.ResetTargetSelection();
        Assert.False(movement.CompleteTargetSelection());
        Assert.False(movement.IsPullAttemptTimedOut(Now.AddSeconds(2), false));
    }

    [Theory]
    [InlineData(5f, false, CombatMovement.EscortAction.Stop)]
    [InlineData(5f, true, CombatMovement.EscortAction.Stop)]
    [InlineData(5.01f, false, CombatMovement.EscortAction.Stay)]
    [InlineData(9.99f, false, CombatMovement.EscortAction.Stay)]
    [InlineData(10f, false, CombatMovement.EscortAction.Approach)]
    [InlineData(7f, true, CombatMovement.EscortAction.Continue)]
    public void Escort_Uses10To5Hysteresis(float distance, bool active, CombatMovement.EscortAction expected)
    {
        CombatMovement movement = new(Config());
        movement.ConfirmEscortRequest(Now, new(20, 0, 0));
        Assert.Equal(expected, movement.FollowEscort(Now.AddSeconds(1), new(20 - distance, 0, 0), new(20, 0, 0), active));
    }

    [Theory]
    [InlineData(2.99f, 2, CombatMovement.EscortAction.Continue)]
    [InlineData(3f, 1.999, CombatMovement.EscortAction.Continue)]
    [InlineData(3f, 2, CombatMovement.EscortAction.Approach)]
    public void Escort_RepathRequiresThreeUnitsAndTwoSeconds(float moved, double seconds, CombatMovement.EscortAction expected)
    {
        CombatMovement movement = new(Config());
        movement.ConfirmEscortRequest(Now, new(20, 0, 0));
        Assert.Equal(expected, movement.FollowEscort(Now.AddSeconds(seconds), Vector3.Zero, new(20 + moved, 0, 0), true));
    }

    [Fact]
    public void Escort_ObservationsAndRequestConfirmationRemainDistinct()
    {
        CombatMovement movement = new(Config());
        movement.ConfirmEscortRequest(Now, new(20, 0, 0));
        movement.RememberEscortPosition(new(30, 0, 0));
        Assert.Equal(CombatMovement.EscortAction.Continue, movement.FollowEscort(Now.AddSeconds(3), Vector3.Zero, new(30, 0, 0), true));
        Assert.Equal(CombatMovement.EscortAction.Approach, movement.FollowEscort(Now.AddSeconds(3), Vector3.Zero, new(33, 0, 0), true));
        // Merely requesting a move (without confirmation) must not debounce a rejected request.
        Assert.Equal(CombatMovement.EscortAction.Approach, movement.FollowEscort(Now.AddSeconds(3.1), Vector3.Zero, new(33, 0, 0), true));
        movement.ConfirmEscortRequest(Now.AddSeconds(3.1), new(33, 0, 0));
        Assert.Equal(CombatMovement.EscortAction.Continue, movement.FollowEscort(Now.AddSeconds(4), Vector3.Zero, new(36, 0, 0), true));
        movement.ResetEscort();
        Assert.Equal(CombatMovement.EscortAction.Approach, movement.FollowEscort(Now.AddSeconds(4), Vector3.Zero, new(36, 0, 0), true));
    }

    [Fact]
    public void AreaReturn_KeepsCenterAcrossDestinationRetryAndReset()
    {
        CombatMovement movement = new(Config());
        Vector3 center = new(10, 2, 20);
        movement.BeginAreaReturn(center);
        Vector3 waypoint = movement.GetAreaReturnWaypoint(center);
        Assert.Equal(center, waypoint);
        Assert.True(movement.ResolveAreaReturnDestination(center + new Vector3(1, 0, 0), center, 50));
        movement.ResetAreaReturnDestination();
        Assert.Null(movement.AreaReturnDestination);
        Assert.Equal(waypoint, movement.GetAreaReturnWaypoint(center));
        movement.ResetAreaReturn();
        Assert.Null(movement.AreaReturnWaypoint);
        Vector3 newCenter = new(5, 0, 6);
        Assert.Equal(newCenter, movement.GetAreaReturnWaypoint(newCenter));
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(3f, true)]
    [InlineData(3.01f, false)]
    [InlineData(46.99f, false)]
    [InlineData(47f, false)]
    [InlineData(60f, false)]
    public void AreaReturn_AcceptsGroundOnlyWithinThreeYalmsOfCenter(float x, bool accepted)
    {
        CombatMovement movement = new(Config());
        movement.BeginAreaReturn(Vector3.Zero);
        Assert.False(movement.ResolveAreaReturnDestination(null, Vector3.Zero, 50));
        Assert.Equal(accepted, movement.ResolveAreaReturnDestination(new(x, 0, 0), Vector3.Zero, 50));
        Assert.Equal(accepted, movement.AreaReturnDestination.HasValue);
    }

    [Fact]
    public void AreaReturn_HandlesUnavailableInitialObjectsAndThreeDimensionalArrival()
    {
        CombatMovement movement = new(Config());
        movement.BeginAreaReturn(null);
        Assert.Null(movement.AreaReturnWaypoint);
        Assert.False(movement.IsAtAreaReturnDestination(Vector3.Zero, Vector3.Zero, 50));
        Assert.Equal(Vector3.Zero, movement.GetAreaReturnWaypoint(Vector3.Zero));
        movement.ResolveAreaReturnDestination(Vector3.Zero, Vector3.Zero, 50);
        Assert.True(movement.IsAtAreaReturnDestination(new(0, 3, 0), Vector3.Zero, 50));
        Assert.False(movement.IsAtAreaReturnDestination(new(0, 3.01f, 0), Vector3.Zero, 50));
        Assert.False(movement.IsAtAreaReturnDestination(new(49, 0, 0), Vector3.Zero, 50));
    }

    [Fact]
    public void AreaReturn_OffsetGroundPointDoesNotAllowStoppingSixYalmsFromCenter()
    {
        CombatMovement movement = new(Config());
        movement.BeginAreaReturn(Vector3.Zero);
        Assert.True(movement.ResolveAreaReturnDestination(new(3, 10, 0), Vector3.Zero, 50));
        Assert.True(movement.IsAtAreaReturnDestination(new(3, 10, 0), Vector3.Zero, 50));
        Assert.False(movement.IsAtAreaReturnDestination(new(6, 10, 0), Vector3.Zero, 50));
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, 0f, float.NaN)]
    public void AreaReturn_RejectsNonFiniteGroundPoints(float x, float y, float z)
    {
        CombatMovement movement = new(Config());
        Assert.False(movement.ResolveAreaReturnDestination(new(x, y, z), Vector3.Zero, 50));
        Assert.Null(movement.AreaReturnDestination);
    }

    [Fact]
    public void AreaReturn_RejectsResolvedPointOnRadiusMinusThreeEvenNearSmallFateCenter()
    {
        CombatMovement movement = new(Config());
        Assert.True(movement.ResolveAreaReturnDestination(new(0.9f, 0, 0), Vector3.Zero, 4));
        movement.ResetAreaReturnDestination();
        Assert.False(movement.ResolveAreaReturnDestination(new(1, 0, 0), Vector3.Zero, 4));
        Assert.Null(movement.AreaReturnDestination);
    }

    [Fact]
    public void AreaReturn_SoulFireUsesCenterInsteadOfPreviousPerimeterPoint()
    {
        Vector3 center = new(-431.3446f, 53.73468f, -63.87013f);
        CombatMovement movement = new(Config());
        movement.BeginAreaReturn(center);
        Assert.Equal(center, movement.GetAreaReturnWaypoint(center));
        Assert.Equal(52f, FateAreaReturnSession.GetBoundaryDistance(55));
        Assert.False(movement.ResolveAreaReturnDestination(new(-386, 58.75f, -47.5f), center, 55));
        Assert.False(movement.IsAtAreaReturnDestination(new(-383.20465f, 57.94516f, -48.045967f), center, 55));
        Assert.True(movement.ResolveAreaReturnDestination(center, center, 55));
        Assert.True(movement.IsAtAreaReturnDestination(center, center, 55));
    }

    [Theory]
    [InlineData(false, 4.999, true)]
    [InlineData(false, 5, false)]
    [InlineData(true, 0, false)]
    public void Escape_NormalWaitsFiveSecondsButPreemptionAndCollectionStartImmediately(bool immediate, double seconds, bool waiting)
    {
        CombatMovement movement = new(Config());
        Assert.True(movement.BeginEscapeWait(Now, immediate));
        Assert.False(movement.BeginEscapeWait(Now.AddSeconds(1), immediate));
        Assert.Equal(waiting, movement.EscapeGraceRemaining(Now.AddSeconds(seconds)) > TimeSpan.Zero);
    }

    [Fact]
    public void Escape_PreemptionUsesOldOriginAndPreservesHeight()
    {
        CombatMovement movement = new(Config()) { EscapeOrigin = Vector3.Zero };
        Vector3 destination = movement.BeginEscape(Now, new(10, 8, 0), new(100, 0, 0), 1);
        Assert.Equal(new Vector3(50, 8, 0), destination);
        Assert.Equal(destination, movement.EscapeDestination);
    }

    [Fact]
    public void Escape_CenterUsesStableFateIdDirectionAndNearestPointFallback()
    {
        CombatMovement movement = new(Config());
        Vector3 destination = movement.BeginEscape(Now, Vector3.Zero, Vector3.Zero, 4);
        Assert.Equal(0, destination.X, 4);
        Assert.Equal(40, destination.Z, 4);
        movement.ResolveEscapeDestination(null);
        Assert.Equal(destination, movement.EscapeDestination);
        movement.ResolveEscapeDestination(new(1, 2, 3));
        Assert.Equal(new Vector3(1, 2, 3), movement.EscapeDestination);
    }

    [Theory]
    [InlineData(3f, false)]
    [InlineData(3.01f, true)]
    public void Escape_StopsIssuingMovesWithinThreeUnits(float distance, bool shouldMove)
    {
        CombatMovement movement = new(Config());
        movement.BeginEscape(Now, Vector3.Zero, Vector3.Zero, 0);
        Assert.Equal(shouldMove, movement.ShouldMoveToEscapeDestination(new(40, distance, 0)));
    }

    [Fact]
    public void Escape_OwnTimeoutAndResetDoNotChangeAreaOrPullState()
    {
        AutoFatreConfiguration config = Config();
        CombatMovement movement = new(config);
        movement.BeginPull(Now);
        movement.BeginAreaReturn(Vector3.Zero);
        movement.BeginEscapeWait(Now, true);
        movement.BeginEscape(Now, new(10, 0, 0), Vector3.Zero, 1);
        Assert.True(movement.EscapeStarted);
        Assert.False(movement.IsEscapeTimedOut(Now.AddSeconds(14.999)));
        Assert.True(movement.IsEscapeTimedOut(Now.AddSeconds(15)));
        config.CombatEscapeTimeoutSeconds = 20;
        Assert.False(movement.IsEscapeTimedOut(Now.AddSeconds(15)));
        movement.EscapeOrigin = new(10, 0, 0);
        movement.ResetEscape();
        Assert.False(movement.EscapeStarted);
        Assert.Null(movement.EscapeOrigin);
        Assert.Null(movement.EscapeDestination);
        Assert.NotNull(movement.AreaReturnWaypoint);
        Assert.False(movement.IsPullAttemptTimedOut(Now.AddSeconds(15), false));
        Assert.True(movement.BeginEscapeWait(Now.AddSeconds(15), false));
        Assert.Equal(TimeSpan.FromSeconds(5), movement.EscapeGraceRemaining(Now.AddSeconds(15)));
    }
}
