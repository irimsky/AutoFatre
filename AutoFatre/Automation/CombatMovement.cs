using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoFatre;

/// <summary>
/// Combat movement state and decisions, updated only on the Framework thread.
/// The controller applies these decisions through the existing navigation backend;
/// target selection, game targeting and automation state transitions stay outside.
/// </summary>
public sealed class CombatMovement(AutoFatreConfiguration configuration)
{
    public enum ApproachAction { Approach, ReachedRange, Stay, StopForCast, WaitForCast }
    public enum SelectionAction { Wait, BeginApproach, ContinueApproach }
    public enum EscortAction { Stop, Stay, Continue, Approach }

    private enum ApproachPhase { MovingToApproachRange, ApproachComplete }
    private static readonly TimeSpan PullAttemptTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EscapeGracePeriod = TimeSpan.FromSeconds(5);

    private ApproachPhase pullPhase;
    private ApproachPhase targetPhase;
    private DateTime targetCastProtectionUntil = DateTime.MinValue;
    private DateTime pullAttemptStartedAt = DateTime.MinValue;
    private DateTime pullPhaseStartedAt = DateTime.MinValue;
    private DateTime pullCastProtectionUntil = DateTime.MinValue;
    private ulong? selectingTargetId;
    private DateTime selectionFailedAt = DateTime.MinValue;
    private bool selectionMoving;
    private DateTime escortLastRequestAt = DateTime.MinValue;
    private Vector3? escortLastPosition;
    private DateTime escapeNoTargetSince = DateTime.MinValue;
    private DateTime escapeStartedAt = DateTime.MinValue;

    public ulong? SelectedTargetId => this.selectingTargetId;
    public Vector3? AreaReturnWaypoint { get; private set; }
    public Vector3? AreaReturnDestination { get; private set; }
    public Vector3? EscapeDestination { get; private set; }
    // Preemption must escape from the old encounter, not toward the newly chosen FATE.
    public Vector3? EscapeOrigin { get; set; }

    public void BeginPull(DateTime now)
    {
        this.pullAttemptStartedAt = now;
        this.pullCastProtectionUntil = DateTime.MinValue;
        this.EnterApproachPhase(ApproachPhase.MovingToApproachRange, now);
    }

    public ApproachAction UpdatePullApproach(float distance, bool casting, DateTime now, bool attackingCast = false)
    {
        ApproachPhase previous = this.pullPhase;
        ApproachAction action = this.UpdateApproach(distance, casting, attackingCast, now,
            ref this.pullPhase, ref this.pullCastProtectionUntil);
        if (this.pullPhase != previous)
            this.pullPhaseStartedAt = now;
        // Pull -> kill for the same actor must not trigger a second approach.
        if (this.pullPhase == ApproachPhase.ApproachComplete)
            this.CompleteApproach(now);
        return action;
    }

    // The controller calls this only for unengaged targets, using the same range/cast protection.
    // Non-pull approaches must not reset the pull attempt/aggro timers.
    public ApproachAction UpdateTargetApproach(float distance, bool casting, DateTime now, bool attackingCast = false) =>
        this.UpdateApproach(distance, casting, attackingCast, now, ref this.targetPhase, ref this.targetCastProtectionUntil);

    public static bool IsAttackCast(byte actionType, uint actionId, ulong castTargetId, ulong targetId) =>
        actionType == (byte)ActionType.Action && actionId != 0
        && targetId is not 0 and not 0xE0000000 && castTargetId == targetId;

