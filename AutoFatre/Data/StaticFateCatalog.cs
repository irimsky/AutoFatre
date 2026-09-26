using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoFatre;

/// <summary>
/// Read-only, build-time FATE metadata. The source catalog is embedded in the
/// plugin assembly; game-time automation must never fetch the Wiki.
/// </summary>
public sealed class StaticFateCatalog
{
    private const string ResourceName = "AutoFatre.StaticFateCatalog.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private IReadOnlyDictionary<ushort, StaticFateCatalogEntry> byFateId =
        new Dictionary<ushort, StaticFateCatalogEntry>();
    private IReadOnlyDictionary<string, IReadOnlyList<StaticFateCatalogEntry>> byMapName =
        new Dictionary<string, IReadOnlyList<StaticFateCatalogEntry>>(StringComparer.Ordinal);
    private IReadOnlyDictionary<int, IReadOnlyList<ushort>> collectionMembers =
        new Dictionary<int, IReadOnlyList<ushort>>();
    private IReadOnlyList<StaticFateCatalogEntry> entries = [];

    public bool IsLoaded { get; private set; }
    public string? LoadError { get; private set; }
    public bool IsAvailable => this.IsLoaded && this.LoadError is null;
    public int FateCount => this.byFateId.Count;
    public int CollectionCount => this.collectionMembers.Count;
    public IReadOnlyList<StaticFateCatalogEntry> Entries => this.entries;
    public IReadOnlyList<string> MapNames => this.byMapName.Keys.Order(StringComparer.Ordinal).ToArray();

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (this.IsLoaded)
            return;

        try
        {
            CatalogIndexes indexes = await Task.Run(() => ReadIndexes(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            this.byFateId = indexes.ByFateId;
            this.byMapName = indexes.ByMapName;
            this.collectionMembers = indexes.CollectionMembers;
            this.entries = indexes.ByFateId.Values.OrderBy(entry => entry.FateId).ToArray();
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

    public bool TryGet(ushort fateId, out StaticFateCatalogEntry entry) =>
        this.byFateId.TryGetValue(fateId, out entry!);

    public IReadOnlyList<StaticFateCatalogEntry> GetByMapName(string mapName) =>
        this.byMapName.GetValueOrDefault(mapName, []);

    public IReadOnlyList<ushort> GetCollectionMembers(int collectionId) =>
        this.collectionMembers.GetValueOrDefault(collectionId, []);

    private static CatalogIndexes ReadIndexes(CancellationToken cancellationToken)
    {
        Assembly assembly = typeof(StaticFateCatalog).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException($"未找到嵌入 FATE 目录资源 {ResourceName}。");
        EmbeddedCatalog document = JsonSerializer.Deserialize<EmbeddedCatalog>(stream, JsonOptions)
            ?? throw new InvalidDataException("嵌入 FATE 目录 JSON 为空或格式无效。");

        Dictionary<ushort, StaticFateCatalogEntry> byId = [];
        Dictionary<string, List<StaticFateCatalogEntry>> byMap = new(StringComparer.Ordinal);
        foreach (EmbeddedFate source in document.Fates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.FateId is not { } rawFateId)
                continue; // Audit-only unresolved Wiki entry.
            if (rawFateId > ushort.MaxValue)
                throw new InvalidDataException($"静态目录含有超出运行时 FateId 范围的 ID：{rawFateId}。");

            ushort fateId = (ushort)rawFateId;
            StaticFateCatalogEntry entry = new(
                fateId,
                source.TaskName,
                source.ClientTaskName ?? source.TaskName,
                source.MapName,
                source.Category,
                source.Trigger,
                new StaticFateTargets(
                    source.Targets.Destroy,
                    source.Targets.Protect,
                    source.Targets.Collect,
                    source.Targets.Escort,
                    source.Targets.Defeat),
                source.CollectionIds);
            if (!byId.TryAdd(fateId, entry))
                throw new InvalidDataException($"静态目录含有重复 FateId：{fateId}。");
            if (!byMap.TryGetValue(entry.MapName, out List<StaticFateCatalogEntry>? entries))
            {
                entries = [];
                byMap.Add(entry.MapName, entries);
            }
            entries.Add(entry);
        }

        Dictionary<int, IReadOnlyList<ushort>> collections = [];
        foreach (EmbeddedCollection collection in document.Collections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ushort[] memberIds = collection.Members
                .SelectMany(member => member.FateIds)
                .Where(id => id <= ushort.MaxValue && byId.ContainsKey((ushort)id))
                .Select(id => (ushort)id)
                .Distinct()
                .Order()
                .ToArray();
            if (!collections.TryAdd(collection.CollectionId, memberIds))
                throw new InvalidDataException($"静态目录含有重复合集编号：{collection.CollectionId}。");
        }

        return new CatalogIndexes(
            byId,
            byMap.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<StaticFateCatalogEntry>)pair.Value.ToArray(), StringComparer.Ordinal),
            collections);
    }

