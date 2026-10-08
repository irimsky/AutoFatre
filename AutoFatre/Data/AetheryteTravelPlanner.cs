using System.Numerics;
using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Plugin.Services;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AutoFatre;

/// <summary>
/// Finds an unlocked main aetheryte which makes travelling to a FATE worthwhile.
/// Positions are recovered from the same MapMarker data used by Questionable rather than
/// maintaining a second hand-written list of coordinates.
/// </summary>
public sealed class AetheryteTravelPlanner
{
    public const float TeleportCostDistance = 110f;

    private readonly IDataManager dataManager;
    private readonly IAetheryteList aetheryteList;
    private readonly Lazy<IReadOnlyDictionary<uint, AetherytePosition>> positions;

    public AetheryteTravelPlanner(IDataManager dataManager, IAetheryteList aetheryteList)
    {
        this.dataManager = dataManager;
        this.aetheryteList = aetheryteList;
        this.positions = new Lazy<IReadOnlyDictionary<uint, AetherytePosition>>(
            this.BuildAetherytePositions);
    }

    public bool TryFindBest(
        uint territoryId,
        Vector3 currentPosition,
        Vector3 targetPosition,
        out AetheryteTravelPlan plan)
    {
        plan = default!;
        if (territoryId == 0
            || !IsValidPosition(currentPosition)
            || !IsValidPosition(targetPosition))
            return false;

        IEnumerable<AetheryteTravelPlan> candidates = this.FindCandidates(territoryId, targetPosition);

        plan = candidates
            .OrderBy(candidate => candidate.DistanceToTarget)
            .ThenBy(candidate => candidate.AetheryteId)
            .FirstOrDefault()!;
        if (plan is null)
            return false;

        if (!ShouldTeleport(currentPosition, targetPosition, plan.Position))
        {
            plan = default!;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Finds the nearest unlocked main aetheryte to a destination. Unlike <see cref="TryFindBest"/>,
    /// this method deliberately does not compare the route against the current position: callers
    /// use it when a cross-territory teleport has already been chosen and only need the best
    /// arrival crystal for the destination NPC or waypoint.
    /// </summary>
    public bool TryFindNearest(
        uint territoryId,
        Vector3 targetPosition,
        out AetheryteTravelPlan plan)
    {
        plan = this.FindCandidates(territoryId, targetPosition)
            .OrderBy(candidate => candidate.DistanceToTarget)
            .ThenBy(candidate => candidate.AetheryteId)
            .FirstOrDefault()!;
        return plan is not null;
    }

    public static bool ShouldTeleport(
        Vector3 currentPosition,
        Vector3 targetPosition,
        Vector3 aetherytePosition,
        float teleportCostDistance = TeleportCostDistance) =>
        HorizontalDistance(currentPosition, targetPosition)
        > teleportCostDistance + HorizontalDistance(aetherytePosition, targetPosition);

    public static float HorizontalDistance(Vector3 left, Vector3 right) =>
        Vector2.Distance(new Vector2(left.X, left.Z), new Vector2(right.X, right.Z));

    private AetheryteTravelPlan? CreatePlan(
        IAetheryteEntry entry,
        uint territoryId,
        Vector3 targetPosition)
    {
        if (!this.positions.Value.TryGetValue(entry.AetheryteId, out AetherytePosition? position)
            || position is null
            || !position.IsMain
            || position.TerritoryId != territoryId)
            return null;

        return new AetheryteTravelPlan(
            entry.AetheryteId,
            entry.SubIndex,
            territoryId,
            new Vector3(position.Position.X, 0, position.Position.Y),
            Vector2.Distance(position.Position, new Vector2(targetPosition.X, targetPosition.Z)));
    }

    private IEnumerable<AetheryteTravelPlan> FindCandidates(uint territoryId, Vector3 targetPosition)
    {
        if (territoryId == 0 || !IsValidPosition(targetPosition))
            return [];

        return this.aetheryteList
            .Where(entry => entry.TerritoryId == territoryId
                            && !entry.IsSharedHouse
                            && !entry.IsApartment)
            .GroupBy(entry => entry.AetheryteId)
            .Select(group => group
                .OrderBy(entry => entry.SubIndex != 0)
                .ThenBy(entry => entry.GilCost)
                .First())
            .Select(entry => this.CreatePlan(entry, territoryId, targetPosition))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!);
    }

    private IReadOnlyDictionary<uint, AetherytePosition> BuildAetherytePositions()
    {
        try
        {
            ExcelSheet<Aetheryte>? aetherytes = this.dataManager.GetExcelSheet<Aetheryte>();
            ExcelSheet<Map>? maps = this.dataManager.GetExcelSheet<Map>();
            SubrowExcelSheet<MapMarker>? markers = this.dataManager.GetSubrowExcelSheet<MapMarker>();
            if (aetherytes is null || maps is null || markers is null)
                return new Dictionary<uint, AetherytePosition>();

            Dictionary<uint, uint> aetheryteByAethernetName = [];
            foreach (Aetheryte aetheryte in aetherytes)
            {
                if (aetheryte.AethernetName.RowId != 0)
                    aetheryteByAethernetName[aetheryte.AethernetName.RowId] = aetheryte.RowId;
            }

            Dictionary<uint, AetherytePosition> positions = [];
            foreach (Map map in maps)
            {
                if (map.MapMarkerRange == 0 || map.SizeFactor == 0)
                    continue;

                if (!markers.TryGetRow(map.MapMarkerRange, out SubrowCollection<MapMarker> group))
                    continue;

                foreach (MapMarker marker in group)
                {
                    uint? aetheryteId = marker.DataType switch
                    {
                        3 => marker.DataKey.RowId,
                        4 when aetheryteByAethernetName.TryGetValue(marker.DataKey.RowId, out uint mappedId) => mappedId,
                        _ => null,
                    };
                    if (aetheryteId is not { } resolvedId || resolvedId == 0)
                        continue;

                    Aetheryte? aetheryte = aetherytes.GetRowOrDefault(resolvedId);
                    if (aetheryte is not { Territory.RowId: > 0 })
                        continue;

                    float scale = map.SizeFactor / 100f;
                    Vector2 worldPosition = new(
                        (marker.X - 1024f) / scale - map.OffsetX,
                        (marker.Y - 1024f) / scale - map.OffsetY);
                    bool preferred = map.TerritoryType.RowId != 0
                                     && map.TerritoryType.RowId == aetheryte.Value.Territory.RowId;

                    if (preferred || !positions.ContainsKey(resolvedId))
                    {
                        positions[resolvedId] = new AetherytePosition(
                            aetheryte.Value.Territory.RowId,
                            aetheryte.Value.IsAetheryte,
                            worldPosition);
                    }
                }
            }

            return positions;
        }
        catch
        {
            // A missing or changing Lumina sheet must disable the optimization, not the plugin.
            return new Dictionary<uint, AetherytePosition>();
        }
    }

    private static bool IsValidPosition(Vector3 position) =>
        position != Vector3.Zero
        && float.IsFinite(position.X)
        && float.IsFinite(position.Y)
        && float.IsFinite(position.Z);

    private sealed record AetherytePosition(uint TerritoryId, bool IsMain, Vector2 Position);
}

public sealed record AetheryteTravelPlan(
    uint AetheryteId,
    byte SubIndex,
    uint TerritoryId,
    Vector3 Position,
    float DistanceToTarget);
