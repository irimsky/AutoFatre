using System.Numerics;

namespace AutoFatre;

/// <summary>Tracks changes from copied game data; retains no native object references.</summary>
public sealed class FateAlertTracking
{
    private FateOccurrence? occurrence;
    private HashSet<ulong> players = [];
    private bool gemstonesReachedThreshold;
    private uint gemstonesThreshold;

    public bool ObservePlayers(FateOccurrence? fate, IEnumerable<ulong> currentPlayers)
    {
        HashSet<ulong> current = currentPlayers.Where(id => id is not 0 and not 0xE0000000).ToHashSet();
        bool entered = fate is not null && (this.occurrence != fate
            ? current.Count > 0
            : current.Except(this.players).Any());
        this.occurrence = fate;
        this.players = fate is null ? [] : current;
        return entered;
    }

    public bool ObserveGemstones(uint count, uint maximum)
        => this.ObserveGemstones(count, maximum, maximum);

    public bool ObserveGemstones(uint count, uint maximum, uint threshold)
    {
        // An unavailable currency snapshot must not rearm a previously delivered alert.
        if (maximum == 0 || threshold == 0)
            return false;
        if (threshold != this.gemstonesThreshold)
        {
            this.gemstonesThreshold = threshold;
            this.gemstonesReachedThreshold = false;
        }

        bool reached = count >= threshold;
        bool crossed = reached && !this.gemstonesReachedThreshold;
        this.gemstonesReachedThreshold = reached;
        return crossed;
    }

    public void ResetPlayers()
    {
        this.occurrence = null;
        this.players.Clear();
    }

    public void Reset()
    {
        this.ResetPlayers();
        this.gemstonesReachedThreshold = false;
        this.gemstonesThreshold = 0;
    }

    public static bool IsInRange(Vector3 position, Vector3 center, float radius) =>
        float.IsFinite(radius) && radius > 0
        && float.IsFinite(position.X) && float.IsFinite(position.Z)
        && float.IsFinite(center.X) && float.IsFinite(center.Z)
        && Vector2.DistanceSquared(new(position.X, position.Z), new(center.X, center.Z)) <= radius * radius;
}
