using Dalamud.Configuration;

namespace AutoFatre;

public enum AutomationMode
{
    SingleMapLoop,
    TargetFate,
    PresetSequence,
}

public enum TargetFateFallbackPolicy
{
    WaitOnly,
    FarmOtherFates,
}

public enum SequenceCompletionPolicy
{
    Stop,
    Loop,
}

public enum PullRefillPolicy
{
    RefillAtHalf,
    ClearBatchFirst,
}

public enum StopConditionKind
{
    FateCount,
    ItemCount,
    TargetFate,
}

public sealed class StopCondition
{
    public StopConditionKind Kind { get; set; } = StopConditionKind.FateCount;
    public int FateCount { get; set; } = 1;
    public uint ItemId { get; set; }
    public int ItemCount { get; set; } = 1;
    public ushort TargetFateId { get; set; }
}

public sealed class MapPreset
{
    public string Name { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
    public uint AetheryteId { get; set; }
    public ushort? TargetFateId { get; set; }
    public TargetFateFallbackPolicy TargetFallback { get; set; } = TargetFateFallbackPolicy.FarmOtherFates;
    /// <summary>Optional minion item to summon immediately after entering this map.</summary>
    public uint EntryPetItemId { get; set; }
    /// <summary>Optional weapon item to equip after entering this map.</summary>
    public uint EntryWeaponItemId { get; set; }

    /// <summary>All enabled completion conditions for this map. Conditions are combined with AND.</summary>
    public List<StopCondition> StopConditions { get; set; } = [];

    /// <summary>Legacy single-condition field retained only for configuration migration.</summary>
    public StopCondition? StopCondition { get; set; }
}

public sealed class MapPresetSequence
{
    public string Name { get; set; } = "默认地图列表";
    public List<MapPreset> Maps { get; set; } = [];
}

public sealed class AutoFatreConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 17;
    // Runtime-only switch. Persisting this value causes a plugin reload to resume automation
    // unexpectedly when the previous instance was still enabled during shutdown.
    [Newtonsoft.Json.JsonIgnore]
    public bool Enabled { get; set; }
    public AutomationMode Mode { get; set; } = AutomationMode.SingleMapLoop;
    public uint SingleMapTerritoryId { get; set; }
    public uint SingleMapAetheryteId { get; set; }
    /// <summary>Legacy single target retained for configuration compatibility; the first item in TargetFateIds is authoritative.</summary>
    public ushort? TargetFateId { get; set; }
    /// <summary>Ordered target FATEs used by TargetFate mode. All entries must belong to one territory.</summary>
    public List<ushort> TargetFateIds { get; set; } = [];
    public TargetFateFallbackPolicy TargetFateFallback { get; set; } = TargetFateFallbackPolicy.FarmOtherFates;
    public SequenceCompletionPolicy SequenceCompletion { get; set; } = SequenceCompletionPolicy.Stop;
    public bool FlyToFates { get; set; } = true;
    public int MaxAggroCount { get; set; } = 4;
    public PullRefillPolicy PullRefillPolicy { get; set; } = PullRefillPolicy.RefillAtHalf;
    public int MaxRecoveryAttempts { get; set; } = 3;
    public int AggroConfirmationTimeoutSeconds { get; set; } = 8;
    public int SkippedTargetCooldownSeconds { get; set; } = 30;
    public bool AutoAcceptRaise { get; set; } = true;
    public bool AutoReturnAfterDeathTimeout { get; set; } = true;
    public int DeathRaiseWaitSeconds { get; set; } = 20;
    public int DeathFateCooldownSeconds { get; set; } = 300;
    public List<DeathRecord> DeathRecords { get; set; } = [];
    public List<FateFailureRecord> FateFailureRecords { get; set; } = [];
    public bool AutoSummonChocoboCompanion { get; set; }
    public bool ShowOverlayWindow { get; set; }
    /// <summary>
    /// Delay before starting travel to the next FATE. 0 means no delay, 1 means exactly one
    /// second, and values >= 2 choose a random whole-second delay from 2 through this value.
    /// </summary>
    public int NextFateDelaySeconds { get; set; } = 3;
    public int NavigationStuckSeconds { get; set; } = 8;
    public float PullApproachDistance { get; set; } = 3f;
    public int CombatEscapeTimeoutSeconds { get; set; } = 20;
    public float CombatEscapeDistance { get; set; } = 80f;
    public bool EnableSoundAlerts { get; set; } = true;
    /// <summary>Legacy shared sound setting retained for migration.</summary>
    public uint SoundAlertEffectId { get; set; } = 1;
    public uint SoundAlertTargetAppearedEffectId { get; set; } = 1;
    public uint SoundAlertFateCompletedEffectId { get; set; } = 1;
    public uint SoundAlertDeathEffectId { get; set; } = 1;
    public uint SoundAlertNavigationSkippedEffectId { get; set; } = 1;
    public int SoundAlertCooldownSeconds { get; set; } = 3;
    /// <summary>FATE ids which are ignored by automatic selection and target-FATE priority checks.</summary>
    public List<ushort> FateBlacklist { get; set; } = [];
    /// <summary>Recently selected FATE ids shared by all FATE selectors in the UI.</summary>
    public List<ushort> RecentTargetFateIds { get; set; } = [];

