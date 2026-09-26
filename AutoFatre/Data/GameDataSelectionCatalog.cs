using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AutoFatre;

/// <summary>Cached game-language names used only by configuration selectors.</summary>
public sealed class GameDataSelectionCatalog(
    IDataManager dataManager,
    IAetheryteList aetheryteList,
    StaticFateCatalog fateCatalog,
    StaticFateTerritoryCatalog fateTerritoryCatalog)
{
    private IReadOnlyList<NamedGameRow> territories = [];
    private IReadOnlyList<NamedGameRow> items = [];
    private IReadOnlyList<NamedGameRow> companionItems = [];
    private IReadOnlyList<FateSelectionEntry> fates = [];
    private IReadOnlyList<FateSelectionEntry> selectableFates = [];
    private IReadOnlyDictionary<uint, string> territoryNames = new Dictionary<uint, string>();
    private IReadOnlyDictionary<string, uint> canonicalTerritoryIds =
        new Dictionary<string, uint>(StringComparer.Ordinal);
    private IReadOnlyDictionary<uint, string> itemNames = new Dictionary<uint, string>();
    private IReadOnlyDictionary<uint, string> aetheryteNames = new Dictionary<uint, string>();
    private IReadOnlyDictionary<ushort, FateSelectionEntry> fatesById =
        new Dictionary<ushort, FateSelectionEntry>();

    public bool IsLoaded { get; private set; }
    public string? LoadError { get; private set; }
    public IReadOnlyList<NamedGameRow> Territories => this.territories;
    public IReadOnlyList<NamedGameRow> Items => this.items;
    public IReadOnlyList<NamedGameRow> CompanionItems => this.companionItems;
    /// <summary>FATEs which can be assigned to a canonical open-world territory.</summary>
    public IReadOnlyList<FateSelectionEntry> Fates => this.selectableFates;
    public int ClientFateCount => this.fates.Count;
    public int MappedFateCount => this.selectableFates.Count;

    public void Load()
    {
        if (this.IsLoaded)
            return;

        try
        {
            TerritoryType[] territorySheet = dataManager.GetExcelSheet<TerritoryType>()
                .Where(row => row.RowId != 0)
                .ToArray();
            NamedGameRow[] territoryRows = territorySheet
                .Select(row => new NamedGameRow(
                    row.RowId,
                    row.PlaceName.ValueNullable?.Name.ToString()
                    ?? row.PlaceNameZone.ValueNullable?.Name.ToString()
                    ?? string.Empty))
                .Where(row => row.Name.Length != 0)
                .ToArray();
            this.territoryNames = territoryRows
                .GroupBy(row => row.Id)
                .ToDictionary(group => group.Key, group => group.First().Name);
            this.territories = territorySheet
                // PlaceName is reused by quest-battle and duty instances which share the
                // overworld map asset. FATE plans may target only the canonical field zone.
                .Where(row => row.TerritoryIntendedUse.RowId == 1
                              && row.ContentFinderCondition.RowId == 0
                              && row.QuestBattle.RowId == 0
                              && row.IsInUse)
                .Select(row => new NamedGameRow(
                    row.RowId,
                    row.PlaceName.ValueNullable?.Name.ToString()
                    ?? row.PlaceNameZone.ValueNullable?.Name.ToString()
                    ?? string.Empty))
                .Where(row => row.Name.Length != 0)
                .GroupBy(row => row.Name, StringComparer.Ordinal)
                .Select(group => group.OrderBy(row => row.Id).First())
                .OrderBy(row => row.Name, StringComparer.CurrentCulture)
                .ThenBy(row => row.Id)
                .ToArray();
            this.canonicalTerritoryIds = this.territories
                .GroupBy(row => row.Name, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(row => row.Id).First().Id,
                    StringComparer.Ordinal);

            this.fates = dataManager.GetExcelSheet<Fate>()
                .Where(row => row.RowId is > 0 and <= ushort.MaxValue
                              && !string.IsNullOrWhiteSpace(row.Name.ToString()))
                .Select(row =>
                {
                    ushort fateId = (ushort)row.RowId;
                    fateCatalog.TryGet(fateId, out StaticFateCatalogEntry? enrichment);
                    uint? territoryId = null;
                    uint? mapId = null;
                    string? mapName = null;
                    if (fateTerritoryCatalog.TryGet(fateId, out FateTerritoryEntry? clientLocation))
                    {
                        territoryId = clientLocation.TerritoryId;
                        mapId = clientLocation.MapId;
                        mapName = this.territoryNames.GetValueOrDefault(clientLocation.TerritoryId)
                                  ?? clientLocation.TerritoryName;
                    }
                    else if (enrichment?.MapName is { Length: > 0 } wikiMapName
                             && this.canonicalTerritoryIds.TryGetValue(wikiMapName, out uint wikiTerritoryId))
                    {
                        // Wiki is only a fallback for entries absent from the client LGB map.
                        territoryId = wikiTerritoryId;
                        mapName = this.territoryNames.GetValueOrDefault(wikiTerritoryId) ?? wikiMapName;
                    }

                    return new FateSelectionEntry(
                        fateId,
                        row.Name.ToString(),
                        row.Rule,
                        row.Icon,
                        territoryId,
                        mapId,
                        mapName,
                        enrichment);
                })
                .OrderBy(row => row.ClientName, StringComparer.CurrentCulture)
                .ThenBy(row => row.FateId)
                .ToArray();
            this.fatesById = this.fates.ToDictionary(row => row.FateId);
            this.selectableFates = this.fates.Where(row => row.TerritoryId is not null).ToArray();

            this.items = dataManager.GetExcelSheet<Item>()
                .Where(row => row.RowId != 0 && !string.IsNullOrWhiteSpace(row.Name.ToString()))
                .Select(row => new NamedGameRow(row.RowId, row.Name.ToString()))
                .OrderBy(row => row.Name, StringComparer.CurrentCulture)
                .ThenBy(row => row.Id)
                .ToArray();
            this.itemNames = this.items.ToDictionary(row => row.Id, row => row.Name);
            // Minion/companion consumables use ItemAction.Action = 853 (Companion). Keep the
            // selector item-based because that is the native action path used to summon a minion.
            this.companionItems = dataManager.GetExcelSheet<Item>()
                .Where(row => row.RowId != 0
                              && !string.IsNullOrWhiteSpace(row.Name.ToString())
                              && row.ItemAction.ValueNullable?.Action.RowId == 853)
                .Select(row => new NamedGameRow(row.RowId, row.Name.ToString()))
                .OrderBy(row => row.Name, StringComparer.CurrentCulture)
                .ThenBy(row => row.Id)
                .ToArray();

            this.aetheryteNames = dataManager.GetExcelSheet<Aetheryte>()
                .Where(row => row.RowId != 0)
                .Select(row => new NamedGameRow(
                    row.RowId,
                    row.PlaceName.ValueNullable?.Name.ToString()
                    ?? row.AethernetName.ValueNullable?.Name.ToString()
                    ?? string.Empty))
                .Where(row => row.Name.Length != 0)
                .GroupBy(row => row.Id)
                .ToDictionary(group => group.Key, group => group.First().Name);
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

    public string GetTerritoryName(uint territoryId) =>
        territoryId == 0 ? "当前地图" : this.territoryNames.GetValueOrDefault(territoryId, $"未知地图（{territoryId}）");

    public string? TryGetTerritoryName(uint territoryId) =>
        territoryId == 0 ? null : this.territoryNames.GetValueOrDefault(territoryId);

    public uint CanonicalizeTerritoryId(uint territoryId)
    {
        if (territoryId == 0 || this.territories.Any(row => row.Id == territoryId))
            return territoryId;

        return this.territoryNames.TryGetValue(territoryId, out string? mapName)
               && this.canonicalTerritoryIds.TryGetValue(mapName, out uint canonicalId)
            ? canonicalId
            : territoryId;
    }

    public string GetItemName(uint itemId) =>
        itemId == 0 ? "未选择物品" : this.itemNames.GetValueOrDefault(itemId, $"未知物品（{itemId}）");

    public string GetAetheryteName(uint aetheryteId) =>
        aetheryteId == 0 ? "自动选择最近的以太之光" : this.aetheryteNames.GetValueOrDefault(aetheryteId, $"未知以太之光（{aetheryteId}）");

    public string GetFateName(ushort? fateId)
    {
        if (fateId is null or 0)
            return "未选择 FATE";

        return this.fatesById.TryGetValue(fateId.Value, out FateSelectionEntry? entry)
            ? entry.ClientName
            : $"未知 FATE（{fateId}）";
    }

    public string GetFateDisplayName(ushort? fateId, bool includeMapName)
    {
        string fateName = this.GetFateName(fateId);
        if (!includeMapName || fateId is not { } id || !this.fatesById.TryGetValue(id, out FateSelectionEntry? entry))
            return fateName;

        return $"{entry.MapName ?? "地图未知"} | {fateName}";
    }

    public bool TryGetFate(ushort fateId, out FateSelectionEntry entry) =>
        this.fatesById.TryGetValue(fateId, out entry!);

    public bool TryGetTerritoryIdForFate(ushort fateId, out uint territoryId)
    {
        territoryId = 0;
        return this.fatesById.TryGetValue(fateId, out FateSelectionEntry? fate)
               && fate.TerritoryId is { } mappedTerritoryId
               && (territoryId = mappedTerritoryId) != 0;
    }

    public IReadOnlyList<NamedGameRow> GetUnlockedAetherytes(uint territoryId) => aetheryteList
        .Where(entry => territoryId == 0 || entry.TerritoryId == territoryId)
        .Where(entry => !entry.IsSharedHouse && !entry.IsApartment)
        .GroupBy(entry => entry.AetheryteId)
        .Select(group => group.First())
        .Select(entry => new NamedGameRow(entry.AetheryteId, this.GetAetheryteName(entry.AetheryteId)))
        .OrderBy(row => row.Name, StringComparer.CurrentCulture)
        .ThenBy(row => row.Id)
        .ToArray();

    public IReadOnlyList<FateSelectionEntry> GetFatesForTerritory(uint territoryId)
    {
        uint canonicalTerritory = this.CanonicalizeTerritoryId(territoryId);
        return this.fates.Where(fate => fate.TerritoryId == canonicalTerritory).ToArray();
    }
}

public sealed record NamedGameRow(uint Id, string Name);

/// <summary>
/// A client-authoritative FATE definition with optional build-time Wiki enrichment.
/// ClientName and FateId always come from the installed game's Fate sheet.
/// </summary>
public sealed record FateSelectionEntry(
    ushort FateId,
    string ClientName,
    byte Rule,
    uint IconId,
    uint? TerritoryId,
    uint? MapId,
    string? MapName,
    StaticFateCatalogEntry? Enrichment)
{
    public string TypeName => FateSnapshot.GetTypeName(this.Rule, this.IconId);
    public FateCombatProfile CombatProfile => FateCombatProfile.From(this.Rule, this.IconId);
    public bool IsAutomationSupported => this.CombatProfile.IsSupported;
}
