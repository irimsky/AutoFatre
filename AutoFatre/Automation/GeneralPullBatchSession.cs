namespace AutoFatre;

/// <summary>Keeps a batch sealed until it drains after ordinary pulls become too distant.</summary>
public sealed class GeneralPullBatchSession
{
    public bool MustDrain { get; private set; }

    public bool TrySealForDistantTarget(int engagedCount, float nearestDistanceSquared, bool isProtectionThreat)
    {
        if (isProtectionThreat || engagedCount < 2 || nearestDistanceSquared < 25f * 25f)
            return false;

        this.MustDrain = true;
        return true;
    }

    public void Reset() => this.MustDrain = false;
}
