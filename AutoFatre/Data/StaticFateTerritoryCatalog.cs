using System.Reflection;
using System.Text.Json;

namespace AutoFatre;

/// <summary>
/// Build-time mapping from the current client's Fate.Location EventRange
/// instance IDs to canonical open-world TerritoryType rows. The generated
/// resource is independent from the optional Wiki enrichment catalog.
/// </summary>
public sealed class StaticFateTerritoryCatalog
{
    private const string ResourceName = "AutoFatre.StaticFateTerritories.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private IReadOnlyDictionary<ushort, FateTerritoryEntry> byFateId =
        new Dictionary<ushort, FateTerritoryEntry>();

    public bool IsLoaded { get; private set; }
    public string? LoadError { get; private set; }
    public bool IsAvailable => this.IsLoaded && this.LoadError is null;
    public int FateCount => this.byFateId.Count;
    public string? GameVersion { get; private set; }
    public string? SnapshotId { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (this.IsLoaded)
            return;

        try
        {
            FateTerritoryDocument document = await Task.Run(() => ReadDocument(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            this.byFateId = document.Fates.ToDictionary(entry => entry.FateId);
            this.GameVersion = document.Metadata.GameVersion;
            this.SnapshotId = document.Metadata.SnapshotId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            this.LoadError = exception.Message;
        }
        finally
        {
            this.IsLoaded = true;
        }
    }

    public bool TryGet(ushort fateId, out FateTerritoryEntry entry) =>
        this.byFateId.TryGetValue(fateId, out entry!);

    private static FateTerritoryDocument ReadDocument(CancellationToken cancellationToken)
    {
        Assembly assembly = typeof(StaticFateTerritoryCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"未找到嵌入 FATE 地图资源 {ResourceName}。");
        FateTerritoryDocument document = JsonSerializer.Deserialize<FateTerritoryDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException("嵌入 FATE 地图 JSON 为空或格式无效。");

        var seen = new HashSet<ushort>();
        foreach (FateTerritoryEntry entry in document.Fates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FateId == 0 || entry.TerritoryId == 0 || !seen.Add(entry.FateId))
                throw new InvalidDataException($"嵌入 FATE 地图目录含无效或重复条目：FATE {entry.FateId}。");
        }

        return document;
    }

    private sealed class FateTerritoryDocument
    {
        public FateTerritoryMetadata Metadata { get; init; } = new();
        public FateTerritoryEntry[] Fates { get; init; } = [];
    }

    private sealed class FateTerritoryMetadata
    {
        public string? GameVersion { get; init; }
        public string? SnapshotId { get; init; }
    }
}

public sealed record FateTerritoryEntry(
    ushort FateId,
    uint TerritoryId,
    uint MapId,
    string TerritoryName);