    private sealed record CatalogIndexes(
        IReadOnlyDictionary<ushort, StaticFateCatalogEntry> ByFateId,
        IReadOnlyDictionary<string, IReadOnlyList<StaticFateCatalogEntry>> ByMapName,
        IReadOnlyDictionary<int, IReadOnlyList<ushort>> CollectionMembers);

    private sealed class EmbeddedCatalog
    {
        [JsonPropertyName("FATE")]
        public List<EmbeddedFate> Fates { get; init; } = [];

        [JsonPropertyName("合集")]
        public List<EmbeddedCollection> Collections { get; init; } = [];
    }

    private sealed class EmbeddedFate
    {
        [JsonPropertyName("FateId")]
        public uint? FateId { get; init; }
        [JsonPropertyName("任务名")]
        public string TaskName { get; init; } = "";
        [JsonPropertyName("客户端任务名")]
        public string? ClientTaskName { get; init; }
        [JsonPropertyName("地图")]
        public string MapName { get; init; } = "";
        [JsonPropertyName("任务种类")]
        public string Category { get; init; } = "";
        [JsonPropertyName("触发条件")]
        public string Trigger { get; init; } = "";
        [JsonPropertyName("目标")]
        public EmbeddedTargets Targets { get; init; } = new();
        [JsonPropertyName("合集编号")]
        public int[] CollectionIds { get; init; } = [];
    }

    private sealed class EmbeddedTargets
    {
        [JsonPropertyName("破坏")]
        public string[] Destroy { get; init; } = [];
        [JsonPropertyName("守护/保护")]
        public string[] Protect { get; init; } = [];
        [JsonPropertyName("收集")]
        public string[] Collect { get; init; } = [];
        [JsonPropertyName("护送")]
        public string[] Escort { get; init; } = [];
        [JsonPropertyName("讨伐")]
        public string[] Defeat { get; init; } = [];
    }

    private sealed class EmbeddedCollection
    {
        [JsonPropertyName("合集编号")]
        public int CollectionId { get; init; }
        [JsonPropertyName("成员")]
        public List<EmbeddedCollectionMember> Members { get; init; } = [];
    }

    private sealed class EmbeddedCollectionMember
    {
        [JsonPropertyName("FateIds")]
        public uint[] FateIds { get; init; } = [];
    }
}

public sealed record StaticFateCatalogEntry(
    ushort FateId,
    string WikiTaskName,
    string ClientTaskName,
    string MapName,
    string Category,
    string Trigger,
    StaticFateTargets Targets,
    IReadOnlyList<int> CollectionIds);

public sealed record StaticFateTargets(
    IReadOnlyList<string> Destroy,
    IReadOnlyList<string> Protect,
    IReadOnlyList<string> Collect,
    IReadOnlyList<string> Escort,
    IReadOnlyList<string> Defeat);
