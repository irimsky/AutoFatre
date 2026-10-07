namespace AutoFatre;

/// <summary>Counts only continuously observed empty-map time for one preset entry.</summary>
public sealed class NoFateStopTimer
{
    private (int Preset, uint Territory, uint Instance)? context;
    private DateTime? emptySince;

    public int ElapsedSeconds { get; private set; }

    public void Observe(DateTime now, int preset, uint territory, uint instance, bool canObserve, bool hasFates)
    {
        var nextContext = (preset, territory, instance);
        if (!canObserve || hasFates || this.context != nextContext)
            this.Reset();
        if (!canObserve || hasFates)
            return;

        this.context = nextContext;
        this.emptySince ??= now;
        this.ElapsedSeconds = (int)Math.Clamp((now - this.emptySince.Value).TotalSeconds, 0, 10);
    }

    public void Reset()
    {
        this.context = null;
        this.emptySince = null;
        this.ElapsedSeconds = 0;
    }
}
