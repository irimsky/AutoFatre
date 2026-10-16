namespace AutoFatre;

public enum AutoRepairMethod { SelfRepair, NearbyNpc, SelfRepairThenNearbyNpc }
public enum AutoRepairFailurePolicy { Pause, Continue }

public sealed class AutoRepairSettings
{
    public bool Enabled { get; set; }
    public int ThresholdPercent { get; set; } = 30;
    public AutoRepairMethod Method { get; set; } = AutoRepairMethod.SelfRepair;
    public AutoRepairFailurePolicy FailurePolicy { get; set; } = AutoRepairFailurePolicy.Pause;
    public int TimeoutSeconds { get; set; } = 30;
    public int RetryIntervalSeconds { get; set; } = 60;
    public int MaxNpcRepairCost { get; set; } = 10000;

    public void Normalize()
    {
        this.ThresholdPercent = Math.Clamp(this.ThresholdPercent, 1, 99);
        this.TimeoutSeconds = Math.Clamp(this.TimeoutSeconds, 10, 120);
        this.RetryIntervalSeconds = Math.Clamp(this.RetryIntervalSeconds, 10, 600);
        this.MaxNpcRepairCost = Math.Clamp(this.MaxNpcRepairCost, 1, 100000);
        if (!Enum.IsDefined(this.Method)) this.Method = AutoRepairMethod.SelfRepair;
        if (!Enum.IsDefined(this.FailurePolicy)) this.FailurePolicy = AutoRepairFailurePolicy.Pause;
    }

    public AutoRepairSettings Copy() => (AutoRepairSettings)this.MemberwiseClone();
}