    /// <summary>Custom mode is one ordered list containing multiple map entries.</summary>
    public MapPresetSequence PresetSequence { get; set; } = new();

    /// <summary>Saved preset lists. The selected list is edited and executed in preset mode.</summary>
    public List<MapPresetSequence> PresetSequences { get; set; } = [];
    public int ActivePresetSequenceIndex { get; set; }

    /// <summary>Legacy representation from versions before the map-list model.</summary>
    public List<MapPreset> Presets { get; set; } = [];

    public void Migrate()
    {
        this.PresetSequence ??= new MapPresetSequence();
        this.PresetSequence.Maps ??= [];
        this.PresetSequences ??= [];
        this.Presets ??= [];
        this.FateBlacklist ??= [];
        this.RecentTargetFateIds ??= [];
        this.TargetFateIds ??= [];
        if (this.TargetFateIds.Count == 0 && this.TargetFateId is { } legacyTarget && legacyTarget != 0)
            this.TargetFateIds.Add(legacyTarget);
        this.DeathRecords ??= [];
        this.FateFailureRecords ??= [];
        int previousVersion = this.Version;
        if (this.Version < 2)
        {
            this.TargetFateFallback = TargetFateFallbackPolicy.FarmOtherFates;
            this.SequenceCompletion = SequenceCompletionPolicy.Stop;
            this.AggroConfirmationTimeoutSeconds = 8;
            this.SkippedTargetCooldownSeconds = 30;
            this.DeathRaiseWaitSeconds = 20;
            this.NavigationStuckSeconds = 8;
            this.PullApproachDistance = 3f;
        }

        // Aetheryte overrides are retained as compatibility fields, but are no longer exposed or
        // honored. Normalize() clears values from configurations saved by older plugin versions.
        if (previousVersion < 4)
        {
            // Preserve the old automatic-return behavior while enabling the newly separated
            // player-raise acceptance policy by default.
            this.AutoAcceptRaise = true;
            this.AutoReturnAfterDeathTimeout = true;
        }

        if (previousVersion < 5)
        {
            this.CombatEscapeTimeoutSeconds = 20;
            this.CombatEscapeDistance = 80f;
        }

        if (previousVersion < 6)
        {
            if (this.PresetSequence.Maps.Count == 0 && this.Presets.Count > 0)
            {
                this.PresetSequence.Maps = this.Presets.ToList();
                this.PresetSequence.Name = "迁移的地图列表";
            }

            foreach (MapPreset map in this.PresetSequence.Maps)
            {
                map.StopConditions ??= [];
                if (map.StopConditions.Count == 0 && map.StopCondition is not null)
                    map.StopConditions.Add(map.StopCondition);
                if (map.TargetFateId is { } legacyMapTarget && legacyMapTarget != 0
                    && !map.StopConditions.Any(c => c.Kind == StopConditionKind.TargetFate && c.TargetFateId == legacyMapTarget))
                {
                    map.StopConditions.Add(new StopCondition { Kind = StopConditionKind.TargetFate, TargetFateId = legacyMapTarget });
                }

                map.StopCondition = null;
            }

            this.Presets.Clear();
        }

        if (previousVersion < 8)
        {
            if (this.PresetSequences.Count == 0)
                this.PresetSequences.Add(this.PresetSequence);

            uint legacySound = this.SoundAlertEffectId;
            if (legacySound <= 16)
            {
                this.SoundAlertTargetAppearedEffectId = legacySound;
                this.SoundAlertFateCompletedEffectId = legacySound;
                this.SoundAlertDeathEffectId = legacySound;
                this.SoundAlertNavigationSkippedEffectId = legacySound;
            }
        }

        if (previousVersion < 9)
            this.PullRefillPolicy = PullRefillPolicy.RefillAtHalf;

        if (previousVersion < 16)
            this.NextFateDelaySeconds = 3;

        if (this.PresetSequences.Count == 0)
            this.PresetSequences.Add(this.PresetSequence);
        this.ActivePresetSequenceIndex = Math.Clamp(this.ActivePresetSequenceIndex, 0, this.PresetSequences.Count - 1);
        this.PresetSequence = this.PresetSequences[this.ActivePresetSequenceIndex];
        this.Version = 17;
    }