    private ApproachAction UpdateApproach(float distance, bool casting, bool attackingCast, DateTime now,
        ref ApproachPhase phase, ref DateTime castProtectionUntil)
    {
        if (casting)
        {
            // Any cast pauses movement, but a summon/heal/buff is not an attack on this
            // target. Only a confirmed attack ends its one-time approach permanently.
            if (attackingCast)
                phase = ApproachPhase.ApproachComplete;
            castProtectionUntil = now.AddSeconds(1);
            return ApproachAction.StopForCast;
        }
        if (now < castProtectionUntil)
            return ApproachAction.WaitForCast;

        if (phase == ApproachPhase.MovingToApproachRange)
        {
            if (distance <= configuration.PullApproachDistance)
            {
                phase = ApproachPhase.ApproachComplete;
                return ApproachAction.ReachedRange;
            }
            return ApproachAction.Approach;
        }

        // Approach once. Combat positioning and dodging must not restart this movement.
        return ApproachAction.Stay;
    }

    // The original/resolved navigation point also completes the approach, even if the actor moved.
    public void CompleteApproach(DateTime now, bool pulling = false)
    {
        this.targetPhase = ApproachPhase.ApproachComplete;
        if (pulling && this.pullPhase != ApproachPhase.ApproachComplete)
            this.EnterApproachPhase(ApproachPhase.ApproachComplete, now);
    }

    public bool IsPullAttemptTimedOut(DateTime now, bool casting) =>
        !casting && now >= this.pullCastProtectionUntil
        && (now - this.pullAttemptStartedAt >= PullAttemptTimeout
            || this.pullPhase == ApproachPhase.ApproachComplete
                && now - this.pullPhaseStartedAt >= TimeSpan.FromSeconds(configuration.AggroConfirmationTimeoutSeconds));

    public void RejectPullNavigation(DateTime now) => this.pullAttemptStartedAt = now - PullAttemptTimeout;

    public void ResetPull()
    {
        this.pullAttemptStartedAt = DateTime.MinValue;
        this.pullPhaseStartedAt = DateTime.MinValue;
        this.pullCastProtectionUntil = DateTime.MinValue;
        this.pullPhase = ApproachPhase.MovingToApproachRange;
    }

    private void EnterApproachPhase(ApproachPhase phase, DateTime now)
    {
        this.pullPhase = phase;
        this.pullPhaseStartedAt = now;
    }

    /// <returns>Whether the controller must stop navigation for the previous combat target.</returns>
    public bool BeginTargetSelection(ulong targetId)
    {
        if (this.selectingTargetId == targetId)
            return false;
        bool stop = this.selectingTargetId is not null;
        this.ResetTargetSelection();
        this.selectingTargetId = targetId;
        return stop;
    }

    public bool CompleteTargetSelection()
    {
        bool stop = this.selectionMoving;
        this.selectionMoving = false;
        this.selectionFailedAt = DateTime.MinValue;
        return stop;
    }

    public SelectionAction TargetSelectionFailed(DateTime now)
    {
        if (this.selectionFailedAt == DateTime.MinValue)
            this.selectionFailedAt = now;
        if (now - this.selectionFailedAt < TimeSpan.FromSeconds(1))
            return SelectionAction.Wait;
        if (this.selectionMoving)
            return SelectionAction.ContinueApproach;
        this.selectionMoving = true;
        return SelectionAction.BeginApproach;
    }

    public void ResetTargetSelection()
    {
        this.selectingTargetId = null;
        this.targetPhase = ApproachPhase.MovingToApproachRange;
        this.targetCastProtectionUntil = DateTime.MinValue;
        this.selectionFailedAt = DateTime.MinValue;
        this.selectionMoving = false;
    }

    public EscortAction FollowEscort(DateTime now, Vector3 playerPosition, Vector3 targetPosition, bool moveActive)
    {
        float distance = Vector3.Distance(playerPosition, targetPosition);
        if (distance <= 5f)
        {
            this.RememberEscortPosition(targetPosition);
            return EscortAction.Stop;
        }
        if (distance < 10f && !moveActive)
            return EscortAction.Stay;

        bool targetMoved = this.escortLastPosition is null
            || Vector3.DistanceSquared(this.escortLastPosition.Value, targetPosition) >= 3f * 3f;
        return moveActive && (!targetMoved || now - this.escortLastRequestAt < TimeSpan.FromSeconds(2))
            ? EscortAction.Continue
            : EscortAction.Approach;
    }

