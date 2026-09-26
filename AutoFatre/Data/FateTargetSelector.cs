using System.Numerics;
using System.Text;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using BNpcNameSheet = Lumina.Excel.Sheets.BNpcName;
using StatusSheet = Lumina.Excel.Sheets.Status;

namespace AutoFatre;

public sealed record FateTargetDiagnostic(
    ulong GameObjectId,
    string Name,
    uint NameId,
    uint BaseId,
    uint LayoutId,
    uint CurrentHp,
    uint MaxHp,
    bool IsFriendly,
    byte NamePlateKind,
    bool IsAttackable,
    bool TargetsPlayer,
    ulong TargetObjectId,
    BattleNpcSubKind BattleNpcKind);

public sealed record FateObjectDiagnostic(
    ulong GameObjectId,
    ObjectKind ObjectKind,
    string Name,
    uint NameId,
    uint BaseId,
    uint LayoutId,
    ushort FateId,
    Vector3 Position,
    float Distance,
    bool IsTargetable,
    bool NearObjective);

/// <summary>
/// Finds battle NPCs that belong to a specific FATE.
///
/// The FATE ownership marker is not exposed by the public Dalamud IBattleNpc API;
/// BOCCHI reads it from the native BattleChara structure in the same way.
/// Callers must use the returned references promptly and re-query on later ticks.
/// </summary>
public sealed class FateTargetSelector
{
    private const uint InvalidGameObjectId = 0xE0000000;

    private static readonly uint[] KnownInvulnerabilityStatusIds =
    [
        151, 198, 469, 592, 1240, 1302, 1303, 1567,
        1936, 2413, 2654, 3012, 3039, 3052, 3054, 4175,
    ];

    private readonly IObjectTable objectTable;
    private readonly HashSet<uint> invulnerabilityStatusIds;
    private readonly IReadOnlyDictionary<string, uint[]> bnpcNameIdsByName;

    public FateTargetSelector(IObjectTable objectTable, IDataManager dataManager)
    {
        this.objectTable = objectTable;
        this.invulnerabilityStatusIds = LoadInvulnerabilityStatusIds(dataManager);
        this.bnpcNameIdsByName = LoadBNpcNameIds(dataManager);
    }

    private static IReadOnlyDictionary<string, uint[]> LoadBNpcNameIds(IDataManager dataManager)
    {
        try
        {
            return dataManager.GetExcelSheet<BNpcNameSheet>()
                .GroupBy(row => NormalizeObjectName(row.Singular.ToString()), StringComparer.Ordinal)
                .Where(group => group.Key.Length > 0)
                .ToDictionary(
                    group => group.Key,
                    group => group.Select(row => row.RowId).Distinct().ToArray(),
                    StringComparer.Ordinal);
        }
        catch
        {
            // Name matching remains available if the sheet is unavailable during startup.
            return new Dictionary<string, uint[]>(StringComparer.Ordinal);
        }
    }

