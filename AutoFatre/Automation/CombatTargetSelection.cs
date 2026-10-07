using System.Numerics;
using System.Text;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;

namespace AutoFatre;

public enum PullTargetKind { Normal, ProtectionThreat }
public enum LostPriorityTargetKind { LostGirl, LostOne }
public enum LostTargetSelectionAction { None, Disabled, Waiting, Disappeared, Target }
public readonly record struct PullTargetChoice(IBattleNpc? Target, PullTargetKind Kind);
public readonly record struct LostTargetChoice(LostTargetSelectionAction Action, IBattleNpc? Target = null);

/// <summary>
/// Chooses combat targets and holds logical locks/batch membership. Does not move the player,
/// set the game's current target, or change automation states. Object references are tick-local.
/// The controller commits execution locks at the original action points and resets them when
/// an activity is interrupted; resetting locks must not implicitly discard an engaged batch.
/// </summary>
public sealed class CombatTargetSelection(AutoFatreConfiguration configuration)
{
    public const float CombatRangePadding = 5f;
    private readonly HashSet<ulong> batchTargets = [];
    private readonly Dictionary<ulong, DateTime> skippedTargets = [];
    private DateTime lostTargetMissingSince = DateTime.MinValue;

    public GeneralPullBatchSession PullBatch { get; } = new();
    public DestroyObjectiveSession Destroy { get; } = new();
    public ulong? PullTargetId { get; set; }
    public PullTargetKind PullKind { get; set; }
    public ulong? KillTargetId { get; set; }
    public ulong? BossTargetId { get; set; }
    public ulong? BossCleanupTargetId { get; set; }
    public ulong? CleanupTargetId { get; set; }
    public ulong? LostTargetId { get; private set; }
    public LostPriorityTargetKind? LostKind { get; private set; }