    // Also used by the ordinary combat scan to preserve its existing position observation.
    public void RememberEscortPosition(Vector3 position) => this.escortLastPosition = position;

    public void ConfirmEscortRequest(DateTime now, Vector3 targetPosition)
    {
        this.escortLastPosition = targetPosition;
        this.escortLastRequestAt = now;
    }

    public void ResetEscort()
    {
        this.escortLastRequestAt = DateTime.MinValue;
        this.escortLastPosition = null;
    }

    public void BeginAreaReturn(Vector3? center)
    {
        this.ResetAreaReturn();
        this.AreaReturnWaypoint = center;
    }

    public Vector3 GetAreaReturnWaypoint(Vector3 center)
    {
        this.AreaReturnWaypoint ??= center;
        return this.AreaReturnWaypoint.Value;
    }

    [MemberNotNullWhen(true, nameof(AreaReturnDestination))]
    public bool ResolveAreaReturnDestination(Vector3? nearest, Vector3 center, float radius)
    {
        if (nearest is not { } point || !float.IsFinite(radius) || radius <= 0
            || !float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z)
            || Vector2.DistanceSquared(new(point.X, point.Z), new(center.X, center.Z)) > 3f * 3f
            || FateAreaReturnSession.IsNearBoundary(point, center, radius))
            return false;
        this.AreaReturnDestination = nearest;
        return true;
    }

    public bool IsAtAreaReturnDestination(Vector3 playerPosition, Vector3 center, float radius) =>
        this.AreaReturnDestination is { } destination
        && FateAreaReturnSession.IsAtDestination(playerPosition, destination, center, radius);

    public void ResetAreaReturnDestination() => this.AreaReturnDestination = null;

    public void ResetAreaReturn()
    {
        this.AreaReturnDestination = null;
        this.AreaReturnWaypoint = null;
    }

    public bool BeginEscapeWait(DateTime now, bool immediate)
    {
        if (this.escapeNoTargetSince != DateTime.MinValue)
            return false;
        this.escapeNoTargetSince = immediate ? now - EscapeGracePeriod : now;
        return true;
    }

    public TimeSpan EscapeGraceRemaining(DateTime now) => EscapeGracePeriod - (now - this.escapeNoTargetSince);
    public bool EscapeStarted => this.escapeStartedAt != DateTime.MinValue;

    public Vector3 BeginEscape(DateTime now, Vector3 playerPosition, Vector3 fallbackOrigin, ushort fateId)
    {
        this.escapeStartedAt = now;
        Vector3 origin = this.EscapeOrigin ?? fallbackOrigin;
        Vector2 away = new(playerPosition.X - origin.X, playerPosition.Z - origin.Z);
        if (away.LengthSquared() < 0.01f)
        {
            float angle = (fateId % 16) * (MathF.PI / 8f);
            away = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
        else
            away = Vector2.Normalize(away);

        this.EscapeDestination = new Vector3(
            playerPosition.X + away.X * configuration.CombatEscapeDistance,
            playerPosition.Y,
            playerPosition.Z + away.Y * configuration.CombatEscapeDistance);
        return this.EscapeDestination.Value;
    }

    public void ResolveEscapeDestination(Vector3? nearest) => this.EscapeDestination = nearest ?? this.EscapeDestination;

    public bool IsEscapeTimedOut(DateTime now) =>
        now - this.escapeStartedAt >= TimeSpan.FromSeconds(configuration.CombatEscapeTimeoutSeconds);

    public bool ShouldMoveToEscapeDestination(Vector3 playerPosition) =>
        this.EscapeDestination is { } destination && Vector3.Distance(playerPosition, destination) > 3f;

    public void ResetEscape()
    {
        this.escapeNoTargetSince = DateTime.MinValue;
        this.escapeStartedAt = DateTime.MinValue;
        this.EscapeDestination = null;
        this.EscapeOrigin = null;
    }
}
