namespace AutoFatre;

public readonly record struct CombatCandidate(ulong Id, uint Hp, float DistanceSquared);
public enum DestroyAction { Fallback, Destroy, Cleanup }
public readonly record struct DestroyDecision(DestroyAction Action, ulong? TargetId);

/// <summary>Locks each objective/cleanup victim and drains a full batch before resuming objectives.</summary>
public sealed class DestroyObjectiveSession
{
    public ulong? TargetId { get; private set; }
    public bool ClearingAggro { get; private set; }
    private ulong? cleanupTargetId;
    public ulong? CleanupTargetId => this.cleanupTargetId;

    public DestroyDecision Choose(IReadOnlyList<CombatCandidate> objectives, IReadOnlyList<CombatCandidate> engaged)
    {
        if (!this.ClearingAggro && this.TargetId is { } locked && objectives.Any(t => t.Id == locked))
            return new(DestroyAction.Destroy, locked);

        bool finishedObjective = this.TargetId is not null;
        this.TargetId = null;
        if (finishedObjective && engaged.Count >= 5)
            this.ClearingAggro = true;

        if (this.ClearingAggro)
        {
            this.cleanupTargetId = ChooseLocked(this.cleanupTargetId, engaged, lowestHp: true);
            if (this.cleanupTargetId is { } cleanup)
                return new(DestroyAction.Cleanup, cleanup);
            this.ClearingAggro = false;
        }

        this.TargetId = ChooseLocked(null, objectives, lowestHp: false);
        return this.TargetId is { } target
            ? new(DestroyAction.Destroy, target)
            : new(DestroyAction.Fallback, null);
    }

    public static ulong? ChooseLocked(ulong? locked, IReadOnlyList<CombatCandidate> candidates, bool lowestHp)
    {
        if (locked is { } id && candidates.Any(t => t.Id == id))
            return id;
        IEnumerable<CombatCandidate> sorted = lowestHp
            ? candidates.OrderBy(t => t.Hp).ThenBy(t => t.DistanceSquared)
            : candidates.OrderBy(t => t.DistanceSquared);
        return sorted.Select(t => (ulong?)t.Id).FirstOrDefault();
    }

    public void Reset() { this.TargetId = this.cleanupTargetId = null; this.ClearingAggro = false; }
}
