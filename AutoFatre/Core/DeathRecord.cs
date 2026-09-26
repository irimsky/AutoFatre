namespace AutoFatre;

/// <summary>Persistent record of a death observed while participating in a FATE.</summary>
public sealed class DeathRecord
{
    public DateTime OccurredAtUtc { get; set; }
    public ushort FateId { get; set; }
    public string FateName { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
}