    private static HashSet<uint> LoadInvulnerabilityStatusIds(IDataManager dataManager)
    {
        HashSet<uint> ids = KnownInvulnerabilityStatusIds.ToHashSet();
        try
        {
            var sheet = dataManager.GetExcelSheet<StatusSheet>();
            foreach (var row in sheet)
            {
                if (row.Icon == 215024)
                    ids.Add(row.RowId);
            }
        }
        catch
        {
            // Fixed IDs still cover common invulnerability states if the sheet is unavailable
            // during startup or a client data revision changes.
        }

        return ids;
    }
    // The static catalog keeps the Wiki wording, while the client can expose a different
    // localized name for the same objective. Keep these aliases explicit and narrowly scoped:
    // broad fuzzy matching could redirect combat to an unrelated FATE actor.
    private static readonly IReadOnlyDictionary<string, string[]> PriorityNameAliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["地灵族的采掘工具"] = ["地灵族的采矿工具"],
            ["地灵族的采矿工具"] = ["地灵族的采掘工具"],
        };

    public IReadOnlyList<IBattleNpc> FindAllTargets(ushort fateId, Vector3 playerPosition) =>
        this.FindAllFateCombatants(fateId, playerPosition)
            .Where(IsAttackableEnemy)
            .ToArray();

    /// <summary>Returns every live BattleNpc associated with a FATE, including protection
    /// targets that may be friendly despite a combat-style nameplate. Callers must re-query this list each
    /// framework tick.</summary>
    public IReadOnlyList<IBattleNpc> FindAllFateObjects(ushort fateId, Vector3 playerPosition) =>
        this.FindAllFateCombatants(fateId, playerPosition).ToArray();

    /// <summary>
    /// Finds the single NPC that can start a Preparing FATE. Some client builds expose this
    /// opener as a BattleNpc with the same FateId as the event. Prefer exact native NPC IDs;
    /// otherwise accept only one unique non-enemy Fate combatant.
    /// </summary>
    public IGameObject? FindPreparingOpener(
        ushort fateId,
        Vector3 fatePosition,
        float fateRadius,
        uint objectiveNpc,
        uint motivationNpc,
        IReadOnlyList<Vector3>? mapMarkerPositions = null)
    {
        // The opener is often offset from the FATE marker. After flying to the centre, keep a
        // wider object-table search radius so we can locate it before descending.
        float markerSearchRange = mapMarkerPositions is { Count: > 0 }
            ? mapMarkerPositions.Max(marker => Vector3.Distance(marker, fatePosition)) + 30f
            : 0f;
        float range = Math.Max(Math.Max(80f, fateRadius * 3f), markerSearchRange);
        IGameObject[] objects = objectTable
            .Where(o => o is not null
                && o.IsValid()
                && o.IsTargetable
                && Vector3.DistanceSquared(o.Position, fatePosition) <= range * range
                && o.ObjectKind is ObjectKind.EventNpc or ObjectKind.BattleNpc)
            .ToArray();

        // On this client build FateContext.MotivationNpc is the live GameObjectId (for example
        // FATE 195 reports 0x40020AC2, which is also logged as object=40020AC2), not a
        // BaseId/NameId and not the unmanaged Address. Treat it as the authoritative opener
        // identity before applying any coordinate-based heuristic.
        bool motivationIsObjectId = motivationNpc >= 0x10000000;
        if (motivationNpc != 0)
        {
            IGameObject? pointedOpener = objects.FirstOrDefault(o =>
                o.GameObjectId == motivationNpc
                && o.IsTargetable);
            if (pointedOpener is not null)
                return pointedOpener;

            // Do not guess from a marker while an authoritative native object id exists;
            // nearby enemies/chocobos can otherwise be selected before that object streams in.
            if (motivationIsObjectId)
                return null;
        }

        // Preparing FATEs publish the opener location as a map marker. Prefer objects close to
        // those marker coordinates; this excludes the enemy FATE combatants that also carry
        // the same FateId and may otherwise be the only nearby BattleNpc objects.
        if (mapMarkerPositions is { Count: > 0 })
        {
            IGameObject[] markerMatches = objects
                .Where(o => mapMarkerPositions.Any(marker =>
                    Vector3.DistanceSquared(o.Position, marker) <= 18f * 18f))
                .Where(o => !(o is IBattleNpc enemyNpc && IsAttackableEnemy(enemyNpc)))
                .OrderBy(o => mapMarkerPositions.Min(marker => Vector3.DistanceSquared(o.Position, marker)))
                .ToArray();
            if (markerMatches.Length > 0)
                return markerMatches[0];
        }

        IGameObject[] exact = objects
            .Where(o => !(o is IBattleNpc enemyNpc && IsAttackableEnemy(enemyNpc)))
            .Where(o => o.BaseId == objectiveNpc
                || o.BaseId == motivationNpc
                || (o is IBattleNpc battle && (battle.NameId == objectiveNpc || battle.NameId == motivationNpc)))
            .OrderBy(o => Vector3.DistanceSquared(o.Position, fatePosition))
            .ToArray();
        if (exact.Length > 0)
            return exact[0];

        IGameObject[] fateObjects = objects
            .OfType<IBattleNpc>()
            .Where(npc => BelongsToFate(npc, fateId) && !IsAttackableEnemy(npc))
            .Cast<IGameObject>()
            .OrderBy(o => Vector3.DistanceSquared(o.Position, fatePosition))
            .ToArray();
        return fateObjects.Length == 1 ? fateObjects[0] : null;
    }

    /// <summary>
    /// Finds the nearest live collection point for a collection FATE. EventObj is intentional:
    /// collection points are event objects, not gather nodes or BattleNpcs. The native FateId is
    /// the sole ownership key: do not guess from map markers or positions belonging to another
    /// event.
    /// </summary>
    public IGameObject? FindNearestCollectionObject(
        ushort fateId,
        Vector3 playerPosition)
    {
        IGameObject[] objects = objectTable
            .Where(o => o is not null
                && o.IsValid()
                && o.ObjectKind == ObjectKind.EventObj
                && o.IsTargetable)
            .Select(o => new
            {
                Object = o,
                FateId = GetFateId(o),
                Distance = Vector3.DistanceSquared(playerPosition, o.Position),
            })
            .Where(x => x.FateId == fateId)
            .OrderBy(x => x.Distance)
            .Select(x => x.Object)
            .ToArray();
        return objects.FirstOrDefault();
    }

    // Kept temporarily for callers outside the collection state machine. The additional
    // arguments deliberately no longer relax the native FateId match.
    public IGameObject? FindNearestCollectionObject(
        ushort fateId,
        Vector3 playerPosition,
        Vector3 fatePosition,
        float fateRadius,
        IReadOnlyList<Vector3>? objectivePositions,
        IReadOnlySet<ulong> completedObjectIds) =>
        this.FindNearestCollectionObject(fateId, playerPosition);

    /// <summary>
    /// Resolves a collection turn-in NPC directly from the native FateContext.ObjectiveNpc object
    /// identity. This intentionally does not guess from names, map markers, proximity, or the
    /// opening NPC: all of those can identify the wrong actor in a busy FATE area.
    /// </summary>
    public IGameObject? FindCollectionTurnInNpc(
        uint objectiveNpc)
    {
        return objectiveNpc == 0 || objectiveNpc == InvalidGameObjectId
            ? null
            : objectTable.FirstOrDefault(o => o is not null
                && o.IsValid()
                && o.IsTargetable
                && o.GameObjectId == objectiveNpc);
    }

    // Compatibility overload. Collection hand-in selection is intentionally ObjectiveNpc-only.
    public IGameObject? FindCollectionTurnInNpc(
        ushort fateId,
        Vector3 fatePosition,
        float fateRadius,
        uint objectiveNpc,
        uint motivationNpc,
        IReadOnlyList<Vector3>? objectivePositions,
        IReadOnlyList<Vector3>? mapMarkerPositions) =>
        this.FindCollectionTurnInNpc(objectiveNpc);

    private IReadOnlyList<IBattleNpc> FindAllFateCombatants(ushort fateId, Vector3 playerPosition) =>
        objectTable
            .OfType<IBattleNpc>()
            .Where(npc => npc.IsValid() && !npc.IsDead)
            .Where(npc => npc.BattleNpcKind == BattleNpcSubKind.Combatant)
            .Where(npc => BelongsToFate(npc, fateId))
            .OrderBy(npc => Vector3.DistanceSquared(playerPosition, npc.Position))
            .ToArray();

    public IReadOnlyList<FateTargetDiagnostic> DescribeTargets(
        ushort fateId,
        Vector3 playerPosition,
        IPlayerCharacter? player) => this.FindAllFateCombatants(fateId, playerPosition)
            .Select(target => new FateTargetDiagnostic(
                target.GameObjectId,
                target.Name.ToString(),
                target.NameId,
                target.BaseId,
                GetLayoutId(target),
                target.CurrentHp,
                target.MaxHp,
                IsFriendly(target),
                GetNamePlateKind(target),
                IsAttackableEnemy(target),
                player is not null && IsTargetingPlayer(target, player),
                target.TargetObjectId,
                target.BattleNpcKind))
            .ToArray();

    /// <summary>
    /// Finds a high-priority destroy target. Verified FateObjectiveIdentityCatalog entries use
    /// exact BaseId + NameId matching; FATEs without a verified identity fall back to the
    /// configured client/Wiki name and documented aliases. Arbitrary fuzzy matching is not used.
    /// </summary>
    public IReadOnlyList<IBattleNpc> FindNamedPriorityTargets(
        ushort fateId,
        Vector3 playerPosition,
        IReadOnlyList<string> targetNames)
    {
        if (FateObjectiveIdentityCatalog.TryGetDestroyObjectives(fateId, out IReadOnlyList<FateObjectiveIdentity> identities))
        {
            IBattleNpc[] identityMatches = objectTable
                .OfType<IBattleNpc>()
                .Where(target => target.IsValid() && !target.IsDead && target.IsTargetable)
                .Where(target => BelongsToFate(target, fateId))
                .Where(target => !IsFriendly(target))
                .Where(target => identities.Any(identity =>
                    target.BaseId == identity.BaseId
                    || target.NameId == identity.NameId))
                .OrderBy(target => Vector3.DistanceSquared(playerPosition, target.Position))
                .ToArray();
            return identityMatches;
        }

        if (targetNames.Count == 0)
            return [];

        HashSet<string> normalizedNames = targetNames
            .Select(NormalizeObjectName)
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        // Include only documented client/Wiki spelling aliases. Matching remains exact or
        // one-sided-prefix for every expanded name, so suffixes are still handled without
        // enabling arbitrary fuzzy matching.
        foreach (string configuredName in normalizedNames.ToArray())
        {
            if (PriorityNameAliases.TryGetValue(configuredName, out string[]? aliases))
            {
                foreach (string alias in aliases)
                    normalizedNames.Add(NormalizeObjectName(alias));
            }
        }
        HashSet<uint> resolvedNameIds = normalizedNames
            .SelectMany(name => this.bnpcNameIdsByName.GetValueOrDefault(name, []))
            .ToHashSet();
        // Destroy objectives can be stationary objects and do not always use an enemy nameplate.
        // An exact configured name is a stronger discriminator here than the nameplate, so inspect
        // all FATE combatants and exclude only objects that the native
        // action-target test identifies as friendly. Prefix matching handles client-side suffixes
        // without turning unrelated nearby monsters into priority targets.
        IBattleNpc[] matches = objectTable
            .OfType<IBattleNpc>()
            .Where(target => target.IsValid() && !target.IsDead && target.IsTargetable)
            .Where(target => BelongsToFate(target, fateId))
            .Where(target => !IsFriendly(target))
            .Where(target => resolvedNameIds.Contains(target.NameId)
                || normalizedNames.Any(name => IsPriorityNameMatch(name, NormalizeObjectName(target.Name.ToString()))))
            .OrderBy(target => Vector3.DistanceSquared(playerPosition, target.Position))
            .ToArray();
        return matches;
    }

    private static bool IsPriorityNameMatch(string configuredName, string objectName) =>
        string.Equals(configuredName, objectName, StringComparison.Ordinal)
        || objectName.StartsWith(configuredName, StringComparison.Ordinal)
        || configuredName.StartsWith(objectName, StringComparison.Ordinal);

    /// <summary>Valid Boss candidates. The controller retains its chosen living target.</summary>
    public IReadOnlyList<IBattleNpc> FindBossTargets(ushort fateId, Vector3 playerPosition) =>
        this.FindAllFateCombatants(fateId, playerPosition)
            .Where(IsAttackableBossTarget)
            // Boss FATEs can expose several adds with the same FateId. The largest maximum HP
            // is the runtime Boss discriminator; distance must only break ties.
            .OrderByDescending(npc => npc.MaxHp)
            .ThenByDescending(npc => npc.CurrentHp)
            .ThenBy(npc => Vector3.DistanceSquared(playerPosition, npc.Position))
            .ToArray();

    /// <summary>
    /// Describes non-BattleNpc objects that may represent collection, destruction, escort, or
    /// defense objectives. Collection points are not guaranteed to be BattleNpc, and some client
    /// revisions do not populate the object's FateId, so objective-position and bounded-distance
    /// fallbacks are intentionally included for diagnostics.
    /// </summary>
    public IReadOnlyList<FateObjectDiagnostic> DescribeFateObjects(
        ushort fateId,
        Vector3 fatePosition,
        float fateRadius,
        IReadOnlyList<Vector3>? objectivePositions = null)
    {
        float scanRange = Math.Max(50f, fateRadius + 30f);
        float scanRangeSquared = scanRange * scanRange;
        HashSet<ObjectKind> candidateKinds =
        [
            ObjectKind.EventObj,
            ObjectKind.EventNpc,
            ObjectKind.GatheringPoint,
            ObjectKind.Treasure,
            ObjectKind.AreaObject,
            ObjectKind.HousingEventObject,
        ];

        return objectTable
            .Where(obj => obj is not null
                && obj.IsValid()
                && candidateKinds.Contains(obj.ObjectKind))
            .Select(obj =>
            {
                float distance = Vector3.Distance(obj.Position, fatePosition);
                bool nearObjective = objectivePositions is { Count: > 0 }
                    && objectivePositions.Any(position => Vector3.DistanceSquared(position, obj.Position) <= 25f * 25f);
                ushort objectFateId = GetFateId(obj);
                return new
                {
                    Object = obj,
                    Distance = distance,
                    NearObjective = nearObjective,
                    FateId = objectFateId,
                    InFateRange = Vector3.DistanceSquared(obj.Position, fatePosition) <= scanRangeSquared,
                };
            })
            .Where(item => item.FateId == fateId
                || item.NearObjective
                || (item.InFateRange && item.Object.IsTargetable))
            .OrderBy(item => item.Distance)
            .Select(item => new FateObjectDiagnostic(
                item.Object.GameObjectId,
                item.Object.ObjectKind,
                item.Object.Name.ToString(),
                GetNameId(item.Object),
                item.Object.BaseId,
                GetLayoutId(item.Object),
                item.FateId,
                item.Object.Position,
                item.Distance,
                item.Object.IsTargetable,
                item.NearObjective))
            .ToArray();
    }

    public IBattleNpc? FindBossTarget(ushort fateId, Vector3 playerPosition) =>
        this.FindBossTargets(fateId, playerPosition).FirstOrDefault();

    public IBattleNpc? FindLiveTarget(ulong id) => objectTable.OfType<IBattleNpc>()
        .FirstOrDefault(npc => npc.GameObjectId == id
            && npc.IsValid()
            && !npc.IsDead
            && npc.IsTargetable
            && !IsFriendly(npc));

    /// <summary>Read the native enemy list, including enemies currently attacking a companion.</summary>
    public unsafe IReadOnlyList<IBattleNpc> FindEngagedTargets(IPlayerCharacter player)
    {
        HashSet<uint> ids = [];
        UIState* ui = UIState.Instance();
        if (ui != null)
        {
            int count = Math.Clamp(ui->Hater.HaterCount, 0, 32);
            for (int i = 0; i < count; i++)
                ids.Add(ui->Hater.Haters[i].EntityId);
        }
        return objectTable.OfType<IBattleNpc>()
            .Where(this.IsAttackableCleanupTarget)
            .Where(npc => ids.Contains(npc.EntityId) || IsTargetingPlayer(npc, player))
            .ToArray();
    }

    /// <summary>
    /// Finds all live combatants currently targeting the local player, irrespective of FATE ID.
    /// This is intentionally broader than FindAllTargets so former FATE enemies can be cleaned up
    /// after the FATE context has already ended.
    /// </summary>
    public IReadOnlyList<IBattleNpc> FindTargetsAggroedOnPlayer(IPlayerCharacter player) =>
        objectTable
            .OfType<IBattleNpc>()
            .Where(this.IsAttackableCleanupTarget)
            .Where(npc => npc.BattleNpcKind == BattleNpcSubKind.Combatant)
            // A monster may still be passive-looking while its target id already
            // points at the player (for example after another enemy's death). Keep it in the
            // cleanup list; IsAttackableCleanupTarget excludes only non-attackable actors.
            .Where(npc => IsTargetingPlayer(npc, player))
            .OrderBy(npc => Vector3.DistanceSquared(player.Position, npc.Position))
            .ToArray();

    /// <summary>
    /// Fallback cleanup candidates when the client combat flag is set but TargetObjectId has not
    /// yet synchronized (this can happen immediately after a FATE ends). Native InCombat is used
    /// only within a bounded radius so unrelated fights elsewhere are not pulled in.
    /// </summary>
    public IReadOnlyList<IBattleNpc> FindCombatCleanupTargets(IPlayerCharacter player, float radius = 80f) =>
        objectTable
            .OfType<IBattleNpc>()
            .Where(this.IsAttackableCleanupTarget)
            .Where(npc => npc.BattleNpcKind == BattleNpcSubKind.Combatant)
            .Where(npc => Vector3.DistanceSquared(npc.Position, player.Position) <= radius * radius)
            // A direct target lock is stronger evidence than the friendly classifier: some FATE
            // actors expose unusual action-target flags while still targeting the player. Native
            // InCombat fallback remains guarded by !IsFriendly to avoid selecting allies.
            .Where(npc => npc.TargetObjectId == player.GameObjectId
                || (!IsFriendly(npc) && IsNativeInCombat(npc)))
            .OrderBy(npc => Vector3.DistanceSquared(player.Position, npc.Position))
            .ToArray();

    /// <summary>
    /// Finds enemy combatants near an origin which do not belong to the active FATE. A native
    /// FateId of zero is intentionally included: ordinary overworld monsters are precisely the
    /// hazards this query is meant to identify.
    /// </summary>
    public IReadOnlyList<IBattleNpc> FindNonFateTargetsNear(
        ushort fateId,
        Vector3 origin,
        float radius) => objectTable
            .OfType<IBattleNpc>()
            .Where(this.IsAttackableCleanupTarget)
            .Where(npc => npc.BattleNpcKind == BattleNpcSubKind.Combatant)
            // Passive monsters may not have an enemy nameplate until they are attacked.
            // Friendly/protection NPCs are excluded by IsAttackableCleanupTarget.
            .Where(npc => !BelongsToFate(npc, fateId))
            .Where(npc => Vector3.DistanceSquared(npc.Position, origin) <= radius * radius)
            .OrderBy(npc => Vector3.DistanceSquared(npc.Position, origin))
            .ToArray();

    public bool IsAttackableCleanupTarget(IBattleNpc npc) =>
        npc.IsValid()
        && !npc.IsDead
        && npc.IsTargetable
        && !IsFriendly(npc)
        && !this.IsInvulnerable(npc);

    private bool IsInvulnerable(IBattleNpc npc)
    {
        try
        {
            return npc.StatusList.Any(status => this.invulnerabilityStatusIds.Contains(status.StatusId));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the native FATE owner, or zero for an ordinary non-FATE combatant.</summary>
    public static unsafe ushort GetFateId(IBattleNpc npc)
    {
        if (npc.Address == nint.Zero)
            return 0;

        return ((BattleChara*)npc.Address)->FateId;
    }

    public static unsafe ushort GetFateId(IGameObject obj)
    {
        if (obj.Address == nint.Zero)
            return 0;

        return ((GameObjectStruct*)obj.Address)->FateId;
    }

    public static bool IsTargetingPlayer(IBattleNpc npc, IPlayerCharacter player) =>
        npc.TargetObjectId == player.GameObjectId;

    public static unsafe bool IsNativeInCombat(IBattleNpc npc) =>
        npc.Address != nint.Zero && ((BattleChara*)npc.Address)->InCombat;

    public static unsafe byte GetNamePlateKind(IGameObject? obj)
    {
        if (obj is null || obj.Address == nint.Zero)
            return 0;

        return ((GameObjectStruct*)obj.Address)->GetNamePlateColorType();
    }

    private static bool HasEnemyNamePlate(IGameObject obj) =>
        GetNamePlateKind(obj) is 4 or 5 or 6 or 7 or 9 or 10 or 11;

    /// <summary>
    /// Uses the same native target-classification technique as Wrath Combo:
    /// an object is friendly when the game accepts a universal healer action on it.
    /// The nameplate alone is insufficient for FATE protection NPCs; use the native action
    /// target test as the authoritative friendly check.
    /// </summary>
    public static unsafe bool IsFriendly(IGameObject? obj)
    {
        if (obj is null || obj.Address == nint.Zero)
            return false;

        // Nameplate kinds 12/13 are friendly battle-NPC colors. This native presentation state
        // updates when a FATE actor changes allegiance, which is exactly what happens in some
        // multi-boss encounters.
        if (GetNamePlateKind(obj) is 12 or 13)
            return true;

        GameObjectStruct* gameObject = (GameObjectStruct*)obj.Address;
        // RoleActions.Healer.Esuna and WHM Cure, as used by Wrath Combo's
        // TargetIsFriendly, are stable universal target-classification actions.
        return ActionManager.CanUseActionOnTarget(7568, gameObject)
            || (obj.ObjectKind == ObjectKind.EventNpc
                && ActionManager.CanUseActionOnTarget(120, gameObject));
    }

    public static bool IsFriendly(IBattleNpc npc) => IsFriendly((IGameObject)npc);

    public static bool IsAttackableEnemy(IBattleNpc npc) =>
        npc.IsValid()
        && !npc.IsDead
        && npc.IsTargetable
        // Some FATE combatants (for example FATE 1860's "圆扇刺种植者") are
        // attackable and use an enemy nameplate. The nameplate is the client's target
        // classification; keep the native friendly check as the guard against protection
        // NPCs and other allies.
        && HasEnemyNamePlate(npc)
        && !IsFriendly(npc);

    private static bool IsAttackableBossTarget(IBattleNpc npc) =>
        npc.IsValid()
        && !npc.IsDead
        && npc.IsTargetable
        && !IsFriendly(npc)
        && HasEnemyNamePlate(npc);

    public static unsafe bool BelongsToFate(IBattleNpc npc, ushort fateId)
    {
        if (npc.Address == nint.Zero)
            return false;

        BattleChara* battleChara = (BattleChara*)npc.Address;
        return battleChara->FateId == fateId;
    }

    private static unsafe uint GetLayoutId(IGameObject obj)
    {
        if (obj.Address == nint.Zero)
            return 0;

        return ((GameObjectStruct*)obj.Address)->LayoutId;
    }

    private static uint GetNameId(IGameObject obj) => obj is ICharacter character ? character.NameId : 0;

    private static string NormalizeObjectName(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormKC);
        return string.Concat(normalized.Where(character => !char.IsWhiteSpace(character)));
    }
}
