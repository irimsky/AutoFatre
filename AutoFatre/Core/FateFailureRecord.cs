namespace AutoFatre;

/// <summary>Persistent record of a FATE that could not be completed or reached.</summary>
public sealed class FateFailureRecord
{
    public DateTime OccurredAtUtc { get; set; }
    public ushort FateId { get; set; }
    public string FateName { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
    public string FailureKind { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