    // 普通拉怪：批次成员、冷却、保护威胁优先、距离和补怪限制。
    public bool IsInBatch(ulong id) => this.batchTargets.Contains(id);
    public bool IsEngaged(IBattleNpc target, ulong playerId) =>
        this.IsInBatch(target.GameObjectId) || target.TargetObjectId == playerId;
    public void ConfirmEngaged(ulong id) => this.batchTargets.Add(id);
    public void SkipTarget(ulong id, DateTime until) => this.skippedTargets[id] = until;
    public void RemoveExpiredCooldowns(DateTime now)
    {
        foreach (ulong id in this.skippedTargets.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
            this.skippedTargets.Remove(id);
    }

    public void ResetBatch()
    {
        this.batchTargets.Clear();
        this.PullKind = PullTargetKind.Normal;
        this.PullBatch.Reset();
    }

    public void ResetLostTarget()
    {
        this.LostTargetId = null;
        this.LostKind = null;
        this.lostTargetMissingSince = DateTime.MinValue;
    }

    // 回正/重新同步只清当前 FATE 锁定，不清批次、距离封批或战后清场锁。
    public void ResetFateTargets()
    {
        this.PullTargetId = null;
        this.KillTargetId = null;
        this.ResetLostTarget();
        this.Destroy.Reset();
        this.BossTargetId = null;
    }

    public static IBattleNpc[] GetProtectedTargets(IReadOnlyList<IBattleNpc> related, Vector3 playerPosition) => related
        .Where(FateTargetSelector.IsFriendly)
        .OrderBy(target => target.CurrentHp)
        .ThenBy(target => Vector3.DistanceSquared(playerPosition, target.Position))
        .ToArray();

    public static IBattleNpc[] GetAllEnemies(IReadOnlyList<IBattleNpc> related) => related
        .Where(FateTargetSelector.IsAttackableEnemy).ToArray();

    public static IBattleNpc[] GetInRangeEnemies(IReadOnlyList<IBattleNpc> enemies, FateSnapshot fate) => enemies
        .Where(enemy => IsInsideCombatRange(enemy.Position, fate.Position, fate.Radius)).ToArray();

    public static bool IsInsideCombatRange(Vector3 position, Vector3 center, float radius)
    {
        float x = position.X - center.X;
        float z = position.Z - center.Z;
        return MathF.Sqrt(x * x + z * z) <= Math.Max(6f, radius) + CombatRangePadding;
    }

    public static bool IsInsideOrdinaryPullRange(IBattleNpc target, FateSnapshot fate) =>
        FateAreaReturnSession.IsInsidePullRange(target.Position, fate.Position, fate.Radius);

    public IBattleNpc[] GetActiveTargets(IReadOnlyList<IBattleNpc> allEnemies,
        IReadOnlyList<IBattleNpc> nonFateAggroTargets, ulong playerId)
    {
        // Prune against all living FATE enemies, not just new-pull candidates in range.
        HashSet<ulong> liveIds = allEnemies.Select(target => target.GameObjectId).ToHashSet();
        this.batchTargets.RemoveWhere(id => !liveIds.Contains(id));
        return allEnemies.Where(target => target.TargetObjectId == playerId || this.IsInBatch(target.GameObjectId))
            .Concat(nonFateAggroTargets).DistinctBy(target => target.GameObjectId).ToArray();
    }

    public IBattleNpc? GetCurrentPull(IReadOnlyList<IBattleNpc> enemies, FateSnapshot fate, ulong playerId)
    {
        IBattleNpc? target = FindLock(this.PullTargetId, enemies);
        if (target is not null && this.PullKind == PullTargetKind.Normal
            && target.TargetObjectId != playerId && !IsInsideOrdinaryPullRange(target, fate))
            return null;
        return target;
    }

    public PullTargetChoice ChoosePull(DateTime now, FateSnapshot fate, Vector3 playerPosition,
        IReadOnlyList<IBattleNpc> enemies, IReadOnlyList<IBattleNpc> activeTargets,
        IReadOnlyList<IBattleNpc> protectedTargets)
    {
        HashSet<ulong> activeIds = activeTargets.Select(target => target.GameObjectId).ToHashSet();
        foreach (IBattleNpc protectedTarget in protectedTargets)
        {
            IBattleNpc? threat = enemies.Where(enemy => this.IsAvailable(enemy, activeIds, now))
                .Where(enemy => enemy.TargetObjectId == protectedTarget.GameObjectId)
                .OrderBy(enemy => enemy.CurrentHp)
                .ThenBy(enemy => Vector3.DistanceSquared(protectedTarget.Position, enemy.Position))
                .FirstOrDefault();
            if (threat is not null)
                return new(threat, PullTargetKind.ProtectionThreat);
        }

        return new(this.ChooseOrdinaryPull(now, fate, playerPosition, enemies, activeTargets), PullTargetKind.Normal);
    }

    // The distant-current-pull recheck intentionally uses ordinary candidates only.
    public IBattleNpc? ChooseOrdinaryPull(DateTime now, FateSnapshot fate, Vector3 playerPosition,
        IReadOnlyList<IBattleNpc> enemies, IReadOnlyList<IBattleNpc> activeTargets)
    {
        HashSet<ulong> activeIds = activeTargets.Select(target => target.GameObjectId).ToHashSet();
        return enemies.Where(enemy => this.IsAvailable(enemy, activeIds, now))
            .Where(enemy => IsInsideOrdinaryPullRange(enemy, fate))
            .OrderBy(enemy => Vector3.DistanceSquared(playerPosition, enemy.Position)).FirstOrDefault();
    }

    private bool IsAvailable(IBattleNpc enemy, HashSet<ulong> activeIds, DateTime now) =>
        !activeIds.Contains(enemy.GameObjectId)
        && (!this.skippedTargets.TryGetValue(enemy.GameObjectId, out DateTime until) || until <= now);

    public int GetEffectiveAggroLimit(FateSnapshot fate, byte classJobRole) =>
        fate.Progress >= 90 ? 1 : configuration.GetMaxAggroCount(classJobRole);
    public bool CanRefillAtHalf(FateSnapshot fate) =>
        configuration.PullRefillPolicy == PullRefillPolicy.RefillAtHalf && fate.Progress < 80;
    public bool ShouldRefill(FateSnapshot fate, int activeCount, byte classJobRole) =>
        !this.PullBatch.MustDrain && this.CanRefillAtHalf(fate) && activeCount <= this.GetEffectiveAggroLimit(fate, classJobRole) / 2;

    public IBattleNpc? ChooseKill(Vector3 playerPosition, IReadOnlyList<IBattleNpc> activeTargets) =>
        FindLock(this.KillTargetId, activeTargets) ?? activeTargets.OrderBy(target => target.CurrentHp)
            .ThenBy(target => Vector3.DistanceSquared(playerPosition, target.Position)).FirstOrDefault();

    // 迷失目标：首次选取要求剩余时间达到对应阈值，锁定后等到死亡/确认消失。
    public LostTargetChoice ChooseLostTarget(DateTime now, FateSnapshot fate, Vector3 playerPosition,
        IReadOnlyList<IBattleNpc> related, IReadOnlyList<IBattleNpc> nearbyEnemies)
    {
        if (!configuration.PrioritizeLostGirlAndLostOne)
            return this.ChooseLostTarget(now, fate.TimeRemaining, playerPosition, null, []);
        IBattleNpc? lockedTarget = this.LostTargetId is { } id
            ? related.FirstOrDefault(target => target.GameObjectId == id && target.IsValid() && !target.IsDead
                && FateTargetSelector.BelongsToFate(target, fate.FateId))
            : null;
        return this.ChooseLostTarget(now, fate.TimeRemaining, playerPosition, lockedTarget, nearbyEnemies);
    }

    // survivingLock is live and still belongs to this FATE; do not reapply attackability,
    // range, cooldown or any time condition once a lost target has been locked.
    public LostTargetChoice ChooseLostTarget(DateTime now, long timeRemaining, Vector3 playerPosition,
        IBattleNpc? survivingLock, IReadOnlyList<IBattleNpc> nearbyEnemies)
    {
        if (!configuration.PrioritizeLostGirlAndLostOne)
        {
            this.ResetLostTarget();
            return new(LostTargetSelectionAction.Disabled);
        }
        if (this.LostTargetId is not null && survivingLock is null)
        {
            if (this.lostTargetMissingSince == DateTime.MinValue)
                this.lostTargetMissingSince = now;
            if (now - this.lostTargetMissingSince < TimeSpan.FromSeconds(1))
                return new(LostTargetSelectionAction.Waiting);
            this.ResetLostTarget();
            return new(LostTargetSelectionAction.Disappeared);
        }

        IBattleNpc? target = survivingLock ?? nearbyEnemies
            .Select(candidate => (Target: candidate, Kind: GetLostKind(candidate)))
            .Where(candidate => candidate.Kind is { } kind
                && timeRemaining >= this.GetLostThresholdSeconds(kind))
            .OrderBy(candidate => Vector3.DistanceSquared(playerPosition, candidate.Target.Position))
            .Select(candidate => candidate.Target).FirstOrDefault();
        if (target is null)
            return new(LostTargetSelectionAction.None);
        if (this.LostTargetId is null)
        {
            this.LostTargetId = target.GameObjectId;
            this.LostKind = GetLostKind(target);
        }
        this.lostTargetMissingSince = DateTime.MinValue;
        return new(LostTargetSelectionAction.Target, target);
    }

    private int GetLostThresholdSeconds(LostPriorityTargetKind kind) => kind switch
    {
        LostPriorityTargetKind.LostGirl => configuration.LostGirlRemainingTimeThresholdSeconds,
        LostPriorityTargetKind.LostOne => configuration.LostOneRemainingTimeThresholdSeconds,
        _ => 0,
    };

    public static LostPriorityTargetKind? GetLostKind(IBattleNpc target) => GetLostKind(target.BaseId, target.NameId,
        target.BaseId == 0 && target.NameId == 0 ? target.Name.ToString() : string.Empty);

    public static LostPriorityTargetKind? GetLostKind(uint baseId, uint nameId, string name)
    {
        if (baseId == 7586 || nameId == 6737)
            return LostPriorityTargetKind.LostGirl;
        if (nameId == 6738)
            return LostPriorityTargetKind.LostOne;
        if (baseId != 0 || nameId != 0)
            return null;
        string normalized = string.Concat(name.Normalize(NormalizationForm.FormKC).Where(character => !char.IsWhiteSpace(character)));
        if (normalized.StartsWith("迷失少女", StringComparison.Ordinal))
            return LostPriorityTargetKind.LostGirl;
        return normalized.StartsWith("迷失者", StringComparison.Ordinal) ? LostPriorityTargetKind.LostOne : null;
    }

    // 破坏目标：沿用已有 DestroyObjectiveSession 的锁定和整批清场规则。
    public (DestroyDecision Decision, IBattleNpc? Target, int EngagedCount) ChooseDestroy(
        FateTargetSelector scanner, FateSnapshot fate, IPlayerCharacter player, IReadOnlyList<string> objectiveNames)
    {
        IBattleNpc[] objectives = scanner.FindNamedPriorityTargets(fate.FateId, player.Position, objectiveNames).ToArray();
        // Classification/targetability changes do not replace a living objective lock.
        if (this.Destroy.TargetId is { } objectiveId && scanner.FindLiveTarget(objectiveId) is { } lockedObjective
            && FateTargetSelector.BelongsToFate(lockedObjective, fate.FateId) && !FateTargetSelector.IsFriendly(lockedObjective))
            objectives = objectives.Append(lockedObjective).DistinctBy(target => target.GameObjectId).ToArray();
        IBattleNpc[] engaged = scanner.FindEngagedTargets(player)
            .Concat(scanner.FindAllFateObjects(fate.FateId, player.Position)
                .Where(target => this.IsInBatch(target.GameObjectId) && scanner.IsAttackableCleanupTarget(target)))
            .DistinctBy(target => target.GameObjectId).ToArray();
        if (this.Destroy.ClearingAggro && this.Destroy.CleanupTargetId is { } cleanupId
            && scanner.FindLiveTarget(cleanupId) is { } lockedCleanup && scanner.IsAttackableCleanupTarget(lockedCleanup))
            engaged = engaged.Append(lockedCleanup).DistinctBy(target => target.GameObjectId).ToArray();
        DestroyDecision decision = this.ChooseDestroy(player.Position, objectives, engaged);
        IBattleNpc? target = decision.Action == DestroyAction.Fallback ? null
            : (decision.Action == DestroyAction.Destroy ? objectives : engaged).First(target => target.GameObjectId == decision.TargetId);
        return (decision, target, engaged.Length);
    }

    public DestroyDecision ChooseDestroy(Vector3 playerPosition, IReadOnlyList<IBattleNpc> objectives,
        IReadOnlyList<IBattleNpc> engaged) => this.Destroy.Choose(
            objectives.Select(target => ToCandidate(target, playerPosition)).ToArray(),
            engaged.Select(target => ToCandidate(target, playerPosition)).ToArray());

    // Boss 与清场：只返回选择结果，实际切换目标仍由控制器执行。
    public IBattleNpc? ChooseBoss(FateTargetSelector scanner, ushort fateId, Vector3 playerPosition,
        out IReadOnlyList<IBattleNpc> candidates)
    {
        candidates = scanner.FindBossTargets(fateId, playerPosition);
        IBattleNpc? locked = this.BossTargetId is { } id ? scanner.FindLiveTarget(id) : null;
        if (locked is not null && (!FateTargetSelector.BelongsToFate(locked, fateId) || FateTargetSelector.IsFriendly(locked)))
            locked = null;
        // The scanner orders initial candidates by MaxHp then distance. The controller
        // commits this choice only after its emergency-cleanup branch has been checked.
        return ChooseBoss(locked, candidates);
    }

    public static IBattleNpc? ChooseBoss(IBattleNpc? survivingLock, IReadOnlyList<IBattleNpc> orderedCandidates) =>
        survivingLock ?? orderedCandidates.FirstOrDefault();

    public IBattleNpc? ChooseBossCleanup(Vector3 playerPosition, ulong playerId, IReadOnlyList<IBattleNpc> candidates) =>
        FindLock(this.BossCleanupTargetId, candidates) ?? candidates
            .OrderByDescending(target => target.TargetObjectId == playerId).ThenBy(target => target.CurrentHp)
            .ThenBy(target => Vector3.DistanceSquared(playerPosition, target.Position)).FirstOrDefault();

    public IBattleNpc? ChooseCleanup(IReadOnlyList<IBattleNpc> candidates) =>
        FindLock(this.CleanupTargetId, candidates) ?? candidates.FirstOrDefault();

    private static IBattleNpc? FindLock(ulong? id, IReadOnlyList<IBattleNpc> candidates) =>
        id is { } locked ? candidates.FirstOrDefault(target => target.GameObjectId == locked) : null;
    private static CombatCandidate ToCandidate(IBattleNpc target, Vector3 playerPosition) =>
        new(target.GameObjectId, target.CurrentHp, Vector3.DistanceSquared(playerPosition, target.Position));
}
