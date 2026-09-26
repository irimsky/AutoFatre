using System.Numerics;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Plugin.Services;
using NativeFateContext = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateContext;

namespace AutoFatre;

public sealed record FateObjectiveSnapshot(
    uint IconId,
    uint TargetMarkerLayoutId,
    Vector3 Position,
    uint Flags);

public enum FateCombatStrategyKind
{
    Unsupported,
    General,
    Boss,
    Collection,
}

public enum FateProtectionMode
{
    None,
    Static,
    Moving,
}

/// <summary>
/// Describes which automation branch owns a FATE.  Type classification is deliberately based on
/// the client Fate row only; the embedded supplemental catalog may refine target names, but it
/// must never decide whether the FATE is runnable.
/// </summary>
public sealed record FateCombatProfile(
    FateCombatStrategyKind Strategy,
    FateProtectionMode ProtectionMode,
    bool IsSupported,
    string SupportDescription)
{
    public static FateCombatProfile From(byte rule, uint iconId) => iconId switch
    {
        FateSnapshot.DefeatEnemiesIconId => new(
            FateCombatStrategyKind.General,
            FateProtectionMode.None,
            true,
            "支持：通用战斗"),
        FateSnapshot.DefeatBossIconId => new(
            FateCombatStrategyKind.Boss,
            FateProtectionMode.None,
            true,
            "支持：BOSS 专注战斗"),
        FateSnapshot.CollectItemsIconId => new(
            FateCombatStrategyKind.Collection,
            FateProtectionMode.None,
            true,
            "支持：收集物品（EventObj 交互与 NPC 交付）"),
        FateSnapshot.DefenseIconId => new(
            FateCombatStrategyKind.General,
            FateProtectionMode.Static,
            true,
            "支持：防御战斗"),
        FateSnapshot.EscortIconId => new(
            FateCombatStrategyKind.General,
            FateProtectionMode.Moving,
            true,
            "支持：护送战斗"),
        _ => new(
            FateCombatStrategyKind.Unsupported,
            FateProtectionMode.None,
            false,
            $"暂未支持：{FateSnapshot.GetTypeName(rule, iconId)}"),
    };
}

public sealed record FateSnapshot(
    ushort FateId,
    string Name,
    FateState State,
    byte Progress,
    long TimeRemaining,
    byte Level,
    byte MaxLevel,
    Vector3 Position,
    float Radius,
    bool HasBonus,
    byte Rule,
    uint IconId,
    uint EventItem,
    uint ObjectiveNpc,
    uint MotivationNpc,
    IReadOnlyList<Vector3> MapMarkerPositions,
    IReadOnlyList<FateObjectiveSnapshot> Objectives,
    string EligibilityReason,
    int StartTimeEpoch,
    short Duration)
{
    public const uint DefeatEnemiesIconId = 60721;
    public const uint DefeatBossIconId = 60722;
    public const uint CollectItemsIconId = 60723;
    public const uint DefenseIconId = 60724;
    public const uint EscortIconId = 60725;

    public FateCombatProfile CombatProfile => FateCombatProfile.From(this.Rule, this.IconId);
    public bool IsAutomationSupported => this.CombatProfile.IsSupported;

    public string CombatKind => GetTypeName(this.Rule, this.IconId);

    public static string GetTypeName(byte rule, uint iconId) => iconId switch
    {
        DefeatEnemiesIconId => "消灭普通怪物",
        DefeatBossIconId => "讨伐BOSS",
        CollectItemsIconId => "收集物品",
        DefenseIconId => "防御",
        EscortIconId => "护送",
        60727 => "拦截",
        _ => rule switch
        {
            1 => "消灭普通怪物",
            2 => "收集物品",
            3 => "护送",
            4 => "防御",
            5 => "特殊事件",
            6 => "特殊战斗",
            7 or 9 => "共同作业",
            8 => "庆典",
            _ => "其他",
        },
    };

    /// <summary>Preparing FATEs have not started their server-side countdown yet.</summary>
    public bool HasStartedTimer => this.StartTimeEpoch > 0 && this.Duration > 0;
}

/// <summary>Materializes native IFate values so no native-backed object survives an update tick.</summary>
public sealed unsafe class FateRepository(IFateTable fateTable, IClientState clientState)
{
    private IReadOnlyList<FateSnapshot> snapshot = [];

    public IReadOnlyList<FateSnapshot> Snapshot => this.snapshot;

    public void Refresh()
    {
        List<FateSnapshot> next = [];
        foreach (IFate fate in fateTable)
        {
            if (!fateTable.IsValid(fate))
                continue;

            // IFateTable already represents the current area's FATE director. During Preparing,
            // MapMarkers[0].TerritoryTypeId can still be zero, so rejecting zero here hides the
            // very events that need an NPC/event trigger.
            uint fateTerritory = fate.TerritoryType.RowId;
            if (fateTerritory != 0 && fateTerritory != clientState.TerritoryType)
                continue;

            byte rule = fate.GameData.ValueNullable?.Rule ?? 0;
            FateCombatProfile combatProfile = FateCombatProfile.From(rule, fate.IconId);
            bool preparing = fate.State == FateState.Preparing
                || fate.State.ToString().Contains("Prepar", StringComparison.OrdinalIgnoreCase);
            string reason = preparing
                ? combatProfile.IsSupported
                    ? "需要 NPC/事件触发（倒计时尚未开始）"
                    : $"{combatProfile.SupportDescription}；不会前往或触发"
                : combatProfile.SupportDescription;
            NativeFateContext* context = (NativeFateContext*)fate.Address;
            uint objectiveNpc = context == null ? 0 : context->ObjectiveNpc;
            uint motivationNpc = context == null ? 0 : context->MotivationNpc;
            uint eventItem = context == null ? 0 : context->EventItem;
            Vector3[] markerPositions = context is null
                ? []
                : context->MapMarkers
                    .ToArray()
                    .Select(marker => marker.MapMarkerData.Position)
                    .Where(FateRepository.HasValidPosition)
                    .Distinct()
                    .ToArray();
            FateObjectiveSnapshot[] objectives = context is null
                ? []
                : context->Objectives
                    .ToArray()
                    .Where(objective => objective.IconId != 0
                        || objective.TargetMarkerLayoutId != 0
                        || FateRepository.HasValidPosition(objective.Position))
                    .Select(objective => new FateObjectiveSnapshot(
                        objective.IconId,
                        objective.TargetMarkerLayoutId,
                        objective.Position,
                        objective.Flags))
                    .ToArray();
            next.Add(new FateSnapshot(
                fate.FateId,
                fate.Name.ToString(),
                fate.State,
                fate.Progress,
                fate.TimeRemaining,
                fate.Level,
                fate.MaxLevel,
                fate.Position,
                fate.Radius,
                fate.HasBonus,
                rule,
                fate.IconId,
                eventItem,
                objectiveNpc,
                motivationNpc,
                markerPositions,
                objectives,
                reason,
                fate.StartTimeEpoch,
                fate.Duration));
        }

        this.snapshot = next;
    }

    public FateSnapshot? Find(ushort fateId) => this.snapshot.FirstOrDefault(f => f.FateId == fateId);

    public static bool IsActive(FateSnapshot fate) =>
        fate.State == FateState.Running || fate.State.ToString() is "Preparing" or "Preparation";

    public static bool HasValidPosition(FateSnapshot fate) =>
        HasValidPosition(fate.Position);

    public static bool HasValidPosition(Vector3 position) =>
        position != Vector3.Zero
        && !float.IsNaN(position.X)
        && !float.IsNaN(position.Y)
        && !float.IsNaN(position.Z);
}
