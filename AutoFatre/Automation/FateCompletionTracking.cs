using Dalamud.Game.ClientState.Fates;

namespace AutoFatre;

public readonly record struct FateOccurrence(uint Territory, uint Instance, ushort FateId, int StartTimeEpoch);

/// <summary>One run's two independent counters. State notification follows AutoFate; packets do not drive automation.</summary>
public sealed class FateCompletionTracking
{
    public sealed class Entry(FateOccurrence occurrence, FateSnapshot snapshot)
    {
        public FateOccurrence Occurrence { get; } = occurrence;
        public FateSnapshot Snapshot { get; internal set; } = snapshot;
        public int Scope { get; internal set; }
        public bool Participated { get; internal set; }
        public bool CompletionNotified { get; internal set; }
        public bool RewardObserved { get; internal set; }
        public FateRewardObservation? Reward { get; internal set; }
    }

    private readonly Dictionary<FateOccurrence, Entry> entries = [];
    private readonly Dictionary<int, (int State, int Reward)> scopeCounts = [];
    private FateOccurrence? current;
    private FateOccurrence? last;

    public long Session { get; private set; }
    public int Scope { get; private set; }
    public bool Tracking { get; private set; }
    public int StateTotal { get; private set; }
    public int RewardTotal { get; private set; }

    public void Start()
    {
        this.Session++;
        this.Scope = 1;
        this.Tracking = true;
        this.StateTotal = this.RewardTotal = 0;
        this.entries.Clear();
        this.scopeCounts.Clear();
        this.current = this.last = null;
    }

    public void Stop() => this.Tracking = false;
    public void AdvanceScope() => this.Scope++;
    public (int State, int Reward) Counts(int scope) => this.scopeCounts.GetValueOrDefault(scope);

    public static bool IsComplete(FateSnapshot fate) => fate.State != FateState.Failed
        && (fate.State is FateState.Ending or FateState.Ended || fate.Progress >= 100);

    public bool WasNotified(uint territory, uint instance, FateSnapshot fate) =>
        this.entries.TryGetValue(new(territory, instance, fate.FateId, fate.StartTimeEpoch), out Entry? entry)
        && entry.CompletionNotified;

    public IReadOnlyList<Entry> Observe(IReadOnlyList<FateSnapshot> fates, uint territory, uint instance, ushort currentId)
    {
        if (!this.Tracking)
            return [];

        FateSnapshot? nativeCurrent = fates.FirstOrDefault(fate => fate.FateId == currentId);
        FateOccurrence? next = nativeCurrent is null ? null : new(territory, instance, currentId, nativeCurrent.StartTimeEpoch);
        if (next != this.current)
        {
            if (this.current is not null)
                this.last = this.current;
            this.current = next;
        }

        List<Entry> completed = [];
        foreach (FateSnapshot fate in fates)
        {
            FateOccurrence occurrence = new(territory, instance, fate.FateId, fate.StartTimeEpoch);
            bool existed = this.entries.TryGetValue(occurrence, out Entry? entry);
            entry ??= new Entry(occurrence, fate);
            bool wasComplete = IsComplete(entry.Snapshot);
            // Never treat arriving at an already completed event as participation.
            if (occurrence == this.current && !entry.Participated && !wasComplete && !IsComplete(fate)
                && fate.State == FateState.Running)
            {
                entry.Participated = true;
                entry.Scope = this.Scope;
            }
            entry.Snapshot = fate;
            this.entries[occurrence] = entry;
            if (existed && !wasComplete && IsComplete(fate) && entry.Participated && !entry.CompletionNotified
                && (occurrence == this.current || occurrence == this.last))
            {
                entry.CompletionNotified = true;
                this.StateTotal++;
                var counts = this.Counts(entry.Scope);
                this.scopeCounts[entry.Scope] = (counts.State + 1, counts.Reward);
                completed.Add(entry);
            }
        }
        return completed;
    }

    public (bool Accepted, int Scope, Entry? Entry) ObserveReward(FateRewardObservation reward)
    {
        if (reward.Session == 0 || reward.Session != this.Session || reward.FateId == 0)
            return (false, reward.Scope, null);

        // The packet itself has no start time/instance. Prefer a recent participation on the
        // capture map; otherwise retain the old step for a delayed reward after a map switch.
        Entry? entry = this.entries.Values
            .Where(e => e.Occurrence.FateId == reward.FateId && e.Participated && !e.RewardObserved)
            .OrderByDescending(e => e.Occurrence.Territory == reward.Territory && e.Occurrence.Instance == reward.Instance)
            .ThenByDescending(e => e.Occurrence.StartTimeEpoch).ThenByDescending(e => e.Scope)
            .FirstOrDefault();
        if (entry is null && !reward.AutomationActive)
            return (false, reward.Scope, null);

        int scope = entry?.Scope ?? reward.Scope;
        if (entry is not null)
        {
            entry.RewardObserved = true;
            entry.Reward = reward;
        }
        // Deliberately count actual successful handler invocations independently. Requiring a
        // state notification (or using it to suppress packets) would hide the very mismatches we need.
        if (reward.Success)
        {
            this.RewardTotal++;
            var counts = this.Counts(scope);
            this.scopeCounts[scope] = (counts.State, counts.Reward + 1);
        }
        return (true, scope, entry);
    }
}
