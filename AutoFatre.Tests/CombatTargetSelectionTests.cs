using System.Numerics;
using System.Reflection;
using Dalamud.Game.ClientState.Objects.Types;

namespace AutoFatre.Tests;

public sealed class CombatTargetSelectionTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);
    private static FateSnapshot Fate(byte progress = 0, float radius = 50) => new(
        1, "test", default, progress, 300, 1, 1, Vector3.Zero, radius, false, 0, 0, 0, 0, 0, [], [], "", 0, 300);

    private static IBattleNpc Target(ulong id, float x = 0, uint hp = 100, ulong targets = 0,
        uint baseId = 1, uint nameId = 0, float y = 0, float z = 0)
    {
        IBattleNpc target = DispatchProxy.Create<IBattleNpc, TargetProxy>();
        ((TargetProxy)(object)target).Values = new()
        {
            ["GameObjectId"] = id, ["Position"] = new Vector3(x, y, z),
            ["CurrentHp"] = hp, ["TargetObjectId"] = targets, ["BaseId"] = baseId, ["NameId"] = nameId,
        };
        return target;
    }

    // No native pointers or game client needed: tests fail if selection unexpectedly reads
    // another service/property. Native classification remains the scanner's responsibility.
    public class TargetProxy : DispatchProxy
    {
        public Dictionary<string, object> Values { get; set; } = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method is not null && method.Name.StartsWith("get_", StringComparison.Ordinal)
                && this.Values.TryGetValue(method.Name[4..], out object? value))
                return value;
            throw new NotSupportedException(method?.Name);
        }
    }

    [Fact]
    public void OrdinaryPull_ChoosesNearestAvailableAndHonorsCooldownBoundary()
    {
        CombatTargetSelection selection = new(new());
        IBattleNpc active = Target(1, 1), cooling = Target(2, 2), nearest = Target(3, 4), farther = Target(4, 6);
        selection.SkipTarget(2, Now.AddSeconds(1));
        Assert.Same(nearest, selection.ChoosePull(Now, Fate(), Vector3.Zero, [active, farther, cooling, nearest], [active], []).Target);
        Assert.Same(cooling, selection.ChooseOrdinaryPull(Now.AddSeconds(1), Fate(), Vector3.Zero, [active, farther, cooling, nearest], [active]));
        selection.RemoveExpiredCooldowns(Now.AddSeconds(1));
        Assert.Same(cooling, selection.ChooseOrdinaryPull(Now.AddSeconds(1), Fate(), Vector3.Zero, [cooling, nearest], []));
    }

    [Fact]
    public void OrdinaryPull_UsesHorizontalFateBoundaryBut3DDistanceForOrdering()
    {
        CombatTargetSelection selection = new(new());
        IBattleNpc outside = Target(1, 55.01f), edge = Target(2, 55), high = Target(3, 1, y: 100);
        Assert.Same(edge, selection.ChooseOrdinaryPull(Now, Fate(), Vector3.Zero, [outside, edge, high], []));
        Assert.Same(high, selection.ChooseOrdinaryPull(Now, Fate(), Vector3.Zero, [outside, high], []));
    }

    [Theory]
    [InlineData(11f, 0f, true)]
    [InlineData(11.01f, 0f, false)]
    [InlineData(55f, 50f, true)]
    [InlineData(55.01f, 50f, false)]
    public void CombatCandidateRange_PreservesMinimumRadiusAndPadding(float x, float radius, bool inside)
    {
        Assert.Equal(inside, CombatTargetSelection.IsInsideCombatRange(new(x, 100, 0), Vector3.Zero, radius));
    }

    [Fact]
    public void ProtectionPull_UsesProtectedNpcOrderThenHpThenDistanceToNpc()
    {
        CombatTargetSelection selection = new(new());
        IBattleNpc firstNpc = Target(100, 40), secondNpc = Target(101, 0);
        IBattleNpc secondThreat = Target(1, 1, hp: 1, targets: 101);
        IBattleNpc highHp = Target(2, 40, hp: 20, targets: 100);
        IBattleNpc lowHpFar = Target(3, 30, hp: 10, targets: 100);
        IBattleNpc lowHpNear = Target(4, 41, hp: 10, targets: 100);
        PullTargetChoice choice = selection.ChoosePull(Now, Fate(), Vector3.Zero,
            [secondThreat, highHp, lowHpFar, lowHpNear, Target(5)], [], [firstNpc, secondNpc]);
        Assert.Same(lowHpNear, choice.Target);
        Assert.Equal(PullTargetKind.ProtectionThreat, choice.Kind);
    }

    [Fact]
    public void ProtectionPull_BypassesOrdinaryRangeAndDistanceSealButNotCooldown()
    {
        CombatTargetSelection selection = new(new());
        IBattleNpc npc = Target(100), threat = Target(1, 55, targets: 100), ordinary = Target(2, 1);
        PullTargetChoice choice = selection.ChoosePull(Now, Fate(radius: 49), Vector3.Zero, [threat, ordinary], [], [npc]);
        Assert.Same(threat, choice.Target);
        Assert.False(selection.PullBatch.TrySealForDistantTarget(2, 55 * 55, choice.Kind == PullTargetKind.ProtectionThreat));
        selection.SkipTarget(1, Now.AddSeconds(10));
        Assert.Same(ordinary, selection.ChoosePull(Now, Fate(radius: 49), Vector3.Zero, [threat, ordinary], [], [npc]).Target);
    }

    [Fact]
    public void OrdinaryRetarget_DoesNotUseProtectionPriority()
    {
        CombatTargetSelection selection = new(new());
        IBattleNpc threat = Target(1, 30, targets: 100), nearest = Target(2, 2), npc = Target(100);
        Assert.Same(threat, selection.ChoosePull(Now, Fate(), Vector3.Zero, [threat, nearest], [], [npc]).Target);
        Assert.Same(nearest, selection.ChooseOrdinaryPull(Now, Fate(), Vector3.Zero, [threat, nearest], []));
    }

    [Fact]
    public void ActiveBatch_KeepsEngagedOutsideCandidateRangeAndIncludesNonFateAggroOnce()
    {
        CombatTargetSelection selection = new(new());
        IBattleNpc outside = Target(1, 100), direct = Target(2, targets: 999), idle = Target(3), nonFate = Target(4, targets: 999);
        selection.ConfirmEngaged(1);
        selection.ConfirmEngaged(5); // vanished FATE enemy
        IBattleNpc[] active = selection.GetActiveTargets([outside, direct, idle], [nonFate, direct], 999);
        Assert.Equal(new ulong[] { 1, 2, 4 }, active.Select(target => target.GameObjectId));
        Assert.True(selection.IsInBatch(1));
        Assert.False(selection.IsInBatch(5));
        Assert.DoesNotContain(outside, CombatTargetSelection.GetInRangeEnemies([outside, direct], Fate()));
    }

    [Fact]
    public void Engagement_IsPerTargetAndIncludesConfirmedBatchAfterTargetChanges()
    {
        CombatTargetSelection selection = new(new());
        IBattleNpc confirmed = Target(1, 100, targets: 100);
        IBattleNpc attacker = Target(2, 100, targets: 999);
        IBattleNpc newPull = Target(3, 100);
        IBattleNpc unrelatedFight = Target(4, 100, targets: 100);
        selection.ConfirmEngaged(confirmed.GameObjectId);

        Assert.True(selection.IsEngaged(confirmed, 999));
        Assert.True(selection.IsEngaged(attacker, 999));
        Assert.False(selection.IsEngaged(newPull, 999));
        Assert.False(selection.IsEngaged(unrelatedFight, 999));
        selection.ResetFateTargets();
        Assert.True(selection.IsEngaged(confirmed, 999));
        selection.ResetBatch();
        Assert.False(selection.IsEngaged(confirmed, 999));
    }

    [Fact]
    public void CurrentPull_DropsOutOfRangeOrdinaryOnlyUntilItTargetsPlayer()
    {
        CombatTargetSelection selection = new(new()) { PullTargetId = 1 };
        IBattleNpc target = Target(1, 55, targets: 100);
        Assert.Null(selection.GetCurrentPull([target], Fate(radius: 49), 999));
        selection.PullKind = PullTargetKind.ProtectionThreat;
        Assert.Same(target, selection.GetCurrentPull([target], Fate(radius: 49), 999));
        selection.PullKind = PullTargetKind.Normal;
        Assert.Same(target, selection.GetCurrentPull([target], Fate(radius: 49), 100));
        Assert.Null(selection.GetCurrentPull([], Fate(), 100));
    }

    [Theory]
    [InlineData(79, 2, true, 4)]
    [InlineData(79, 3, false, 4)]
    [InlineData(80, 2, false, 4)]
    [InlineData(89, 1, false, 4)]
    [InlineData(90, 1, false, 1)]
    public void BatchLimitAndHalfRefill_PreserveProgressThresholds(byte progress, int activeCount, bool refill, int limit)
    {
        CombatTargetSelection selection = new(new() { DpsMaxAggroCount = 4, PullRefillPolicy = PullRefillPolicy.RefillAtHalf });
        Assert.Equal(limit, selection.GetEffectiveAggroLimit(Fate(progress), 2));
        Assert.Equal(refill, selection.ShouldRefill(Fate(progress), activeCount, 2));
    }

    [Theory]
    [InlineData(1, 4, 2, true)]
    [InlineData(1, 4, 3, false)]
    [InlineData(2, 3, 1, true)]
    [InlineData(2, 3, 2, false)]
    [InlineData(3, 3, 1, true)]
    [InlineData(3, 3, 2, false)]
    [InlineData(4, 4, 2, true)]
    [InlineData(4, 4, 3, false)]
    public void RoleLimits_DefaultBatchLimitAndHalfRefill(byte role, int limit, int count, bool refill)
    {
        CombatTargetSelection selection = new(new());
        Assert.Equal(limit, selection.GetEffectiveAggroLimit(Fate(), role));
        Assert.Equal(refill, selection.ShouldRefill(Fate(), count, role));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RoleLimits_SixTargetLimitUsesThreeForHalfRefillAndPreservesProgressRules(byte role)
    {
        CombatTargetSelection selection = new(new()
        {
            TankMaxAggroCount = 6, DpsMaxAggroCount = 6, HealerMaxAggroCount = 6,
        });
        Assert.Equal(6, selection.GetEffectiveAggroLimit(Fate(79), role));
        Assert.True(selection.ShouldRefill(Fate(79), 3, role));
        Assert.False(selection.ShouldRefill(Fate(79), 4, role));
        Assert.Equal(6, selection.GetEffectiveAggroLimit(Fate(80), role));
        Assert.False(selection.ShouldRefill(Fate(80), 3, role));
        Assert.Equal(1, selection.GetEffectiveAggroLimit(Fate(90), role));
        Assert.False(selection.ShouldRefill(Fate(90), 1, role));
        Assert.True(selection.PullBatch.TrySealForDistantTarget(2, 25 * 25, false));
        Assert.False(selection.ShouldRefill(Fate(), 1, role));
    }

    [Fact]
    public void RoleLimits_SwitchingRolesAndChangingSettingsUsesLiveValuesWithoutClearingBatch()
    {
        AutoFatreConfiguration config = new() { TankMaxAggroCount = 6, DpsMaxAggroCount = 3, HealerMaxAggroCount = 4 };
        CombatTargetSelection selection = new(config);
        selection.ConfirmEngaged(1);
        Assert.Equal(6, selection.GetEffectiveAggroLimit(Fate(), 1));
        Assert.True(selection.ShouldRefill(Fate(), 2, 1));
        Assert.Equal(3, selection.GetEffectiveAggroLimit(Fate(), 2));
        Assert.False(selection.ShouldRefill(Fate(), 2, 2));
        Assert.Equal(4, selection.GetEffectiveAggroLimit(Fate(), 4));
        config.HealerMaxAggroCount = 6;
        Assert.Equal(6, selection.GetEffectiveAggroLimit(Fate(), 4));
        Assert.True(selection.ShouldRefill(Fate(), 3, 4));
        Assert.True(selection.IsInBatch(1));
    }

    [Fact]
    public void SealedBatch_DisablesRefillUntilBatchResetWithoutClearingExecutionLocks()
    {
        CombatTargetSelection selection = new(new() { PullRefillPolicy = PullRefillPolicy.RefillAtHalf, DpsMaxAggroCount = 4 })
        { KillTargetId = 9, BossTargetId = 10 };
        selection.ConfirmEngaged(1);
        Assert.True(selection.PullBatch.TrySealForDistantTarget(2, 25 * 25, false));
        Assert.False(selection.ShouldRefill(Fate(), 1, 2));
        selection.ResetLostTarget();
        selection.Destroy.Reset();
        Assert.True(selection.PullBatch.MustDrain);
        Assert.True(selection.IsInBatch(1));
        selection.ResetBatch();
        Assert.False(selection.PullBatch.MustDrain);
        Assert.False(selection.IsInBatch(1));
        Assert.True(selection.ShouldRefill(Fate(), 1, 2));
        Assert.Equal(9UL, selection.KillTargetId);
        Assert.Equal(10UL, selection.BossTargetId);
    }

    [Fact]
    public void ClearBatchPolicy_NeverAllowsHalfRefill()
    {
        CombatTargetSelection selection = new(new() { PullRefillPolicy = PullRefillPolicy.ClearBatchFirst });
        Assert.False(selection.ShouldRefill(Fate(), 1, 2));
    }

    [Fact]
    public void KillTarget_LocksSurvivorOtherwiseLowestHpThenDistanceWithoutCommitting()
    {
        CombatTargetSelection selection = new(new()) { KillTargetId = 1 };
        IBattleNpc survivor = Target(1, 20), weakFar = Target(2, 10, hp: 1), weakNear = Target(3, 2, hp: 1);
        Assert.Same(survivor, selection.ChooseKill(Vector3.Zero, [survivor, weakFar, weakNear]));
        Assert.Same(weakNear, selection.ChooseKill(Vector3.Zero, [weakFar, weakNear]));
        Assert.Equal(1UL, selection.KillTargetId);
        Assert.Null(selection.ChooseKill(Vector3.Zero, []));
    }

    [Theory]
    [InlineData(7586u, 0u, "ignored", LostPriorityTargetKind.LostGirl)]
    [InlineData(1u, 6737u, "ignored", LostPriorityTargetKind.LostGirl)]
    [InlineData(1u, 6738u, "ignored", LostPriorityTargetKind.LostOne)]
    [InlineData(7586u, 6738u, "ignored", LostPriorityTargetKind.LostGirl)]
    [InlineData(0u, 0u, " 迷失 少女（特殊）", LostPriorityTargetKind.LostGirl)]
    [InlineData(0u, 0u, "迷失者 Ａ", LostPriorityTargetKind.LostOne)]
    [InlineData(1u, 0u, "迷失少女", null)]
    [InlineData(0u, 1u, "迷失者", null)]
    [InlineData(0u, 0u, "普通怪物", null)]
    public void LostIdentity_PrefersIdsAndOnlyFallsBackWhenBothAreZero(uint baseId, uint nameId, string name, LostPriorityTargetKind? expected)
    {
        Assert.Equal(expected, CombatTargetSelection.GetLostKind(baseId, nameId, name));
    }

    [Theory]
    [InlineData(179, 180, 240, null)]
    [InlineData(180, 180, 240, 1UL)]
    [InlineData(1, 600, 600, null)]
    [InlineData(0, 600, 600, null)]
    public void LostTargets_RequireRemainingThresholds(long remaining, int girlThreshold, int oneThreshold, ulong? expectedTarget)
    {
        CombatTargetSelection selection = new(new() { PrioritizeLostGirlAndLostOne = true,
            LostGirlRemainingTimeThresholdSeconds = girlThreshold, LostOneRemainingTimeThresholdSeconds = oneThreshold });
        IBattleNpc girl = Target(1, 20, baseId: 7586), one = Target(2, 2, nameId: 6738);
        Assert.Equal(expectedTarget, selection.ChooseLostTarget(Now, remaining, Vector3.Zero, null, [girl, one]).Target?.GameObjectId);
        selection.ResetLostTarget();
        Assert.Equal(expectedTarget, selection.ChooseLostTarget(Now, remaining, Vector3.Zero, null, [girl]).Target?.GameObjectId);
    }

    [Fact]
    public void LostLock_IgnoresCooldownAndKeepsSurvivorBelowThresholdAndOutsideRange()
    {
        CombatTargetSelection selection = new(new() { PrioritizeLostGirlAndLostOne = true });
        IBattleNpc girl = Target(1, 20, baseId: 7586), other = Target(2, 1, nameId: 6738);
        selection.SkipTarget(1, Now.AddMinutes(1));
        Assert.Same(girl, selection.ChooseLostTarget(Now, 300, Vector3.Zero, null, [girl]).Target);
        Assert.Same(girl, selection.ChooseLostTarget(Now.AddSeconds(1), 0, new(1000, 0, 0), girl, [other]).Target);
    }

    [Fact]
    public void LostMissing_WaitsOneSecondThenClearsWithoutSelectingReplacementInSameCall()
    {
        CombatTargetSelection selection = new(new() { PrioritizeLostGirlAndLostOne = true });
        IBattleNpc girl = Target(1, baseId: 7586), other = Target(2, nameId: 6738);
        selection.ChooseLostTarget(Now, 300, Vector3.Zero, null, [girl]);
        Assert.Equal(LostTargetSelectionAction.Waiting, selection.ChooseLostTarget(Now, 300, Vector3.Zero, null, [other]).Action);
        Assert.Equal(LostTargetSelectionAction.Waiting, selection.ChooseLostTarget(Now.AddMilliseconds(999), 300, Vector3.Zero, null, [other]).Action);
        Assert.Equal(LostTargetSelectionAction.Disappeared, selection.ChooseLostTarget(Now.AddSeconds(1), 300, Vector3.Zero, null, [other]).Action);
        Assert.Null(selection.LostTargetId);
        Assert.Null(selection.LostKind);
        Assert.Same(other, selection.ChooseLostTarget(Now.AddSeconds(1), 300, Vector3.Zero, null, [other]).Target);
    }

    [Fact]
    public void LostReappearance_ResetsMissingGraceAndDisableClearsLock()
    {
        AutoFatreConfiguration config = new() { PrioritizeLostGirlAndLostOne = true };
        CombatTargetSelection selection = new(config);
        IBattleNpc girl = Target(1, baseId: 7586);
        selection.ChooseLostTarget(Now, 300, Vector3.Zero, null, [girl]);
        selection.ChooseLostTarget(Now, 300, Vector3.Zero, null, []);
        Assert.Same(girl, selection.ChooseLostTarget(Now.AddMilliseconds(800), 300, Vector3.Zero, girl, []).Target);
        Assert.Equal(LostTargetSelectionAction.Waiting, selection.ChooseLostTarget(Now.AddSeconds(2), 300, Vector3.Zero, null, []).Action);
        config.PrioritizeLostGirlAndLostOne = false;
        Assert.Equal(LostTargetSelectionAction.Disabled, selection.ChooseLostTarget(Now, 300, Vector3.Zero, girl, [girl]).Action);
        Assert.Null(selection.LostTargetId);
    }

    [Fact]
    public void BossChoice_KeepsLiveLockOutsideScanOtherwiseUsesScannerOrder()
    {
        IBattleNpc locked = Target(1), first = Target(2), second = Target(3);
        Assert.Same(locked, CombatTargetSelection.ChooseBoss(locked, [first, second]));
        Assert.Same(first, CombatTargetSelection.ChooseBoss(null, [first, second]));
        Assert.Null(CombatTargetSelection.ChooseBoss(null, []));
    }

    [Fact]
    public void BossCleanup_KeepsLockOtherwisePlayerAggroThenHpThenDistance()
    {
        CombatTargetSelection selection = new(new()) { BossCleanupTargetId = 1 };
        IBattleNpc locked = Target(1), idle = Target(2, hp: 1), strong = Target(3, hp: 10, targets: 999),
            weakFar = Target(4, 10, hp: 5, targets: 999), weakNear = Target(5, 1, hp: 5, targets: 999);
        Assert.Same(locked, selection.ChooseBossCleanup(Vector3.Zero, 999, [locked, idle, strong, weakFar, weakNear]));
        Assert.Same(weakNear, selection.ChooseBossCleanup(Vector3.Zero, 999, [idle, strong, weakFar, weakNear]));
        Assert.Equal(1UL, selection.BossCleanupTargetId);
    }

    [Fact]
    public void PostCombatCleanup_PreservesLockAndScannerOrderWithoutCommitting()
    {
        CombatTargetSelection selection = new(new()) { CleanupTargetId = 2 };
        IBattleNpc first = Target(1, hp: 100), locked = Target(2, hp: 1);
        Assert.Same(locked, selection.ChooseCleanup([first, locked]));
        Assert.Same(first, selection.ChooseCleanup([first]));
        Assert.Null(selection.ChooseCleanup([]));
        Assert.Equal(2UL, selection.CleanupTargetId);
    }

    [Fact]
    public void DestroySelection_LocksObjectivesAndDrainsFiveEngagedBeforeNextObjective()
    {
        CombatTargetSelection selection = new(new());
        IBattleNpc objective = Target(1, 10), next = Target(2, 1);
        Assert.Equal(1UL, selection.ChooseDestroy(Vector3.Zero, [objective], []).TargetId);
        Assert.Equal(1UL, selection.ChooseDestroy(Vector3.Zero, [next, objective], []).TargetId);
        IBattleNpc[] engaged = [Target(10, hp: 10), Target(11, hp: 1), Target(12), Target(13), Target(14)];
        DestroyDecision cleanup = selection.ChooseDestroy(Vector3.Zero, [next], engaged);
        Assert.Equal(DestroyAction.Cleanup, cleanup.Action);
        Assert.Equal(11UL, cleanup.TargetId);
        Assert.Equal(11UL, selection.ChooseDestroy(Vector3.Zero, [next], [engaged[0], engaged[1]]).TargetId);
        Assert.Equal(2UL, selection.ChooseDestroy(Vector3.Zero, [next], []).TargetId);
        Assert.False(selection.Destroy.ClearingAggro);
    }

    [Fact]
    public void DestroySelection_BelowFiveEngagedResumesObjectiveImmediately()
    {
        CombatTargetSelection selection = new(new());
        selection.ChooseDestroy(Vector3.Zero, [Target(1)], []);
        Assert.Equal(DestroyAction.Destroy, selection.ChooseDestroy(Vector3.Zero, [Target(2)], [Target(10), Target(11)]).Action);
    }

    [Fact]
    public void FateTargetReset_ClearsLocksButPreservesBatchSealCooldownAndCleanupLocks()
    {
        CombatTargetSelection selection = new(new() { PrioritizeLostGirlAndLostOne = true })
        {
            PullTargetId = 1, KillTargetId = 2, BossTargetId = 3, CleanupTargetId = 4, BossCleanupTargetId = 5,
        };
        IBattleNpc girl = Target(6, baseId: 7586), skipped = Target(7);
        selection.ChooseLostTarget(Now, 300, Vector3.Zero, null, [girl]);
        selection.ChooseDestroy(Vector3.Zero, [Target(8)], []);
        selection.ConfirmEngaged(2);
        selection.SkipTarget(7, Now.AddSeconds(1));
        selection.PullBatch.TrySealForDistantTarget(2, 25 * 25, false);
        selection.ResetFateTargets();
        Assert.Null(selection.PullTargetId);
        Assert.Null(selection.KillTargetId);
        Assert.Null(selection.BossTargetId);
        Assert.Null(selection.LostTargetId);
        Assert.Null(selection.Destroy.TargetId);
        Assert.Equal(4UL, selection.CleanupTargetId);
        Assert.Equal(5UL, selection.BossCleanupTargetId);
        Assert.True(selection.PullBatch.MustDrain);
        Assert.True(selection.IsInBatch(2));
        Assert.Null(selection.ChooseOrdinaryPull(Now, Fate(), Vector3.Zero, [skipped], []));
        Assert.Same(girl, selection.ChooseLostTarget(Now, 300, Vector3.Zero, null, [girl]).Target);
    }

    [Fact]
    public void LostDisabled_FateEntryDoesNotInspectObjectState()
    {
        CombatTargetSelection selection = new(new() { PrioritizeLostGirlAndLostOne = false });
        Assert.Equal(LostTargetSelectionAction.Disabled,
            selection.ChooseLostTarget(Now, Fate(), Vector3.Zero, [Target(1)], [Target(2)]).Action);
    }

    [Fact]
    public void PullAndRefill_NoAvailableCandidateReturnsNoChoiceWithoutChangingLocks()
    {
        CombatTargetSelection selection = new(new() { DpsMaxAggroCount = 4, PullRefillPolicy = PullRefillPolicy.RefillAtHalf })
        { KillTargetId = 1 };
        IBattleNpc active = Target(1), cooling = Target(2);
        selection.SkipTarget(2, Now.AddMinutes(1));
        Assert.True(selection.ShouldRefill(Fate(), 1, 2));
        PullTargetChoice choice = selection.ChoosePull(Now, Fate(), Vector3.Zero, [active, cooling], [active], []);
        Assert.Null(choice.Target);
        Assert.Equal(PullTargetKind.Normal, choice.Kind);
        Assert.Equal(1UL, selection.KillTargetId);
    }
}
