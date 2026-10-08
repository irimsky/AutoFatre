namespace AutoFatre;

public enum GemstoneExchangeQuantityMode { BagTarget, SpendRemaining }

public sealed class GemstoneExchangeRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public bool Enabled { get; set; } = true;
    public uint ItemId { get; set; }
    public uint NpcId { get; set; }
    public uint ShopId { get; set; }
    public uint TerritoryId { get; set; }
    public GemstoneExchangeQuantityMode QuantityMode { get; set; }
    public int TargetQuantity { get; set; } = 1;
    public bool SkipIfUsed { get; set; }

    public GemstoneExchangeRule Copy() => (GemstoneExchangeRule)this.MemberwiseClone();
}

public sealed class GemstoneExchangeSettings
{
    public bool Enabled { get; set; }
    public int Threshold { get; set; } = 1400;
    public int Reserve { get; set; }
    // Order is priority. Never sort by vendor or maintain a separate priority number.
    public List<GemstoneExchangeRule> Rules { get; set; } = [];

    public void Normalize()
    {
        this.Threshold = Math.Clamp(this.Threshold, 1, 99999);
        this.Reserve = Math.Clamp(this.Reserve, 0, this.Threshold - 1);
        this.Rules ??= [];
        this.Rules.RemoveAll(rule => rule is null);
        HashSet<string> ids = [];
        foreach (GemstoneExchangeRule rule in this.Rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || !ids.Add(rule.Id))
            {
                rule.Id = Guid.NewGuid().ToString("N");
                ids.Add(rule.Id);
            }
            rule.TargetQuantity = Math.Clamp(rule.TargetQuantity, 1, 99999);
            if (!Enum.IsDefined(rule.QuantityMode)) rule.QuantityMode = GemstoneExchangeQuantityMode.BagTarget;
        }
    }

    public GemstoneExchangeSettings Copy() => new()
    {
        Enabled = this.Enabled, Threshold = this.Threshold, Reserve = this.Reserve,
        Rules = (this.Rules ?? []).Where(rule => rule is not null).Select(rule => rule.Copy()).ToList(),
    };
}