    public void Normalize()
    {
        // Travel always uses the default unlocked aetheryte candidates. Keep these legacy fields
        // deserializable so old configuration files remain compatible, but never let an old manual
        // choice affect the runtime plan.
        this.SingleMapAetheryteId = 0;
        this.PresetSequence ??= new MapPresetSequence();
        this.PresetSequence.Maps ??= [];
        this.PresetSequences ??= [];
        foreach (MapPreset map in this.PresetSequence.Maps)
            map.AetheryteId = 0;
        foreach (MapPreset map in this.PresetSequences.SelectMany(sequence => sequence.Maps ?? []))
            map.AetheryteId = 0;

        this.MaxAggroCount = Math.Clamp(this.MaxAggroCount, 1, 4);
        if (!Enum.IsDefined(this.PullRefillPolicy))
            this.PullRefillPolicy = PullRefillPolicy.RefillAtHalf;
        this.MaxRecoveryAttempts = Math.Clamp(this.MaxRecoveryAttempts, 0, 20);
        this.AggroConfirmationTimeoutSeconds = Math.Clamp(this.AggroConfirmationTimeoutSeconds, 2, 60);
        this.SkippedTargetCooldownSeconds = Math.Clamp(this.SkippedTargetCooldownSeconds, 5, 300);
        this.DeathRaiseWaitSeconds = Math.Clamp(this.DeathRaiseWaitSeconds, 0, 120);
        this.DeathFateCooldownSeconds = Math.Clamp(this.DeathFateCooldownSeconds, 30, 3600);
        this.DeathRecords ??= [];
        this.FateFailureRecords ??= [];
        if (this.DeathRecords.Count > 200)
            this.DeathRecords = this.DeathRecords.Take(200).ToList();
        if (this.FateFailureRecords.Count > 500)
            this.FateFailureRecords = this.FateFailureRecords.Take(500).ToList();
        this.NavigationStuckSeconds = Math.Clamp(this.NavigationStuckSeconds, 4, 60);
        this.NextFateDelaySeconds = Math.Clamp(this.NextFateDelaySeconds, 0, 60);
        this.PullApproachDistance = Math.Clamp(this.PullApproachDistance, 1f, 20f);
        this.CombatEscapeTimeoutSeconds = Math.Clamp(this.CombatEscapeTimeoutSeconds, 5, 120);
        this.CombatEscapeDistance = Math.Clamp(this.CombatEscapeDistance, 30f, 200f);
        this.SoundAlertEffectId = Math.Clamp(this.SoundAlertEffectId, 0u, 16u);
        this.SoundAlertTargetAppearedEffectId = Math.Clamp(this.SoundAlertTargetAppearedEffectId, 0u, 16u);
        this.SoundAlertFateCompletedEffectId = Math.Clamp(this.SoundAlertFateCompletedEffectId, 0u, 16u);
        this.SoundAlertDeathEffectId = Math.Clamp(this.SoundAlertDeathEffectId, 0u, 16u);
        this.SoundAlertNavigationSkippedEffectId = Math.Clamp(this.SoundAlertNavigationSkippedEffectId, 0u, 16u);
        this.SoundAlertCooldownSeconds = Math.Clamp(this.SoundAlertCooldownSeconds, 0, 60);
        this.FateBlacklist ??= [];
        this.FateBlacklist = this.FateBlacklist
            .Where(id => id != 0)
            .Distinct()
            .Order()
            .ToList();
        this.RecentTargetFateIds ??= [];
        this.RecentTargetFateIds = this.RecentTargetFateIds
            .Where(id => id != 0)
            .Distinct()
            .Take(15)
            .ToList();
        this.TargetFateIds ??= [];
        this.TargetFateIds = this.TargetFateIds
            .Where(id => id != 0)
            .Distinct()
            .ToList();
        this.TargetFateId = this.TargetFateIds.FirstOrDefault() is { } firstTarget && firstTarget != 0
            ? firstTarget
            : null;
        this.PresetSequence ??= new MapPresetSequence();
        this.PresetSequence.Maps ??= [];
        this.PresetSequences ??= [];
        if (this.PresetSequences.Count == 0)
            this.PresetSequences.Add(this.PresetSequence);
        this.ActivePresetSequenceIndex = Math.Clamp(this.ActivePresetSequenceIndex, 0, this.PresetSequences.Count - 1);
        this.PresetSequence = this.PresetSequences[this.ActivePresetSequenceIndex];
        foreach (MapPresetSequence sequence in this.PresetSequences)
        {
            sequence.Maps ??= [];
            foreach (MapPreset map in sequence.Maps)
            {
                map.StopConditions ??= [];
                map.StopConditions.RemoveAll(c => c is null);
            }
        }
    }

    public MapPresetSequence GetActivePresetSequence()
    {
        this.PresetSequences ??= [];
        if (this.PresetSequences.Count == 0)
            this.PresetSequences.Add(this.PresetSequence ?? new MapPresetSequence());
        this.ActivePresetSequenceIndex = Math.Clamp(this.ActivePresetSequenceIndex, 0, this.PresetSequences.Count - 1);
        this.PresetSequence = this.PresetSequences[this.ActivePresetSequenceIndex];
        return this.PresetSequence;
    }
}
