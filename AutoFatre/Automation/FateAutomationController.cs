using FieldNavigation;
using System.Numerics;
using Dalamud.Game.ClientState.Aetherytes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game;
using NativeFateManager = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateManager;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoFatre;

/// <summary>A framework-thread-only, recoverable orchestration state machine.</summary>
public sealed unsafe class FateAutomationController : IDisposable
{
    private static readonly TimeSpan CombatEscapeGracePeriod = TimeSpan.FromSeconds(5);
    // DailyRoutines-style collection is event driven: issue one interaction, then immediately
    // rescan on a short throttle instead of treating an animation/cast as a success signal.
    private static readonly TimeSpan CollectionInteractionRepeatDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan CollectionTurnInConfirmationTimeout = TimeSpan.FromSeconds(2);
    // Retained only while the obsolete legacy methods remain in this source file; the active
    // collection path never waits on these cast/object heuristics.
    private static readonly TimeSpan CollectionCastStartWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CollectionCastInterruptWindow = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CollectionObjectGoneConfirmationWindow = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan CollectionInteractionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FailedFateCooldown = TimeSpan.FromMinutes(5);
    // Idyllshire is the cross-zone fallback hub for maps whose outdoor aetheryte is not unlocked.
    // Stable Territory id for Idyllshire/田园郡 in the supported client data.
    private const uint IdyllshireTerritoryFallback = 478;

    public sealed record FateCandidateSnapshot(
        ushort FateId,
        string Name,
        string State,
        byte Progress,
        long TimeRemaining,
        float Distance,
        float Score,
        float DistanceWeight,
        float TimeWeight,
        bool HasBonus,
        byte Rule,
        uint IconId,
        string CombatKind,
        bool IsEligible,
        string EligibilityReason);

    private enum CleanupContinuation
    {
        ScanFates,
        RevalidatePlan,
        ResumeTravel,
        ResumeCompanionCheckpoint,
        PreemptTravel,
        ResumeCollection,
        FinalizeFate,
    }

    private enum CompanionCheckpointKind
    {
        MapArrival,
        FateArrival,
        PostFate,
        StartupGround,
        CombatPeriodic,
    }

    private enum BossCleanupContinuation
    {
        BeforeBossFight,
        ResumeBossAfterResync,
        ManualTestResync,
    }

    private enum PullApproachPhase
    {
        MovingToApproachRange,
        MeleeAttackWindow,
    }

    private enum PullTargetKind
    {
        Normal,
        ProtectionThreat,
    }

    private enum LostPriorityTargetKind
    {
        LostGirl,
        LostOne,
    }

    private enum AggroDutyResetPhase
    {
        None,
        Queueing,
        WaitingForEntry,
        LeavingDuty,
        WaitingForReturn,
    }

    private sealed record PendingFateResult(FateSnapshot Fate, bool Succeeded);

    private readonly record struct LoggedFateContext(
        uint TerritoryId,
        ushort FateId,
        uint ObjectiveNpc,
        uint MotivationNpc);

    private readonly record struct LoggedTargetIdentity(
        uint TerritoryId,
        ushort FateId,
        uint NameId,
        uint BaseId,
        uint LayoutId,
        byte NamePlateKind,
        bool IsFriendly,
        bool IsAttackable);

    private readonly record struct ClientFateDefinition(string Name, FateCombatProfile CombatProfile);

    private readonly AutoFatreConfiguration configuration;
    private readonly IPluginLog log;
    private readonly IChatGui chat;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly ICondition condition;
    private readonly IPartyList partyList;
    private readonly IAetheryteList aetheryteList;
    private readonly AetheryteTravelPlanner aetheryteTravelPlanner;
    private readonly IDataManager dataManager;
    private readonly IGameGui gameGui;
    private readonly VnavmeshIpc vnavmesh;
    private readonly LifestreamIpc lifestream;
    private readonly FateRepository fates;
    private readonly StaticFateCatalog staticFateCatalog;
    private readonly FateTargetSelector targetSelector;
    private readonly InventoryCounter inventoryCounter;
    private readonly CompanionAdapter companion;
    private readonly System.Action persistConfiguration;
    private readonly MountAdapter mount;
    private readonly LandingAdapter landing;
    private readonly DutyAggroResetAdapter dutyAggroReset;
    private readonly SoundAlertAdapter soundAlerts;
    private readonly TextAdvanceIpc textAdvance;
    private readonly LevelSyncAdapter levelSync;
    private readonly DeathRecoveryAdapter deathRecovery;
    private readonly IReadOnlyDictionary<ushort, ClientFateDefinition> clientFateDefinitions;
    private readonly NavigationTravelSession travelSession;
    private readonly NoFlyZoneCatalog noFlyZones;

    private readonly Queue<DiagnosticEntry> diagnostics = new();
    private readonly Dictionary<ushort, DateTime> skippedFates = [];
    private readonly Dictionary<ulong, DateTime> skippedTargets = [];
    private readonly HashSet<LoggedFateContext> loggedFateContexts = [];
    private readonly HashSet<LoggedTargetIdentity> loggedTargetIdentities = [];
    private readonly HashSet<ulong> pullBatchTargets = [];
    private AutomationState state = AutomationState.Stopped;
    private ushort? activeFateId;
    private FateSnapshot? lastActiveFateSnapshot;
    private DateTime activeFateMissingSince = DateTime.MinValue;
    private int presetIndex;
    private int presetCompletedFates;
    private readonly Dictionary<ushort, int> presetTargetFateCounts = [];
    private int totalCompletedFates;
    // A configured territory of 0 means "the map where automation starts". Keep that
    // meaning stable for the whole run so a death return to another territory cannot
    // silently turn the respawn point into the new target map.
    private uint? currentMapTerritory;
    private AutomationMode? currentMapTerritoryMode;
    private int currentMapTerritoryPresetIndex = -1;
    private int recoveryAttempts;
    private AutomationState recoveryResumeState = AutomationState.ValidatingPlan;
    private int levelSyncAttempts;
    private int aggroCount;
    private ulong? pullTargetId;
    private PullTargetKind pullTargetKind;
    private ulong? killTargetId;
    private ulong? lostPriorityTargetId;
    private LostPriorityTargetKind? lostPriorityTargetKind;
    private DateTime lostPriorityTargetMissingSince = DateTime.MinValue;
    private ulong? priorityDestroyTargetId;
    private ulong? bossTargetId;
    private ulong? cleanupTargetId;
    private PendingFateResult? pendingFateResult;
    private CleanupContinuation cleanupContinuation;
    private DateTime pullAttemptStartedAt;
    private DateTime pullPhaseStartedAt;
    private DateTime pullCastProtectionUntil = DateTime.MinValue;
    private PullApproachPhase pullApproachPhase;
    private DateTime pullBatchEmptySince = DateTime.MinValue;
    private DateTime outsideFateAreaSince = DateTime.MinValue;
    private DateTime cleanupNoTargetSince = DateTime.MinValue;
    private DateTime cleanupDiagnosticAt = DateTime.MinValue;
    private DateTime returnAreaDiagnosticAt = DateTime.MinValue;
    private DateTime outOfRangeTargetDiagnosticAt = DateTime.MinValue;
    private DateTime cleanupEscapeStartedAt = DateTime.MinValue;
    private DateTime dutyResetStartedAt = DateTime.MinValue;
    private DateTime dutyResetLastActionAt = DateTime.MinValue;
    private Vector3? cleanupEscapeDestination;
    private Vector3? cleanupEscapeOrigin;
    private ushort? preemptSyncFateId;
    private uint dutyResetOriginTerritory;
    private AggroDutyResetPhase dutyResetPhase;
    private bool manualDutyRoundTrip;
    private bool resumeAutomationAfterManualDuty;
    private bool groundTravelToActiveFate;
    private DateTime stateEnteredAt = DateTime.UtcNow;
    private DateTime nextActionAt = DateTime.MinValue;
    private DateTime nextSnapshotAt = DateTime.MinValue;
    private bool travelObservationDelayPending;
    private bool travelSessionActive;
    private NavigationPurpose travelPurpose;
    private Vector3 travelDestination;
    private bool travelFly;
    private bool travelHorizontal;
    private GroundDestinationKind travelGroundKind;
    private string travelContext = string.Empty;
    private DateTime nextTravelSnapshotAt = DateTime.MinValue;
    private readonly TeleportArrivalGate teleportArrival = new();
    private bool startupGroundCheckPending;
    private bool startupFateRangeSelectionPending;
    private bool preparingGroundArrivalPending;
    private bool mapArrivalCheckPending;
    private readonly DestroyObjectiveSession destroySession = new();
    private ulong? selectingTargetId;
    private DateTime targetSelectionFailedAt = DateTime.MinValue;
    private bool targetSelectionMoving;
    private DateTime teleportStartedAt = DateTime.MinValue;
    private uint teleportCandidateTerritory;
    private readonly HashSet<(uint AetheryteId, byte SubIndex)> failedTeleportCandidates = [];
    private uint teleportIssuedAetheryteId;
    private byte teleportIssuedSubIndex;
    private DateTime navigationRecoveryStartedAt = DateTime.MinValue;
    private DateTime territoryStableAfter = DateTime.MinValue;
    private DateTime deathStartedAt = DateTime.MinValue;
    private DateTime deathReturnAttemptStartedAt = DateTime.MinValue;
    private DateTime raiseAcceptedAt = DateTime.MinValue;
    private DateTime soundAlertCooldownUntil = DateTime.MinValue;
    private CompanionCheckpointKind companionCheckpointKind;
    private AutomationState companionResumeState = AutomationState.ScanningFates;
    private DateTime companionCheckpointStartedAt = DateTime.MinValue;
    private DateTime nextCompanionCheckAt = DateTime.MinValue;
    private bool companionEntryPetHandled;
    private bool companionChocoboHandled;
    private DateTime companionActionDeadline = DateTime.MinValue;
    private bool entryWeaponHandled;
    private DateTime nextIdleMountAttemptAt = DateTime.MinValue;
    private bool idleFlightRaised;
    private DateTime idleFlightDiagnosticAt = DateTime.MinValue;
    private uint? entryPetSummonedTerritory;
    private DateTime entryPetRetryAt = DateTime.MinValue;
    private ushort? deathRecordedFateId;
    private DateTime presetConditionRecheckUntil = DateTime.MinValue;
    private DateTime fateParticipationSince = DateTime.MinValue;
    private bool fallbackLandingForParticipation;
    private DateTime completedProgressObservedAt = DateTime.MinValue;
    private bool activeFateParticipationObserved;
    private DateTime terminalFateObservedAt = DateTime.MinValue;
    private readonly HashSet<ulong> completedCollectionObjects = [];
    private ushort? collectionFateId;
    private uint collectionItemId;
    private int collectionInteractionCount;
    private ulong? collectionObjectId;
    private ulong? collectionTurnInObjectId;
    private DateTime collectionInteractionStartedAt = DateTime.MinValue;
    private DateTime collectionTurnInStartedAt = DateTime.MinValue;
    private DateTime collectionLastDiagnosticAt = DateTime.MinValue;
    private DateTime collectionInteractionDiagnosticAt = DateTime.MinValue;
    private DateTime collectionDialogueLastActionAt = DateTime.MinValue;
    private int collectionInteractionActionSequence;
    private bool collectionInteractionCheckSequence;
    private bool collectionCastObserved;
    private bool collectionTurnInUrgent;
    private bool collectionTurnInPhase;
    private bool collectionTurnInInteractionIssued;
    private bool collectionTurnInDialogueSeen;
    private bool collectionSprintIssued;
    private int collectionTurnInItemCountBefore;
    private byte collectionTurnInProgressBefore;
    private bool collectionCombatFallback;
    private float collectionProgressPerItem;
    private DateTime collectionNextInteractionAt = DateTime.MinValue;
    private IReadOnlyDictionary<uint, int> collectionInventoryBefore = new Dictionary<uint, int>();
    private IReadOnlyDictionary<uint, int> collectionObservedInventory = new Dictionary<uint, int>();
    private FateState? terminalFateState;
    private static readonly TimeSpan TerminalFateConfirmationWindow = TimeSpan.FromSeconds(1);
    private const float BossNonFateCleanupRadius = 12f;
    private const float FateCombatRangePadding = 10f;
    private const float BossEmergencyCleanupHpRatio = 0.35f;
    private const float IdleFlightHeight = 22f;
    private const float IdleFlightMinHorizontalOffset = 12f;
    private const float IdleFlightMaxHorizontalOffset = 20f;
    private static readonly TimeSpan UnreachableCenterFallbackDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LandingTimeout = TimeSpan.FromSeconds(18);
    private DateTime preparationStartedAt = DateTime.MinValue;
    private DateTime preparationLastActionAt = DateTime.MinValue;
    private DateTime preparationInteractionAt = DateTime.MinValue;
    private DateTime preparationTalkStartedAt = DateTime.MinValue;
    private int preparationTalkCallbackAttempts;
    private bool preparationTextAdvanceEnabled;
    private ulong? preparationTargetObjectId;
    private DateTime escortFollowLastRequestAt = DateTime.MinValue;
    private Vector3? escortFollowLastPosition;
    private BossCleanupContinuation bossCleanupContinuation;
    private ushort? bossCleanupFateId;
    private ulong? bossCleanupTargetId;
    private DateTime bossCleanupNoTargetSince = DateTime.MinValue;
    private DateTime bossCancelSyncStartedAt = DateTime.MinValue;
    private DateTime bossEnvironmentLogAt = DateTime.MinValue;
    private bool bossPreCombatCleanupChecked;
    private bool teleportIssuedByUs;
    private bool teleportBusyObserved;
    private AetheryteTravelPlan? fateAetheryteTeleportPlan;
    private bool aethernetTeleportIssuedByUs;
    private uint aethernetDestinationPlaceNameId;
    private DateTime aethernetTeleportStartedAt = DateTime.MinValue;
    private bool navigationRecoveryTeleportIssued;
    private int navigationRecoveryAttempts;
    private bool navigationOnlyRequested;
    private ushort? navigationOnlyFateId;
    private bool temporaryTargetActive;
    private ushort temporaryTargetFateId;
    private AutomationMode temporaryPreviousMode;
    private ushort? temporaryPreviousTargetFateId;
    private List<ushort> temporaryPreviousTargetFateIds = [];
    private bool temporaryPreviousEnabled;
    private bool raiseAcceptIssued;
    private bool disposed;
    private string statusReason = "未启动";
    private IReadOnlyList<FateCandidateSnapshot> candidateSnapshot = [];

    public FateAutomationController(
        AutoFatreConfiguration configuration,
        IPluginLog log,
        IChatGui chat,
        IFramework framework,
        IClientState clientState,
        IPlayerState playerState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        IPartyList partyList,
        IAetheryteList aetheryteList,
        AetheryteTravelPlanner aetheryteTravelPlanner,
        IDataManager dataManager,
        VnavmeshIpc vnavmesh,
        LifestreamIpc lifestream,
        FateRepository fates,
        StaticFateCatalog staticFateCatalog,
        FateTargetSelector targetSelector,
        InventoryCounter inventoryCounter,
        CompanionAdapter companion,
        System.Action persistConfiguration,
        MountAdapter mount,
        LandingAdapter landing,
        DutyAggroResetAdapter dutyAggroReset,
        SoundAlertAdapter soundAlerts,
        TextAdvanceIpc textAdvance,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui)
    {
        this.configuration = configuration;
        this.log = log;
        this.chat = chat;
        this.framework = framework;
        this.clientState = clientState;
        this.playerState = playerState;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.condition = condition;
        this.partyList = partyList;
        this.aetheryteList = aetheryteList;
        this.aetheryteTravelPlanner = aetheryteTravelPlanner;
        this.dataManager = dataManager;
        this.gameGui = gameGui;
        this.vnavmesh = vnavmesh;
        this.lifestream = lifestream;
        this.fates = fates;
        this.staticFateCatalog = staticFateCatalog;
        this.targetSelector = targetSelector;
        this.inventoryCounter = inventoryCounter;
        this.companion = companion;
        this.persistConfiguration = persistConfiguration;
        this.mount = mount;
        this.landing = landing;
        this.travelSession = new NavigationTravelSession(vnavmesh, landing, mount);
        this.noFlyZones = new NoFlyZoneCatalog(dataManager);
        this.dutyAggroReset = dutyAggroReset;
        this.soundAlerts = soundAlerts;
        this.textAdvance = textAdvance;
        this.levelSync = new LevelSyncAdapter(addonLifecycle, message => this.AddDiagnostic(DiagnosticSeverity.Debug, message));
        this.deathRecovery = new DeathRecoveryAdapter(gameGui);
        this.clientFateDefinitions = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Fate>()
            .Where(row => row.RowId is > 0 and <= ushort.MaxValue)
            .ToDictionary(
                row => (ushort)row.RowId,
                row => new ClientFateDefinition(
                    row.Name.ToString(),
                    FateCombatProfile.From(row.Rule, row.Icon)));

        this.framework.Update += this.OnFrameworkUpdate;
        this.clientState.Login += this.OnLogin;
        this.clientState.Logout += this.OnLogout;
        this.clientState.TerritoryChanged += this.OnTerritoryChanged;
    }

    public AutomationState State => this.state;
    public bool IsRunning => (this.configuration.Enabled && this.state is not AutomationState.Stopped) || this.manualDutyRoundTrip;
    public bool IsPaused => this.state is AutomationState.Paused or AutomationState.Faulted;
    public string StatusReason => this.statusReason;
    public ushort? ActiveFateIdSnapshot => this.activeFateId ?? this.pendingFateResult?.Fate.FateId;
    public string? ActiveFateNameSnapshot => this.activeFateId is { } id
        ? this.fates.Find(id)?.Name
        : this.pendingFateResult?.Fate.Name;
    public string ActiveFateCombatKind => this.activeFateId is { } id
        ? this.fates.Find(id)?.CombatKind ?? "未知"
        : this.pendingFateResult?.Fate.CombatKind ?? "未选择";
    public uint CurrentTerritory => this.clientState.TerritoryType;
    public uint DesiredTerritory => this.GetCurrentPlan().TerritoryId;
    public bool IsVnavmeshAvailable => this.vnavmesh.IsAvailable;
    public bool IsVnavmeshPathActive => this.vnavmesh.IsMoveActive;
    public bool IsLifestreamAvailable => this.lifestream.IsAvailable;
    public bool IsLifestreamBusy => this.lifestream.IsBusy;
    public bool IsMounted => this.mount.IsMounted;
    public bool IsInFlight => this.mount.IsInFlight;
    public bool IsLandingInputActive => this.landing.IsDescending;
    public bool IsLevelSynced => this.playerState.IsLevelSynced;
    public bool IsStaticFateCatalogAvailable => this.staticFateCatalog.IsAvailable;
    public string? StaticFateCatalogLoadError => this.staticFateCatalog.LoadError;
    public int StaticFateCatalogFateCount => this.staticFateCatalog.FateCount;
    public int StaticFateCatalogCollectionCount => this.staticFateCatalog.CollectionCount;
    public int AggroCount => this.aggroCount;
    public int PresetIndex => this.presetIndex;
    public int PresetCompletedFates => this.presetCompletedFates;
    public int TotalCompletedFates => this.totalCompletedFates;
    public IReadOnlyList<FateCandidateSnapshot> CandidateSnapshot => this.candidateSnapshot;
    public IReadOnlyList<DiagnosticEntry> Diagnostics => this.diagnostics.ToArray();
    public IReadOnlyList<DeathRecord> DeathRecords => this.configuration.DeathRecords;
    public IReadOnlyList<FateFailureRecord> FateFailureRecords => this.configuration.FateFailureRecords;
    public bool HasChocoboCompanion => this.companion.HasChocobo;
    public float ChocoboTimeLeft => this.companion.ChocoboTimeLeft;
    public int GysahlGreensCount => this.companion.GysahlGreensCount;
    public string CurrentMinionName => this.companion.CurrentMinionName;
    public string CurrentMainHandName => this.companion.CurrentMainHandName;
    public bool AutoSummonChocoboCompanion => this.configuration.AutoSummonChocoboCompanion;
    public AutomationMode CurrentMode => this.configuration.Mode;
    public ushort? ConfiguredTargetFateId => this.configuration.TargetFateIds.FirstOrDefault() is { } first && first != 0
        ? first
        : null;
    public IReadOnlyList<ushort> ConfiguredTargetFateIds => this.configuration.TargetFateIds
        .Where(id => id != 0)
        .ToArray();
    public uint ConfiguredSingleMapTerritoryId => this.configuration.SingleMapTerritoryId;
    public int ConfiguredTargetFateCompleted => this.configuration.Mode == AutomationMode.TargetFate
        && this.configuration.TargetFateIds.Count > 0
        ? this.configuration.TargetFateIds
            .Where(id => id != 0)
            .SelectMany(this.ExpandFateSelection)
            .Distinct()
            .Sum(fateId => this.presetTargetFateCounts.GetValueOrDefault(fateId))
        : 0;
    public int ConfiguredTargetFateTotal => this.configuration.Mode == AutomationMode.TargetFate
        ? this.configuration.TargetFateIds
            .Where(id => id != 0)
            .SelectMany(this.ExpandFateSelection)
            .Distinct()
            .Count()
        : 0;

    public int GetPresetStopConditionProgress(int mapIndex, StopCondition stop)
    {
        IReadOnlyList<MapPreset> maps = this.GetPresetMaps();
        if (this.configuration.Mode != AutomationMode.PresetSequence
            || mapIndex < 0
            || mapIndex >= maps.Count)
            return 0;

        int target = Math.Max(1, stop.Kind == StopConditionKind.ItemCount ? stop.ItemCount : stop.FateCount);
        if (mapIndex < this.presetIndex)
            return target;
        if (mapIndex > this.presetIndex)
            return 0;

        return stop.Kind switch
        {
            StopConditionKind.FateCount => this.presetCompletedFates,
            StopConditionKind.ItemCount => stop.ItemId == 0 ? 0 : this.inventoryCounter.Count(stop.ItemId),
            StopConditionKind.TargetFate => this.ExpandFateSelection(stop.TargetFateId)
                .Sum(fateId => this.presetTargetFateCounts.GetValueOrDefault(fateId)),
            _ => 0,
        };
    }

    public void NavigateToFateForUi(ushort fateId)
    {
        if (this.fates.Find(fateId) is not { } fate)
            return;

        this.CancelOwnedActions();
        this.navigationOnlyRequested = true;
        this.navigationOnlyFateId = fateId;
        this.configuration.Enabled = true;
        this.SelectFate(fate, "UI 请求导航至 FATE");
    }

    public void SetTemporaryTargetFateForUi(ushort fateId)
    {
        if (this.fates.Find(fateId) is not { } fate)
            return;

        bool wasInCombat = this.IsCombatEngaged();
        ushort? previousFateId = this.activeFateId;
        Vector3? previousFatePosition = previousFateId is { } activeFateId
            ? this.fates.Find(activeFateId)?.Position
            : null;

        if (!this.temporaryTargetActive)
        {
            this.temporaryTargetActive = true;
            this.temporaryTargetFateId = fateId;
            this.temporaryPreviousMode = this.configuration.Mode;
            this.temporaryPreviousTargetFateId = this.configuration.TargetFateId;
            this.temporaryPreviousTargetFateIds = this.configuration.TargetFateIds.ToList();
            this.temporaryPreviousEnabled = this.configuration.Enabled;
        }

        this.CancelOwnedActions();
        this.ClearTarget();
        this.configuration.TargetFateIds = [fateId];
        this.configuration.TargetFateId = fateId;
        this.configuration.Mode = AutomationMode.TargetFate;
        this.configuration.Enabled = true;
        this.SelectFate(fate, "UI 临时目标 FATE");
        if (wasInCombat)
        {
            this.preemptSyncFateId = previousFateId;
            this.BeginCombatCleanup(
                CleanupContinuation.PreemptTravel,
                "临时目标 FATE 已指定；立即停止当前接战并跑离，再前往临时目标");
            this.cleanupEscapeOrigin = previousFatePosition;
        }
    }

    public void Start()
    {
        this.configuration.Migrate();
        this.configuration.Normalize();
        this.ClearCurrentMapTerritory();
        this.configuration.Enabled = true;
        this.presetIndex = 0;
        this.presetCompletedFates = 0;
        this.presetTargetFateCounts.Clear();
        this.totalCompletedFates = 0;
        // Capture a "current map" target as soon as a run starts. If the client is still
        // loading, ResolvePlanTerritory will capture it on the first usable framework tick.
        this.GetCurrentPlan();
        this.CancelOwnedActions();
        this.ResetCurrentActivity();
        this.entryPetSummonedTerritory = null;
        this.entryPetRetryAt = DateTime.MinValue;
        this.idleFlightRaised = false;
        this.idleFlightDiagnosticAt = DateTime.MinValue;
        this.teleportArrival.CancelRequest();
        this.mapArrivalCheckPending = false;
        this.preparingGroundArrivalPending = false;
        this.ResetFailedTeleportCandidates();
        this.startupGroundCheckPending = this.objectTable.LocalPlayer is not null
            && TravelSafetyRules.CanCheckAtStartup(this.mount.IsInFlight, this.mount.IsJumping, this.mount.IsMountTransition);
        this.startupFateRangeSelectionPending = true;
        this.Transition(AutomationState.WaitingForLogin, "自动化已启动；按当前位置规划赶路与检查时机");
    }

    public void Stop(string reason = "已由用户停止")
    {
        bool restoreTemporary = this.temporaryTargetActive;
        this.configuration.Enabled = false;
        this.startupGroundCheckPending = false;
        this.startupFateRangeSelectionPending = false;
        this.mapArrivalCheckPending = false;
        this.preparingGroundArrivalPending = false;
        this.CancelOwnedActions();
        this.ResetCurrentActivity();
        if (restoreTemporary)
            this.RestoreTemporaryTarget(reason, restoreEnabled: false);
        this.ClearCurrentMapTerritory();
        this.Transition(AutomationState.Stopped, reason);
    }

    public void Pause(string reason = "已由用户暂停")
    {
        this.CancelOwnedActions();
        this.ResetManualDutyRoundTripState();
        this.ClearTarget();
        this.Transition(AutomationState.Paused, reason, DiagnosticSeverity.Warning);
    }

    public void Retry()
    {
        if (!this.configuration.Enabled)
            this.configuration.Enabled = true;

        this.recoveryAttempts = 0;
        this.navigationRecoveryAttempts = 0;
        this.navigationRecoveryTeleportIssued = false;
        this.navigationRecoveryStartedAt = DateTime.MinValue;
        this.levelSyncAttempts = 0;
        this.Transition(
            this.pendingFateResult is null ? AutomationState.ValidatingPlan : AutomationState.CleaningUpCombat,
            this.pendingFateResult is null ? "重新验证并继续" : "继续战后清场与结算");
    }

    public void RequestDutyRoundTrip() => _ = this.framework.RunOnFrameworkThread(() =>
    {
        if (this.disposed)
            return;

        if (this.state == AutomationState.ResettingAggroViaDuty)
        {
            this.AddDiagnostic(DiagnosticSeverity.Warning, "泰坦副本进退已经在执行，忽略重复请求");
            return;
        }

        if (!this.clientState.IsLoggedIn || !this.playerState.IsLoaded || this.objectTable.LocalPlayer is null)
        {
            this.statusReason = "无法执行副本进退：角色尚未登录或玩家对象未加载";
            this.AddDiagnostic(DiagnosticSeverity.Warning, this.statusReason);
            return;
        }

        if (this.IsBetweenAreas())
        {
            this.statusReason = "无法执行副本进退：当前正在切换区域";
            this.AddDiagnostic(DiagnosticSeverity.Warning, this.statusReason);
            return;
        }

        if (this.partyList.Length > 1)
        {
            this.statusReason = "无法执行副本进退：当前处于小队中，为避免带其他玩家进入副本已拒绝执行";
            this.AddDiagnostic(DiagnosticSeverity.Warning, this.statusReason);
            return;
        }

        if (this.dutyAggroReset.IsInTitanDuty
            || this.condition[ConditionFlag.BoundByDuty]
            || this.condition[ConditionFlag.BoundByDuty56]
            || this.condition[ConditionFlag.BoundByDuty95])
        {
            this.statusReason = "无法执行副本进退：当前已经处于副本中";
            this.AddDiagnostic(DiagnosticSeverity.Warning, this.statusReason);
            return;
        }

        if (!this.dutyAggroReset.Validate(out string validationError))
        {
            this.statusReason = $"无法执行副本进退：{validationError}";
            this.AddDiagnostic(DiagnosticSeverity.Warning, this.statusReason);
            return;
        }

        if (!this.dutyAggroReset.QueueIssuedByUs
            && this.dutyAggroReset.QueueState != ContentsFinderQueueState.None)
        {
            this.statusReason = $"无法执行副本进退：检测到已有副本队列（{this.dutyAggroReset.QueueState}），不会接管";
            this.AddDiagnostic(DiagnosticSeverity.Warning, this.statusReason);
            return;
        }

        bool shouldResumeAutomation = this.configuration.Enabled;
        this.CancelOwnedActions();
        this.ResetCurrentActivity();
        this.manualDutyRoundTrip = true;
        this.resumeAutomationAfterManualDuty = shouldResumeAutomation;
        this.BeginAggroDutyReset(DateTime.UtcNow, manual: true);
    });

    /// <summary>
    /// Test-only UI action. It uses the same adapter as the Boss emergency recovery path and
    /// deliberately does not change the automation state, so the diagnostic page can show the
    /// native state transition before the caller decides whether to resume automation.
    /// </summary>
    public void RequestCancelLevelSync() => _ = this.framework.RunOnFrameworkThread(() =>
    {
        if (this.disposed)
            return;

        ushort? fateId = this.activeFateId;
        if (fateId is null)
        {
            NativeFateManager* manager = NativeFateManager.Instance();
            if (manager is not null && manager->CurrentFate is not null)
                fateId = manager->GetCurrentFateId();
        }

        if (fateId is null or 0)
        {
            this.statusReason = "取消等级同步失败：当前没有可识别的 FATE";
            this.AddDiagnostic(DiagnosticSeverity.Warning, this.statusReason);
            return;
        }

        bool canceled = this.levelSync.TryCancelSync(fateId.Value);
        this.statusReason = canceled
            ? $"已请求取消 FATE #{fateId.Value} 等级同步（等待客户端状态刷新）"
            : $"取消 FATE #{fateId.Value} 等级同步失败";
        this.AddDiagnostic(canceled ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning, this.statusReason);
    });

    /// <summary>Testing action: cancel the current Boss FATE sync, then immediately clean the
    /// expanded Boss-area non-FATE set and resync before resuming the Boss handler.</summary>
    public void RequestCancelSyncAndCleanBossAreaTest() => _ = this.framework.RunOnFrameworkThread(() =>
    {
        if (this.disposed)
            return;

        FateSnapshot? configuredFate = this.ResolveActiveFate() ?? this.lastActiveFateSnapshot;
        if (configuredFate?.CombatProfile.Strategy != FateCombatStrategyKind.Boss
            || this.state is AutomationState.CleaningUpCombat
                or AutomationState.CleaningBossNonFate
                or AutomationState.Stopped
                or AutomationState.Paused)
        {
            this.statusReason = $"Boss 周围清理测试只能在讨伐BOSS流程执行（当前状态={this.state}，当前 FATE={configuredFate?.FateId.ToString() ?? "无"}）";
            this.AddDiagnostic(DiagnosticSeverity.Warning, this.statusReason);
            return;
        }

        FateSnapshot? fate = this.ResolveActiveFate();
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        IBattleNpc? boss = fate is null || player is null
            ? null
            : this.targetSelector.FindBossTarget(fate.FateId, player.Position);
        if (fate is null || player is null || boss is null)
        {
            this.statusReason = "Boss 周围清理测试失败：当前没有可识别的 Boss FATE 或 Boss 目标";
            this.AddDiagnostic(DiagnosticSeverity.Warning, this.statusReason);
            return;
        }

        if (!this.levelSync.TryCancelSync(fate.FateId))
        {
            this.statusReason = $"Boss 周围清理测试失败：无法取消 FATE #{fate.FateId} 等级同步";
            this.AddDiagnostic(DiagnosticSeverity.Warning, this.statusReason);
            return;
        }

        float radius = BossNonFateCleanupRadius;
        IReadOnlyList<IBattleNpc> targets = this.targetSelector.FindNonFateTargetsNear(fate.FateId, boss.Position, radius);
        this.AddDiagnostic(
            DiagnosticSeverity.Warning,
            $"测试按钮已触发 Boss 周围非 FATE 清理：Boss={boss.Name}({boss.GameObjectId:X})，范围={radius:0.0}，目标数={targets.Count}；目标={FormatNonFateTargets(targets)}");
        this.bossCancelSyncStartedAt = DateTime.UtcNow;
        this.BeginBossNonFateCleanup(
            fate,
            BossCleanupContinuation.ManualTestResync,
            $"测试按钮：已取消等级同步，立即清理 Boss 周围 {targets.Count} 个非 FATE 目标");
    });

    /// <summary>Plays the currently configured game sound immediately for in-game verification.</summary>
    public void PreviewSoundAlert(uint soundEffectId) => _ = this.framework.RunOnFrameworkThread(() =>
    {
        if (this.disposed)
            return;

        try
        {
            soundEffectId = Math.Clamp(soundEffectId, 0u, 16u);
            if (soundEffectId == 0)
            {
                this.AddDiagnostic(DiagnosticSeverity.Information, "试听音效已设为无音效");
                return;
            }

            this.soundAlerts.Play(soundEffectId);
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"已试听游戏内置音效（音效 {soundEffectId}）");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "试听游戏内置音效失败");
        }
    });

    public void RequestLogScan() => _ = this.framework.RunOnFrameworkThread(() =>
    {
        this.fates.Refresh();
        this.RefreshCandidateSnapshot();
        foreach (FateCandidateSnapshot fate in this.candidateSnapshot)
        {
            this.AddDiagnostic(DiagnosticSeverity.Information,
                $"FATE {fate.FateId} '{fate.Name}' state={fate.State} rule={fate.Rule} icon={fate.IconId} kind={fate.CombatKind} eligible={fate.IsEligible} reason={fate.EligibilityReason} progress={fate.Progress}% remaining={fate.TimeRemaining}s distance={fate.Distance:0.0} score={fate.Score:0.0} distanceWeight={fate.DistanceWeight:0.0} timeWeight={fate.TimeWeight:0.0}");

            if (this.staticFateCatalog.TryGet(fate.FateId, out StaticFateCatalogEntry staticEntry))
            {
                this.AddDiagnostic(DiagnosticSeverity.Information,
                    $"FATE {fate.FateId} 静态目录：地图={staticEntry.MapName} 合集=[{string.Join(',', staticEntry.CollectionIds)}] 破坏=[{string.Join('、', staticEntry.Targets.Destroy)}] 守护/保护=[{string.Join('、', staticEntry.Targets.Protect)}] 收集=[{string.Join('、', staticEntry.Targets.Collect)}] 护送=[{string.Join('、', staticEntry.Targets.Escort)}] 讨伐=[{string.Join('、', staticEntry.Targets.Defeat)}]");
            }
            else
            {
                this.AddDiagnostic(DiagnosticSeverity.Debug, $"FATE {fate.FateId} 未在静态目录中对账，继续仅使用运行时数据");
            }

            FateSnapshot? nativeFate = this.fates.Find(fate.FateId);
            if (nativeFate is null)
                continue;

            this.AddDiagnostic(DiagnosticSeverity.Information,
                $"FATE {nativeFate.FateId} native objectiveNpc={nativeFate.ObjectiveNpc} (0x{nativeFate.ObjectiveNpc:X}) motivationNpc={nativeFate.MotivationNpc} (0x{nativeFate.MotivationNpc:X}) startTimeEpoch={nativeFate.StartTimeEpoch} duration={nativeFate.Duration}s timerStarted={nativeFate.HasStartedTimer}");
            foreach (FateObjectiveSnapshot objective in nativeFate.Objectives)
                this.AddDiagnostic(
                    DiagnosticSeverity.Information,
                    $"FATE {nativeFate.FateId} native objective icon={objective.IconId} targetMarkerLayoutId={objective.TargetMarkerLayoutId} position=({objective.Position.X:0.00},{objective.Position.Y:0.00},{objective.Position.Z:0.00}) flags=0x{objective.Flags:X}");
            foreach (IGameObject nearby in this.objectTable.Where(o =>
                         o is not null
                         && Vector3.Distance(o.Position, nativeFate.Position) <= Math.Max(100f, nativeFate.Radius * 2f)
                         && o.ObjectKind is ObjectKind.EventNpc or ObjectKind.BattleNpc))
            {
                this.AddDiagnostic(DiagnosticSeverity.Information,
                    $"FATE {nativeFate.FateId} nearby opener object={nearby.GameObjectId:X} kind={nearby.ObjectKind} name='{nearby.Name}' baseId={nearby.BaseId} targetable={nearby.IsTargetable} distance={Vector3.Distance(nearby.Position, nativeFate.Position):0.0}");
            }
            Vector3 origin = this.objectTable.LocalPlayer?.Position ?? nativeFate.Position;
            foreach (FateTargetDiagnostic target in this.targetSelector.DescribeTargets(
                         nativeFate.FateId,
                         origin,
                         this.objectTable.LocalPlayer))
            {
                this.AddDiagnostic(DiagnosticSeverity.Information,
                    $"FATE {nativeFate.FateId} target '{target.Name}' object={target.GameObjectId:X} nameId={target.NameId} baseId={target.BaseId} layoutId={target.LayoutId} hp={target.CurrentHp}/{target.MaxHp} friendly={target.IsFriendly} namePlateKind={target.NamePlateKind} attackable={target.IsAttackable} targetObject={target.TargetObjectId:X} battleNpcKind={target.BattleNpcKind} targetsPlayer={target.TargetsPlayer} nativeObjectiveMatch={nativeFate.Objectives.Any(objective => objective.TargetMarkerLayoutId == target.LayoutId)}");
            }
            Vector3[] objectivePositions = nativeFate.Objectives
                .Select(objective => objective.Position)
                .Where(FateRepository.HasValidPosition)
                .ToArray();
            foreach (FateObjectDiagnostic target in this.targetSelector.DescribeFateObjects(
                         nativeFate.FateId,
                         nativeFate.Position,
                         nativeFate.Radius,
                         objectivePositions))
            {
                this.AddDiagnostic(DiagnosticSeverity.Information,
                    $"FATE {nativeFate.FateId} object kind={target.ObjectKind} name='{target.Name}' object={target.GameObjectId:X} nameId={target.NameId} baseId={target.BaseId} layoutId={target.LayoutId} fateId={target.FateId} targetable={target.IsTargetable} distance={target.Distance:0.0} nearObjective={target.NearObjective}");
            }
        }
    });

    public IReadOnlyList<string> ValidateConfiguration()
    {
        List<string> errors = [];
        IReadOnlyList<MapPreset> presetMaps = this.GetPresetMaps();
        if (this.configuration.Mode == AutomationMode.PresetSequence && presetMaps.Count == 0)
            errors.Add("自定义模式至少需要一个地图列表项。");
        if (this.configuration.Mode == AutomationMode.TargetFate
            && !this.configuration.TargetFateIds.Any(id => id != 0))
            errors.Add("指定 FATE 模式必须至少选择一个 FATE。");
        else if (this.configuration.Mode == AutomationMode.TargetFate)
        {
            foreach (ushort configuredTarget in this.configuration.TargetFateIds.Where(id => id != 0))
            {
                if (this.configuration.FateBlacklist.Contains(configuredTarget))
                {
                    errors.Add($"指定 FATE #{configuredTarget} 已加入黑名单，请先从黑名单移除或更换目标。");
                    continue;
                }

                if (!this.IsConfiguredFateSupported(configuredTarget, out string unsupportedReason))
                    errors.Add($"指定 FATE #{configuredTarget} 暂未支持：{unsupportedReason}。");
            }
        }

        IEnumerable<(uint Territory, string Label)> plans = this.configuration.Mode switch
        {
            AutomationMode.PresetSequence => presetMaps.Select((p, i) => (p.TerritoryId, $"地图 {i + 1}")),
            _ => [(this.configuration.SingleMapTerritoryId, "当前模式")],
        };

        foreach (var plan in plans)
        {
            if (plan.Territory == 0)
                continue;
            if (plan.Territory != this.clientState.TerritoryType
                     && this.ResolveTeleportAetheryte(plan.Territory, 0) is null)
            {
                if (this.ResolveAethernetDestination(plan.Territory) is not { } placeNameId)
                {
                    errors.Add($"{plan.Label} 没有可用的已解锁普通以太之光，也没有都市传送网节点。");
                }
                else if (this.ResolveTeleportAetheryte(this.ResolveIdyllshireTerritory(), 0) is null)
                {
                    errors.Add($"{plan.Label} 无直达以太之光；需要先经田园郡都市传送网，但田园郡未解锁。");
                }
            }
        }

        if (this.configuration.Mode == AutomationMode.PresetSequence)
        {
            for (int i = 0; i < presetMaps.Count; i++)
            {
                MapPreset map = presetMaps[i];
                if (map.StopConditions.Count == 0)
                    errors.Add($"地图 {i + 1} 至少需要一个停止条件，序列将无法前进。");
                foreach (StopCondition stop in map.StopConditions)
                {
                    if (stop.Kind == StopConditionKind.ItemCount && stop.ItemId == 0)
                        errors.Add($"地图 {i + 1} 的物品停止条件尚未选择物品。");
                    else if (stop.Kind == StopConditionKind.TargetFate && stop.TargetFateId == 0)
                        errors.Add($"地图 {i + 1} 的指定 FATE 停止条件尚未选择 FATE。");
                    else if (stop.Kind == StopConditionKind.TargetFate
                             && this.configuration.FateBlacklist.Contains(stop.TargetFateId))
                        errors.Add($"地图 {i + 1} 的指定 FATE 停止条件已被加入黑名单。");
                    else if (stop.Kind == StopConditionKind.TargetFate
                             && !this.IsConfiguredFateSupported(stop.TargetFateId, out string unsupportedReason))
                    {
                        errors.Add($"地图 {i + 1} 的指定 FATE 暂未支持：{unsupportedReason}。");
                    }
                }
            }
        }

        return errors;
    }

    private bool IsConfiguredFateSupported(ushort fateId, out string reason)
    {
        if (!this.clientFateDefinitions.TryGetValue(fateId, out ClientFateDefinition definition))
        {
            reason = $"客户端数据中找不到 FATE #{fateId}";
            return false;
        }

        reason = $"{definition.Name}（{definition.CombatProfile.SupportDescription}）";
        return definition.CombatProfile.IsSupported;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.framework.Update -= this.OnFrameworkUpdate;
        this.clientState.Login -= this.OnLogin;
        this.clientState.Logout -= this.OnLogout;
        this.clientState.TerritoryChanged -= this.OnTerritoryChanged;
        this.CancelOwnedActions();
        this.dutyAggroReset.Dispose();
        this.levelSync.Dispose();
        this.travelSession.Dispose();
        this.landing.Dispose();
        this.textAdvance.Dispose();
    }

    private void OnLogin()
    {
        if (this.state == AutomationState.Paused)
            return;
        // A duty transition can raise Login again after the client has loaded the
        // duty territory. Preserve the duty reset state for both automatic combat
        // recovery and the manual round-trip button; otherwise OnLogin below would
        // resume normal teleport/navigation logic inside the duty load window.
        if (this.manualDutyRoundTrip || this.state == AutomationState.ResettingAggroViaDuty)
        {
            if (this.state != AutomationState.ResettingAggroViaDuty)
                this.Transition(AutomationState.ResettingAggroViaDuty, "泰坦副本进退登录事件恢复，保持退出流程");
            this.statusReason = "手动泰坦副本进退等待角色数据恢复，保持退出流程";
            return;
        }
        this.Transition(
            this.configuration.Enabled ? AutomationState.ValidatingPlan : AutomationState.Stopped,
            "角色已登录");
    }

    private void OnLogout(int _, int __)
    {
        bool dutyResetInProgress = this.manualDutyRoundTrip
            || this.state == AutomationState.ResettingAggroViaDuty;
        if (dutyResetInProgress)
        {
            // A duty load can raise Logout before the destination territory is ready. Do not
            // clear the duty phase here: the next update must continue the exit sequence.
            this.StopNavigationOperation();
            this.landing.StopDescending();
            this.Transition(AutomationState.ResettingAggroViaDuty, "泰坦副本进退区域切换中，保持退出流程");
            return;
        }

        this.CancelOwnedActions();
        this.ResetCurrentActivity();
        if (this.configuration.Enabled && this.state != AutomationState.Paused)
            this.Transition(AutomationState.WaitingForLogin, "等待角色登录");
    }

    private void OnTerritoryChanged(uint territory)
    {
        this.teleportArrival.Observe(loading: true);
        this.entryPetSummonedTerritory = null;
        this.entryPetRetryAt = DateTime.MinValue;
        this.idleFlightRaised = false;
        this.idleFlightDiagnosticAt = DateTime.MinValue;
        this.returnAreaDiagnosticAt = DateTime.MinValue;
        bool deathRecoveryInProgress = this.state is AutomationState.DeadWaitingForRaise or AutomationState.DeadReturning;
        bool dutyResetInProgress = this.state == AutomationState.ResettingAggroViaDuty;
        bool navigationRecoveryInProgress = this.state == AutomationState.RecoveringNavigation;
        bool fateAetheryteTeleportInProgress = this.fateAetheryteTeleportPlan is not null;
        this.StopNavigationOperation();
        this.landing.StopDescending();
        this.pullTargetId = null;
        this.killTargetId = null;
        this.lostPriorityTargetId = null;
        this.lostPriorityTargetKind = null;
        this.priorityDestroyTargetId = null;
        this.destroySession.Reset();
        this.bossTargetId = null;
        this.bossCleanupFateId = null;
        this.bossCleanupTargetId = null;
        this.bossCleanupNoTargetSince = DateTime.MinValue;
        this.bossCancelSyncStartedAt = DateTime.MinValue;
        this.bossEnvironmentLogAt = DateTime.MinValue;
        this.bossPreCombatCleanupChecked = false;
        this.ResetPullBatch();
        this.escortFollowLastRequestAt = DateTime.MinValue;
        this.escortFollowLastPosition = null;
        this.cleanupTargetId = null;
        this.cleanupDiagnosticAt = DateTime.MinValue;
        this.preparationStartedAt = DateTime.MinValue;
        this.preparationLastActionAt = DateTime.MinValue;
        this.preparationInteractionAt = DateTime.MinValue;
        this.preparationTalkStartedAt = DateTime.MinValue;
        this.preparationTalkCallbackAttempts = 0;
        this.preparationTextAdvanceEnabled = false;
        this.preparationTargetObjectId = null;
        this.fateParticipationSince = DateTime.MinValue;
        this.fallbackLandingForParticipation = false;
        this.completedProgressObservedAt = DateTime.MinValue;
        this.activeFateParticipationObserved = false;
        this.terminalFateObservedAt = DateTime.MinValue;
        this.terminalFateState = null;
        if (!dutyResetInProgress && !navigationRecoveryInProgress && !fateAetheryteTeleportInProgress)
        {
            this.activeFateId = null;
            this.lastActiveFateSnapshot = null;
            this.activeFateMissingSince = DateTime.MinValue;
            this.pendingFateResult = null;
        }
        this.territoryStableAfter = DateTime.UtcNow.AddSeconds(2);
        this.AddDiagnostic(DiagnosticSeverity.Information, $"区域切换至 {territory}");
        if (dutyResetInProgress)
        {
            this.statusReason = "仇恨重置正在切换区域，等待区域稳定";
        }
        else if (navigationRecoveryInProgress)
        {
            this.statusReason = "导航恢复传送正在切换区域，等待区域稳定";
        }
        else if (fateAetheryteTeleportInProgress)
        {
            this.statusReason = "FATE 中转大水晶正在切换区域，等待区域稳定";
        }
        else if (deathRecoveryInProgress)
        {
            this.statusReason = "死亡恢复已开始切换区域，等待角色恢复可操作";
        }
        else if (this.configuration.Enabled && this.state != AutomationState.Paused)
            this.Transition(AutomationState.WaitingForTerritory, "等待区域加载稳定");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (this.disposed)
            return;

        DateTime now = DateTime.UtcNow;
        this.teleportArrival.Observe(this.IsBetweenAreas());
        bool manualDutyOperation = this.manualDutyRoundTrip;
        bool dutyResetOperation = manualDutyOperation || this.state == AutomationState.ResettingAggroViaDuty;
        if (!this.clientState.IsLoggedIn || !this.playerState.IsLoaded || this.objectTable.LocalPlayer is null)
        {
            if (dutyResetOperation)
            {
                this.statusReason = "泰坦副本进退等待区域角色数据恢复，不切换到普通自动化流程";
                return;
            }

            // A territory change temporarily clears the player object and can raise Logout.
            // Keep a user pause/fault intact while that happens; otherwise this branch would
            // move Paused to WaitingForLogin and OnLogin would resume automation afterwards.
            if (this.state is AutomationState.Paused or AutomationState.Faulted)
                return;

            if (this.configuration.Enabled && this.state != AutomationState.WaitingForLogin)
                this.Transition(AutomationState.WaitingForLogin, "等待角色数据");
            return;
        }

        if (this.manualDutyRoundTrip && this.state != AutomationState.ResettingAggroViaDuty)
        {
            this.Transition(AutomationState.ResettingAggroViaDuty, "泰坦副本进退检测到状态被区域事件改写，恢复退出流程");
        }

        if (now >= this.nextSnapshotAt)
        {
            this.fates.Refresh();
            this.RefreshCandidateSnapshot();
            this.LogNewFateTargetData();
            this.RemoveExpiredCooldowns(now);
            this.nextSnapshotAt = now.AddMilliseconds(500);
        }

        if ((!this.configuration.Enabled && !manualDutyOperation)
            || this.state is AutomationState.Stopped or AutomationState.Paused or AutomationState.Faulted)
            return;

        if (this.condition[ConditionFlag.Unconscious])
        {
            if (this.state is not AutomationState.DeadWaitingForRaise and not AutomationState.DeadReturning)
                this.EnterDeadState();
            this.HandleDeath(now);
            return;
        }

        if (this.state is AutomationState.DeadWaitingForRaise or AutomationState.DeadReturning)
        {
            this.deathStartedAt = DateTime.MinValue;
            if (this.pendingFateResult is not null)
                this.Transition(AutomationState.CleaningUpCombat, "角色已复活，继续完成战后清场与结算");
            else
            {
                this.ResetCurrentActivity();
                bool waitForTerritory = DateTime.UtcNow < this.territoryStableAfter || this.IsBetweenAreas();
                this.Transition(
                    waitForTerritory ? AutomationState.WaitingForTerritory : AutomationState.ValidatingPlan,
                    waitForTerritory ? "角色已复活，等待返回区域稳定" : "角色已复活，重新规划");
            }
        }

        // Planning can run both at startup and immediately after advancing a preset map.  If a
        // stale combat flag is still present, clear it before teleporting, but return to plan
        // validation afterwards. Returning directly to ScanningFates here would bypass the new
        // preset entry's territory check and continue farming on the previous map.
        if (this.condition[ConditionFlag.InCombat]
            && this.state is AutomationState.WaitingForLogin
                or AutomationState.ValidatingPlan
                or AutomationState.WaitingForDependency
                or AutomationState.WaitingForTerritory)
        {
            this.BeginCombatCleanup(
                CleanupContinuation.RevalidatePlan,
                "规划或切换地图前检测到战斗状态；先跑离并尝试脱战，脱战后重新校验目标地图");
            return;
        }

        if (this.state != AutomationState.ResettingAggroViaDuty && this.activeFateId is { } activeId)
        {
            FateSnapshot? active = this.fates.Find(activeId);
            if (active is not null)
            {
                this.lastActiveFateSnapshot = active;
                this.activeFateMissingSince = DateTime.MinValue;
                if (!this.activeFateParticipationObserved && IsPlayerInFate(activeId))
                {
                    this.activeFateParticipationObserved = true;
                    this.AddDiagnostic(
                        DiagnosticSeverity.Debug,
                        $"已确认玩家进入活动 FATE #{activeId}，后续目标消失可用于完成结算");
                }
            }
            if (active?.State is FateState.Ended or FateState.Failed)
            {
                if (this.terminalFateState != active.State)
                {
                    this.terminalFateState = active.State;
                    this.terminalFateObservedAt = now;
                    this.AddDiagnostic(
                        DiagnosticSeverity.Warning,
                        $"检测到 FATE #{active.FateId} 终止状态 {active.State}，开始连续确认（进度 {active.Progress}%；剩余 {active.TimeRemaining}s）");
                }

                if (now - this.terminalFateObservedAt >= TerminalFateConfirmationWindow)
                {
                    this.BeginFateCleanup(active, succeeded: active.State == FateState.Ended);
                    return;
                }

                this.statusReason = $"确认 FATE #{active.FateId} 终止状态，避免瞬时状态抖动误清场";
                this.nextActionAt = now.AddMilliseconds(250);
                return;
            }
            else
            {
                this.terminalFateState = null;
                this.terminalFateObservedAt = DateTime.MinValue;
            }

            // Collection FATEs get a higher-priority hand-in path only while the player still
            // holds native EventItem(s). At 100% the game keeps the FATE alive for about a
            // minute, but that minute is for other players to hand in — it is never a reason for
            // us to wait after our own items have already been submitted.
            if (active is not null
                && active.CombatProfile.Strategy == FateCombatStrategyKind.Collection
                && active.Progress >= 100
                && active.EventItem != 0
                && this.inventoryCounter.Count(active.EventItem) > 0)
            {
                if (!this.collectionTurnInUrgent)
                {
                    this.collectionTurnInUrgent = true;
                    this.AddDiagnostic(
                        DiagnosticSeverity.Warning,
                        $"收集 FATE #{active.FateId} 进度已达到 100%，立即中断收集/战斗并前往交付（剩余 {active.TimeRemaining}s）");
                }

                if (active.State is not FateState.Ended and not FateState.Failed)
                {
                    if (this.state != AutomationState.TurningInCollectionFate)
                        this.BeginCollectionTurnIn(active, now, urgent: true);
                    this.statusReason = $"收集 FATE 已完成，紧急交付（剩余 {active.TimeRemaining}s）";
                    if (this.state == AutomationState.TurningInCollectionFate)
                    {
                        this.nextActionAt = now;
                        this.HandleCollectionTurnIn(now);
                        return;
                    }
                }
            }

            // Some client revisions leave a completed FATE in Running until the next
            // director update. A stable 100% progress is enough to settle success, while
            // retaining the same confirmation window used by the explicit Ended state.
            if (active is not null && active.Progress >= 100)
            {
                if (this.completedProgressObservedAt == DateTime.MinValue)
                {
                    this.completedProgressObservedAt = now;
                    this.AddDiagnostic(
                        DiagnosticSeverity.Debug,
                        $"FATE #{active.FateId} 进度达到 100%，等待稳定确认完成");
                }
                else if (now - this.completedProgressObservedAt >= TerminalFateConfirmationWindow)
                {
                    this.BeginFateCleanup(
                        active,
                        succeeded: true,
                        completionReason: $"FATE 进度稳定达到 {active.Progress}%");
                    return;
                }

                this.statusReason = $"确认 FATE #{active.FateId} 已完成（进度 {active.Progress}%）";
                this.nextActionAt = now.AddMilliseconds(250);
                return;
            }

            this.completedProgressObservedAt = DateTime.MinValue;

            // A missing FATE is not normally proof of success. Some client revisions remove a
            // completed FATE before exposing its Ended state, so a stable disappearance after
            // 100% progress is the safe fallback completion signal.
            if (active is null && this.lastActiveFateSnapshot is { } last)
            {
                if (this.activeFateMissingSince == DateTime.MinValue)
                {
                    this.activeFateMissingSince = now;
                }
                if (now - this.activeFateMissingSince >= TimeSpan.FromSeconds(2))
                {
                    bool disappearedAfterParticipation = this.activeFateParticipationObserved
                        && last.TimeRemaining > 0;
                    if (last.Progress >= 100 || disappearedAfterParticipation)
                    {
                        this.AddDiagnostic(
                            DiagnosticSeverity.Information,
                            last.Progress >= 100
                                ? $"FATE #{last.FateId} 已从 IFateTable 消失且最后进度为 {last.Progress}%，按完成结算"
                                : $"FATE #{last.FateId} 已从 IFateTable 消失；玩家已进入且剩余时间为 {last.TimeRemaining}s，按完成结算");
                        this.BeginFateCleanup(
                            last,
                            succeeded: true,
                            completionReason: last.Progress >= 100
                                ? $"FATE 从列表消失，最后观测进度 {last.Progress}%"
                                : $"FATE 从列表消失，玩家已进入且最后剩余时间 {last.TimeRemaining}s");
                        return;
                    }

                    this.AddDiagnostic(
                        DiagnosticSeverity.Warning,
                        $"FATE #{last.FateId} 已从 IFateTable 消失，但未观察到 Ended（最后进度 {last.Progress}%）；放弃当前活动并重新扫描，不计为完成");
                    this.AbandonActiveFate("目标 FATE 消失且未确认 Ended 状态");
                    return;
                }

                // The FATE table can briefly remove an ended event before the confirmation
                // window has elapsed. Keep the active state alive during that window; falling
                // through to HandleCombat/HandleBossCombat would resolve the missing FATE as
                // an ordinary failure and discard a completion that is about to be confirmed.
                this.statusReason = last.Progress >= 100 || this.activeFateParticipationObserved
                    ? "FATE 已从列表消失，等待完成结算确认"
                    : "FATE 暂时从列表消失，等待状态确认";
                this.nextActionAt = now.AddMilliseconds(250);
                return;
            }
        }

        if (this.activeFateId is null
            && this.pendingFateResult is null
            && this.state == AutomationState.ScanningFates)
        {
            bool waitingForInventorySettlement = now < this.presetConditionRecheckUntil;
            if (this.CheckPresetStopCondition(logUnmet: waitingForInventorySettlement))
                return;

            if (waitingForInventorySettlement)
            {
                this.statusReason = "等待战利品进入背包后复查预设停止条件";
                this.nextActionAt = now.AddMilliseconds(250);
                return;
            }
        }

        this.TryPreemptForTargetFate();
        this.TryPreemptTravelForHigherScore(now);
        if (now < this.nextActionAt)
            return;

        if (this.travelObservationDelayPending)
        {
            this.travelObservationDelayPending = false;
            this.AddDiagnostic(DiagnosticSeverity.Debug, "FATE 观察延迟结束，继续执行当前流程");
        }

        switch (this.state)
        {
            case AutomationState.WaitingForLogin:
            case AutomationState.ValidatingPlan:
                this.ValidateAndContinue();
                break;
            case AutomationState.WaitingForDependency:
            case AutomationState.Teleporting:
                this.EnsureCorrectTerritory(now);
                break;
            case AutomationState.WaitingForTerritory:
                if (now >= this.territoryStableAfter && !this.IsBetweenAreas())
                    this.ValidateAndContinue();
                break;
            case AutomationState.CheckingCompanions:
                this.HandleCompanionCheckpoint(now);
                break;
            case AutomationState.ScanningFates:
                this.ScanAndSelectFate(now);
                break;
            case AutomationState.OpeningPreparingFate:
                this.HandlePreparingFate(now);
                break;
            case AutomationState.MountingForTravel:
                this.HandleMountForTravel(now);
                break;
            case AutomationState.NavigatingToFate:
                this.NavigateToActiveFate(now);
                break;
            case AutomationState.LandingForFate:
                this.HandleLandingForFate(now);
                break;
            case AutomationState.DismountingForFate:
                this.HandleDismountForFate(now);
                break;
            case AutomationState.WaitingForLevelSync:
                this.HandleLevelSync(now);
                break;
            case AutomationState.CollectingFateItems:
                this.HandleCollectionItems(now);
                break;
            case AutomationState.TurningInCollectionFate:
                this.HandleCollectionTurnIn(now);
                break;
            case AutomationState.PullingTargets:
            case AutomationState.Fighting:
                this.HandleCombat(now);
                break;
            case AutomationState.BossFighting:
                this.HandleBossCombat(now);
                break;
            case AutomationState.CleaningBossNonFate:
                this.HandleBossNonFateCleanup(now);
                break;
            case AutomationState.ReturningToFateArea:
                this.HandleReturnToFateArea(now);
                break;
            case AutomationState.CleaningUpCombat:
                this.HandleCombatCleanup(now);
                break;
            case AutomationState.ResettingAggroViaDuty:
                this.HandleAggroDutyReset(now);
                break;
            case AutomationState.RecoveringNavigation:
                this.HandleNavigationRecovery(now);
                break;
            case AutomationState.Recovering:
                this.RecoverCurrentActivity();
                break;
        }
    }

    private void BeginCompanionCheckpoint(
        CompanionCheckpointKind kind,
        AutomationState resumeState,
        string reason)
    {
        this.StopNavigationOperation();
        this.companionCheckpointKind = kind;
        this.companionResumeState = resumeState;
        this.companionCheckpointStartedAt = DateTime.UtcNow;
        this.companionEntryPetHandled = false;
        this.entryPetSummonedTerritory = null;
        this.entryPetRetryAt = DateTime.MinValue;
        this.companionChocoboHandled = !this.configuration.AutoSummonChocoboCompanion;
        // The periodic combat checkpoint only refreshes companion state. Gear changes are
        // reserved for the safe map/FATE arrival checkpoints and must never be attempted in
        // the middle of combat.
        this.entryWeaponHandled = kind == CompanionCheckpointKind.CombatPeriodic
            || this.GetCurrentPresetEntryWeaponId() == 0;
        this.companionActionDeadline = DateTime.UtcNow.AddSeconds(1);
        if (kind == CompanionCheckpointKind.CombatPeriodic)
            this.nextCompanionCheckAt = DateTime.UtcNow.AddSeconds(30);
        this.Transition(AutomationState.CheckingCompanions, reason);
        this.nextActionAt = DateTime.MinValue;
    }

    private void HandleCompanionCheckpoint(DateTime now)
    {
        if (this.manualDutyRoundTrip)
            return;

        if (this.companionCheckpointKind == CompanionCheckpointKind.FateArrival
            && this.ResolveActiveFate() is null)
        {
            this.AbandonActiveFate("伙伴检查期间目标 FATE 已失效");
            return;
        }

        if (this.IsBetweenAreas() || now < this.territoryStableAfter)
        {
            this.statusReason = "伙伴检查等待区域和角色状态稳定";
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        // These are checkpoints after travel events, never a reason to force landing/teleport.
        if (this.mount.IsInFlight || this.mount.IsJumping)
        {
            AutomationState resume = this.companionCheckpointKind == CompanionCheckpointKind.FateArrival
                ? AutomationState.LandingForFate : this.companionResumeState;
            this.ResetCompanionCheckpoint();
            this.Transition(resume, "检查时角色已离地，延后至下次传送完成或 FATE 落地/结束检查点");
            return;
        }

        this.landing.StopDescending();
        if (this.mount.IsMountTransition || this.mount.IsJumping)
        {
            this.statusReason = "伙伴检查前等待坐骑过渡/落地动作结束";
            this.nextActionAt = now.AddMilliseconds(100);
            return;
        }

        if (!this.mount.IsDismounted && this.companionCheckpointKind != CompanionCheckpointKind.CombatPeriodic)
        {
            bool sent = this.mount.TryDismount();
            this.statusReason = sent ? "伙伴检查前已请求下坐骑" : "伙伴检查前下坐骑请求未被接受，准备重试";
            this.AddDiagnostic(sent ? DiagnosticSeverity.Information : DiagnosticSeverity.Debug, this.statusReason);
            this.nextActionAt = now.AddMilliseconds(250);
            if (now - this.companionCheckpointStartedAt > LandingTimeout)
                this.FailCompanionCheckpoint("伙伴检查前 18 秒内未能下坐骑");
            return;
        }

        bool combatPeriodicCheckpoint = this.companionCheckpointKind == CompanionCheckpointKind.CombatPeriodic;
        if (this.condition[ConditionFlag.InCombat] && !combatPeriodicCheckpoint)
        {
            // Arrival/startup/post-FATE checkpoints are safe-state checkpoints.  If combat
            // starts before they run, they must enter the normal cleanup owner instead of
            // waiting here and pretending that combat is somebody else's responsibility.
            this.BeginCombatCleanup(
                CleanupContinuation.ResumeCompanionCheckpoint,
                "伙伴检查前检测到战斗，先执行完整战斗清场，再继续伙伴检查");
            return;
        }

        if (combatPeriodicCheckpoint && this.condition[ConditionFlag.InCombat])
        {
            // This checkpoint is deliberately invoked from the combat mainline.  It is only
            // for refreshing the companion and must not wait for combat to finish or transfer
            // ownership to the cleanup state.
            this.statusReason = "战斗中执行周期伙伴检查，继续召唤/续期伙伴";
        }

        if (!this.companionEntryPetHandled)
        {
            if (this.TrySummonEntryPet(now))
                return;
            if (this.entryPetSummonedTerritory != this.clientState.TerritoryType && now < this.companionActionDeadline)
                return;
            this.companionEntryPetHandled = true;
        }

        if (!this.entryWeaponHandled)
        {
            uint weaponId = this.GetCurrentPresetEntryWeaponId();
            if (this.companion.IsItemEquipped(weaponId))
            {
                this.entryWeaponHandled = true;
                this.AddDiagnostic(
                    DiagnosticSeverity.Debug,
                    $"指定武器已装备，跳过重复装备：ItemId={weaponId}");
                this.statusReason = $"指定武器已装备：ItemId={weaponId}";
                this.nextActionAt = now.AddMilliseconds(250);
                return;
            }

            bool equipped = this.companion.TryEquipItem(weaponId);
            this.entryWeaponHandled = equipped && now >= this.companionActionDeadline;
            this.AddDiagnostic(equipped ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
                equipped ? $"已请求装备地图预设指定武器：ItemId={weaponId}" : $"指定武器装备请求失败：ItemId={weaponId}");
            this.statusReason = equipped ? "指定武器装备请求已发送" : "等待指定武器装备请求重试";
            this.nextActionAt = now.AddMilliseconds(500);
            if (!equipped)
                return;
        }

        if (!this.companionChocoboHandled)
        {
            this.HandleChocoboCompanionCheckpoint(now);
            this.companionChocoboHandled = true;
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        AutomationState next = this.companionResumeState;
        if (this.companionCheckpointKind == CompanionCheckpointKind.FateArrival
            && this.ResolveActiveFate() is { } fate)
        {
            next = IsPreparingFate(fate)
                ? AutomationState.OpeningPreparingFate
                : AutomationState.WaitingForLevelSync;
            if (next == AutomationState.WaitingForLevelSync)
                this.levelSyncAttempts = 0;
        }

        CompanionCheckpointKind completedKind = this.companionCheckpointKind;
        this.ResetCompanionCheckpoint();
        this.Transition(next, $"伙伴检查点 {completedKind} 完成，继续主流程");
    }

    private void HandleChocoboCompanionCheckpoint(DateTime now)
    {
        bool hasChocobo = this.companion.HasChocobo;
        float timeLeft = this.companion.ChocoboTimeLeft;
        int gysahlCount = this.inventoryCounter.Count(CompanionAdapter.GysahlGreensItemId);
        if (!this.companion.NeedsChocobo)
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Debug,
                $"陆行鸟伙伴检查：无需召唤；Territory={this.clientState.TerritoryType}，" +
                $"HasChocobo={hasChocobo}，TimeLeft={timeLeft:0.0}s，基萨尔野菜={gysahlCount}，" +
                $"InCombat={this.condition[ConditionFlag.InCombat]}");
            return;
        }

        bool used = this.companion.TrySummonOrExtendChocobo(now);
        this.AddDiagnostic(
            used ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            $"陆行鸟伙伴操作：Territory={this.clientState.TerritoryType}，" +
            $"BetweenAreas={this.IsBetweenAreas()}，InCombat={this.condition[ConditionFlag.InCombat]}，" +
            $"HasChocobo={hasChocobo}，TimeLeft={timeLeft:0.0}s，基萨尔野菜={gysahlCount}，" +
            $"结果={used}，{this.companion.LastActionReason}");
    }

    private void FailCompanionCheckpoint(string reason)
    {
        CompanionCheckpointKind failedKind = this.companionCheckpointKind;
        this.landing.StopDescending();
        this.ResetCompanionCheckpoint();
        this.FailRecoverableAction(
            $"伙伴检查点 {failedKind} 无法满足落地/下坐骑前置条件：{reason}",
            required: true);
    }

    private void ResetCompanionCheckpoint()
    {
        this.companionResumeState = AutomationState.ScanningFates;
        this.companionCheckpointStartedAt = DateTime.MinValue;
        this.companionEntryPetHandled = false;
        this.companionChocoboHandled = false;
        this.entryWeaponHandled = false;
    }

    private bool TryBeginPeriodicCompanionCheckpoint(DateTime now, AutomationState resumeState)
    {
        if (!this.configuration.AutoSummonChocoboCompanion
            || now < this.nextCompanionCheckAt
            || this.manualDutyRoundTrip)
            return false;
        // The first combat tick starts the 30-second interval. Without this guard the
        // MinValue sentinel caused a checkpoint immediately on entering combat, and every
        // completed checkpoint was followed by another one because the deadline was never
        // advanced.
        if (this.nextCompanionCheckAt == DateTime.MinValue)
        {
            this.nextCompanionCheckAt = now.AddSeconds(30);
            return false;
        }
        this.BeginCompanionCheckpoint(CompanionCheckpointKind.CombatPeriodic, resumeState,
            "战斗中到达 30 秒伙伴检查点；检查完成后返回战斗");
        return true;
    }

    private uint GetCurrentPresetEntryWeaponId()
    {
        if (this.configuration.Mode != AutomationMode.PresetSequence)
            return 0;
        IReadOnlyList<MapPreset> maps = this.GetPresetMaps();
        return maps.Count == 0 ? 0 : maps[Math.Clamp(this.presetIndex, 0, maps.Count - 1)].EntryWeaponItemId;
    }

    private void ValidateAndContinue()
    {
        IReadOnlyList<string> errors = this.ValidateConfiguration();
        if (errors.Count > 0)
        {
            string reason = string.Join(" ", errors);
            if (errors.Any(error => error.Contains("没有可用的已解锁普通以太之光", StringComparison.Ordinal)
                                    || error.Contains("田园郡未解锁", StringComparison.Ordinal)))
                this.Stop(reason);
            else
                this.Pause(reason);
            return;
        }

        // Evaluate the current preset's stop conditions before resolving territory. This lets
        // startup skip already-satisfied map entries without teleporting there first.
        if (this.configuration.Mode == AutomationMode.PresetSequence
            && this.activeFateId is null
            && this.pendingFateResult is null
            && this.GetPresetMaps().Count > 0
            && this.CheckPresetStopCondition(logUnmet: true))
        {
            return;
        }

        this.EnsureCorrectTerritory(DateTime.UtcNow);
    }

    private void EnsureCorrectTerritory(DateTime now)
    {
        if (this.IsBetweenAreas() || now < this.territoryStableAfter)
        {
            this.Transition(AutomationState.WaitingForTerritory, "等待传送区域加载稳定");
            return;
        }
        if (this.startupFateRangeSelectionPending
            && this.SelectFateInCurrentRange(now) is { } startupFate)
        {
            this.BeginStartupFateImmediately(startupFate, now);
            return;
        }
        if (this.startupGroundCheckPending)
        {
            this.startupGroundCheckPending = false;
            if (TravelSafetyRules.CanCheckAtStartup(this.mount.IsInFlight, this.mount.IsJumping, this.mount.IsMountTransition))
            {
                this.BeginCompanionCheckpoint(CompanionCheckpointKind.StartupGround,
                    AutomationState.ValidatingPlan, "启动时角色在地面，检查宠物、指定武器与陆行鸟伙伴后继续规划");
                return;
            }
        }
        if (this.fateAetheryteTeleportPlan is not null)
        {
            this.EnsureFateAetheryteTeleport(now);
            return;
        }
        PlanContext plan = this.GetCurrentPlan();
        uint desiredTerritory = plan.TerritoryId;
        bool atDestination = this.clientState.TerritoryType == desiredTerritory;
        if (this.teleportArrival.Complete(atDestination, this.IsBetweenAreas(), this.lifestream.IsBusy))
            this.mapArrivalCheckPending = true;
        if (atDestination && !this.teleportArrival.Pending)
        {
            this.ResetFailedTeleportCandidates();
            this.AddDiagnostic(
                DiagnosticSeverity.Debug,
                $"已位于目标地图 Territory={desiredTerritory}，跳过传送至地图水晶，直接进入本地图流程");
            if (!this.vnavmesh.IsAvailable)
            {
                this.Transition(AutomationState.WaitingForDependency, "等待 vnavmesh 可用", DiagnosticSeverity.Warning);
                this.nextActionAt = now.AddSeconds(2);
                return;
            }

            this.teleportIssuedByUs = false;
            this.teleportBusyObserved = false;
            this.aethernetTeleportIssuedByUs = false;
            this.aethernetDestinationPlaceNameId = 0;
            this.aethernetTeleportStartedAt = DateTime.MinValue;
            this.recoveryAttempts = 0;
            if (this.mapArrivalCheckPending)
            {
                this.mapArrivalCheckPending = false;
                this.BeginCompanionCheckpoint(CompanionCheckpointKind.MapArrival,
                    AutomationState.ScanningFates, "大水晶传送完成，检查宠物、指定武器与陆行鸟伙伴");
            }
            else
                this.Transition(AutomationState.ScanningFates, "已在目标地图，直接选择目的地；抵达 FATE 落地后检查");
            return;
        }

        // Some outdoor territories have no unlocked/main aetheryte entry. Lifestream can
        // still reach them through the city aethernet after first arriving at Idyllshire.
        uint? aethernetDestination = this.ResolveAethernetDestination(desiredTerritory);
        bool needsIdyllshireFallback = this.ResolveTeleportAetheryte(desiredTerritory, 0) is null
            && aethernetDestination is not null;
        if (needsIdyllshireFallback)
        {
            if (this.clientState.TerritoryType == this.ResolveIdyllshireTerritory()
                && aethernetDestination is { } placeNameId)
            {
                // The first (normal teleport) hop has completed. Do not keep ownership of that
                // finished operation while issuing the second, aethernet hop.
                this.teleportIssuedByUs = false;
                this.teleportBusyObserved = false;
                this.EnsureAethernetFallback(now, desiredTerritory, placeNameId);
                return;
            }

            // Continue through the ordinary Lifestream teleport branch below, but redirect its
            // first hop to the unlocked Idyllshire crystal.
            desiredTerritory = this.ResolveIdyllshireTerritory();
        }

        if (!this.lifestream.IsAvailable)
        {
            this.Transition(AutomationState.WaitingForDependency, "等待 Lifestream 可用", DiagnosticSeverity.Warning);
            this.nextActionAt = now.AddSeconds(2);
            return;
        }

        if (this.teleportIssuedByUs)
        {
            bool teleportBusy = this.lifestream.IsBusy;
            if (teleportBusy)
                this.teleportBusyObserved = true;

            // Lifestream can return true for the request and then lose the request when
            // teleporting is interrupted (cancelled, mounted action, network hiccup, etc.).
            // Do not leave the state latched for the full hard timeout in that case.
            if (!this.IsBetweenAreas() && !teleportBusy
                && now - this.teleportStartedAt >= TimeSpan.FromSeconds(15))
            {
                bool wasBusy = this.teleportBusyObserved;
                this.lifestream.Abort();
                this.teleportArrival.CancelRequest();
                this.teleportIssuedByUs = false;
                this.teleportBusyObserved = false;
                this.MarkIssuedTeleportCandidateFailed(
                    desiredTerritory,
                    wasBusy
                        ? "Lifestream 传送 15 秒内未进入区域切换，判定当前默认水晶不可用"
                        : "Lifestream 传送请求 15 秒内未进入忙碌/区域切换，判定当前默认水晶不可用");
                return;
            }

            if (now - this.teleportStartedAt > TimeSpan.FromSeconds(120))
            {
                this.lifestream.Abort();
                this.teleportArrival.CancelRequest();
                this.teleportIssuedByUs = false;
                this.teleportBusyObserved = false;
                this.MarkIssuedTeleportCandidateFailed(desiredTerritory, "Lifestream 传送 120 秒超时，判定当前默认水晶不可用");
            }
            return;
        }

        if (this.IsBetweenAreas() || this.lifestream.IsBusy)
        {
            this.Transition(AutomationState.Teleporting, "等待当前传送/区域切换结束");
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        this.PrepareTeleportCandidates(desiredTerritory);
        IAetheryteEntry? teleportTarget = this.ResolveNextTeleportAetheryte(desiredTerritory);
        if (teleportTarget is null)
        {
            this.Stop(needsIdyllshireFallback
                ? $"目标地图 {plan.TerritoryId} 无直达以太之光，且未找到已解锁的田园郡入口或目标都市传送网节点。"
                : $"目标地图 {desiredTerritory} 没有可用的已解锁以太之光。");
            return;
        }

        this.StopNavigationOperation();
        this.landing.StopDescending();
        if (this.vnavmesh.IsMoveActive)
            return;
        if (!this.lifestream.Teleport(teleportTarget.AetheryteId, teleportTarget.SubIndex))
        {
            this.failedTeleportCandidates.Add((teleportTarget.AetheryteId, teleportTarget.SubIndex));
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                $"Lifestream 拒绝传送至默认以太之光 {teleportTarget.AetheryteId}:{teleportTarget.SubIndex}，准备尝试下一个已解锁入口");
            this.nextActionAt = now;
            return;
        }

        this.teleportIssuedByUs = true;
        this.teleportBusyObserved = false;
        this.teleportIssuedAetheryteId = teleportTarget.AetheryteId;
        this.teleportIssuedSubIndex = teleportTarget.SubIndex;
        this.teleportStartedAt = now;
        this.teleportArrival.Requested();
        this.Transition(
            AutomationState.Teleporting,
            $"正在传送至地图 {desiredTerritory}（以太之光 {teleportTarget.AetheryteId}:{teleportTarget.SubIndex}）");
    }

    private void EnsureFateAetheryteTeleport(DateTime now)
    {
        AetheryteTravelPlan? plan = this.fateAetheryteTeleportPlan;
        if (plan is null)
            return;

        if (this.IsCombatEngaged())
        {
            this.preemptSyncFateId = this.activeFateId;
            this.BeginCombatCleanup(
                CleanupContinuation.PreemptTravel,
                "前往 FATE 前检测到仍在战斗，先跑离脱战后再执行水晶中转");
            return;
        }

        if (this.clientState.TerritoryType != plan.TerritoryId)
        {
            this.fateAetheryteTeleportPlan = null;
            this.teleportArrival.CancelRequest();
            this.teleportIssuedByUs = false;
            this.teleportBusyObserved = false;
            this.FailRecoverableAction(
                $"FATE 中转大水晶抵达了错误地图：期望 {plan.TerritoryId}，实际 {this.clientState.TerritoryType}",
                required: this.activeFateId is { } fateId && this.IsRequiredFate(fateId));
            return;
        }

        if (this.IsBetweenAreas() || now < this.territoryStableAfter)
        {
            this.Transition(AutomationState.WaitingForTerritory, "等待 FATE 中转大水晶区域加载稳定");
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        if (!this.lifestream.IsAvailable)
        {
            this.Transition(AutomationState.WaitingForDependency, "等待 Lifestream 可用以执行 FATE 中转传送", DiagnosticSeverity.Warning);
            this.nextActionAt = now.AddSeconds(2);
            return;
        }

        if (this.teleportIssuedByUs)
        {
            bool teleportBusy = this.lifestream.IsBusy;
            if (teleportBusy)
                this.teleportBusyObserved = true;

            if (this.teleportArrival.Complete(true, this.IsBetweenAreas(), teleportBusy))
            {
                this.fateAetheryteTeleportPlan = null;
                this.teleportIssuedByUs = false;
                this.teleportBusyObserved = false;
                this.territoryStableAfter = now.AddSeconds(2);
                this.recoveryAttempts = 0;
                this.BeginCompanionCheckpoint(
                    CompanionCheckpointKind.MapArrival,
                    this.ActiveFateTravelState,
                    $"已传送至 FATE 附近大水晶 {plan.AetheryteId}，继续导航到目标");
                return;
            }

            if (!this.IsBetweenAreas() && !teleportBusy
                && now - this.teleportStartedAt >= TimeSpan.FromSeconds(15))
            {
                bool wasBusy = this.teleportBusyObserved;
                this.lifestream.Abort();
                this.teleportArrival.CancelRequest();
                this.fateAetheryteTeleportPlan = null;
                this.teleportIssuedByUs = false;
                this.teleportBusyObserved = false;
                this.AddDiagnostic(
                    DiagnosticSeverity.Warning,
                    wasBusy
                        ? "FATE 中转传送 15 秒内未进入区域切换，判定被打断，准备恢复导航"
                        : "FATE 中转传送请求 15 秒内未进入忙碌/区域切换，准备恢复导航");
                this.FailRecoverableAction("FATE 中转大水晶传送未开始", required: this.activeFateId is { } id && this.IsRequiredFate(id));
                return;
            }

            if (now - this.teleportStartedAt > TimeSpan.FromSeconds(120))
            {
                this.lifestream.Abort();
                this.teleportArrival.CancelRequest();
                this.fateAetheryteTeleportPlan = null;
                this.teleportIssuedByUs = false;
                this.teleportBusyObserved = false;
                this.FailRecoverableAction("FATE 中转大水晶传送 120 秒超时", required: this.activeFateId is { } id && this.IsRequiredFate(id));
            }
            return;
        }

        if (this.IsBetweenAreas() || this.lifestream.IsBusy)
        {
            this.statusReason = "等待当前传送结束后执行 FATE 中转大水晶传送";
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        this.StopNavigationOperation();
        this.landing.StopDescending();
        if (this.vnavmesh.IsMoveActive)
            return;

        if (!this.lifestream.Teleport(plan.AetheryteId, plan.SubIndex))
        {
            this.fateAetheryteTeleportPlan = null;
            this.FailRecoverableAction(
                $"Lifestream 拒绝传送至 FATE 附近大水晶 {plan.AetheryteId}:{plan.SubIndex}",
                required: this.activeFateId is { } id && this.IsRequiredFate(id));
            return;
        }

        this.teleportIssuedByUs = true;
        this.teleportBusyObserved = false;
        this.teleportStartedAt = now;
        this.teleportArrival.Requested();
        this.Transition(
            AutomationState.Teleporting,
            $"正在传送至 FATE 附近大水晶 {plan.AetheryteId}:{plan.SubIndex}（距 FATE {plan.DistanceToTarget:0.0}）");
    }

    private bool TrySummonEntryPet(DateTime now)
    {
        if (this.configuration.Mode != AutomationMode.PresetSequence
            || this.entryPetSummonedTerritory == this.clientState.TerritoryType)
            return false;
        if (now < this.entryPetRetryAt)
        {
            this.nextActionAt = this.entryPetRetryAt;
            return true;
        }

        IReadOnlyList<MapPreset> maps = this.GetPresetMaps();
        if (maps.Count == 0)
            return false;
        MapPreset plan = maps[Math.Clamp(this.presetIndex, 0, maps.Count - 1)];
        if (plan.EntryPetItemId == 0)
        {
            this.entryPetSummonedTerritory = this.clientState.TerritoryType;
            return false;
        }

        int itemCount = this.inventoryCounter.Count(plan.EntryPetItemId);
        this.AddDiagnostic(
            DiagnosticSeverity.Debug,
            $"地图进入宠物检查：Territory={this.clientState.TerritoryType}，预设索引={this.presetIndex}，" +
            $"ItemId={plan.EntryPetItemId}，背包数量={itemCount}");
        bool used = this.companion.TryUseMinionItem(plan.EntryPetItemId, now);
        if (used)
        {
            this.entryPetSummonedTerritory = this.clientState.TerritoryType;
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"已确保地图进入宠物生效：ItemId={plan.EntryPetItemId}，{this.companion.LastActionReason}");
            this.statusReason = "已召唤地图预设宠物，等待生效";
            this.nextActionAt = now.AddSeconds(1);
            return true;
        }

        this.AddDiagnostic(
            DiagnosticSeverity.Warning,
            $"地图预设宠物召唤未成功：Territory={this.clientState.TerritoryType}，" +
            $"ItemId={plan.EntryPetItemId}，背包数量={itemCount}，{this.companion.LastActionReason}");

        // A transient agent/UI rejection should be retried after the client settles. Only mark
        // the territory as handled when the item is not configured or is genuinely unavailable;
        // otherwise a failed first attempt must not permanently suppress future attempts.
        if (itemCount <= 0)
        {
            this.entryPetSummonedTerritory = this.clientState.TerritoryType;
            return false;
        }

        this.entryPetRetryAt = now.AddSeconds(3);
        this.nextActionAt = this.entryPetRetryAt;
        return true;
    }

    private void EnsureAethernetFallback(DateTime now, uint desiredTerritory, uint placeNameId)
    {
        if (!this.lifestream.IsAvailable || !this.lifestream.IsAethernetAvailable)
        {
            this.Transition(AutomationState.WaitingForDependency, "等待 Lifestream 都市传送网 IPC 可用", DiagnosticSeverity.Warning);
            this.nextActionAt = now.AddSeconds(2);
            return;
        }

        if (this.aethernetTeleportIssuedByUs)
        {
            if (this.clientState.TerritoryType == desiredTerritory)
            {
                this.aethernetTeleportIssuedByUs = false;
                this.aethernetDestinationPlaceNameId = 0;
                this.Transition(AutomationState.WaitingForTerritory, $"已通过田园郡都市传送网抵达地图 {desiredTerritory}");
                this.territoryStableAfter = now.AddSeconds(2);
                return;
            }

            if (!this.IsBetweenAreas() && now - this.aethernetTeleportStartedAt >= TimeSpan.FromSeconds(5))
            {
                this.lifestream.Abort();
                this.teleportArrival.CancelRequest();
                this.aethernetTeleportIssuedByUs = false;
                this.AddDiagnostic(DiagnosticSeverity.Warning,
                    $"田园郡都市传送网请求 5 秒内未进入区域切换（目标 PlaceName {placeNameId}），准备重试");
                this.nextActionAt = now.AddSeconds(1);
                return;
            }

            this.statusReason = $"等待田园郡都市传送网抵达地图 {desiredTerritory}";
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        if (this.IsBetweenAreas() || this.lifestream.IsBusy)
        {
            this.statusReason = "等待 Lifestream 空闲后执行田园郡都市传送网";
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        if (!this.lifestream.AethernetTeleportByPlaceNameId(placeNameId))
        {
            this.Pause($"Lifestream 拒绝田园郡都市传送网目标 PlaceName {placeNameId}（地图 {desiredTerritory}）。");
            return;
        }

        this.aethernetTeleportIssuedByUs = true;
        this.teleportArrival.Requested();
        this.aethernetDestinationPlaceNameId = placeNameId;
        this.aethernetTeleportStartedAt = now;
        this.Transition(AutomationState.Teleporting,
            $"已在田园郡，正在通过都市传送网前往地图 {desiredTerritory}（PlaceName {placeNameId}）");
    }

    private uint ResolveIdyllshireTerritory()
        => IdyllshireTerritoryFallback;

    private uint? ResolveAethernetDestination(uint territoryId)
    {
        try
        {
            // The Idyllshire route is currently valid only for Territory 399 (Dravanian
            // hinterlands). Do not offer it as a generic fallback for other territories.
            if (territoryId != 399)
                return null;

            uint idyllshireTerritory = this.ResolveIdyllshireTerritory();
            byte[] idyllshireGroups = this.dataManager.GetExcelSheet<Aetheryte>()
                .Where(row => row.Territory.RowId == idyllshireTerritory
                    && row.IsAetheryte
                    && row.AethernetGroup != 0)
                .Select(row => row.AethernetGroup)
                .Distinct()
                .ToArray();
            if (idyllshireGroups.Length == 0)
                return null;

            // AethernetTeleportByPlaceNameId takes Aetheryte.AethernetName (the PlaceName key),
            // not the outdoor aetheryte id. Restrict candidates to Idyllshire's runtime-resolved
            // network group so unrelated city networks cannot be selected.
            return this.dataManager.GetExcelSheet<Aetheryte>()
                .Where(row => row.Territory.RowId == territoryId
                    && row.AethernetName.RowId != 0
                    && idyllshireGroups.Contains(row.AethernetGroup))
                .OrderByDescending(row => row.IsAetheryte)
                .ThenBy(row => row.Order)
                .Select(row => (uint?)row.AethernetName.RowId)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "读取 Territory {Territory} 的都市传送网节点失败", territoryId);
            return null;
        }
    }

    private IAetheryteEntry? ResolveTeleportAetheryte(uint territoryId, uint overrideAetheryteId)
    {
        if (territoryId == 0)
            return null;

        IEnumerable<IAetheryteEntry> candidates = this.ResolveTeleportAetherytes(territoryId);
        if (overrideAetheryteId != 0)
            return candidates.FirstOrDefault(entry => entry.AetheryteId == overrideAetheryteId);

        return candidates.FirstOrDefault();
    }

    private IReadOnlyList<IAetheryteEntry> ResolveTeleportAetherytes(uint territoryId) =>
        this.aetheryteList
            .Where(entry => entry.TerritoryId == territoryId
                            && !entry.IsSharedHouse
                            && !entry.IsApartment)
            .OrderBy(entry => entry.SubIndex != 0)
            .ThenBy(entry => entry.GilCost)
            .ThenBy(entry => entry.AetheryteId)
            .ThenBy(entry => entry.SubIndex)
            .DistinctBy(entry => (entry.AetheryteId, entry.SubIndex))
            .ToArray();

    private IAetheryteEntry? ResolveNextTeleportAetheryte(uint territoryId) =>
        this.ResolveTeleportAetherytes(territoryId)
            .FirstOrDefault(entry => !this.failedTeleportCandidates.Contains((entry.AetheryteId, entry.SubIndex)));

    private void PrepareTeleportCandidates(uint territoryId)
    {
        if (territoryId == this.teleportCandidateTerritory)
            return;

        this.teleportCandidateTerritory = territoryId;
        this.failedTeleportCandidates.Clear();
        this.teleportIssuedAetheryteId = 0;
        this.teleportIssuedSubIndex = 0;
    }

    private void ResetFailedTeleportCandidates()
    {
        this.teleportCandidateTerritory = 0;
        this.failedTeleportCandidates.Clear();
        this.teleportIssuedAetheryteId = 0;
        this.teleportIssuedSubIndex = 0;
    }

    private void MarkIssuedTeleportCandidateFailed(uint territoryId, string reason)
    {
        if (this.teleportIssuedAetheryteId != 0)
            this.failedTeleportCandidates.Add((this.teleportIssuedAetheryteId, this.teleportIssuedSubIndex));

        this.AddDiagnostic(DiagnosticSeverity.Warning, reason);
        if (this.ResolveNextTeleportAetheryte(territoryId) is not null)
        {
            this.nextActionAt = DateTime.UtcNow.AddSeconds(1);
            return;
        }

        this.Stop($"地图 {territoryId} 的默认以太之光均不可用，已停止自动模式。");
    }

    private void ScanAndSelectFate(DateTime now)
    {
        if (this.condition[ConditionFlag.InCombat])
        {
            this.BeginCombatCleanup(CleanupContinuation.ScanFates, "扫描前仍处于战斗状态");
            return;
        }

        IReadOnlyList<ushort> priorityTargets = this.GetPriorityTargetFateIds();
        bool forceCurrentRange = this.startupFateRangeSelectionPending;
        this.startupFateRangeSelectionPending = false;
        FateSnapshot? target = forceCurrentRange
            ? this.SelectFateInCurrentRange(now)
            : this.SelectBestFateCandidate(now);
        if (forceCurrentRange && target is not null)
        {
            this.BeginStartupFateImmediately(target, now);
            return;
        }
        if (target is null
            && priorityTargets.Count > 0
            && this.GetCurrentPlan().Fallback == TargetFateFallbackPolicy.WaitOnly)
        {
            this.statusReason = $"等待指定 FATE 列表：{string.Join(", ", priorityTargets)}";
            this.nextActionAt = now.AddSeconds(1);
            return;
        }

        if (target is null)
        {
            this.TryMountWhileIdle(now);
            this.statusReason = "当前没有可用的 FATE";
            this.nextActionAt = now.AddSeconds(1);
            return;
        }

        this.SelectFate(target, priorityTargets.Contains(target.FateId) ? "选择指定 FATE" : "选择评分最高的 FATE");
    }

    private FateSnapshot? SelectBestFateCandidate(DateTime now)
    {
        IReadOnlyList<FateSnapshot> candidates = this.GetEligibleFates(now);
        IReadOnlyList<ushort> priorityTargets = this.GetPriorityTargetFateIds();
        FateSnapshot? priority = candidates
            .Where(fate => priorityTargets.Contains(fate.FateId))
            .OrderByDescending(this.ScoreFate)
            .ThenBy(fate => this.DistanceToPlayer(fate.Position))
            .FirstOrDefault();
        if (priority is not null)
            return priority;
        if (priorityTargets.Count > 0 && this.GetCurrentPlan().Fallback == TargetFateFallbackPolicy.WaitOnly)
            return null;

        return candidates
            .OrderByDescending(this.ScoreFate)
            .ThenBy(fate => this.DistanceToPlayer(fate.Position))
            .FirstOrDefault();
    }

    private FateSnapshot? SelectFateInCurrentRange(DateTime now)
    {
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (player is null)
            return null;

        FateSnapshot[] nearby = this.GetEligibleFates(now)
            .Where(fate => IsPlayerInFate(fate.FateId)
                || Vector3.Distance(player.Position, fate.Position) <= GetFateAreaBoundary(fate))
            .OrderByDescending(fate => IsPlayerInFate(fate.FateId))
            .ThenBy(fate => Vector3.DistanceSquared(player.Position, fate.Position))
            .ToArray();
        return nearby.FirstOrDefault();
    }

    private void BeginStartupFateImmediately(FateSnapshot fate, DateTime now)
    {
        // This is intentionally before the startup companion checkpoint.  Starting inside an
        // active FATE means the player is already at the encounter; do not run map-entry
        // companion work, navigation, flight, or landing just to return to the same location.
        this.startupFateRangeSelectionPending = false;
        this.startupGroundCheckPending = false;
        this.StopNavigationOperation();
        this.landing.StopDescending();
        this.AddDiagnostic(
            DiagnosticSeverity.Information,
            $"启动时检测到玩家已在 FATE #{fate.FateId}「{fate.Name}」范围内，跳过伙伴检查、导航和落地，直接进入战斗流程");
        this.SelectFate(fate, "启动时已在 FATE 范围内，直接开始战斗");
        this.travelObservationDelayPending = false;
        this.nextActionAt = now;
        if (IsPreparingFate(fate))
        {
            this.Transition(AutomationState.OpeningPreparingFate, "当前处于准备中的 FATE，仅执行必要的开场确认");
            return;
        }

        this.Transition(AutomationState.WaitingForLevelSync, "已在运行中的 FATE 范围内，跳过导航/落地检查，进入战斗前同步");
    }

    private void TryMountWhileIdle(DateTime now)
    {
        if (!this.configuration.FlyToFates
            || now < this.nextIdleMountAttemptAt
            || this.condition[ConditionFlag.InCombat])
            return;

        this.nextIdleMountAttemptAt = now.AddSeconds(5);

        if (this.mount.IsReadyForNavigation)
        {
            if (this.idleFlightRaised)
                return;

            if (!this.vnavmesh.IsAvailable)
            {
                this.AddDiagnostic(DiagnosticSeverity.Debug, "无可用 FATE：已上坐骑但 vnavmesh 不可用，暂不执行升空导航");
                return;
            }

            if (this.vnavmesh.IsMoveActive)
            {
                if (this.travelSessionActive && this.travelPurpose == NavigationPurpose.IdleFlight)
                {
                    NavigationTravelUpdate idleUpdate = this.TickTravelSession(
                        now,
                        this.travelDestination,
                        () => false);
                    if (idleUpdate.Outcome == NavigationTravelOutcome.Failed)
                        this.idleFlightRaised = false;
                }
                return;
            }

            IPlayerCharacter? player = this.objectTable.LocalPlayer;
            if (player is null)
                return;

            Vector3 raiseDestination = CreateIdleFlightDestination(player.Position);
            NavigationRequestResult requestResult = this.TryIssueNavigationRequest(
                now,
                NavigationPurpose.IdleFlight,
                raiseDestination,
                fly: true);
            bool sent = requestResult == NavigationRequestResult.Accepted;
            this.idleFlightRaised = sent;
            this.AddDiagnostic(
                sent ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
                $"无可用 FATE：已上坐骑并请求斜向飞到较低安全高度，目标={raiseDestination}，" +
                $"垂直增量={IdleFlightHeight:0.0}，水平偏移={HorizontalDistance(player.Position, raiseDestination):0.0}，结果={sent}，" +
                $"InFlight={this.mount.IsInFlight}");
            return;
        }

        if (!this.mount.CanAttemptMount)
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Debug,
                $"无可用 FATE：暂时无法上坐骑；InCombat={this.condition[ConditionFlag.InCombat]}，" +
                $"MountTransition={this.mount.IsMountTransition}，BetweenAreas={this.IsBetweenAreas()}");
            return;
        }

        this.ClearTarget();
        bool sentMount = this.mount.TryMount();
        this.AddDiagnostic(
            sentMount ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            $"无可用 FATE：请求上坐骑，结果={sentMount}，Territory={this.clientState.TerritoryType}");
    }

    private static Vector3 CreateIdleFlightDestination(Vector3 origin)
    {
        double angle = Random.Shared.NextDouble() * Math.Tau;
        float horizontalDistance = IdleFlightMinHorizontalOffset
            + (Random.Shared.NextSingle() * (IdleFlightMaxHorizontalOffset - IdleFlightMinHorizontalOffset));
        return origin + new Vector3(
            MathF.Cos((float)angle) * horizontalDistance,
            IdleFlightHeight,
            MathF.Sin((float)angle) * horizontalDistance);
    }

    private void SelectFate(FateSnapshot fate, string reason)
    {
        this.StopNavigationOperation();
        this.idleFlightRaised = false;
        this.idleFlightDiagnosticAt = DateTime.MinValue;
        this.landing.StopDescending();
        this.groundTravelToActiveFate = this.IsInsideFateArea(fate);
        if (this.groundTravelToActiveFate)
        {
            float distance = this.DistanceToPlayer(fate.Position);
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"已处于目标 FATE #{fate.FateId} 三维区域内，改用地面导航：距离={distance:0.0}，允许={GetFateAreaBoundary(fate):0.0}（含 Y 轴）");
        }
        this.activeFateId = fate.FateId;
        this.lastActiveFateSnapshot = fate;
        this.activeFateMissingSince = DateTime.MinValue;
        this.preparationStartedAt = DateTime.UtcNow;
        this.preparationLastActionAt = DateTime.MinValue;
        this.preparationInteractionAt = DateTime.MinValue;
        this.preparationTalkStartedAt = DateTime.MinValue;
        this.preparationTalkCallbackAttempts = 0;
        this.preparationTextAdvanceEnabled = false;
        this.preparationTargetObjectId = null;
        this.fateParticipationSince = DateTime.MinValue;
        this.fallbackLandingForParticipation = false;
        this.completedProgressObservedAt = DateTime.MinValue;
        this.activeFateParticipationObserved = false;
        this.terminalFateObservedAt = DateTime.MinValue;
        this.terminalFateState = null;
        this.pullTargetId = null;
        this.killTargetId = null;
        this.lostPriorityTargetId = null;
        this.lostPriorityTargetKind = null;
        this.priorityDestroyTargetId = null;
        this.destroySession.Reset();
        this.bossTargetId = null;
        this.bossCleanupFateId = null;
        this.bossCleanupTargetId = null;
        this.bossCleanupNoTargetSince = DateTime.MinValue;
        this.bossCancelSyncStartedAt = DateTime.MinValue;
        this.bossEnvironmentLogAt = DateTime.MinValue;
        this.bossPreCombatCleanupChecked = false;
        this.ResetPullBatch();
        this.escortFollowLastRequestAt = DateTime.MinValue;
        this.escortFollowLastPosition = null;
        this.recoveryAttempts = 0;
        this.levelSyncAttempts = 0;
        this.navigationRecoveryAttempts = 0;
        this.navigationRecoveryTeleportIssued = false;
        this.navigationRecoveryStartedAt = DateTime.MinValue;
        this.fateAetheryteTeleportPlan = null;
        this.ResetNavigationProgress();
        bool useFateAetheryte = !this.groundTravelToActiveFate
            && this.TryPlanFateAetheryteTeleport(fate);
        AutomationState next = useFateAetheryte
            ? AutomationState.Teleporting
            : this.ShouldFlyToActiveFate
                ? AutomationState.MountingForTravel
                : AutomationState.NavigatingToFate;
        this.Transition(next, $"{reason}: #{fate.FateId} {fate.Name}（{fate.CombatKind}型）");
        this.BeginTravelObservationDelay();
        if (reason.Contains("指定 FATE", StringComparison.Ordinal))
            this.PlaySoundAlert("指定 FATE 出现");
    }

    private bool TryPlanFateAetheryteTeleport(FateSnapshot fate)
    {
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        uint territoryId = this.clientState.TerritoryType;
        if (player is null || territoryId == 0)
            return false;

        if (!this.aetheryteTravelPlanner.TryFindBest(
                territoryId,
                player.Position,
                fate.Position,
                out AetheryteTravelPlan plan))
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Debug,
                $"FATE #{fate.FateId} 未找到值得使用的同地图已解锁大水晶中转点");
            return false;
        }

        float directDistance = AetheryteTravelPlanner.HorizontalDistance(player.Position, fate.Position);
        float estimatedTeleportRoute = AetheryteTravelPlanner.TeleportCostDistance + plan.DistanceToTarget;
        this.fateAetheryteTeleportPlan = plan;
        this.teleportIssuedByUs = false;
        this.teleportBusyObserved = false;
        this.teleportArrival.CancelRequest();
        this.AddDiagnostic(
            DiagnosticSeverity.Information,
            $"FATE #{fate.FateId} 采用大水晶中转：水晶={plan.AetheryteId}:{plan.SubIndex}，"
            + $"直达水平距离={directDistance:0.0}，估算中转距离={estimatedTeleportRoute:0.0}，"
            + $"水晶到 FATE={plan.DistanceToTarget:0.0}");
        return true;
    }

    private bool ShouldFlyToActiveFate => this.configuration.FlyToFates && !this.groundTravelToActiveFate;

    private AutomationState ActiveFateTravelState =>
        this.ShouldFlyToActiveFate ? AutomationState.MountingForTravel : AutomationState.NavigatingToFate;

    private bool IsInsideFateArea(FateSnapshot fate) =>
        this.objectTable.LocalPlayer is { } player
        && Vector3.Distance(player.Position, fate.Position) <= GetFateAreaBoundary(fate);

    private static float GetFateAreaBoundary(FateSnapshot fate) => Math.Max(12f, fate.Radius) + 2f;

    private void CompleteNavigationOnly(string reason)
    {
        this.StopNavigationOperation();
        this.landing.StopDescending();
        ushort? completedFateId = this.navigationOnlyFateId;
        this.navigationOnlyRequested = false;
        this.navigationOnlyFateId = null;
        this.ResetCurrentActivity();
        this.configuration.Enabled = false;
        this.Transition(
            AutomationState.Stopped,
            completedFateId is { } id ? $"仅导航已到达 FATE #{id}：{reason}" : $"仅导航已完成：{reason}");
    }

    private void RestoreTemporaryTarget(string reason, bool restoreEnabled = true)
    {
        if (!this.temporaryTargetActive)
            return;

        this.configuration.Mode = this.temporaryPreviousMode;
        this.configuration.TargetFateIds = this.temporaryPreviousTargetFateIds.Count > 0
            ? this.temporaryPreviousTargetFateIds.ToList()
            : this.temporaryPreviousTargetFateId is { } legacyTarget
                ? [legacyTarget]
                : [];
        this.configuration.TargetFateId = this.configuration.TargetFateIds.FirstOrDefault() is { } firstTarget
            && firstTarget != 0
            ? firstTarget
            : null;
        this.configuration.Enabled = restoreEnabled && this.temporaryPreviousEnabled;
        this.AddDiagnostic(
            DiagnosticSeverity.Information,
            $"临时目标已结束（{reason}），恢复模式={this.configuration.Mode}，目标 FATE={this.configuration.TargetFateId?.ToString() ?? "无"}，原运行状态={this.configuration.Enabled}");
        this.temporaryTargetActive = false;
        this.temporaryTargetFateId = 0;
        this.temporaryPreviousTargetFateId = null;
        this.temporaryPreviousTargetFateIds.Clear();
    }

    private void BeginTravelObservationDelay()
    {
        int maximum = Math.Clamp(this.configuration.NextFateDelaySeconds, 0, 60);
        if (maximum == 0)
        {
            this.travelObservationDelayPending = false;
            this.nextActionAt = DateTime.UtcNow;
            this.AddDiagnostic(DiagnosticSeverity.Debug, "前往下一个 FATE 前延迟已关闭，立即继续");
            return;
        }

        int seconds = maximum == 1
            ? 1
            : Random.Shared.Next(2, maximum + 1);
        this.travelObservationDelayPending = true;
        this.nextActionAt = DateTime.UtcNow.AddSeconds(seconds);
        this.statusReason = $"前往下一个 FATE 前观察中，{seconds} 秒后继续";
        this.AddDiagnostic(DiagnosticSeverity.Information, this.statusReason);
    }

    private void TryPreemptForTargetFate()
    {
        if (this.navigationOnlyRequested
            || this.pendingFateResult is not null
            || this.state is AutomationState.CleaningUpCombat or AutomationState.ResettingAggroViaDuty
            || (this.activeFateId is null && this.state != AutomationState.ScanningFates))
            return;

        IReadOnlyList<ushort> targetIds = this.GetPriorityTargetFateIds();
        if (targetIds.Count == 0 || (this.activeFateId is { } current && targetIds.Contains(current)))
            return;

        FateSnapshot? target = this.GetEligibleFates(DateTime.UtcNow)
            .Where(f => targetIds.Contains(f.FateId))
            .OrderByDescending(this.ScoreFate)
            .FirstOrDefault();
        if (target is null || !this.IsEligible(target, DateTime.UtcNow))
            return;

        this.StopNavigationOperation();
        bool wasInCombat = this.IsCombatEngaged();
        ushort? previousFateId = this.activeFateId;
        Vector3? previousFatePosition = previousFateId is { } activeFateId
            ? this.fates.Find(activeFateId)?.Position
            : null;
        this.ClearTarget();
        if (!wasInCombat)
            this.AddDiagnostic(DiagnosticSeverity.Information, $"指定 FATE #{target.FateId} 出现，当前不在战斗，立即切换目标");
        else
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"指定 FATE #{target.FateId} 出现；立即停止当前接战并跑离战斗区域，不在原地击杀旧目标");
        this.SelectFate(target, "指定 FATE 出现，抢占当前任务");
        if (wasInCombat)
        {
            this.preemptSyncFateId = previousFateId;
            this.BeginCombatCleanup(
                CleanupContinuation.PreemptTravel,
                "指定 FATE 已出现；停止当前接战并立即跑离，再前往指定目标");
            this.cleanupEscapeOrigin = previousFatePosition;
        }
    }

    private void TryPreemptTravelForHigherScore(DateTime now)
    {
        // Ordinary score changes may only replace a target while the character is still in the
        // travel phase. Landing, companion checks, opener dialogue, level sync, combat and cleanup
        // remain exclusive owners of their work and cannot be interrupted by ordinary scoring.
        if (this.navigationOnlyRequested
            || this.state is not AutomationState.MountingForTravel and not AutomationState.NavigatingToFate
            || this.pendingFateResult is not null
            || this.activeFateId is not { } currentId)
            return;

        IReadOnlyList<ushort> priorityTargets = this.GetPriorityTargetFateIds();
        if (priorityTargets.Contains(currentId))
            return;

        FateSnapshot? current = this.fates.Find(currentId);
        FateSnapshot? replacement = this.SelectBestFateCandidate(now);
        if (current is null
            || replacement is null
            || replacement.FateId == currentId
            || priorityTargets.Contains(replacement.FateId))
            return;

        float currentScore = this.ScoreFate(current);
        float replacementScore = this.ScoreFate(replacement);
        if (replacementScore <= currentScore)
            return;

        this.StopNavigationOperation();
        this.ClearTarget();
        this.AddDiagnostic(
            DiagnosticSeverity.Information,
            $"旅行中评分抢占：FATE #{replacement.FateId} {replacement.Name} 分数 {replacementScore:0.0} " +
            $"> 当前 FATE #{current.FateId} {current.Name} 分数 {currentScore:0.0}；停止旧路径并切换目标");
        this.SelectFate(replacement, "旅行途中发现评分更高的 FATE，抢占导航");
    }

    private void HandlePreparingFate(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (fate is null || player is null)
        {
            this.AbandonActiveFate("准备中的 FATE 已失效");
            return;
        }

        // A FATE can switch to Running as soon as the final confirmation is accepted,
        // while the last Talk page is still visible for a few frames.  Keep the opener
        // dialogue state as the owner until that addon closes; otherwise the next update
        // enters WaitingForLevelSync and its native sync action competes with Talk's final
        // callback, leaving the conversation stuck on its last page.
        if (!IsPreparingFate(fate)
            && this.preparationInteractionAt != DateTime.MinValue
            && now - this.preparationInteractionAt <= TimeSpan.FromSeconds(20)
            && this.HasPreparingDialogueVisible())
        {
            this.statusReason = "FATE 已开始，但仍在等待开启 NPC 对话完成";
            this.HandlePreparingDialogueUi(now);
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        // Let the addon stack finish closing before invoking FateManager level sync.  The
        // final Talk callback can open/close the event and flip the FATE state in the same
        // framework tick; syncing during that transition races the callback.
        if (!IsPreparingFate(fate)
            && this.preparationInteractionAt != DateTime.MinValue
            && now - this.preparationInteractionAt <= TimeSpan.FromSeconds(20)
            && now - this.preparationLastActionAt < TimeSpan.FromMilliseconds(750))
        {
            this.statusReason = "FATE 已开始，等待开启 NPC 对话界面完全关闭";
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        if (!IsPreparingFate(fate))
        {
            this.StopNavigationOperation();
            this.preparationTargetObjectId = null;
            if (this.preparationTextAdvanceEnabled)
            {
                this.textAdvance.Disable();
                this.preparationTextAdvanceEnabled = false;
                this.AddDiagnostic(DiagnosticSeverity.Debug, "FATE 开启对话已结束，释放 TextAdvance 外部控制");
            }
            // Only supported strategies may reach the opener. Keep this guard so a client-data
            // change cannot accidentally count an unsupported event as completed.
            if (fate.IsAutomationSupported)
            {
                this.levelSyncAttempts = 0;
                if (this.preparationInteractionAt != DateTime.MinValue)
                {
                    this.preparingGroundArrivalPending = true;
                    this.Transition(
                        AutomationState.NavigatingToFate,
                        $"FATE #{fate.FateId} NPC/事件触发成功；确认角色回到 FATE 范围后再开始等级同步");
                }
                else
                {
                    this.Transition(
                        AutomationState.WaitingForLevelSync,
                        $"FATE #{fate.FateId} NPC/事件触发成功（已进入 Running），开始等级同步与战斗");
                }
            }
            else
            {
                this.skippedFates[fate.FateId] = now.AddSeconds(60);
                this.AbandonActiveFate($"FATE #{fate.FateId} 触发后识别为{fate.CombatProfile.SupportDescription}，已跳过");
            }
            return;
        }

        if (this.condition[ConditionFlag.InCombat])
        {
            this.statusReason = "等待脱战后才能与 FATE 开启 NPC 对话";
            this.nextActionAt = now.AddSeconds(1);
            return;
        }

        if (!this.preparationTextAdvanceEnabled)
        {
            this.preparationTextAdvanceEnabled = this.textAdvance.EnableForPreparingFate();
            this.AddDiagnostic(
                this.preparationTextAdvanceEnabled ? DiagnosticSeverity.Information : DiagnosticSeverity.Debug,
                this.preparationTextAdvanceEnabled
                    ? "已启用 TextAdvance 加速 FATE 开启 NPC 的任务接受对话"
                    : "TextAdvance 不可用，FATE 开启 NPC 将使用原生对话推进");
        }

        if (now - this.preparationStartedAt > TimeSpan.FromSeconds(90))
        {
            this.FailRecoverableAction($"FATE #{fate.FateId} 90 秒内未能完成 NPC 开启对话", this.IsRequiredFate(fate.FateId));
            return;
        }

        if (this.HandlePreparingDialogueUi(now))
            return;

        IGameObject? opener = this.targetSelector.FindPreparingOpener(
            fate.FateId,
            fate.Position,
            fate.Radius,
            fate.ObjectiveNpc,
            fate.MotivationNpc,
            fate.MapMarkerPositions);

        if (opener is null)
        {
            this.statusReason =
                $"已完成旅行和伙伴检查，但 FATE #{fate.FateId} 开启 NPC 暂未加载；等待对象出现";
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        if (!this.mount.IsDismounted)
        {
            this.Transition(
                AutomationState.DismountingForFate,
                "开启 NPC 交互前检测到坐骑状态，返回统一落地/伙伴检查阶段",
                DiagnosticSeverity.Warning);
            return;
        }

        this.preparationTargetObjectId = opener.GameObjectId;
        float distance = Vector3.Distance(player.Position, opener.Position);
        if (distance > 3f)
        {
            NavigationRequestResult requestResult = this.TryIssueNavigationRequest(
                now,
                NavigationPurpose.PreparingOpener,
                opener.Position,
                fly: false);
            if (requestResult == NavigationRequestResult.Rejected)
            {
                this.FailRecoverableAction($"无法导航至 FATE #{fate.FateId} 开启 NPC", this.IsRequiredFate(fate.FateId));
                return;
            }

            this.statusReason = $"前往 FATE #{fate.FateId} 开启 NPC（距 NPC {distance:0.0}）";
            return;
        }

        this.StopNavigationOperation();
        this.targetManager.Target = opener;
        if (now - this.preparationLastActionAt < TimeSpan.FromMilliseconds(750))
            return;

        if (this.InteractWithPreparingNpc(opener))
        {
            this.preparationInteractionAt = now;
            this.preparationTalkStartedAt = DateTime.MinValue;
            this.preparationTalkCallbackAttempts = 0;
            this.preparationLastActionAt = now;
            this.AddDiagnostic(DiagnosticSeverity.Information,
                $"已与 FATE #{fate.FateId} 开启 NPC 对话：{opener.Name} BaseId={opener.BaseId} Object={opener.GameObjectId:X}");
        }
        else
        {
            this.statusReason = "开启 NPC 交互请求未被客户端接受，准备重试";
            this.preparationLastActionAt = now;
        }
        this.nextActionAt = now.AddMilliseconds(500);
    }

    private bool HandlePreparingDialogueUi(DateTime now)
    {
        if (this.preparationInteractionAt == DateTime.MinValue
            || now - this.preparationInteractionAt > TimeSpan.FromSeconds(20))
            return false;

        // SelectYesno is checked first because the final confirmation can briefly coexist
        // with Talk while the addon transition is being rebuilt.  FATE opener confirmations
        // are not regular quest-accept dialogs, so TextAdvance may leave this prompt open;
        // AutoFatre owns the affirmative callback even while TextAdvance handles Talk pages.
        AtkUnitBase* yesno = this.GetAddon("SelectYesno");
        if (yesno is not null && yesno->IsVisible && yesno->IsReady)
        {
            string prompt = ReadAddonString(yesno, 0);
            yesno->FireCallbackInt(0);
            this.preparationLastActionAt = now;
            this.AddDiagnostic(DiagnosticSeverity.Information,
                $"确认 FATE 开启 NPC 弹窗：选择是；提示={prompt}");
            return true;
        }

        if (this.preparationTextAdvanceEnabled && this.textAdvance.IsActive)
        {
            this.statusReason = "TextAdvance 正在推进 FATE 开启 NPC 任务接受对话";
            return true;
        }

        AtkUnitBase* talk = this.GetAddon("Talk");
        if (talk is not null && talk->IsVisible)
        {
            if (this.preparationTalkStartedAt == DateTime.MinValue)
                this.preparationTalkStartedAt = now;

            if (!talk->IsReady)
            {
                // Keep ownership of the interaction while the final Talk page is being
                // rebuilt. Falling through to InteractWithObject here can reopen/reset the
                // conversation and makes the automation appear stuck on the last page.
                this.statusReason = "等待 FATE 开启 NPC 对话界面就绪";
                this.nextActionAt = now.AddMilliseconds(250);
                return true;
            }

            if (now - this.preparationLastActionAt >= TimeSpan.FromMilliseconds(500))
            {
                // Talk normally advances with callback 0. Some final event-dialogue pages use
                // the generic close/advance callback (-1), however, and remain visible after
                // repeated callback-0 attempts. Mirror the proven addon helper behaviour first,
                // then use -1 as a bounded fallback rather than leaving the state machine stuck.
                this.preparationTalkCallbackAttempts++;
                int callback = this.preparationTalkCallbackAttempts >= 5 ? -1 : 0;
                talk->FireCallbackInt(callback);
                this.preparationLastActionAt = now;
                this.AddDiagnostic(
                    callback == 0 ? DiagnosticSeverity.Debug : DiagnosticSeverity.Warning,
                    $"推进 FATE 开启 NPC 对话（回调 {callback}，尝试 {this.preparationTalkCallbackAttempts}）");
            }
            this.statusReason = "推进 FATE 开启 NPC 对话";
            return true;
        }

        this.statusReason = "等待 FATE 开启 NPC 对话或确认弹窗";
        return false;
    }

    private bool HasPreparingDialogueVisible()
    {
        AtkUnitBase* talk = this.GetAddon("Talk");
        if (talk is not null && talk->IsVisible)
            return true;

        AtkUnitBase* yesno = this.GetAddon("SelectYesno");
        return yesno is not null && yesno->IsVisible;
    }

    private bool InteractWithPreparingNpc(IGameObject opener)
    {
        if (!opener.IsValid() || !opener.IsTargetable || opener.Address == nint.Zero)
            return false;

        long result = (long)TargetSystem.Instance()->InteractWithObject(
            (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)opener.Address,
            checkLineOfSight: false);
        return result != 7 && result > 0;
    }

    private AtkUnitBase* GetAddon(string name)
    {
        var addon = this.gameGui.GetAddonByName(name);
        return addon.Address == nint.Zero ? null : (AtkUnitBase*)addon.Address;
    }

    private static string ReadAddonString(AtkUnitBase* addon, int index)
    {
        if (addon is null || index < 0 || index >= addon->AtkValuesCount)
            return string.Empty;

        AtkValue value = addon->AtkValues[index];
        if (!value.String.HasValue)
            return string.Empty;
        return MemoryHelper.ReadSeStringNullTerminated(new(value.String)).ToString();
    }

    private void HandleMountForTravel(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        if (fate is null)
        {
            this.AbandonActiveFate("上坐骑前目标 FATE 已失效");
            return;
        }

        if (!this.ShouldFlyToActiveFate)
        {
            this.Transition(AutomationState.NavigatingToFate, "当前使用地面导航，无需强制上坐骑");
            return;
        }

        if (this.condition[ConditionFlag.InCombat])
        {
            this.BeginCombatCleanup(CleanupContinuation.ResumeTravel, "上坐骑前发现残余战斗");
            return;
        }

        if (this.mount.IsReadyForNavigation)
        {
            this.ResetNavigationProgress();
            this.Transition(AutomationState.NavigatingToFate, "已上坐骑，开始飞行导航");
            return;
        }

        if (now - this.stateEnteredAt >= TimeSpan.FromSeconds(12))
        {
            this.FailRecoverableAction("12 秒内未能成功上坐骑", this.IsRequiredFate(fate.FateId));
            return;
        }

        if (this.mount.IsMountTransition)
        {
            this.statusReason = "等待上坐骑动作完成";
            this.nextActionAt = now.AddMilliseconds(100);
            return;
        }

        if (now < this.mount.NextMountRequestAt)
        {
            this.statusReason = "等待上坐骑请求结果";
            this.nextActionAt = this.mount.NextMountRequestAt;
            return;
        }

        if (!this.mount.CanAttemptMount)
        {
            this.statusReason = $"暂时无法上坐骑：{this.mount.MountBlockReason}";
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        // Aetherytes and former enemies can remain hard-targeted after teleport/combat; the client
        // rejects Mount Roulette with "Invalid target" until that target is cleared.
        this.ClearTarget();
        bool sent = this.mount.TryMount();
        this.statusReason = sent ? "已请求坐骑轮盘，等待上坐骑" : "坐骑轮盘请求未被客户端接受，准备重试";
        this.AddDiagnostic(sent ? DiagnosticSeverity.Information : DiagnosticSeverity.Debug, this.statusReason);
        this.nextActionAt = now.AddMilliseconds(750);
    }

    private void NavigateToActiveFate(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        if (fate is null)
        {
            this.AbandonActiveFate("目标 FATE 已消失或不再有效");
            return;
        }

        if (!this.vnavmesh.IsAvailable)
        {
            this.Transition(AutomationState.WaitingForDependency, "等待 vnavmesh 可用", DiagnosticSeverity.Warning);
            this.nextActionAt = now.AddSeconds(2);
            return;
        }

        bool preparing = IsPreparingFate(fate);
        IGameObject? preparingOpener = preparing
            ? this.targetSelector.FindPreparingOpener(
                fate.FateId,
                fate.Position,
                fate.Radius,
                fate.ObjectiveNpc,
                fate.MotivationNpc,
                fate.MapMarkerPositions)
            : null;
        Vector3 destination = preparing
            ? preparingOpener?.Position ?? GetPreparingFateWaypoint(fate)
            : fate.Position;
        if (this.preparingGroundArrivalPending)
            destination = fate.Position;

        NoFlyZone? noFlyZone = this.GetCurrentNoFlyZone();
        bool wantsFlight = this.ShouldFlyToActiveFate && !this.preparingGroundArrivalPending;
        if (wantsFlight
            && noFlyZone is null
            && !this.mount.IsMounted
            && !this.mount.IsInFlight
            && !this.mount.IsMountTransition)
        {
            this.StopNavigationOperation();
            this.Transition(AutomationState.MountingForTravel, "飞行导航前坐骑状态丢失，重新上坐骑", DiagnosticSeverity.Warning);
            return;
        }

        if (preparing && now - this.preparationStartedAt > TimeSpan.FromSeconds(90))
        {
            this.FailRecoverableAction(
                $"FATE #{fate.FateId} 90 秒内未能抵达并加载开启 NPC",
                this.IsRequiredFate(fate.FateId));
            return;
        }

        float distance = this.DistanceToPlayer(destination);
        float horizontalDistance = this.HorizontalDistanceToPlayer(destination);
        float arrival = preparing && preparingOpener is not null
            ? 3f
            : Math.Max(3f, fate.Radius * 0.40f);
        bool markerArrived = preparing
            && preparingOpener is null
            && horizontalDistance <= Math.Max(20f, fate.Radius * 1.5f);
        bool inFate = !preparing && IsPlayerInFate(fate.FateId);
        if (inFate && this.fateParticipationSince == DateTime.MinValue)
            this.fateParticipationSince = now;
        if (!inFate)
            this.fateParticipationSince = DateTime.MinValue;
        bool centerFallback = inFate
            && horizontalDistance <= arrival
            && this.fateParticipationSince != DateTime.MinValue
            && now - this.fateParticipationSince >= UnreachableCenterFallbackDelay;
        bool arrived = markerArrived
            || distance <= arrival
            || (centerFallback && wantsFlight);

        if (!this.travelSessionActive)
        {
            this.StartTravelSession(
                now,
                NavigationPurpose.TravelToFate,
                destination,
                wantsFlight,
                preparing && preparingOpener is null
                    ? GroundDestinationKind.MapWaypoint
                    : preparing ? GroundDestinationKind.LiveObject : GroundDestinationKind.MapWaypoint,
                horizontalProgress: false,
                context: preparing ? "前往 Preparing FATE" : "前往 FATE");
        }

        NavigationTravelUpdate update = this.TickTravelSession(now, destination, () => arrived);
        if (update.Outcome == NavigationTravelOutcome.Failed)
        {
            this.HandleTravelSessionFailure(now, fate, destination, horizontal: false, update);
            return;
        }
        if (update.RequestResult == NavigationRequestResult.Rejected)
        {
            this.EndTravelSession();
            this.BeginNavigationRecovery(
                now,
                fate,
                $"vnavmesh 拒绝导航请求：{update.Reason}");
            return;
        }
        if (update.State is NavigationTravelState.Landing or NavigationTravelState.JumpRecovery)
        {
            this.nextActionAt = now.AddMilliseconds(150);
            return;
        }
        if (update.Outcome is not NavigationTravelOutcome.Arrived)
        {
            this.statusReason = preparing
                ? $"正在前往 Preparing FATE #{fate.FateId}（距目标 {distance:0.0}）"
                : $"正在前往 FATE #{fate.FateId}（距目标 {distance:0.0}）";
            this.nextActionAt = now.AddMilliseconds(100);
            return;
        }

        this.EndTravelSession();
        if (markerArrived)
        {
            if (this.navigationOnlyRequested)
            {
                this.CompleteNavigationOnly("已到达准备 FATE 目标点附近");
                return;
            }
            this.statusReason = $"已抵达准备中的 FATE #{fate.FateId} 开启标记附近，等待开启 NPC 加载";
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }
        if (this.navigationOnlyRequested)
        {
            this.CompleteNavigationOnly(centerFallback ? "已进入目标 FATE 区域" : "已到达目标点");
            return;
        }

        if (wantsFlight && (this.mount.IsInFlight || this.mount.IsMounted))
        {
            this.BeginStableLanding(now, centerFallback ? "FATE 中心不可达，当前位置落地" : "FATE");
            this.fallbackLandingForParticipation = centerFallback;
            this.Transition(
                AutomationState.LandingForFate,
                centerFallback
                    ? $"已进入 FATE 区域但中心点不可达，在当前位置下降（距中心 {distance:0.0}）"
                    : $"已到达 FATE 导航目标，停止导航并垂直下降（距目标 {distance:0.0}）");
            return;
        }

        if (this.preparingGroundArrivalPending)
        {
            if (!this.mount.IsDismounted)
            {
                this.Transition(
                    AutomationState.DismountingForFate,
                    $"交互 FATE 已地面导航回到中心，但角色仍在坐骑上（距中心 {distance:0.0}），先下坐骑");
                return;
            }

            this.preparingGroundArrivalPending = false;
            this.levelSyncAttempts = 0;
            this.Transition(
                AutomationState.WaitingForLevelSync,
                $"开启 FATE 后已地面导航回到中心（距中心 {distance:0.0}），开始等级同步");
            return;
        }

        this.Transition(AutomationState.DismountingForFate, $"已进入 FATE 范围并落地（距中心 {distance:0.0}）");
    }
    private LandingStatus BeginStableLanding(DateTime now, string reason)
    {
        Vector3? previousDestination = this.travelSession.Landing.Destination;
        LandingStatus result = this.travelSession.Landing.Update(now, this.objectTable.LocalPlayer?.Position,
            new FlightState(this.mount.IsInFlight, this.mount.IsJumping, this.mount.IsMountTransition));
        this.statusReason = reason + "：" + (result switch
        {
            LandingStatus.WaitingForPath => "等待旧路径及算路停止",
            LandingStatus.Approaching => "等待落地点导航完成",
            LandingStatus.Descending => "释放导航控制，持续下降",
            LandingStatus.Settling => "等待落地状态稳定",
            _ => "已稳定落地",
        });
        if (this.travelSession.Landing.Destination is { } destination && previousDestination != destination)
            this.AddDiagnostic(DiagnosticSeverity.Information, $"{reason}：移动至落地点 {destination}");
        return result;
    }

    private static Vector3 GetPreparingFateWaypoint(FateSnapshot fate) => fate.MapMarkerPositions.Count == 0
        ? fate.Position
        : fate.MapMarkerPositions
            .OrderByDescending(marker => Vector3.DistanceSquared(marker, fate.Position))
            .First();

    private void HandleLandingForFate(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        if (fate is null)
        {
            this.AbandonActiveFate("落地前目标 FATE 已失效");
            return;
        }

        if (now - this.stateEnteredAt >= LandingTimeout)
        {
            this.StopNavigationOperation();
            this.landing.StopDescending();
            this.FailRecoverableAction("进入 FATE 范围后 18 秒仍未落地", this.IsRequiredFate(fate.FateId));
            return;
        }
        if (this.BeginStableLanding(now, "FATE 落地") != LandingStatus.Landed)
            return;
        this.StopNavigationOperation();
        if (this.preparingGroundArrivalPending)
        {
            this.Transition(
                AutomationState.NavigatingToFate,
                "交互 FATE 已稳定落地，继续使用地面导航回到中心");
            return;
        }
        this.Transition(AutomationState.DismountingForFate, "落地状态稳定，准备下坐骑");
    }

    private void HandleDismountForFate(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        if (fate is null)
        {
            this.AbandonActiveFate("下坐骑前目标 FATE 已失效");
            return;
        }

        // Landing is an intermediate action, not proof that the FATE area was reached. A
        // transient flight/condition update can make the native descent input appear complete
        // while the character is still far from the event. Never hand such a position to the
        // level-sync state; resume travel instead.
        bool preparing = IsPreparingFate(fate);
        Vector3 arrivalPosition = preparing
            ? this.targetSelector.FindPreparingOpener(
                    fate.FateId,
                    fate.Position,
                    fate.Radius,
                    fate.ObjectiveNpc,
                    fate.MotivationNpc,
                    fate.MapMarkerPositions)?.Position
                ?? GetPreparingFateWaypoint(fate)
            : fate.Position;
        float distance = this.DistanceToPlayer(arrivalPosition);
        float horizontalDistance = this.HorizontalDistanceToPlayer(arrivalPosition);
        float arrivalBoundary = Math.Max(12f, fate.Radius) + 2f;
        if (!preparing
            && !this.mount.IsInFlight
            && distance > arrivalBoundary
            && !this.fallbackLandingForParticipation)
        {
            this.StopNavigationOperation();
            this.landing.StopDescending();
            this.levelSyncAttempts = 0;
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                $"落地状态校验失败：距 FATE #{fate.FateId} 中心三维距离仍有 {distance:0.0}（水平 {horizontalDistance:0.0}，允许 {arrivalBoundary:0.0}），恢复赶路");
            if (this.ShouldFlyToActiveFate && !this.mount.IsMounted)
            {
                this.Transition(AutomationState.MountingForTravel, "落地点远离 FATE 区域，重新上坐骑赶路");
            }
            else
            {
                this.Transition(AutomationState.NavigatingToFate, "落地点远离 FATE 区域，重新导航");
            }
            return;
        }

        if (this.mount.IsInFlight)
        {
            this.StopNavigationOperation();
            this.BeginStableLanding(now, "FATE");
            this.Transition(AutomationState.LandingForFate, "角色仍在飞行，禁止空中下坐骑", DiagnosticSeverity.Warning);
            return;
        }

        this.landing.StopDescending();
        this.StopNavigationOperation();
        if (now - this.stateEnteredAt >= TimeSpan.FromSeconds(10))
        {
            this.FailRecoverableAction("落地后 10 秒内未能成功下坐骑", this.IsRequiredFate(fate.FateId));
            return;
        }

        if (this.mount.IsMountTransition || this.mount.IsJumping)
        {
            this.statusReason = "等待坐骑过渡/落地跳跃结束";
            this.nextActionAt = now.AddMilliseconds(100);
            return;
        }

        if (this.mount.IsDismounted)
        {
            this.preparingGroundArrivalPending = false;
            this.levelSyncAttempts = 0;
            this.BeginCompanionCheckpoint(
                CompanionCheckpointKind.FateArrival,
                preparing ? AutomationState.OpeningPreparingFate : AutomationState.WaitingForLevelSync,
                "已确认抵达 FATE、落地且下坐骑；先检查伙伴再继续");
            return;
        }

        bool sent = this.mount.TryDismount();
        this.statusReason = sent ? "已请求下坐骑，等待角色落地稳定" : "下坐骑请求未被客户端接受，准备重试";
        this.AddDiagnostic(sent ? DiagnosticSeverity.Information : DiagnosticSeverity.Debug, this.statusReason);
        this.nextActionAt = now.AddMilliseconds(250);
    }

    private void CheckNavigationProgress(
        DateTime now,
        float distance,
        FateSnapshot fate,
        Vector3? destination = null,
        bool horizontal = false)
    {
        if (!this.travelSessionActive)
            return;

        Vector3 targetPosition = destination ?? this.travelDestination;
        NavigationTravelUpdate update = this.TickTravelSession(now, targetPosition, () => false);
        if (update.Outcome == NavigationTravelOutcome.Failed)
            this.HandleTravelSessionFailure(now, fate, targetPosition, horizontal, update);
        else if (update.RequestResult == NavigationRequestResult.Rejected)
        {
            this.EndTravelSession();
            this.BeginNavigationRecovery(now, fate, $"vnavmesh 拒绝导航请求：{update.Reason}");
        }
    }

    private void StartTravelSession(
        DateTime now,
        NavigationPurpose purpose,
        Vector3 destination,
        bool fly,
        GroundDestinationKind groundKind,
        bool horizontalProgress,
        string context)
    {
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (player is null)
            return;

        bool same = this.travelSessionActive
            && this.travelPurpose == purpose
            && this.travelFly == fly
            && this.travelGroundKind == groundKind
            && this.travelHorizontal == horizontalProgress
            && Vector3.DistanceSquared(this.travelDestination, destination) <= 1f;
        if (same && this.travelSession.IsActive)
            return;

        this.EndTravelSession();
        NoFlyZone? zone = this.GetCurrentNoFlyZone();
        this.travelPurpose = purpose;
        this.travelDestination = destination;
        this.travelFly = fly;
        this.travelGroundKind = groundKind;
        this.travelHorizontal = horizontalProgress;
        this.travelContext = context;
        this.travelSession.Start(
            now,
            player.Position,
            destination,
            zone is not null,
            new NavigationTravelOptions
            {
                InitialIntent = fly ? NavigationTravelIntent.Fly : NavigationTravelIntent.Ground,
                GroundKind = groundKind,
                HorizontalProgress = horizontalProgress,
                StallTimeout = TimeSpan.FromSeconds(this.configuration.NavigationStuckSeconds),
            });
        this.travelSessionActive = true;
        this.nextTravelSnapshotAt = DateTime.MinValue;
    }

    private void EndTravelSession()
    {
        if (this.travelSessionActive)
            this.travelSession.Cancel();
        this.travelSessionActive = false;
        this.travelPurpose = NavigationPurpose.None;
        this.travelContext = string.Empty;
        this.nextTravelSnapshotAt = DateTime.MinValue;
    }

    private NavigationTravelUpdate TickTravelSession(
        DateTime now,
        Vector3 destination,
        Func<bool> arrived)
    {
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (player is null || !this.travelSessionActive)
            return new NavigationTravelUpdate(
                NavigationTravelState.Idle,
                NavigationTravelOutcome.None,
                NavigationTravelMode.Ground,
                NavigationGroundReason.None,
                NavigationFailureStage.None,
                LandingStatus.Descending,
                NavigationRequestResult.NotDue,
                NavigationRejection.None,
                "导航会话尚未启动");

        NoFlyZone? zone = this.GetCurrentNoFlyZone();
        FlightState flight = new(this.mount.IsInFlight, this.mount.IsJumping, this.mount.IsMountTransition);
        bool arrivedNow = arrived();
        NavigationTravelUpdate update = this.travelSession.Tick(new NavigationTravelFrame(
            now,
            player.Position,
            Vector3.Distance(player.Position, destination),
            arrivedNow,
            zone is not null,
            flight));

        if (now >= this.nextTravelSnapshotAt)
        {
            TimeSpan settledFor = this.travelSession.Landing.SettledSince is { } settled
                ? now - settled
                : TimeSpan.Zero;
            this.AddDiagnostic(
                DiagnosticSeverity.Debug,
                $"[NAV_SNAPSHOT] context={this.travelContext}; businessState={this.state}; "
                + $"sessionState={update.State}; outcome={update.Outcome}; initialIntent={this.travelSession.InitialIntent}; "
                + $"mode={update.Mode}; groundKind={this.travelSession.GroundKind}; groundReason={update.GroundReason}; "
                + $"failureStage={update.FailureStage}; failureRejection={this.travelSession.FailureRejection}; "
                + $"request={update.RequestResult}; requestRejection={update.Rejection}; insideNoFly={zone is not null}; "
                + $"purpose={this.travelPurpose}; position={player.Position}; destination={destination}; "
                + $"resolvedDestination={this.travelSession.ResolvedDestination?.ToString() ?? "null"}; "
                + $"distance={Vector3.Distance(player.Position, destination):0.0}; arrived={arrivedNow}; "
                + $"inFlight={flight.InFlight}; jumping={flight.Jumping}; mountTransition={flight.MountTransition}; "
                + $"moveInProgress={this.vnavmesh.IsMoveInProgress}; pathRunning={this.vnavmesh.IsPathRunning}; "
                + $"moveActive={this.vnavmesh.IsMoveActive}; landingStatus={update.LandingStatus}; "
                + $"settledForMs={settledFor.TotalMilliseconds:0}; canResumeFlight={this.travelSession.CanResumeFlight}; "
                + $"reason={update.Reason}");
            this.nextTravelSnapshotAt = now.AddSeconds(1);
        }

        if (update.State is NavigationTravelState.Landing or NavigationTravelState.JumpRecovery)
            this.statusReason = $"{this.travelContext}：{update.Reason}";
        else if (!string.IsNullOrWhiteSpace(update.Reason)
                 && update.Outcome == NavigationTravelOutcome.Progress)
            this.statusReason = update.Reason;
        return update;
    }

    private void HandleTravelSessionFailure(
        DateTime now,
        FateSnapshot fate,
        Vector3 targetPosition,
        bool horizontal,
        NavigationTravelUpdate update)
    {
        this.AddDiagnostic(
            DiagnosticSeverity.Warning,
            $"导航会话失败：purpose={this.travelPurpose}，stage={update.FailureStage}，"
            + $"rejection={update.Rejection}，target={targetPosition}，reason={update.Reason}");
        this.EndTravelSession();
        this.BeginNavigationRecovery(now, fate, update.Reason + $"，目标={targetPosition}，水平判定={horizontal}");
    }

    private NavigationRequestResult TryIssueNavigationRequest(
        DateTime now,
        NavigationPurpose purpose,
        Vector3 destination,
        bool fly,
        bool mapWaypoint = false)
    {
        GroundDestinationKind groundKind = purpose switch
        {
            NavigationPurpose.TravelToFate => mapWaypoint
                ? GroundDestinationKind.MapWaypoint
                : GroundDestinationKind.LiveObject,
            NavigationPurpose.PreparingOpener
                or NavigationPurpose.CollectionObjective
                or NavigationPurpose.CollectionTurnIn
                or NavigationPurpose.EscortFollow
                or NavigationPurpose.PullTarget => GroundDestinationKind.LiveObject,
            _ => GroundDestinationKind.Raw,
        };
        this.StartTravelSession(
            now,
            purpose,
            destination,
            fly,
            groundKind,
            purpose == NavigationPurpose.ReturnToFate,
            purpose.ToString());
        NavigationTravelUpdate update = this.TickTravelSession(now, destination, () => false);
        if (update.Outcome == NavigationTravelOutcome.Failed)
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                $"导航请求失败：purpose={purpose}，原始目标={destination}，原因={update.Reason}");
            this.EndTravelSession();
            return NavigationRequestResult.Rejected;
        }
        if (update.RequestResult == NavigationRequestResult.Rejected)
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                $"导航请求被拒绝：purpose={purpose}，原始目标={destination}，原因={update.Reason}");
            this.EndTravelSession();
            return NavigationRequestResult.Rejected;
        }
        if (update.RequestResult == NavigationRequestResult.Accepted)
        {
            this.AddDiagnostic(
                purpose == NavigationPurpose.TravelToFate ? DiagnosticSeverity.Information : DiagnosticSeverity.Debug,
                $"导航请求已接受：purpose={purpose}，fly={fly}，原始目标={destination}，"
                + $"实际终点={this.travelSession.ResolvedDestination}，原因={update.Reason}");
            return NavigationRequestResult.Accepted;
        }
        return NavigationRequestResult.NotDue;
    }

    private NoFlyZone? GetCurrentNoFlyZone()
    {
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        return player is null ? null : this.noFlyZones.Find(this.clientState.TerritoryType, player.Position);
    }

    private void BeginNavigationRecovery(DateTime now, FateSnapshot fate, string reason)
    {
        this.StopNavigationOperation();
        if (this.IsRequiredFate(fate.FateId))
        {
            this.FailRecoverableAction($"指定 FATE 导航失败，暂不自动跳过：{reason}", required: true);
            return;
        }

        // A normal navigation stall gets one map-crystal recovery only. If navigation still
        // fails after returning to that crystal, repeating the same teleport cannot improve the
        // route and only causes an unnecessary loop.
        if (this.navigationRecoveryAttempts > 0)
        {
            this.SkipNavigationFate(fate, $"地图水晶恢复后仍无法导航：{reason}");
            return;
        }

        int maxAttempts = 1;
        this.navigationRecoveryAttempts++;
        if (this.navigationRecoveryAttempts > maxAttempts)
        {
            this.SkipNavigationFate(fate, reason);
            return;
        }

        if (!this.lifestream.IsAvailable)
        {
            this.FailRecoverableAction($"导航恢复需要 Lifestream，但依赖不可用：{reason}", required: false);
            return;
        }

        IAetheryteEntry? aetheryte = this.ResolveTeleportAetheryte(this.clientState.TerritoryType, 0);
        if (aetheryte is null)
        {
            this.AddDiagnostic(DiagnosticSeverity.Warning, $"当前地图没有可用大水晶，无法执行导航恢复（{reason}）");
            if (this.navigationRecoveryAttempts >= maxAttempts)
                this.SkipNavigationFate(fate, reason + "；当前地图没有可用大水晶");
            else
                this.FailRecoverableAction(reason + "；当前地图没有可用大水晶", required: false);
            return;
        }

        if (this.lifestream.IsBusy)
        {
            this.statusReason = "等待 Lifestream 空闲后执行导航恢复传送";
            this.nextActionAt = now.AddSeconds(2);
            return;
        }

        if (!this.lifestream.Teleport(aetheryte.AetheryteId, aetheryte.SubIndex))
        {
            this.AddDiagnostic(DiagnosticSeverity.Warning, $"导航恢复传送请求失败：{reason}");
            this.SkipNavigationFate(fate, reason + "；恢复传送失败");
            return;
        }

        this.teleportIssuedByUs = true;
        this.navigationRecoveryTeleportIssued = true;
        this.teleportArrival.Requested();
        this.teleportBusyObserved = false;
        this.navigationRecoveryStartedAt = now;
        this.teleportStartedAt = now;
        this.Transition(
            AutomationState.RecoveringNavigation,
            $"{reason}；第 {this.navigationRecoveryAttempts}/{maxAttempts} 次传送至当前地图大水晶后重试");
    }

    private void HandleNavigationRecovery(DateTime now)
    {
        if (!this.navigationRecoveryTeleportIssued)
        {
            this.Transition(AutomationState.NavigatingToFate, "导航恢复传送状态已结束，重新寻路");
            return;
        }

        if (now - this.navigationRecoveryStartedAt > TimeSpan.FromSeconds(120))
        {
            this.lifestream.Abort();
            this.teleportArrival.CancelRequest();
            this.navigationRecoveryTeleportIssued = false;
            this.teleportIssuedByUs = false;
            this.teleportBusyObserved = false;
            if (this.ResolveActiveFate() is { } fate)
                this.SkipNavigationFate(fate, "导航恢复传送超时");
            else
                this.AbandonActiveFate("导航恢复传送超时且目标已失效");
            return;
        }

        bool recoveryTeleportBusy = this.lifestream.IsBusy;
        if (this.teleportArrival.Complete(true, this.IsBetweenAreas() || now < this.territoryStableAfter, recoveryTeleportBusy))
        {
            this.navigationRecoveryTeleportIssued = false;
            this.teleportIssuedByUs = false;
            this.teleportBusyObserved = false;
            this.ResetNavigationProgress();
            this.BeginCompanionCheckpoint(CompanionCheckpointKind.MapArrival,
                AutomationState.NavigatingToFate, "导航恢复大水晶传送完成，检查状态后继续前往 FATE");
            return;
        }
        if (!this.IsBetweenAreas() && !recoveryTeleportBusy
            && now - this.navigationRecoveryStartedAt >= TimeSpan.FromSeconds(15))
        {
            this.lifestream.Abort();
            this.teleportArrival.CancelRequest();
            this.navigationRecoveryTeleportIssued = false;
            this.teleportIssuedByUs = false;
            this.teleportBusyObserved = false;
            if (this.ResolveActiveFate() is { } interruptedFate)
                this.SkipNavigationFate(interruptedFate, "地图水晶传送请求未完成区域切换");
            else
                this.AbandonActiveFate("地图水晶传送失败且目标已失效");
            return;
        }
        this.statusReason = "等待地图水晶传送完成及区域稳定，再执行状态检查";
        this.nextActionAt = now.AddMilliseconds(500);
    }

    private void SkipNavigationFate(FateSnapshot fate, string reason)
    {
        this.StopNavigationOperation();
        this.skippedFates[fate.FateId] = DateTime.UtcNow.AddSeconds(60);
        this.RecordFateFailure(fate, "寻路失败", reason);
        string message = $"普通 FATE #{fate.FateId}「{fate.Name}」导航恢复耗尽，已跳过 60 秒：{reason}";
        this.chat.Print(message, "AutoFatre");
        this.PlaySoundAlert("普通 FATE 导航恢复耗尽");
        this.AddDiagnostic(DiagnosticSeverity.Warning, message);
        this.AbandonActiveFate(message);
    }

    private void HandleLevelSync(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        if (fate is null)
        {
            this.AbandonActiveFate("同步前目标 FATE 已失效");
            return;
        }

        float distance = this.DistanceToPlayer(fate.Position);
        float syncBoundary = Math.Max(12f, fate.Radius) + 2f;
        if (distance > syncBoundary
            && !this.fallbackLandingForParticipation
            && !IsPlayerInFate(fate.FateId))
        {
            this.StopNavigationOperation();
            this.levelSyncAttempts = 0;
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                $"同步前位置校验失败：距 FATE #{fate.FateId} 中心三维距离 {distance:0.0}（允许 {syncBoundary:0.0}），返回导航流程");
            this.Transition(
                this.ShouldFlyToActiveFate && !this.mount.IsMounted
                    ? AutomationState.MountingForTravel
                    : AutomationState.NavigatingToFate,
                "尚未到达 FATE 区域，禁止等级同步");
            return;
        }

        if (this.mount.IsInFlight || this.mount.IsMounted || this.mount.IsMountTransition || this.mount.IsJumping)
        {
            this.Transition(AutomationState.DismountingForFate, "等级同步前检测到坐骑/飞行状态，返回落地流程", DiagnosticSeverity.Warning);
            return;
        }

        // Boss-area safety inspection must happen before the first level-sync request. If the
        // low-level FATE has a large nearby pack, clean it while still at the player's normal
        // level, then come back through this state and sync only after the pack is gone.
        if (this.TryBeginBossPreSyncCleanup(now, fate))
            return;

        byte syncCap = fate.MaxLevel > 0 ? fate.MaxLevel : fate.Level;
        bool required = this.playerState.Level > syncCap;
        bool syncedToCurrentFate = this.playerState.IsLevelSynced && this.levelSync.IsSyncedTo(fate.FateId);
        if (!required || syncedToCurrentFate)
        {
            string syncResult = required ? "等级同步成功" : "当前等级无需同步";
            switch (fate.CombatProfile.Strategy)
            {
                case FateCombatStrategyKind.General when fate.IsAutomationSupported:
                    this.Transition(
                        AutomationState.PullingTargets,
                        $"{syncResult}，进入通用战斗模式（上限 {this.configuration.MaxAggroCount}）");
                    return;
                case FateCombatStrategyKind.Boss when fate.IsAutomationSupported:
                    this.Transition(AutomationState.BossFighting, $"{syncResult}，进入独立的讨伐BOSS处理器");
                    return;
                case FateCombatStrategyKind.Collection:
                    this.BeginCollectionItems(fate, now);
                    return;
                default:
                    this.skippedFates[fate.FateId] = now.AddSeconds(60);
                    this.AbandonActiveFate($"FATE #{fate.FateId} {fate.CombatProfile.SupportDescription}，不会按抵达或同步计为完成");
                    return;
            }
        }

        if (this.levelSyncAttempts >= 5)
        {
            this.StopNavigationOperation();
            this.ClearTarget();
            string syncFailureReason =
                $"等级同步连续 5 次未成功：FATE=#{fate.FateId}，" +
                $"FateManagerSynced={this.levelSync.IsSyncedTo(fate.FateId)}，" +
                $"PlayerIsLevelSynced={this.playerState.IsLevelSynced}，" +
                $"尝试次数={this.levelSyncAttempts}，当前位置距中心={distance:0.0}，" +
                $"InCombat={this.condition[ConditionFlag.InCombat]}";
            this.RecordFateFailure(fate, "等级同步失败", syncFailureReason);
            this.AbandonActiveFate($"FATE #{fate.FateId} 等级同步失败，临时跳过并继续扫描");
            return;
        }

        this.levelSyncAttempts++;
        bool sent = this.levelSync.TrySync(fate.FateId);
        this.AddDiagnostic(
            sent ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
            $"等级同步请求：FATE=#{fate.FateId}，第 {this.levelSyncAttempts}/5 次，" +
            $"发送结果={sent}，PlayerIsLevelSynced={this.playerState.IsLevelSynced}，" +
            $"FateManagerSynced={this.levelSync.IsSyncedTo(fate.FateId)}，" +
            $"当前位置距中心={distance:0.0}");
        this.statusReason = sent
            ? $"等待等级同步生效（{this.levelSyncAttempts}/5）"
            : $"等待等级同步按钮出现（{this.levelSyncAttempts}/5）";
        this.nextActionAt = now.AddSeconds(1);
    }

    private bool TryBeginBossPreSyncCleanup(DateTime now, FateSnapshot fate)
    {
        if (fate.CombatProfile.Strategy != FateCombatStrategyKind.Boss
            || this.bossPreCombatCleanupChecked)
            return false;

        if (!this.IsBossLevelGapLarge(fate))
        {
            if (now >= this.bossEnvironmentLogAt)
            {
                this.AddDiagnostic(
                    DiagnosticSeverity.Information,
                    $"Boss FATE #{fate.FateId} 等级同步前环境检查：不清理，等级差 {this.playerState.Level - this.GetFateLevel(fate)} 小于 10；FATE等级={this.GetFateLevel(fate)}，玩家等级={this.playerState.Level}");
                this.bossEnvironmentLogAt = now.AddSeconds(2);
            }
            this.bossPreCombatCleanupChecked = true;
            return false;
        }

        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (player is null)
            return false;

        // A named destroy object owns combat even when the FATE's normal strategy is Boss.
        if (this.staticFateCatalog.TryGet(fate.FateId, out StaticFateCatalogEntry destroyEntry)
            && this.targetSelector.FindNamedPriorityTargets(fate.FateId, player.Position, destroyEntry.Targets.Destroy).Count > 0)
            return false;

        IBattleNpc? boss = this.targetSelector.FindBossTarget(fate.FateId, player.Position);
        if (boss is null)
        {
            if (now >= this.bossEnvironmentLogAt)
            {
                this.AddDiagnostic(
                    DiagnosticSeverity.Information,
                    $"Boss FATE #{fate.FateId} 等级同步前环境检查：未找到 Boss，暂不清理；FATE等级={this.GetFateLevel(fate)}，玩家等级={this.playerState.Level}，等级差={this.playerState.Level - this.GetFateLevel(fate)}");
                this.bossEnvironmentLogAt = now.AddSeconds(2);
            }
            this.statusReason = "等级同步前等待 Boss 对象加载，以完成周围非 FATE 环境检查";
            this.nextActionAt = now.AddMilliseconds(500);
            return true;
        }

        float radius = BossNonFateCleanupRadius;
        IReadOnlyList<IBattleNpc> nearby = this.targetSelector.FindNonFateTargetsNear(fate.FateId, boss.Position, radius);
        if (now >= this.bossEnvironmentLogAt)
        {
            string reason = nearby.Count >= 3
                ? "满足条件：等级同步前进入非 FATE 清理"
                : $"不清理：Boss 周围非 FATE 怪物数量 {nearby.Count} 小于 3";
            this.AddDiagnostic(
                nearby.Count >= 3 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Information,
                $"Boss FATE #{fate.FateId} 等级同步前环境检查：Boss={boss.Name}({boss.GameObjectId:X})，FATE等级={this.GetFateLevel(fate)}，玩家等级={this.playerState.Level}，等级差={this.playerState.Level - this.GetFateLevel(fate)}，范围={radius:0.0}，非FATE数量={nearby.Count}；{reason}" +
                (nearby.Count > 0 ? $"；目标={FormatNonFateTargets(nearby)}" : string.Empty));
            this.bossEnvironmentLogAt = now.AddSeconds(2);
        }

        this.bossPreCombatCleanupChecked = true;
        if (nearby.Count < 3)
            return false;

        this.BeginBossNonFateCleanup(
            fate,
            BossCleanupContinuation.BeforeBossFight,
            $"等级同步前 Boss 周围发现 {nearby.Count} 个非 FATE 怪物，先清理后同步");
        return true;
    }

    private bool EnsureLevelSyncBeforeCombat(DateTime now, FateSnapshot fate, string context)
    {
        byte syncCap = this.GetFateLevel(fate);
        if (this.playerState.Level <= syncCap)
            return false;

        if (this.playerState.IsLevelSynced && this.levelSync.IsSyncedTo(fate.FateId))
            return false;

        this.ClearTarget();
        this.pullTargetId = null;
        this.killTargetId = null;
        this.lostPriorityTargetId = null;
        this.lostPriorityTargetKind = null;
        this.priorityDestroyTargetId = null;
        this.destroySession.Reset();
        this.bossTargetId = null;
        this.ResetPullBatch();
        this.levelSyncAttempts = 0;
        this.Transition(
            AutomationState.WaitingForLevelSync,
            $"{context}检测到尚未同步到 FATE #{fate.FateId}，先执行等级同步",
            DiagnosticSeverity.Warning);
        this.nextActionAt = now;
        return true;
    }

    private void BeginCollectionItems(FateSnapshot fate, DateTime now)
    {
        if (this.collectionFateId != fate.FateId)
        {
            this.collectionFateId = fate.FateId;
            this.collectionItemId = fate.EventItem;
            this.collectionInteractionCount = 0;
            this.completedCollectionObjects.Clear();
            this.collectionObjectId = null;
            this.collectionTurnInObjectId = null;
            this.collectionInventoryBefore = this.inventoryCounter.Snapshot();
            this.collectionObservedInventory = this.collectionInventoryBefore;
            this.collectionInteractionStartedAt = DateTime.MinValue;
            this.collectionTurnInStartedAt = DateTime.MinValue;
            this.collectionInteractionDiagnosticAt = DateTime.MinValue;
            this.collectionDialogueLastActionAt = DateTime.MinValue;
            this.collectionInteractionActionSequence = 0;
            this.collectionInteractionCheckSequence = false;
            this.collectionCastObserved = false;
            this.collectionTurnInUrgent = false;
            this.collectionTurnInPhase = false;
            this.collectionTurnInInteractionIssued = false;
            this.collectionTurnInDialogueSeen = false;
            this.collectionSprintIssued = false;
            this.collectionTurnInItemCountBefore = 0;
            this.collectionTurnInProgressBefore = 0;
            this.collectionCombatFallback = false;
            this.collectionProgressPerItem = 0;
            this.collectionNextInteractionAt = now;
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"进入收集 FATE #{fate.FateId}「{fate.Name}」：EventItem={fate.EventItem}；" +
                $"ObjectiveNpc={fate.ObjectiveNpc:X}，MotivationNpc={fate.MotivationNpc:X}");
        }

        this.Transition(AutomationState.CollectingFateItems, "等级同步完成，开始寻找最近的 FATE 收集点");
        this.nextActionAt = now;
    }

    private void HandleCollectionItems(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (fate is null || player is null || this.collectionFateId != fate.FateId)
        {
            this.AbandonActiveFate("收集流程中的 FATE 或玩家对象失效");
            return;
        }

        if (fate.EventItem == 0)
        {
            this.AddDiagnostic(DiagnosticSeverity.Warning,
                $"收集 FATE #{fate.FateId} 未提供原生 EventItem，不能安全计数或交付；切换到普通战斗流程");
            this.collectionCombatFallback = true;
            this.Transition(AutomationState.PullingTargets, "原生 EventItem 缺失，使用普通小怪拉怪流程");
            return;
        }

        // The combat branch always wins over collection. Do not wait for an interaction cast or
        // infer its result from the inventory: the next cycle resumes from the currently visible
        // EventObj after all aggro targets are clear.
        if (this.condition[ConditionFlag.InCombat])
        {
            this.StopNavigationOperation();
            this.ClearTarget();
            this.BeginCombatCleanup(CleanupContinuation.ResumeCollection, "收集 FATE 进入战斗，优先清理所有接战目标");
            return;
        }

        int itemCount = this.inventoryCounter.Count(fate.EventItem);
        int threshold = this.GetCollectionTurnInThreshold(fate);
        if ((fate.Progress >= 100 && itemCount > 0) || itemCount >= threshold)
        {
            this.BeginCollectionTurnIn(fate, now, urgent: fate.Progress >= 100);
            return;
        }

        IGameObject? collectionObject = this.targetSelector.FindNearestCollectionObject(fate.FateId, player.Position);
        if (collectionObject is null)
        {
            this.collectionCombatFallback = true;
            this.StopNavigationOperation();
            this.Transition(AutomationState.PullingTargets, "没有可交互 EventObj，回退至 FATE 小怪常规拉怪流程");
            return;
        }

        this.collectionObjectId = collectionObject.GameObjectId;
        float distance = Vector3.Distance(player.Position, collectionObject.Position);
        if (distance > 3.5f)
        {
            this.statusReason = $"前往最近收集点「{collectionObject.Name}」（{distance:0.0} yalms）";
            NavigationRequestResult request = this.TryIssueNavigationRequest(
                now, NavigationPurpose.CollectionObjective, collectionObject.Position, fly: false);
            if (request == NavigationRequestResult.Rejected)
            {
                this.BeginNavigationRecovery(now, fate, "收集点导航失败");
                return;
            }
            this.nextActionAt = now.AddMilliseconds(150);
            return;
        }

        this.StopNavigationOperation();
        if (!this.mount.IsDismounted)
        {
            this.mount.TryDismount();
            this.nextActionAt = now.AddMilliseconds(150);
            return;
        }

        if (now < this.collectionNextInteractionAt || this.HasCollectionDialogueVisible())
        {
            this.statusReason = $"等待收集点可交互：任务物品 {itemCount}/{threshold}";
            this.nextActionAt = now.AddMilliseconds(100);
            return;
        }

        bool accepted = this.InteractWithCollectionObject(collectionObject);
        this.collectionNextInteractionAt = now.Add(CollectionInteractionRepeatDelay);
        this.statusReason = accepted
            ? $"已交互收集点「{collectionObject.Name}」，立即重新扫描"
            : $"收集点交互未接受，等待重试：{collectionObject.Name}";
        this.AddDiagnostic(
            accepted ? DiagnosticSeverity.Debug : DiagnosticSeverity.Warning,
            $"收集交互：object={collectionObject.GameObjectId:X}，FateId={fate.FateId}，EventItem={fate.EventItem}，" +
            $"accepted={accepted}；不等待读条，下一周期重新按 FateId 扫描 EventObj");
        this.nextActionAt = now.AddMilliseconds(100);
    }

    [Obsolete("Replaced by the native EventItem/EventObj collection flow.")]
    private void HandleLegacyCollectionItems(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (fate is null || player is null || this.collectionFateId != fate.FateId)
        {
            this.AbandonActiveFate("收集流程中的 FATE 或玩家对象失效");
            return;
        }

        this.ObserveCollectionInventory();

        if (this.collectionCombatFallback)
        {
            if (!this.condition[ConditionFlag.InCombat]
                && this.targetSelector.FindNearestCollectionObject(
                    fate.FateId,
                    player.Position,
                    fate.Position,
                    fate.Radius,
                    fate.Objectives.Select(o => o.Position).Where(FateRepository.HasValidPosition).ToArray(),
                    this.completedCollectionObjects) is not null)
            {
                this.collectionCombatFallback = false;
                this.Transition(AutomationState.CollectingFateItems, "收集点重新出现，结束普通小怪回退并恢复收集");
                return;
            }

            this.collectionCombatFallback = false;
            this.Transition(AutomationState.PullingTargets, "收集点仍不可见，使用普通小怪拉怪流程");
            return;
        }

        // This check is deliberately before the item-count and interaction logic: being attacked
        // while walking between points or while a point is merely being selected must also enter
        // the same full aggro cleanup path.
        if (this.condition[ConditionFlag.InCombat])
        {
            this.StopNavigationOperation();
            this.ClearTarget();
            this.collectionObjectId = null;
            this.collectionInteractionStartedAt = DateTime.MinValue;
            this.collectionCastObserved = false;
            this.BeginCombatCleanup(CleanupContinuation.ResumeCollection, "收集流程任何阶段进入战斗，立即清理当前所有接战目标");
            return;
        }

        if (this.collectionTurnInUrgent || fate.Progress >= 100)
        {
            this.BeginCollectionTurnIn(fate, now, urgent: true);
            return;
        }

        int targetCount = GetCollectionTurnInThreshold(fate);
        int count = this.GetCollectionItemCount();
        this.statusReason = $"收集 FATE 任务物品：{count} / {targetCount}";
        if (count >= targetCount)
        {
            this.BeginCollectionTurnIn(fate, now, urgent: false);
            return;
        }

        bool casting = this.condition[ConditionFlag.Casting] || this.condition[ConditionFlag.Casting87];
        if (this.collectionInteractionStartedAt != DateTime.MinValue)
        {
            if (this.condition[ConditionFlag.InCombat])
            {
                this.StopNavigationOperation();
                this.ClearTarget();
                this.collectionObjectId = null;
                this.collectionInteractionStartedAt = DateTime.MinValue;
                this.collectionCastObserved = false;
                this.BeginCombatCleanup(CleanupContinuation.ResumeCollection, "收集读条被攻击打断，先消灭当前所有接战目标");
                return;
            }

            int elapsedMilliseconds = (int)(now - this.collectionInteractionStartedAt).TotalMilliseconds;
            ActionManager* progressActionManager = ActionManager.Instance();
            bool actionSequenceComplete = progressActionManager != null
                && (!this.collectionInteractionCheckSequence
                    || (this.collectionInteractionActionSequence == progressActionManager->LastUsedActionSequence
                        && this.collectionInteractionActionSequence == progressActionManager->LastHandledActionSequence));
            bool nativeCastRunning = progressActionManager != null
                && progressActionManager->CastTimeTotal > 0
                && progressActionManager->CastTimeElapsed > 0
                && progressActionManager->CastTimeElapsed < progressActionManager->CastTimeTotal;
            bool castComplete = progressActionManager != null
                && progressActionManager->CastTimeElapsed > 0
                && Math.Abs(progressActionManager->CastTimeElapsed - progressActionManager->CastTimeTotal) < 0.001f;
            bool actionInterrupted = progressActionManager != null
                && this.collectionCastObserved
                && elapsedMilliseconds >= CollectionCastInterruptWindow.TotalMilliseconds
                && progressActionManager->CastTimeTotal > 0
                && progressActionManager->CastTimeElapsed == 0
                && this.collectionInteractionCheckSequence
                && this.collectionInteractionActionSequence != progressActionManager->LastUsedActionSequence;

            // The framework can enter this method just before ConditionFlag.Casting flips.  The
            // native cast timer catches that short interval and prevents us from losing the
            // "cast really started" edge.
            // Do not use a stale CastTimeElapsed==CastTimeTotal from the action immediately
            // before this interaction as proof that this interaction completed.  The current
            // interaction is considered started only after a post-request casting/native timer
            // observation.
            this.collectionCastObserved |= casting || nativeCastRunning;

            IGameObject? liveObject = this.collectionObjectId is { } objectId
                ? this.objectTable.FirstOrDefault(o => o.IsValid() && o.GameObjectId == objectId)
                : null;
            bool objectGone = liveObject is null || !liveObject.IsTargetable;
            if (now >= this.collectionInteractionDiagnosticAt)
            {
                this.collectionInteractionDiagnosticAt = now.AddMilliseconds(250);
                string objectIdText = this.collectionObjectId is { } diagnosticObjectId
                    ? diagnosticObjectId.ToString("X")
                    : "unknown";
                string liveObjectText = liveObject is null
                    ? "null"
                    : $"valid={liveObject.IsValid()},targetable={liveObject.IsTargetable},kind={liveObject.ObjectKind},name={liveObject.Name}";
                string actionText = progressActionManager is null
                    ? "null"
                    : $"cast={progressActionManager->CastTimeElapsed:0.000}/{progressActionManager->CastTimeTotal:0.000}," +
                      $"last={progressActionManager->LastUsedActionSequence},handled={progressActionManager->LastHandledActionSequence}";
                this.AddDiagnostic(
                    DiagnosticSeverity.Debug,
                    $"采集交互诊断：elapsed={elapsedMilliseconds}ms，object={objectIdText}，live={liveObjectText}，" +
                    $"casting={casting},observed={this.collectionCastObserved},nativeRunning={nativeCastRunning}," +
                    $"castComplete={castComplete},actionSequenceComplete={actionSequenceComplete}," +
                    $"actionInterrupted={actionInterrupted},objectGone={objectGone}，ActionManager[{actionText}]，" +
                    $"interactionSeq={this.collectionInteractionActionSequence}," +
                    $"checkSeq={this.collectionInteractionCheckSequence},state={this.state}");
            }
            if (this.collectionCastObserved && castComplete && actionSequenceComplete)
            {
                this.CompleteCollectionInteraction(now, "交互读条完成（不依赖任务物品变化）");
                return;
            }

            // A cast ending is not by itself a successful pickup.  Only an explicit native
            // action-sequence interruption may release this interaction here; a transient
            // Casting flag transition is not enough evidence and must not cause a rapid retry
            // loop.
            if (actionInterrupted)
            {
                this.collectionInteractionStartedAt = DateTime.MinValue;
                this.collectionCastObserved = false;
                this.collectionInteractionActionSequence = 0;
                this.collectionInteractionCheckSequence = false;
                this.collectionObjectId = liveObject is not null && !objectGone
                    ? liveObject.GameObjectId
                    : this.collectionObjectId;
                this.AddDiagnostic(
                    DiagnosticSeverity.Warning,
                    $"收集点动作序列明确中断，立即重试：object=" +
                    (this.collectionObjectId is { } interruptedObjectId
                        ? $"{interruptedObjectId:X}"
                        : "unknown"));
                this.nextActionAt = now.AddMilliseconds(75);
                return;
            }

            // A collection point can disappear as soon as the interaction starts, while its
            // cast is still running.  Never advance to another point in that window.  If the
            // cast was observed and has ended, the interruption branch above has already handled
            // it; this fallback is only for an object that vanished before any cast signal was
            // observable.
            if (objectGone
                && !casting
                && !nativeCastRunning
                && !this.collectionCastObserved
                && elapsedMilliseconds >= CollectionObjectGoneConfirmationWindow.TotalMilliseconds)
            {
                // A broken interaction can also make the EventObj disappear.  Release this
                // attempt without adding the instance to completedCollectionObjects; when the
                // point respawns, it must remain eligible for a retry.  This is intentionally
                // independent of inventory changes because FATE enemies can drop the same item.
                this.CompleteCollectionInteraction(
                    now,
                    "交互对象消失但没有读条完成信号，释放实例并等待重新出现",
                    markObjectCompleted: false);
                return;
            }

            // If the interaction request was accepted but no cast signal appeared at all, it
            // was cancelled before the first observable frame (or the game rejected it after
            // the target-system call).  Do not leave this instance alive until the 15-second
            // emergency timeout; retry the same live point now.
            if (!this.collectionCastObserved
                && !casting
                && !nativeCastRunning
                && !objectGone
                && elapsedMilliseconds >= CollectionCastStartWindow.TotalMilliseconds)
            {
                this.collectionInteractionStartedAt = DateTime.MinValue;
                this.collectionInteractionActionSequence = 0;
                this.collectionInteractionCheckSequence = false;
                string objectIdText = this.collectionObjectId is { } pendingObjectId
                    ? pendingObjectId.ToString("X")
                    : "unknown";
                this.AddDiagnostic(
                    DiagnosticSeverity.Warning,
                    $"收集点交互已接受但未出现读条信号，立即重试：object={objectIdText}");
                this.nextActionAt = now.AddMilliseconds(75);
                return;
            }

            if (elapsedMilliseconds >= CollectionInteractionTimeout.TotalMilliseconds)
            {
                if (objectGone)
                {
                    this.CompleteCollectionInteraction(
                        now,
                        "交互超时后对象已消失但没有完成信号，释放实例并等待重新出现",
                        markObjectCompleted: false);
                }
                else
                {
                    this.collectionInteractionStartedAt = DateTime.MinValue;
                    this.collectionCastObserved = false;
                    this.AddDiagnostic(DiagnosticSeverity.Warning, "收集点交互 5 秒无完成信号且对象仍存在，保留对象并重试");
                }
                this.nextActionAt = now.AddMilliseconds(100);
                return;
            }

            this.statusReason = $"等待收集读条完成（{Math.Min(elapsedMilliseconds / 1000f, CollectionInteractionTimeout.TotalSeconds):0.0}s）";
            // Poll quickly while a pickup is active so an incoming hit is handled on the next
            // framework tick instead of waiting behind a coarse interaction timer.
            this.nextActionAt = now.AddMilliseconds(50);
            return;
        }

        IGameObject? collectionObject = this.collectionObjectId is { } remembered
            ? this.objectTable.FirstOrDefault(o => o.IsValid() && o.GameObjectId == remembered && o.IsTargetable)
            : null;
        collectionObject ??= this.targetSelector.FindNearestCollectionObject(
            fate.FateId,
            player.Position,
            fate.Position,
            fate.Radius,
            fate.Objectives.Select(o => o.Position).Where(FateRepository.HasValidPosition).ToArray(),
            this.completedCollectionObjects);
        if (collectionObject is null)
        {
            this.collectionCombatFallback = true;
            this.StopNavigationOperation();
            this.Transition(AutomationState.PullingTargets, "范围内没有收集点，切换到 FATE 小怪常规拉怪流程");
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"收集 FATE #{fate.FateId} 范围内未找到可交互 EventObj，开始常规小怪拉怪");
            return;
        }

        this.collectionObjectId = collectionObject.GameObjectId;
        float distance = Vector3.Distance(player.Position, collectionObject.Position);
        if (distance > 3.5f)
        {
            this.statusReason = $"前往最近收集点「{collectionObject.Name}」（{distance:0.0} yalms）";
            NavigationRequestResult request = this.TryIssueNavigationRequest(
                now, NavigationPurpose.CollectionObjective, collectionObject.Position, fly: false);
            if (request == NavigationRequestResult.Rejected)
            {
                this.BeginNavigationRecovery(now, fate, "旧收集流程导航失败");
                return;
            }
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        this.StopNavigationOperation();
        if (!this.mount.IsDismounted)
        {
            if (this.mount.TryDismount())
                this.AddDiagnostic(DiagnosticSeverity.Debug, "收集点附近请求下坐骑");
            this.nextActionAt = now.AddMilliseconds(150);
            return;
        }

        if (this.condition[ConditionFlag.InCombat])
        {
            this.BeginCombatCleanup(CleanupContinuation.ResumeCollection, "到达收集点但仍在战斗，先清理接战目标");
            return;
        }

        this.collectionInventoryBefore = this.inventoryCounter.Snapshot();
        ActionManager* interactionActionManager = ActionManager.Instance();
        int previousSequence = interactionActionManager == null ? 0 : interactionActionManager->LastUsedActionSequence;
        bool interactionAccepted = this.InteractWithCollectionObject(collectionObject);
        if (interactionAccepted)
        {
            this.collectionInteractionStartedAt = now;
            this.collectionInteractionDiagnosticAt = DateTime.MinValue;
            // The interaction call itself can be made while the client still exposes a stale
            // Casting flag from the previous action.  Marking that flag here makes the next
            // frame look like an interrupted cast even though this request never started.
            // Observe Casting/native CastTime only on frames after the request instead.
            this.collectionCastObserved = false;
            int currentSequence = interactionActionManager == null
                ? previousSequence
                : interactionActionManager->LastUsedActionSequence;
            this.collectionInteractionActionSequence = currentSequence;
            this.collectionInteractionCheckSequence =
                interactionActionManager != null && currentSequence != previousSequence;
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"已交互收集点：object={collectionObject.GameObjectId:X}，name={collectionObject.Name}，" +
                $"BaseId={collectionObject.BaseId}，FateId={FateTargetSelector.GetFateId(collectionObject)}");
        }
        else
        {
            this.statusReason = "收集点交互请求未被客户端接受，准备重试";
            this.nextActionAt = now.AddMilliseconds(500);
        }
    }

    private void CompleteCollectionInteraction(
        DateTime now,
        string reason,
        bool markObjectCompleted = true)
    {
        if (markObjectCompleted && this.collectionObjectId is { } objectId)
            this.completedCollectionObjects.Add(objectId);
        this.collectionObjectId = null;
        this.collectionInteractionStartedAt = DateTime.MinValue;
        this.collectionCastObserved = false;
        this.collectionInteractionActionSequence = 0;
        this.collectionInteractionCheckSequence = false;
        this.collectionInteractionCount++;
        this.collectionInventoryBefore = this.inventoryCounter.Snapshot();
        this.collectionObservedInventory = this.collectionInventoryBefore;
        int count = this.GetCollectionItemCount();
        int threshold = this.ResolveActiveFate() is { } fate
            ? GetCollectionTurnInThreshold(fate)
            : 0;
        this.AddDiagnostic(
            threshold > 0 && count >= threshold ? DiagnosticSeverity.Information : DiagnosticSeverity.Debug,
            $"收集点交互完成：{reason}；当前任务物品={count}/{(threshold == 0 ? "未识别门槛" : threshold)}；" +
            $"已确认交互次数={this.collectionInteractionCount}；ItemId={(this.collectionItemId == 0 ? "未识别" : this.collectionItemId)}");
        this.nextActionAt = now.AddMilliseconds(100);
    }

    private int GetCollectionItemCount()
    {
        uint itemId = this.ResolveActiveFate()?.EventItem ?? this.collectionItemId;
        return itemId == 0 ? 0 : this.inventoryCounter.Count(itemId);
    }

    private void ObserveCollectionInventory()
    {
        if (this.collectionItemId == 0)
        {
            uint? increasedItem = this.inventoryCounter.FindIncreasedItem(
                this.collectionObservedInventory,
                out int increase);
            if (increasedItem is { } itemId)
            {
                this.collectionItemId = itemId;
                this.AddDiagnostic(
                    DiagnosticSeverity.Information,
                    $"观察到任务物品候选：ItemId={itemId}，库存增量={increase}；仅用于总缴纳数量，绝不用于判断采集点交互结果");
            }
        }

        this.collectionObservedInventory = this.inventoryCounter.Snapshot();
    }

    private int GetCollectionTurnInThreshold(FateSnapshot fate)
    {
        if (fate.Progress >= 100)
            return 1;

        // Before the first confirmed hand-in DailyRoutines uses a tiny batch. Afterwards the
        // actual progress gained per submitted item drives the batch size, capped at 20.
        if (this.collectionProgressPerItem <= 0)
            return 2;

        int estimatedRequired = (int)Math.Ceiling((100 - fate.Progress) / this.collectionProgressPerItem);
        return Math.Max(1, Math.Min(estimatedRequired, 20) - 1);
    }

    private bool InteractWithCollectionObject(IGameObject collectionObject)
    {
        if (!collectionObject.IsValid() || !collectionObject.IsTargetable || collectionObject.Address == nint.Zero)
            return false;

        long result = (long)TargetSystem.Instance()->InteractWithObject(
            (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)collectionObject.Address,
            checkLineOfSight: false);
        return result != 7 && result > 0;
    }

    private void BeginCollectionTurnIn(FateSnapshot fate, DateTime now, bool urgent)
    {
        this.collectionTurnInUrgent |= urgent;
        this.collectionTurnInPhase = true;
        this.collectionTurnInStartedAt = DateTime.MinValue;
        this.collectionTurnInObjectId = null;
        this.collectionTurnInInteractionIssued = false;
        this.collectionTurnInDialogueSeen = false;
        this.collectionTurnInItemCountBefore = fate.EventItem == 0 ? 0 : this.inventoryCounter.Count(fate.EventItem);
        this.collectionTurnInProgressBefore = fate.Progress;
        this.StopNavigationOperation();
        this.ClearTarget();
        if (!this.textAdvance.EnableForCollection())
            this.AddDiagnostic(DiagnosticSeverity.Warning, "TextAdvance 不可用或未接受外部控制；交付将使用原生对话界面等待");
        this.Transition(
            AutomationState.TurningInCollectionFate,
            urgent
                ? "收集进度已满，立即前往原生 ObjectiveNpc 交付"
                : $"已达到本轮交付门槛 {GetCollectionTurnInThreshold(fate)} 个任务物品，前往交付 NPC");
        this.nextActionAt = now;
    }

    private void HandleCollectionTurnIn(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate() ?? this.lastActiveFateSnapshot;
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (fate is null || player is null || fate.EventItem == 0)
        {
            this.statusReason = "等待收集 FATE 原生交付数据";
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        // Hand-in remains higher priority than normal combat selection. TextAdvance owns the
        // dialogue/request UI, while this state owns navigation and the one NPC interaction.
        this.ClearTarget();
        IGameObject? turnIn = this.targetSelector.FindCollectionTurnInNpc(fate.ObjectiveNpc);
        if (turnIn is null)
        {
            this.statusReason = "等待 ObjectiveNpc 交付对象出现";
            if (now >= this.collectionLastDiagnosticAt)
            {
                this.collectionLastDiagnosticAt = now.AddSeconds(3);
                this.AddDiagnostic(DiagnosticSeverity.Warning,
                    $"收集交付等待原生 ObjectiveNpc：FateId={fate.FateId}，ObjectiveNpc={fate.ObjectiveNpc:X}");
            }

            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        this.collectionTurnInObjectId = turnIn.GameObjectId;
        float distance = Vector3.Distance(player.Position, turnIn.Position);
        if (distance > 4f)
        {
            this.statusReason = $"前往原生交付 NPC「{turnIn.Name}」（{distance:0.0} yalms）";
            NavigationRequestResult request = this.TryIssueNavigationRequest(
                now, NavigationPurpose.CollectionTurnIn, turnIn.Position, fly: false);
            if (request == NavigationRequestResult.Rejected)
            {
                this.BeginNavigationRecovery(now, fate, "收集交付 NPC 导航失败");
                return;
            }
            this.nextActionAt = now.AddMilliseconds(150);
            return;
        }

        this.StopNavigationOperation();
        if (!this.mount.IsDismounted)
        {
            this.mount.TryDismount();
            this.nextActionAt = now.AddMilliseconds(150);
            return;
        }

        if (!this.collectionTurnInInteractionIssued)
        {
            if (this.HasCollectionDialogueVisible())
            {
                this.collectionTurnInDialogueSeen = true;
                this.statusReason = this.textAdvance.IsActive
                    ? "TextAdvance 正在处理收集交付对话"
                    : "等待收集交付对话结束";
                this.nextActionAt = now.AddMilliseconds(150);
                return;
            }

            if (this.InteractWithCollectionObject(turnIn))
            {
                this.collectionTurnInInteractionIssued = true;
                this.collectionTurnInStartedAt = now;
                this.collectionTurnInItemCountBefore = this.inventoryCounter.Count(fate.EventItem);
                this.collectionTurnInProgressBefore = fate.Progress;
                this.AddDiagnostic(DiagnosticSeverity.Information,
                    $"已交互原生收集交付 NPC：object={turnIn.GameObjectId:X}，FateId={fate.FateId}，" +
                    $"EventItem={fate.EventItem}，TextAdvanceActive={this.textAdvance.IsActive}");
            }

            this.nextActionAt = now.AddMilliseconds(150);
            return;
        }

        if (fate.Progress > this.collectionTurnInProgressBefore)
        {
            int submitted = Math.Max(1, this.collectionTurnInItemCountBefore);
            this.collectionProgressPerItem =
                (fate.Progress - this.collectionTurnInProgressBefore) / (float)submitted;
            this.CompleteCollectionTurnInCycle(
                now,
                fate,
                $"FATE 进度从 {this.collectionTurnInProgressBefore}% 增加到 {fate.Progress}%（学习单物品贡献 {this.collectionProgressPerItem:0.##}%）");
            return;
        }

        int currentItemCount = this.inventoryCounter.Count(fate.EventItem);
        if (this.collectionTurnInProgressBefore >= 100
            && currentItemCount < this.collectionTurnInItemCountBefore)
        {
            this.CompleteCollectionTurnInCycle(now, fate, "满进度阶段检测到 EventItem 数量下降");
            return;
        }

        if (now - this.collectionTurnInStartedAt >= CollectionTurnInConfirmationTimeout
            && !this.HasCollectionDialogueVisible())
        {
            // No inferred success from a disappearing dialogue. Clear the pending request and
            // safely re-open the exact ObjectiveNpc instead.
            this.collectionTurnInInteractionIssued = false;
            this.collectionTurnInStartedAt = DateTime.MinValue;
            this.statusReason = "交付未观察到进度变化，重新请求原生交付 NPC";
            this.nextActionAt = now.AddMilliseconds(200);
            return;
        }

        this.statusReason = this.textAdvance.IsActive
            ? "TextAdvance 正在推进收集交付/填充物品"
            : "等待收集交付确认";
        this.nextActionAt = now.AddMilliseconds(150);
    }

    [Obsolete("Replaced by the native ObjectiveNpc/EventItem hand-in flow.")]
    private void HandleLegacyCollectionTurnIn(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate() ?? this.lastActiveFateSnapshot;
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (fate is null || player is null)
        {
            this.statusReason = "等待收集 FATE 交付对象与角色数据";
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        if (!this.collectionSprintIssued && this.collectionTurnInUrgent)
        {
            ActionManager* manager = ActionManager.Instance();
            if (manager != null && manager->GetActionStatus(ActionType.GeneralAction, 4) == 0)
                this.collectionSprintIssued = manager->UseAction(ActionType.GeneralAction, 4);
        }

        // Delivery is never handed back to the combat state machine. Keep the normal target
        // cleared and continue navigating/interacting even when a monster is attacking us.
        this.ClearTarget();

        IGameObject? turnIn = this.collectionTurnInObjectId is { } remembered
            ? this.objectTable.FirstOrDefault(o => o.IsValid() && o.GameObjectId == remembered && o.IsTargetable)
            : null;
        turnIn ??= this.targetSelector.FindCollectionTurnInNpc(
            fate.FateId,
            fate.Position,
            fate.Radius,
            fate.ObjectiveNpc,
            fate.MotivationNpc,
            fate.Objectives.Select(o => o.Position).Where(FateRepository.HasValidPosition).ToArray(),
            fate.MapMarkerPositions);
        if (turnIn is null)
        {
            this.statusReason = "等待识别收集 FATE 交付 NPC";
            if (now >= this.collectionLastDiagnosticAt)
            {
                this.collectionLastDiagnosticAt = now.AddSeconds(3);
                this.AddDiagnostic(
                    DiagnosticSeverity.Warning,
                    $"未找到收集 FATE #{fate.FateId} 交付 NPC：ObjectiveNpc={fate.ObjectiveNpc:X}，MotivationNpc={fate.MotivationNpc:X}");
            }
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        this.collectionTurnInObjectId = turnIn.GameObjectId;
        float distance = Vector3.Distance(player.Position, turnIn.Position);
        if (distance > 4f)
        {
            this.statusReason = $"前往交付 NPC「{turnIn.Name}」（{distance:0.0} yalms）";
            NavigationRequestResult request = this.TryIssueNavigationRequest(
                now, NavigationPurpose.CollectionTurnIn, turnIn.Position, fly: false);
            if (request == NavigationRequestResult.Rejected)
            {
                this.BeginNavigationRecovery(now, fate, "旧收集交付 NPC 导航失败");
                return;
            }
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        this.StopNavigationOperation();
        if (!this.mount.IsDismounted)
        {
            this.mount.TryDismount();
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        if (this.HasCollectionDialogueVisible())
        {
            this.collectionTurnInDialogueSeen = true;
            if (!this.textAdvance.IsActive)
                this.TryAdvanceCollectionDialogueUi(now);
            this.statusReason = this.textAdvance.IsActive
                ? "TextAdvance 正在推进交付对话/填充物品"
                : "等待交付对话界面完成";
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        if (!this.collectionTurnInInteractionIssued)
        {
            if (this.InteractWithCollectionObject(turnIn))
            {
                this.collectionTurnInInteractionIssued = true;
                this.AddDiagnostic(
                    DiagnosticSeverity.Information,
                    $"已交互收集 FATE 交付 NPC：object={turnIn.GameObjectId:X}，name={turnIn.Name}，" +
                    $"FateId={FateTargetSelector.GetFateId(turnIn)}，TextAdvanceActive={this.textAdvance.IsActive}");
            }
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }

        if (this.collectionItemId != 0
            && this.inventoryCounter.Count(this.collectionItemId) < this.collectionTurnInItemCountBefore)
        {
            this.CompleteCollectionTurnInCycle(now, fate, "检测到任务物品数量下降");
            return;
        }

        if (fate.Progress > this.collectionTurnInProgressBefore)
        {
            this.CompleteCollectionTurnInCycle(now, fate, $"FATE 进度从 {this.collectionTurnInProgressBefore}% 增加到 {fate.Progress}%");
            return;
        }

        if (this.collectionTurnInDialogueSeen
            && !this.HasCollectionDialogueVisible()
            && now - this.collectionTurnInStartedAt >= TimeSpan.FromSeconds(1))
        {
            this.CompleteCollectionTurnInCycle(now, fate, "交付对话已关闭");
            return;
        }

        if (this.collectionTurnInStartedAt != DateTime.MinValue
            && now - this.collectionTurnInStartedAt > TimeSpan.FromSeconds(35))
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                "收集 FATE 交付交互已等待 35 秒；保留当前状态继续等待 FATE 结算，避免重复打开对话");
            this.collectionTurnInStartedAt = now;
        }

        this.statusReason = this.collectionTurnInUrgent
            ? $"等待紧急交付完成（剩余 {fate.TimeRemaining}s）"
            : "等待收集任务物品交付完成";
        this.nextActionAt = now.AddMilliseconds(500);
    }

    private void CompleteCollectionTurnInCycle(DateTime now, FateSnapshot fate, string reason)
    {
        this.AddDiagnostic(
            DiagnosticSeverity.Information,
            $"收集交付完成：{reason}；当前进度={fate.Progress}%；下一轮门槛={GetCollectionTurnInThreshold(fate)}");

        // The final hand-in can itself push the bar to 100%. Once our EventItem count is gone,
        // release the turn-in latch immediately so the normal one-second completion confirmation
        // begins; do not sit in the game's one-minute courtesy window.
        if (fate.Progress >= 100)
        {
            this.collectionTurnInUrgent = false;
            this.collectionTurnInPhase = false;
            this.collectionTurnInInteractionIssued = false;
            this.collectionTurnInDialogueSeen = false;
            this.collectionTurnInObjectId = null;
            this.collectionTurnInStartedAt = DateTime.MinValue;
            this.collectionTurnInProgressBefore = fate.Progress;
            this.statusReason = "收集物品已交付，开始确认 FATE 完成";
            this.nextActionAt = now;
            return;
        }

        this.collectionTurnInPhase = false;
        this.collectionTurnInInteractionIssued = false;
        this.collectionTurnInDialogueSeen = false;
        this.collectionTurnInObjectId = null;
        this.collectionTurnInStartedAt = DateTime.MinValue;
        this.collectionTurnInProgressBefore = fate.Progress;
        this.collectionInteractionCount = 0;
        this.completedCollectionObjects.Clear();
        this.collectionObjectId = null;
        this.collectionInteractionStartedAt = DateTime.MinValue;
        this.collectionCastObserved = false;
        this.collectionInventoryBefore = this.inventoryCounter.Snapshot();
        this.collectionObservedInventory = this.collectionInventoryBefore;
        this.collectionCombatFallback = false;
        this.Transition(
            AutomationState.CollectingFateItems,
            $"交付完成，FATE 进度 {fate.Progress}% 未满，继续收集下一轮（门槛 {GetCollectionTurnInThreshold(fate)}）");
        this.nextActionAt = now.AddMilliseconds(400);
    }

    private bool HasCollectionDialogueVisible()
    {
        AtkUnitBase* talk = this.GetAddon("Talk");
        if (talk != null && talk->IsVisible)
            return true;

        AtkUnitBase* yesno = this.GetAddon("SelectYesno");
        return yesno != null && yesno->IsVisible;
    }

    private void TryAdvanceCollectionDialogueUi(DateTime now)
    {
        if (now - this.collectionDialogueLastActionAt < TimeSpan.FromMilliseconds(500))
            return;

        AtkUnitBase* yesno = this.GetAddon("SelectYesno");
        if (yesno != null && yesno->IsVisible && yesno->IsReady)
        {
            yesno->FireCallbackInt(0);
            this.collectionDialogueLastActionAt = now;
            this.AddDiagnostic(DiagnosticSeverity.Debug, "TextAdvance 不可用，原生推进收集交付确认");
            return;
        }

        AtkUnitBase* talk = this.GetAddon("Talk");
        if (talk != null && talk->IsVisible && talk->IsReady)
        {
            talk->FireCallbackInt(0);
            this.collectionDialogueLastActionAt = now;
            this.AddDiagnostic(DiagnosticSeverity.Debug, "TextAdvance 不可用，原生推进收集交付对话");
        }
    }

    private void HandleCombat(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (fate is null || player is null)
        {
            this.AbandonActiveFate("战斗中 FATE 或玩家对象失效");
            return;
        }

        if (this.TryBeginPeriodicCompanionCheckpoint(now, this.state))
            return;

        FateCombatProfile profile = fate.CombatProfile;
        if (profile.Strategy == FateCombatStrategyKind.Collection
            && this.collectionCombatFallback
            && !this.condition[ConditionFlag.InCombat]
            && this.targetSelector.FindNearestCollectionObject(
                fate.FateId,
                player.Position,
                fate.Position,
                fate.Radius,
                fate.Objectives.Select(o => o.Position).Where(FateRepository.HasValidPosition).ToArray(),
                this.completedCollectionObjects) is not null)
        {
            this.collectionCombatFallback = false;
            this.Transition(AutomationState.CollectingFateItems, "收集点重新出现，结束普通小怪回退并恢复收集");
            return;
        }

        if (profile.Strategy == FateCombatStrategyKind.Collection && !this.collectionCombatFallback)
        {
            this.HandleCollectionItems(now);
            return;
        }
        // A collection FATE with no visible EventObj deliberately falls back to the ordinary
        // combat pull loop.  It is still a supported collection strategy; do not let the
        // unsupported-strategy guard below abandon it on the next combat tick.
        bool collectionFallbackCombat = profile.Strategy == FateCombatStrategyKind.Collection
            && this.collectionCombatFallback;
        if (profile.Strategy != FateCombatStrategyKind.General && !collectionFallbackCombat)
        {
            this.skippedFates[fate.FateId] = now.AddSeconds(60);
            this.AbandonActiveFate($"FATE #{fate.FateId} {profile.SupportDescription}，已跳过");
            return;
        }

        IReadOnlyList<IBattleNpc> related = this.targetSelector.FindAllFateObjects(fate.FateId, player.Position);
        IBattleNpc[] protectedTargets = profile.ProtectionMode == FateProtectionMode.None
            ? []
            : related
                .Where(FateTargetSelector.IsFriendly)
                .OrderBy(candidate => candidate.CurrentHp)
                .ThenBy(candidate => Vector3.DistanceSquared(player.Position, candidate.Position))
                .ToArray();
        Vector3? safetyAnchor = profile.ProtectionMode == FateProtectionMode.Moving
            ? protectedTargets.FirstOrDefault()?.Position
            : null;
        if (safetyAnchor is { } movingAnchor)
            this.escortFollowLastPosition = movingAnchor;
        if (this.ReturnToFateIfOutside(fate, player, now, safetyAnchor))
            return;

        if (this.EnsureLevelSyncBeforeCombat(now, fate, "通用战斗选目标前"))
            return;

        IBattleNpc[] nearbyEnemies = related
            .Where(FateTargetSelector.IsAttackableEnemy)
            .Where(enemy => this.IsInsideFateCombatRange(enemy, fate))
            .ToArray();
        if (this.TryHandleLostPriorityTarget(now, fate, player, related, nearbyEnemies))
            return;

        if (this.TryHandleDestroyObjectiveCombat(now, fate, player))
            return;
        if (profile.Strategy == FateCombatStrategyKind.Boss)
        {
            this.Transition(AutomationState.BossFighting, "切换到独立的讨伐BOSS处理器");
            this.HandleBossCombat(now);
            return;
        }

        this.HandleGeneralCombat(now, fate, profile, player, related, protectedTargets);
    }

    private bool TryHandleDestroyObjectiveCombat(DateTime now, FateSnapshot fate, IPlayerCharacter player)
    {
        if (!this.staticFateCatalog.TryGet(fate.FateId, out StaticFateCatalogEntry entry)
            || entry.Targets.Destroy.Count == 0)
            return false;

        IBattleNpc[] objectives = this.targetSelector.FindNamedPriorityTargets(
            fate.FateId, player.Position, entry.Targets.Destroy).ToArray();
        // Temporary targetability/classification changes must not replace a living lock.
        if (this.destroySession.TargetId is { } objectiveId
            && this.targetSelector.FindLiveTarget(objectiveId) is { } lockedObjective
            && FateTargetSelector.BelongsToFate(lockedObjective, fate.FateId)
            && !FateTargetSelector.IsFriendly(lockedObjective))
            objectives = objectives.Append(lockedObjective).DistinctBy(t => t.GameObjectId).ToArray();
        IBattleNpc[] engaged = this.targetSelector.FindEngagedTargets(player)
            .Concat(this.targetSelector.FindAllFateObjects(fate.FateId, player.Position)
                .Where(t => this.pullBatchTargets.Contains(t.GameObjectId)
                    && this.targetSelector.IsAttackableCleanupTarget(t)))
            .DistinctBy(t => t.GameObjectId).ToArray();
        if (this.destroySession.ClearingAggro && this.destroySession.CleanupTargetId is { } cleanupId
            && this.targetSelector.FindLiveTarget(cleanupId) is { } lockedCleanup
            && this.targetSelector.IsAttackableCleanupTarget(lockedCleanup))
            engaged = engaged.Append(lockedCleanup).DistinctBy(t => t.GameObjectId).ToArray();
        this.aggroCount = engaged.Length;
        DestroyDecision decision = this.destroySession.Choose(
            objectives.Select(t => ToCombatCandidate(t, player)).ToArray(),
            engaged.Select(t => ToCombatCandidate(t, player)).ToArray());
        if (decision.Action == DestroyAction.Fallback)
        {
            if (this.priorityDestroyTargetId is not null)
            {
                this.StopNavigationOperation();
                this.ClearTarget();
            }
            this.priorityDestroyTargetId = null;
            return false;
        }
        if (this.EnsureLevelSyncBeforeCombat(now, fate, "破坏目标处理器"))
            return true;

        IBattleNpc target = (decision.Action == DestroyAction.Destroy ? objectives : engaged)
            .First(t => t.GameObjectId == decision.TargetId);
        if (this.priorityDestroyTargetId != target.GameObjectId)
        {
            this.StopNavigationOperation();
            this.priorityDestroyTargetId = target.GameObjectId;
            this.pullTargetId = null;
            this.killTargetId = null;
            this.AddDiagnostic(DiagnosticSeverity.Information,
                decision.Action == DestroyAction.Destroy
                    ? $"破坏目标锁定 → {target.Name} ({target.GameObjectId:X})，死亡或消失前不切换"
                    : $"破坏后清场锁定 → {target.Name} ({target.GameObjectId:X})，HP={target.CurrentHp}，接战剩余={engaged.Length}；持续清至名单为空");
        }
        if (!this.TrySelectCombatTarget(target, now))
            return true;
        if (Vector3.Distance(player.Position, target.Position) > 14f)
            this.RequestPullMovement(target.Position, now);
        else
            this.StopNavigationOperation();
        this.statusReason = decision.Action == DestroyAction.Destroy
            ? $"破坏目标：专注攻击 {target.Name}，HP {target.CurrentHp}/{target.MaxHp}"
            : $"破坏后清场：锁定 {target.Name}，接战剩余 {engaged.Length}，清空后恢复破坏目标";
        this.nextActionAt = now.AddMilliseconds(250);
        return true;
    }

    private static CombatCandidate ToCombatCandidate(IBattleNpc target, IPlayerCharacter player) =>
        new(target.GameObjectId, target.CurrentHp, Vector3.DistanceSquared(player.Position, target.Position));

    private void HandleGeneralCombat(
        DateTime now,
        FateSnapshot fate,
        FateCombatProfile profile,
        IPlayerCharacter player,
        IReadOnlyList<IBattleNpc> related,
        IReadOnlyList<IBattleNpc> protectedTargets)
    {
        IBattleNpc[] allEnemies = related.Where(FateTargetSelector.IsAttackableEnemy).ToArray();
        IBattleNpc[] enemies = allEnemies
            .Where(enemy => this.IsInsideFateCombatRange(enemy, fate))
            .ToArray();
        this.LogOutOfRangeFateTargets(now, fate, allEnemies, enemies);
        HashSet<ulong> liveEnemyIds = enemies.Select(enemy => enemy.GameObjectId).ToHashSet();
        this.pullBatchTargets.RemoveWhere(id => !liveEnemyIds.Contains(id));
        HashSet<ulong> protectedIds = protectedTargets.Select(target => target.GameObjectId).ToHashSet();

        if (this.TryHandleLostPriorityTarget(now, fate, player, related, enemies))
            return;

        if (this.targetSelectionFailedAt != DateTime.MinValue
            && this.selectingTargetId is { } selecting
            && (this.pullTargetId == selecting || this.killTargetId == selecting)
            && this.targetSelector.FindLiveTarget(selecting) is { } pendingSelection
            && !this.TrySelectCombatTarget(pendingSelection, now))
            return;

        if (this.pullTargetId is { } pullingId)
        {
            IBattleNpc? pulling = enemies.FirstOrDefault(enemy => enemy.GameObjectId == pullingId);
            if (pulling is null)
            {
                this.pullTargetId = null;
                this.ResetPullApproach();
            }
            else
            {
                bool confirmed = this.pullTargetKind == PullTargetKind.ProtectionThreat
                    ? pulling.TargetObjectId != 0 && !protectedIds.Contains(pulling.TargetObjectId)
                    : FateTargetSelector.IsTargetingPlayer(pulling, player);
                if (confirmed)
                {
                    this.pullBatchTargets.Add(pulling.GameObjectId);
                    this.AddDiagnostic(
                        DiagnosticSeverity.Information,
                        this.pullTargetKind == PullTargetKind.ProtectionThreat
                            ? $"保护威胁 {pulling.Name} 的仇恨已离开友方 NPC（Target={pulling.TargetObjectId:X}）"
                            : $"已确认目标 {pulling.Name} 的仇恨");
                    this.pullTargetId = null;
                    this.ResetPullApproach();
                    this.StopNavigationOperation();
                }
                else if (!this.condition[ConditionFlag.Casting]
                         && !this.condition[ConditionFlag.Casting87]
                         && now >= this.pullCastProtectionUntil
                         && this.IsPullAttemptTimedOut(now))
                {
                    this.skippedTargets[pulling.GameObjectId] = now.AddSeconds(this.configuration.SkippedTargetCooldownSeconds);
                    this.AddDiagnostic(DiagnosticSeverity.Warning, $"目标 {pulling.Name} 接战确认超时，暂时跳过");
                    this.pullTargetId = null;
                    this.ResetPullApproach();
                    this.StopNavigationOperation();
                }
                else
                {
                    this.ApproachAndSelectTarget(pulling, player, now);
                    this.statusReason = this.pullTargetKind == PullTargetKind.ProtectionThreat
                        ? $"正在转移 {pulling.Name} 对友方 NPC 的仇恨"
                        : this.statusReason;
                    return;
                }
            }
        }

        IBattleNpc[] activeCombatTargets = enemies
            .Where(target => FateTargetSelector.IsTargetingPlayer(target, player)
                             || this.pullBatchTargets.Contains(target.GameObjectId))
            .DistinctBy(target => target.GameObjectId)
            .ToArray();
        IBattleNpc[] nonFateAggroTargets = this.targetSelector
            .FindTargetsAggroedOnPlayer(player)
            .Where(target => !FateTargetSelector.BelongsToFate(target, fate.FateId))
            .ToArray();
        activeCombatTargets = activeCombatTargets
            .Concat(nonFateAggroTargets)
            .DistinctBy(target => target.GameObjectId)
            .ToArray();
        this.aggroCount = activeCombatTargets.Length;

        int effectiveAggroLimit = this.GetEffectiveAggroLimit(fate);

        if (this.state == AutomationState.Fighting)
        {
            this.HandleSealedGeneralBatch(now, fate, player, activeCombatTargets, enemies, protectedTargets, nonFateAggroTargets.Length);
            return;
        }

        if (activeCombatTargets.Length >= effectiveAggroLimit)
        {
            this.pullBatchEmptySince = DateTime.MinValue;
            this.Transition(
                AutomationState.Fighting,
                this.CanRefillAtHalf(fate)
                    ? $"本批已达上限 {effectiveAggroLimit}，剩余不超过一半时补充"
                    : $"本批已达上限 {effectiveAggroLimit}，清空后再拉",
                logTransition: false);
            this.SelectLockedKillTarget(player, activeCombatTargets);
            return;
        }

        bool IsAvailable(IBattleNpc enemy) =>
            !activeCombatTargets.Any(active => active.GameObjectId == enemy.GameObjectId)
            && (!this.skippedTargets.TryGetValue(enemy.GameObjectId, out DateTime until) || until <= now);

        IBattleNpc? next = null;
        PullTargetKind nextKind = PullTargetKind.Normal;
        foreach (IBattleNpc protectedTarget in protectedTargets)
        {
            next = enemies
                .Where(IsAvailable)
                .Where(enemy => enemy.TargetObjectId == protectedTarget.GameObjectId)
                .OrderBy(enemy => enemy.CurrentHp)
                .ThenBy(enemy => Vector3.DistanceSquared(protectedTarget.Position, enemy.Position))
                .FirstOrDefault();
            if (next is null)
                continue;

            nextKind = PullTargetKind.ProtectionThreat;
            break;
        }

        // No enemy currently threatens a protected NPC: use exactly the ordinary enemy rule.
        next ??= enemies
            .Where(IsAvailable)
            .OrderBy(enemy => Vector3.DistanceSquared(player.Position, enemy.Position))
            .FirstOrDefault();
        if (next is not null)
        {
            this.BeginPullTarget(next.GameObjectId, now, nextKind);
            string source = nextKind == PullTargetKind.ProtectionThreat ? "保护威胁" : "普通目标";
            this.Transition(AutomationState.PullingTargets, $"{source} → {next.Name} ({next.GameObjectId:X})", logTransition: false);
            this.ApproachAndSelectTarget(next, player, now);
            return;
        }

        if (activeCombatTargets.Length > 0)
        {
            this.Transition(AutomationState.Fighting, "当前没有新的可拉取目标，先清理已接战目标", logTransition: false);
            this.SelectLockedKillTarget(player, activeCombatTargets);
            return;
        }

        this.killTargetId = null;
        if (profile.ProtectionMode == FateProtectionMode.Moving && enemies.Length == 0 && protectedTargets.Count > 0)
        {
            this.FollowEscortTarget(now, player, protectedTargets[0]);
            return;
        }

        this.StopNavigationOperation();
        this.statusReason = enemies.Length == 0
            ? "FATE 范围内暂时没有可攻击目标"
            : "等待暂时跳过的目标恢复";
        this.nextActionAt = now.AddMilliseconds(500);
    }

    private bool TryHandleLostPriorityTarget(
        DateTime now,
        FateSnapshot fate,
        IPlayerCharacter player,
        IReadOnlyList<IBattleNpc> related,
        IReadOnlyList<IBattleNpc> nearbyEnemies)
    {
        if (!this.configuration.PrioritizeLostGirlAndLostOne)
        {
            if (this.lostPriorityTargetId is { } disabledId
                && this.targetManager.Target?.GameObjectId == disabledId)
            {
                this.ClearTarget();
            }

            this.lostPriorityTargetId = null;
            this.lostPriorityTargetKind = null;
            this.lostPriorityTargetMissingSince = DateTime.MinValue;
            return false;
        }

        IBattleNpc? target = this.lostPriorityTargetId is { } lockedId
            ? related.FirstOrDefault(candidate => candidate.GameObjectId == lockedId
                && candidate.IsValid()
                && !candidate.IsDead
                && FateTargetSelector.BelongsToFate(candidate, fate.FateId))
            : null;
        if (this.lostPriorityTargetId is not null && target is null)
        {
            if (this.lostPriorityTargetMissingSince == DateTime.MinValue)
                this.lostPriorityTargetMissingSince = now;
            if (now - this.lostPriorityTargetMissingSince < TimeSpan.FromSeconds(1))
            {
                this.statusReason = "等待优先击杀目标状态更新，暂不恢复普通选怪";
                this.nextActionAt = now.AddMilliseconds(250);
                return true;
            }

            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"优先击杀目标已消失，视为已击杀：{this.lostPriorityTargetId.Value:X}");
            if (this.targetManager.Target?.GameObjectId == this.lostPriorityTargetId)
                this.ClearTarget();
            this.lostPriorityTargetId = null;
            this.lostPriorityTargetKind = null;
            this.lostPriorityTargetMissingSince = DateTime.MinValue;
            this.killTargetId = null;
            this.pullTargetId = null;
            this.ResetPullApproach();
            return false;
        }

        if (target is null)
        {
            target = nearbyEnemies
                .Select(candidate => (Target: candidate, Kind: GetLostPriorityTargetKind(candidate)))
                .Where(candidate => candidate.Kind is { } kind
                    && fate.TimeRemaining >= this.GetLostPriorityTargetThresholdSeconds(kind))
                .OrderBy(candidate => Vector3.DistanceSquared(player.Position, candidate.Target.Position))
                .Select(candidate => candidate.Target)
                .FirstOrDefault();
            if (target is null)
                return false;

            this.lostPriorityTargetId = target.GameObjectId;
            this.lostPriorityTargetKind = GetLostPriorityTargetKind(target);
            this.lostPriorityTargetMissingSince = DateTime.MinValue;
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"优先击杀目标锁定 → {target.Name} ({target.GameObjectId:X})，FATE 剩余 {fate.TimeRemaining}s");
        }

        this.lostPriorityTargetMissingSince = DateTime.MinValue;

        LostPriorityTargetKind kind = this.lostPriorityTargetKind
            ?? GetLostPriorityTargetKind(target)
            ?? LostPriorityTargetKind.LostOne;
        bool alreadyPulling = this.pullTargetId == target.GameObjectId;
        bool engaged = FateTargetSelector.IsTargetingPlayer(target, player)
            || this.pullBatchTargets.Contains(target.GameObjectId);
        if (!engaged)
        {
            if (!alreadyPulling)
            {
                this.pullTargetId = null;
                this.ResetPullApproach();
                this.BeginPullTarget(target.GameObjectId, now);
            }

            this.killTargetId = null;
            this.ApproachAndSelectTarget(target, player, now);
            this.statusReason = $"优先接战{GetLostPriorityTargetLabel(kind)}：{target.Name}，正在接近目标";
            return true;
        }

        if (this.pullTargetId is not null && this.pullTargetId != target.GameObjectId)
        {
            this.pullTargetId = null;
            this.ResetPullApproach();
        }

        this.StopNavigationOperation();
        this.pullTargetId = null;
        this.ResetPullApproach();
        this.killTargetId = target.GameObjectId;
        if (!this.TrySelectCombatTarget(target, now))
            return true;

        this.statusReason = $"优先击杀{GetLostPriorityTargetLabel(kind)}：{target.Name}（锁定至击杀）";
        this.nextActionAt = now.AddMilliseconds(250);
        return true;
    }

    private int GetLostPriorityTargetThresholdSeconds(LostPriorityTargetKind kind) => kind switch
    {
        LostPriorityTargetKind.LostGirl => this.configuration.LostGirlRemainingTimeThresholdSeconds,
        LostPriorityTargetKind.LostOne => this.configuration.LostOneRemainingTimeThresholdSeconds,
        _ => 0,
    };

    private static string GetLostPriorityTargetLabel(LostPriorityTargetKind kind) => kind switch
    {
        LostPriorityTargetKind.LostGirl => "迷失少女",
        LostPriorityTargetKind.LostOne => "迷失者",
        _ => "迷失目标",
    };

    private static LostPriorityTargetKind? GetLostPriorityTargetKind(IBattleNpc target)
    {
        // These BNpc identities are stable across client languages. Only fall back to the
        // localized object name when the client did not expose either ID.
        if (target.BaseId == 7586 || target.NameId == 6737)
            return LostPriorityTargetKind.LostGirl;

        if (target.NameId == 6738)
            return LostPriorityTargetKind.LostOne;

        if (target.BaseId != 0 || target.NameId != 0)
            return null;

        string name = string.Concat(target.Name.ToString().Normalize(System.Text.NormalizationForm.FormKC)
            .Where(character => !char.IsWhiteSpace(character)));
        if (name.Equals("迷失少女", StringComparison.Ordinal)
            || name.StartsWith("迷失少女", StringComparison.Ordinal))
        {
            return LostPriorityTargetKind.LostGirl;
        }

        if (name.Equals("迷失者", StringComparison.Ordinal)
            || name.StartsWith("迷失者", StringComparison.Ordinal))
        {
            return LostPriorityTargetKind.LostOne;
        }

        return null;
    }

    private void HandleSealedGeneralBatch(
        DateTime now,
        FateSnapshot fate,
        IPlayerCharacter player,
        IReadOnlyList<IBattleNpc> activeCombatTargets,
        IReadOnlyList<IBattleNpc> enemies,
        IReadOnlyList<IBattleNpc> protectedTargets,
        int nonFateAggroCount)
    {
        this.StopNavigationOperation();
        this.pullTargetId = null;
        this.ResetPullApproach();
        if (activeCombatTargets.Count > 0)
        {
            this.pullBatchEmptySince = DateTime.MinValue;
            int effectiveAggroLimit = this.GetEffectiveAggroLimit(fate);
            bool refill = this.CanRefillAtHalf(fate)
                && activeCombatTargets.Count <= effectiveAggroLimit / 2;
            if (refill)
            {
                HashSet<ulong> activeIds = activeCombatTargets
                    .Select(target => target.GameObjectId)
                    .ToHashSet();
                bool IsAvailable(IBattleNpc enemy) =>
                    !activeIds.Contains(enemy.GameObjectId)
                    && (!this.skippedTargets.TryGetValue(enemy.GameObjectId, out DateTime until) || until <= now);

                IBattleNpc? refillTarget = null;
                foreach (IBattleNpc protectedTarget in protectedTargets)
                {
                    refillTarget = enemies
                        .Where(IsAvailable)
                        .Where(enemy => enemy.TargetObjectId == protectedTarget.GameObjectId)
                        .OrderBy(enemy => enemy.CurrentHp)
                        .ThenBy(enemy => Vector3.DistanceSquared(protectedTarget.Position, enemy.Position))
                        .FirstOrDefault();
                    if (refillTarget is not null)
                        break;
                }

                refillTarget ??= enemies
                    .Where(IsAvailable)
                    .OrderBy(enemy => Vector3.DistanceSquared(player.Position, enemy.Position))
                    .FirstOrDefault();

                // Reaching N/2 is only a permission to refill. If the object table contains no
                // eligible candidate, remain in Fighting instead of bouncing Fighting → Pulling
                // → Fighting every framework tick.
                if (refillTarget is null)
                {
                    this.SelectLockedKillTarget(player, activeCombatTargets);
                    this.statusReason = $"接战目标剩余 {activeCombatTargets.Count} 只（非FATE {nonFateAggroCount}），但当前没有可补充目标，继续战斗";
                    this.nextActionAt = now.AddMilliseconds(250);
                    return;
                }

                this.ResetPullBatch();
                foreach (IBattleNpc target in activeCombatTargets)
                    this.pullBatchTargets.Add(target.GameObjectId);
                this.killTargetId = null;
                this.Transition(
                    AutomationState.PullingTargets,
                    $"接战目标剩余 {activeCombatTargets.Count} 只（非FATE {nonFateAggroCount}），不超过当前上限 {effectiveAggroLimit} 的一半，按当前 FATE 选怪规则补充",
                    logTransition: false);
                return;
            }

            this.SelectLockedKillTarget(player, activeCombatTargets);
            this.statusReason = this.CanRefillAtHalf(fate)
                ? $"本批战斗中：剩余 {activeCombatTargets.Count} 只（非FATE {nonFateAggroCount}），降至 {this.GetEffectiveAggroLimit(fate) / 2} 只后补充"
                : fate.Progress >= 80
                    ? $"FATE 进度 {fate.Progress}%：本批清空后再拉取，暂不补充（当前非FATE {nonFateAggroCount} 只）"
                    : $"本批战斗中：剩余 {activeCombatTargets.Count} 只（非FATE {nonFateAggroCount}），清空前不补充";
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        this.killTargetId = null;
        if (this.pullBatchEmptySince == DateTime.MinValue)
        {
            this.pullBatchEmptySince = now;
            this.ClearTarget();
            this.statusReason = "本批目标已消失，确认批次清空";
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        if (now - this.pullBatchEmptySince < TimeSpan.FromMilliseconds(750))
        {
            this.statusReason = "本批目标已消失，确认批次清空";
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        this.ResetPullBatch();
        this.Transition(AutomationState.PullingTargets, "上一批已清空，按当前 FATE 选怪规则组建下一批");
    }

    private int GetEffectiveAggroLimit(FateSnapshot fate) => fate.Progress >= 90
        ? 1
        : this.configuration.MaxAggroCount;

    private bool CanRefillAtHalf(FateSnapshot fate) =>
        this.configuration.PullRefillPolicy == PullRefillPolicy.RefillAtHalf
        && fate.Progress < 80;

    private void SelectLockedKillTarget(IPlayerCharacter player, IReadOnlyList<IBattleNpc> activeTargets)
    {
        IBattleNpc? target = this.killTargetId is { } locked
            ? activeTargets.FirstOrDefault(candidate => candidate.GameObjectId == locked)
            : null;
        target ??= activeTargets
            .OrderBy(candidate => candidate.CurrentHp)
            .ThenBy(candidate => Vector3.DistanceSquared(player.Position, candidate.Position))
            .FirstOrDefault();
        if (target is null)
            return;

        if (this.killTargetId != target.GameObjectId)
            this.AddDiagnostic(DiagnosticSeverity.Information, $"击杀目标锁定 → {target.Name} ({target.GameObjectId:X})");
        this.killTargetId = target.GameObjectId;
        this.TrySelectCombatTarget(target, DateTime.UtcNow);
    }

    private void FollowEscortTarget(DateTime now, IPlayerCharacter player, IBattleNpc escortTarget)
    {
        const float startDistance = 10f;
        const float stopDistance = 5f;
        float distance = Vector3.Distance(player.Position, escortTarget.Position);
        if (distance <= stopDistance)
        {
            this.StopNavigationOperation();
            this.escortFollowLastPosition = escortTarget.Position;
            this.statusReason = $"护送模式：跟随 {escortTarget.Name}，当前距离 {distance:0.0}";
            return;
        }

        if (distance < startDistance && !this.vnavmesh.IsMoveActive)
        {
            this.statusReason = $"护送模式：保持在 {escortTarget.Name} 附近，当前距离 {distance:0.0}";
            return;
        }

        bool targetMoved = this.escortFollowLastPosition is null
            || Vector3.DistanceSquared(this.escortFollowLastPosition.Value, escortTarget.Position) >= 3f * 3f;
        if (this.vnavmesh.IsMoveActive
            && (!targetMoved || now - this.escortFollowLastRequestAt < TimeSpan.FromSeconds(2)))
        {
            if (this.travelSessionActive && this.travelPurpose == NavigationPurpose.EscortFollow)
                this.TickTravelSession(now, escortTarget.Position, () => false);
            this.statusReason = $"护送模式：正在跟随 {escortTarget.Name}，当前距离 {distance:0.0}";
            return;
        }

        NavigationRequestResult requestResult = this.TryIssueNavigationRequest(
            now,
            NavigationPurpose.EscortFollow,
            escortTarget.Position,
            fly: false);
        if (requestResult == NavigationRequestResult.Accepted)
        {
            this.escortFollowLastPosition = escortTarget.Position;
            this.escortFollowLastRequestAt = now;
        }
        this.statusReason = $"护送模式：正在靠近 {escortTarget.Name}，当前距离 {distance:0.0}";
    }

    private void HandleBossCombat(DateTime now)
    {
        if (this.TryBeginPeriodicCompanionCheckpoint(now, AutomationState.BossFighting))
            return;

        FateSnapshot? fate = this.ResolveActiveFate();
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (fate is null || player is null)
        {
            this.AbandonActiveFate("Boss 战斗中 FATE 或玩家对象失效");
            return;
        }

        if (this.ReturnToFateIfOutside(fate, player, now))
            return;

        if (this.EnsureLevelSyncBeforeCombat(now, fate, "Boss 战斗选目标前"))
            return;

        if (this.TryHandleDestroyObjectiveCombat(now, fate, player))
            return;

        // The displayed/guarded encounter count includes ordinary overworld monsters as well as
        // FATE enemies. Boss cleanup already handles those separately, but hiding them here makes
        // the runtime snapshot report a misleading zero while the player is actually engaged.
        this.aggroCount = this.targetSelector.FindTargetsAggroedOnPlayer(player).Count;
        // Retain the selected living boss; only choose a new candidate after it disappears/dies.
        IReadOnlyList<IBattleNpc> bossCandidates = this.targetSelector.FindBossTargets(fate.FateId, player.Position);
        IBattleNpc? boss = this.bossTargetId is { } lockedBoss
            ? this.targetSelector.FindLiveTarget(lockedBoss)
            : null;
        if (boss is not null && (!FateTargetSelector.BelongsToFate(boss, fate.FateId) || FateTargetSelector.IsFriendly(boss)))
            boss = null;
        boss ??= bossCandidates.FirstOrDefault();
        if (boss is null)
        {
            if (this.bossTargetId is not null)
            {
                this.AddDiagnostic(
                    DiagnosticSeverity.Information,
                    $"Boss 实时扫描：原目标 {this.bossTargetId.Value:X} 已消失、不可选中或转为友方；当前没有可攻击的 FATE 目标");
                this.ClearTarget();
            }
            this.bossTargetId = null;
            this.statusReason = "讨伐BOSS类 FATE 暂未发现可选中的目标，等待刷新";
            this.nextActionAt = now.AddMilliseconds(500);
            return;
        }
        bool levelGapLarge = this.IsBossLevelGapLarge(fate);

        // If a boss fight has already started and HP becomes dangerous while at least two
        // ordinary monsters are actively attacking us, temporarily leave the synced level,
        // clean those attackers, and let the normal sync state restore the FATE level.
        if (levelGapLarge
            && player.MaxHp > 0
            && (float)player.CurrentHp / player.MaxHp <= BossEmergencyCleanupHpRatio)
        {
            IReadOnlyList<IBattleNpc> aggroedNonFate = this.targetSelector
                .FindTargetsAggroedOnPlayer(player)
                .Where(target => !FateTargetSelector.BelongsToFate(target, fate.FateId))
                .ToArray();
            if (aggroedNonFate.Count >= 2)
            {
                this.AddDiagnostic(
                    DiagnosticSeverity.Warning,
                    $"Boss FATE #{fate.FateId} 紧急清理：HP={player.CurrentHp}/{player.MaxHp}（阈值 {BossEmergencyCleanupHpRatio:P0}），接战中的非FATE目标={aggroedNonFate.Count}，先取消同步并清理");
                this.AddDiagnostic(
                    DiagnosticSeverity.Warning,
                    $"Boss 紧急清理非 FATE 目标明细：{FormatNonFateTargets(aggroedNonFate)}");
                if (!this.levelSync.TryCancelSync(fate.FateId))
                {
                    this.statusReason = "取消 Boss FATE 等级同步失败，等待下一帧重试";
                    this.nextActionAt = now.AddSeconds(1);
                    return;
                }

                this.bossCancelSyncStartedAt = now;

                this.BeginBossNonFateCleanup(
                    fate,
                    BossCleanupContinuation.ResumeBossAfterResync,
                    $"Boss 战斗中血量过低且有 {aggroedNonFate.Count} 个非 FATE 怪物接战");
                return;
            }
        }

        if (this.bossTargetId != boss.GameObjectId)
        {
            string previous = this.bossTargetId is { } previousId ? previousId.ToString("X") : "无";
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"Boss 实时扫描切换目标：{previous} → {boss.Name}({boss.GameObjectId:X})，" +
                $"候选数={bossCandidates.Count}，MaxHp={boss.MaxHp}，当前HP={boss.CurrentHp}；候选={FormatBossTargets(bossCandidates)}");
        }
        this.bossTargetId = boss.GameObjectId;
        this.pullTargetId = null;
        if (!this.TrySelectCombatTarget(boss, now))
            return;
        this.StopNavigationOperation();
        this.statusReason = $"讨伐BOSS专注模式：{boss.Name}，HP {boss.CurrentHp}/{boss.MaxHp}";
        this.nextActionAt = now.AddMilliseconds(250);
    }

    private byte GetFateLevel(FateSnapshot fate) => fate.MaxLevel > 0 ? fate.MaxLevel : fate.Level;

    private static string FormatNonFateTargets(IEnumerable<IBattleNpc> targets) => string.Join(
        "；",
        targets.Select(target =>
            $"{target.Name}({target.GameObjectId:X},FateId={FateTargetSelector.GetFateId(target)},HP={target.CurrentHp}/{target.MaxHp},Target={target.TargetObjectId:X})"));

    private static string FormatBossTargets(IEnumerable<IBattleNpc> targets) => string.Join(
        "；",
        targets.Select(target =>
            $"{target.Name}({target.GameObjectId:X},HP={target.CurrentHp}/{target.MaxHp},Target={target.TargetObjectId:X})"));

    private bool IsBossLevelGapLarge(FateSnapshot fate) =>
        this.playerState.Level >= GetFateLevel(fate) + 10;

    private void BeginBossNonFateCleanup(
        FateSnapshot fate,
        BossCleanupContinuation continuation,
        string reason)
    {
        this.bossCleanupContinuation = continuation;
        this.bossCleanupFateId = fate.FateId;
        this.bossCleanupTargetId = null;
        this.bossCleanupNoTargetSince = DateTime.MinValue;
        this.ClearTarget();
        this.StopNavigationOperation();
        this.pullTargetId = null;
        this.killTargetId = null;
        this.bossTargetId = null;
        this.ResetPullBatch();
        this.Transition(AutomationState.CleaningBossNonFate, reason, DiagnosticSeverity.Warning);
    }

    private void HandleBossNonFateCleanup(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (fate is null || player is null || this.bossCleanupFateId != fate.FateId)
        {
            this.AbandonActiveFate("Boss 非 FATE 清理时目标 FATE 已失效");
            return;
        }

        IReadOnlyList<IBattleNpc> candidates;
        if (this.bossCleanupContinuation is BossCleanupContinuation.BeforeBossFight or BossCleanupContinuation.ManualTestResync)
        {
            IBattleNpc? boss = this.targetSelector.FindBossTarget(fate.FateId, player.Position);
            Vector3 origin = boss?.Position ?? fate.Position;
            float radius = BossNonFateCleanupRadius;
            candidates = this.targetSelector.FindNonFateTargetsNear(fate.FateId, origin, radius);
        }
        else
        {
            candidates = this.targetSelector.FindTargetsAggroedOnPlayer(player)
                .Where(target => !FateTargetSelector.BelongsToFate(target, fate.FateId))
                .ToArray();
        }

        IBattleNpc? target = this.bossCleanupTargetId is { } locked
            ? candidates.FirstOrDefault(candidate => candidate.GameObjectId == locked)
            : null;
        target ??= candidates
            .OrderByDescending(candidate => FateTargetSelector.IsTargetingPlayer(candidate, player))
            .ThenBy(candidate => candidate.CurrentHp)
            .ThenBy(candidate => Vector3.DistanceSquared(player.Position, candidate.Position))
            .FirstOrDefault();

        if (target is not null)
        {
            this.bossCleanupNoTargetSince = DateTime.MinValue;
            if (this.bossCleanupTargetId != target.GameObjectId)
            {
                this.bossCleanupTargetId = target.GameObjectId;
                this.AddDiagnostic(
                    DiagnosticSeverity.Information,
                    $"Boss 非 FATE 清理目标锁定 → {target.Name} ({target.GameObjectId:X})，FateId={FateTargetSelector.GetFateId(target)}，HP={target.CurrentHp}/{target.MaxHp}");
            }

            if (!this.TrySelectCombatTarget(target, now))
                return;
            this.StopNavigationOperation();
            this.statusReason = this.bossCleanupContinuation == BossCleanupContinuation.BeforeBossFight
                ? $"Boss 开战前清理周围非 FATE 怪物：{target.Name}，剩余 {candidates.Count}"
                : $"Boss 紧急清理非 FATE 怪物：{target.Name}，剩余 {candidates.Count}";
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        this.bossCleanupTargetId = null;
        this.ClearTarget();
        if (this.bossCleanupNoTargetSince == DateTime.MinValue)
            this.bossCleanupNoTargetSince = now;
        if (now - this.bossCleanupNoTargetSince < TimeSpan.FromMilliseconds(750))
        {
            this.statusReason = "Boss 非 FATE 清理目标暂时消失，确认清理完成";
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        BossCleanupContinuation continuation = this.bossCleanupContinuation;
        this.bossCleanupFateId = null;
        this.bossCleanupNoTargetSince = DateTime.MinValue;
        this.bossPreCombatCleanupChecked = true;
        if (continuation == BossCleanupContinuation.BeforeBossFight)
        {
            this.levelSyncAttempts = 0;
            this.Transition(
                AutomationState.WaitingForLevelSync,
                "Boss 开战前非 FATE 怪物已清理，现在开始等级同步");
            this.nextActionAt = now;
            return;
        }

        if (continuation is BossCleanupContinuation.ResumeBossAfterResync or BossCleanupContinuation.ManualTestResync)
        {
            if (this.playerState.IsLevelSynced)
            {
                if (this.bossCancelSyncStartedAt == DateTime.MinValue)
                    this.bossCancelSyncStartedAt = now;
                if (now - this.bossCancelSyncStartedAt < TimeSpan.FromSeconds(5))
                {
                    this.levelSync.TryCancelSync(fate.FateId);
                    this.statusReason = "非 FATE 怪物已清理，等待原生取消等级同步生效";
                    this.nextActionAt = now.AddMilliseconds(500);
                    return;
                }

                this.Pause("原生 FateLevelSync 取消命令已发送，但等级同步在 5 秒内未解除；为避免继续承受低等级惩罚，已暂停");
                return;
            }

            this.bossCancelSyncStartedAt = DateTime.MinValue;
            this.levelSyncAttempts = 0;
            this.Transition(
                AutomationState.WaitingForLevelSync,
                "Boss 周围非 FATE 怪物已清理，重新进行等级同步");
            this.nextActionAt = now.AddSeconds(1);
        }
        else
        {
            this.Transition(AutomationState.BossFighting, "Boss 开战前非 FATE 怪物已清理，开始专注 Boss");
        }
    }

    private bool ReturnToFateIfOutside(
        FateSnapshot fate,
        IPlayerCharacter player,
        DateTime now,
        Vector3? safetyAnchor = null)
    {
        Vector3 anchor = safetyAnchor ?? fate.Position;
        float horizontalDistance = HorizontalDistance(player.Position, anchor);
        float boundary = safetyAnchor is null ? Math.Max(6f, fate.Radius) + 2f : 14f;
        if (horizontalDistance <= boundary)
        {
            this.outsideFateAreaSince = DateTime.MinValue;
            return false;
        }

        if (this.outsideFateAreaSince == DateTime.MinValue)
        {
            this.outsideFateAreaSince = now;
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                $"战斗中离开 FATE #{fate.FateId} 安全区域：距{(safetyAnchor is null ? "中心" : "护送目标")} {horizontalDistance:0.0}，边界 {boundary:0.0}");
            return false;
        }

        if (now - this.outsideFateAreaSince < TimeSpan.FromSeconds(1.5))
            return false;

        this.StopNavigationOperation();
        this.ClearTarget();
        this.pullTargetId = null;
        this.killTargetId = null;
        this.priorityDestroyTargetId = null;
        this.destroySession.Reset();
        this.bossTargetId = null;
        this.aggroCount = 0;
        this.ResetPullBatch();
        this.ResetNavigationProgress();
        this.returnAreaDiagnosticAt = DateTime.MinValue;
        this.Transition(
            AutomationState.ReturningToFateArea,
            $"战斗中被拖出安全区域，返回{(safetyAnchor is null ? "中心" : "护送目标")}后重新同步（距离 {horizontalDistance:0.0}）",
            DiagnosticSeverity.Warning);
        return true;
    }

    private void HandleReturnToFateArea(DateTime now)
    {
        FateSnapshot? fate = this.ResolveActiveFate();
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (fate is null || player is null)
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                $"自动返回 FATE 中止：fate={(fate is null ? "null" : $"#{fate.FateId}")}，player={(player is null ? "null" : player.GameObjectId.ToString("X"))}，state={this.state}");
            this.AbandonActiveFate("返回 FATE 区域时 FATE 或玩家对象失效");
            return;
        }

        Vector3 returnPosition = fate.Position;
        float safeRadius = Math.Max(3f, fate.Radius * 0.65f);
        if (fate.CombatProfile.ProtectionMode == FateProtectionMode.Moving)
        {
            IBattleNpc? escortTarget = this.targetSelector.FindAllFateObjects(fate.FateId, player.Position)
                .Where(FateTargetSelector.IsFriendly)
                .OrderBy(candidate => candidate.CurrentHp)
                .ThenBy(candidate => Vector3.DistanceSquared(player.Position, candidate.Position))
                .FirstOrDefault();
            if (escortTarget is not null)
            {
                returnPosition = escortTarget.Position;
                this.escortFollowLastPosition = returnPosition;
                safeRadius = 5f;
            }
            else if (this.escortFollowLastPosition is { } lastEscortPosition)
            {
                returnPosition = lastEscortPosition;
                safeRadius = 5f;
            }
        }

        float horizontalDistance = HorizontalDistance(player.Position, returnPosition);
        if (now >= this.returnAreaDiagnosticAt)
        {
            this.returnAreaDiagnosticAt = now.AddSeconds(2);
            this.AddDiagnostic(
                DiagnosticSeverity.Debug,
                $"自动返回 FATE 状态：FATE=#{fate.FateId}，当前位置={player.Position}，目标={returnPosition}，" +
                $"水平距离={horizontalDistance:0.0}，safeRadius={safeRadius:0.0}，" +
                $"vnavActive={this.vnavmesh.IsMoveActive}，PathRunning={this.vnavmesh.IsPathRunning}，" +
                $"PathfindInProgress={this.vnavmesh.IsMoveInProgress}，InCombat={this.condition[ConditionFlag.InCombat]}");
        }
        if (horizontalDistance <= safeRadius)
        {
            this.StopNavigationOperation();
            this.outsideFateAreaSince = DateTime.MinValue;
            this.levelSyncAttempts = 0;
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"自动返回 FATE 已到达安全锚点：水平距离={horizontalDistance:0.0}，safeRadius={safeRadius:0.0}，" +
                $"准备重新同步，当前位置={player.Position}");
            this.Transition(
                AutomationState.WaitingForLevelSync,
                $"已返回 FATE 安全范围（距离 {horizontalDistance:0.0}），重新验证等级同步");
            return;
        }

        if (!this.vnavmesh.IsAvailable)
        {
            this.statusReason = "返回 FATE 区域时等待 vnavmesh 恢复";
            if (now >= this.returnAreaDiagnosticAt)
            {
                this.returnAreaDiagnosticAt = now.AddSeconds(2);
                this.AddDiagnostic(
                    DiagnosticSeverity.Warning,
                    $"自动返回 FATE 等待 vnavmesh：目标={returnPosition}，当前水平距离={horizontalDistance:0.0}，" +
                    $"IsAvailable=False，IsMoveActive={this.vnavmesh.IsMoveActive}");
            }
            this.nextActionAt = now.AddSeconds(2);
            return;
        }

        this.CheckNavigationProgress(now, horizontalDistance, fate, returnPosition, horizontal: true);
        if (this.state != AutomationState.ReturningToFateArea)
            return;

        NavigationRequestResult requestResult = this.TryIssueNavigationRequest(
            now,
            NavigationPurpose.ReturnToFate,
            returnPosition,
            fly: false);
        if (requestResult == NavigationRequestResult.NotDue)
            return;
        if (requestResult == NavigationRequestResult.Rejected)
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                $"自动返回 FATE 区域：vnavmesh 拒绝请求；当前位置={player.Position}，目标={returnPosition}，" +
                $"水平距离={horizontalDistance:0.0}，InCombat={this.condition[ConditionFlag.InCombat]}");
            this.FailRecoverableAction("vnavmesh 拒绝返回 FATE 区域请求", this.IsRequiredFate(fate.FateId));
            return;
        }

        this.statusReason = $"正在返回 FATE 安全锚点，当前距离 {horizontalDistance:0.0}";
        this.AddDiagnostic(
            DiagnosticSeverity.Information,
            $"自动返回 FATE 区域：已请求地面导航；当前位置={player.Position}，目标={returnPosition}，" +
            $"水平距离={horizontalDistance:0.0}，safeRadius={safeRadius:0.0}");
    }

    /// <summary>Verify SetHardTarget's result; walk to a known actor when selection is rejected.</summary>
    private bool TrySelectCombatTarget(IBattleNpc target, DateTime now)
    {
        if (!target.IsValid()
            || target.IsDead
            || !target.IsTargetable
            || FateTargetSelector.IsFriendly(target))
        {
            if (this.targetManager.Target?.GameObjectId == target.GameObjectId)
                this.ClearTarget();
            this.statusReason = $"跳过不可攻击目标 {target.Name}（不可选中、已死亡或友方）";
            this.nextActionAt = now.AddMilliseconds(250);
            return false;
        }

        if (this.selectingTargetId != target.GameObjectId)
        {
            if (this.targetSelectionMoving)
                this.StopNavigationOperation();
            this.selectingTargetId = target.GameObjectId;
            this.targetSelectionFailedAt = DateTime.MinValue;
            this.targetSelectionMoving = false;
        }
        this.targetManager.Target = target;
        if (target.IsTargetable && this.targetManager.Target?.GameObjectId == target.GameObjectId)
        {
            if (this.targetSelectionMoving)
                this.StopNavigationOperation();
            this.targetSelectionMoving = false;
            this.targetSelectionFailedAt = DateTime.MinValue;
            return true;
        }
        if (this.targetSelectionFailedAt == DateTime.MinValue)
            this.targetSelectionFailedAt = now;
        if (now - this.targetSelectionFailedAt >= TimeSpan.FromSeconds(1))
        {
            if (!this.targetSelectionMoving)
            {
                this.StopNavigationOperation();
                this.targetSelectionMoving = true;
                this.AddDiagnostic(DiagnosticSeverity.Warning,
                    $"无法选中已锁定目标 {target.Name} ({target.GameObjectId:X})，地面靠近 {target.Position}，选中后停止");
            }
            this.RequestPullMovement(target.Position, now);
        }
        this.statusReason = $"等待选中 {target.Name}，正在靠近已知位置";
        this.nextActionAt = now.AddMilliseconds(250);
        return false;
    }

    private void ApproachAndSelectTarget(IBattleNpc target, IPlayerCharacter player, DateTime now)
    {
        if (!this.TrySelectCombatTarget(target, now))
            return;
        float distance = Vector3.Distance(player.Position, target.Position);
        bool casting = this.condition[ConditionFlag.Casting] || this.condition[ConditionFlag.Casting87];
        if (casting)
        {
            this.StopNavigationOperation();
            this.pullCastProtectionUntil = now.AddSeconds(1);
            this.statusReason = $"目标 {target.GameObjectId:X}：检测到读条，停止移动";
            return;
        }

        if (now < this.pullCastProtectionUntil)
        {
            this.StopNavigationOperation();
            this.statusReason = $"目标 {target.GameObjectId:X}：等待读条技能结算";
            return;
        }

        switch (this.pullApproachPhase)
        {
            case PullApproachPhase.MovingToApproachRange:
                if (distance <= this.configuration.PullApproachDistance)
                {
                    this.StopNavigationOperation();
                    this.EnterPullPhase(PullApproachPhase.MeleeAttackWindow, now);
                    return;
                }

                this.statusReason = $"目标 {target.GameObjectId:X}：正在接近至拉怪距离，当前 {distance:0.0}";
                this.RequestPullMovement(target.Position, now);
                return;

            case PullApproachPhase.MeleeAttackWindow:
                this.StopNavigationOperation();
                this.statusReason = $"目标 {target.GameObjectId:X}：近距离等待仇恨，距离 {distance:0.0}";
                if (distance > this.configuration.PullApproachDistance + 3f)
                    this.EnterPullPhase(PullApproachPhase.MovingToApproachRange, now);
                return;
        }
    }

    private void BeginPullTarget(ulong targetId, DateTime now, PullTargetKind kind = PullTargetKind.Normal)
    {
        this.pullTargetId = targetId;
        this.pullTargetKind = kind;
        this.pullAttemptStartedAt = now;
        this.pullCastProtectionUntil = DateTime.MinValue;
        this.EnterPullPhase(PullApproachPhase.MovingToApproachRange, now);
        this.StopNavigationOperation();
    }

    private void EnterPullPhase(PullApproachPhase phase, DateTime now)
    {
        this.pullApproachPhase = phase;
        this.pullPhaseStartedAt = now;
    }

    private bool IsPullAttemptTimedOut(DateTime now)
    {
        if (now - this.pullAttemptStartedAt >= TimeSpan.FromSeconds(30))
            return true;

        return this.pullApproachPhase == PullApproachPhase.MeleeAttackWindow
            && now - this.pullPhaseStartedAt >= TimeSpan.FromSeconds(this.configuration.AggroConfirmationTimeoutSeconds);
    }

    private void RequestPullMovement(Vector3 targetPosition, DateTime now)
    {
        NavigationRequestResult result = this.TryIssueNavigationRequest(
            now,
            NavigationPurpose.PullTarget,
            targetPosition,
            fly: false);
        if (result == NavigationRequestResult.Rejected)
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                $"拉怪接近导航失败，进入当前拉怪超时处理：目标={targetPosition}");
            this.pullAttemptStartedAt = now - TimeSpan.FromSeconds(30);
        }
    }

    private void ResetPullApproach()
    {
        this.pullAttemptStartedAt = DateTime.MinValue;
        this.pullPhaseStartedAt = DateTime.MinValue;
        this.pullCastProtectionUntil = DateTime.MinValue;
        this.pullApproachPhase = PullApproachPhase.MovingToApproachRange;
    }

    private void EnterDeadState()
    {
        this.RecordFateDeathIfApplicable();
        this.CancelOwnedActions();
        this.ClearTarget();
        this.pullTargetId = null;
        this.aggroCount = 0;
        this.deathStartedAt = DateTime.UtcNow;
        this.deathReturnAttemptStartedAt = DateTime.MinValue;
        this.raiseAcceptedAt = DateTime.MinValue;
        this.raiseAcceptIssued = false;
        string policy = this.configuration.AutoReturnAfterDeathTimeout
            ? $"等待 {this.configuration.DeathRaiseWaitSeconds} 秒后自动返回"
            : "关闭自动返回，将持续等待手动处理";
        this.Transition(AutomationState.DeadWaitingForRaise, $"角色死亡；{policy}", DiagnosticSeverity.Warning);
        this.PlaySoundAlert("角色死亡");
    }

    private void RecordFateDeathIfApplicable()
    {
        if (this.deathRecordedFateId is not null
            || this.activeFateId is not { } fateId
            || this.lastActiveFateSnapshot is not { } fate
            || fate.State != FateState.Running
            || !IsPlayerInFate(fateId))
            return;

        DateTime occurredAt = DateTime.UtcNow;
        this.configuration.DeathRecords.Insert(0, new DeathRecord
        {
            OccurredAtUtc = occurredAt,
            FateId = fate.FateId,
            FateName = fate.Name,
            TerritoryId = this.clientState.TerritoryType,
        });
        this.RecordFateFailure(
            fate,
            "死亡",
            "角色在 FATE 进行期间死亡",
            occurredAt,
            applyCooldown: false);
        if (this.configuration.DeathRecords.Count > 200)
            this.configuration.DeathRecords.RemoveRange(200, this.configuration.DeathRecords.Count - 200);

        this.skippedFates[fateId] = occurredAt.AddSeconds(this.configuration.DeathFateCooldownSeconds);
        this.deathRecordedFateId = fateId;
        this.persistConfiguration();
        this.AddDiagnostic(
            DiagnosticSeverity.Warning,
            $"记录 FATE 死亡：#{fate.FateId} {fate.Name}；该 FATE 冷却 {this.configuration.DeathFateCooldownSeconds} 秒");
    }

    private void RecordFateFailure(
        FateSnapshot fate,
        string kind,
        string reason,
        DateTime? occurredAt = null,
        bool applyCooldown = true)
    {
        DateTime timestamp = occurredAt ?? DateTime.UtcNow;
        this.configuration.FateFailureRecords.Insert(0, new FateFailureRecord
        {
            OccurredAtUtc = timestamp,
            FateId = fate.FateId,
            FateName = fate.Name,
            TerritoryId = this.clientState.TerritoryType,
            FailureKind = kind,
            Reason = reason,
        });
        if (this.configuration.FateFailureRecords.Count > 500)
            this.configuration.FateFailureRecords.RemoveRange(500, this.configuration.FateFailureRecords.Count - 500);
        if (applyCooldown)
            this.skippedFates[fate.FateId] = timestamp.Add(FailedFateCooldown);
        this.persistConfiguration();
        this.AddDiagnostic(
            DiagnosticSeverity.Warning,
            $"记录 FATE 失败：#{fate.FateId} {fate.Name}，类型={kind}，原因={reason}");
    }

    private void HandleDeath(DateTime now)
    {
        TimeSpan deadFor = now - this.deathStartedAt;
        if (this.configuration.AutoAcceptRaise && this.deathRecovery.TryAcceptRaise())
        {
            this.raiseAcceptedAt = now;
            this.statusReason = "已自动接受其他玩家的复活，等待复活生效";
            if (!this.raiseAcceptIssued)
            {
                this.raiseAcceptIssued = true;
                this.AddDiagnostic(DiagnosticSeverity.Information, this.statusReason);
            }
            this.nextActionAt = now.AddSeconds(1);
            return;
        }

        if (!this.configuration.AutoReturnAfterDeathTimeout)
        {
            this.statusReason = this.configuration.AutoAcceptRaise
                ? "持续等待其他玩家复活或手动返回"
                : "自动复活和自动返回均已关闭，等待手动处理";
            return;
        }

        if (this.raiseAcceptedAt != DateTime.MinValue
            && now - this.raiseAcceptedAt < TimeSpan.FromSeconds(15))
        {
            this.statusReason = "已接受玩家复活，暂停自动返回并等待复活生效";
            return;
        }

        if (deadFor < TimeSpan.FromSeconds(this.configuration.DeathRaiseWaitSeconds))
        {
            string action = this.configuration.AutoAcceptRaise ? "自动接受" : "手动接受";
            this.statusReason = $"等待其他玩家复活（{action}）：剩余 {Math.Ceiling(this.configuration.DeathRaiseWaitSeconds - deadFor.TotalSeconds)} 秒";
            return;
        }

        if (this.deathReturnAttemptStartedAt == DateTime.MinValue)
            this.deathReturnAttemptStartedAt = now;
        if (now - this.deathReturnAttemptStartedAt > TimeSpan.FromSeconds(120))
        {
            this.Pause("自动返回复活点在 120 秒内未成功。");
            return;
        }

        if (this.deathRecovery.TryConfirmReturn())
        {
            this.Transition(AutomationState.DeadReturning, "已确认返回复活点，等待区域加载");
            this.nextActionAt = now.AddSeconds(1);
            return;
        }
        else
            this.statusReason = "已超过等待时间，等待返回复活点确认框";
        this.nextActionAt = now.AddSeconds(1);
    }

    private void BeginFateCleanup(FateSnapshot fate, bool succeeded, string? completionReason = null)
    {
        this.pendingFateResult = new PendingFateResult(fate, succeeded);
        if (succeeded)
        {
            // Register completion before cleanup. Cleanup can take additional time because
            // residual enemies or a stuck combat flag must be handled; delaying this counter
            // until cleanup finished made preset progress appear as 0/1 and blocked transitions.
            this.totalCompletedFates++;
            this.presetCompletedFates++;
            this.presetTargetFateCounts[fate.FateId] = this.presetTargetFateCounts.GetValueOrDefault(fate.FateId) + 1;
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                completionReason is null
                    ? $"FATE #{fate.FateId} {fate.Name} 已进入 Ended；当前地图完成计数 = {this.presetCompletedFates}"
                    : $"{completionReason}；当前地图完成计数 = {this.presetCompletedFates}");
        }
        this.activeFateId = null;
        this.cleanupContinuation = CleanupContinuation.FinalizeFate;
        this.pullTargetId = null;
        this.killTargetId = null;
        this.lostPriorityTargetId = null;
        this.lostPriorityTargetKind = null;
        this.priorityDestroyTargetId = null;
        this.destroySession.Reset();
        this.bossTargetId = null;
        this.bossCleanupFateId = null;
        this.bossCleanupTargetId = null;
        this.bossCleanupNoTargetSince = DateTime.MinValue;
        this.bossCancelSyncStartedAt = DateTime.MinValue;
        this.bossEnvironmentLogAt = DateTime.MinValue;
        this.bossPreCombatCleanupChecked = false;
        this.ResetPullBatch();
        this.escortFollowLastRequestAt = DateTime.MinValue;
        this.escortFollowLastPosition = null;
        this.cleanupTargetId = null;
        this.aggroCount = 0;
        this.ResetCombatEscapeState();
        this.StopNavigationOperation();
        this.landing.StopDescending();
        this.ClearTarget();
        this.nextActionAt = DateTime.MinValue;
        string result = succeeded ? "成功" : "失败";
        this.Transition(AutomationState.CleaningUpCombat, $"FATE #{fate.FateId} 已{result}，先清理残余仇恨再结算");
    }

    private void BeginCombatCleanup(CleanupContinuation continuation, string reason)
    {
        this.cleanupContinuation = continuation;
        this.cleanupDiagnosticAt = DateTime.MinValue;
        this.travelObservationDelayPending = false;
        this.pullTargetId = null;
        this.killTargetId = null;
        this.priorityDestroyTargetId = null;
        this.destroySession.Reset();
        this.bossTargetId = null;
        this.ResetPullBatch();
        this.cleanupTargetId = null;
        this.aggroCount = 0;
        this.ResetCombatEscapeState();
        this.StopNavigationOperation();
        this.landing.StopDescending();
        this.nextActionAt = DateTime.MinValue;
        this.Transition(AutomationState.CleaningUpCombat, reason, DiagnosticSeverity.Warning);
    }

    private void HandleCombatCleanup(DateTime now)
    {
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (player is null)
        {
            this.statusReason = "战后清场等待玩家对象";
            return;
        }

        IReadOnlyList<IBattleNpc> targets = this.cleanupContinuation == CleanupContinuation.ResumeCollection
            ? this.targetSelector.FindEngagedTargets(player)
            : this.targetSelector.FindTargetsAggroedOnPlayer(player);
        if (targets.Count == 0 && this.condition[ConditionFlag.InCombat])
        {
            // TargetObjectId can lag behind the combat condition for a few frames after the
            // FATE disappears. Fall back to nearby native InCombat enemies before deciding to run
            // away, otherwise we abandon a real non-FATE threat and enter escape mode.
            targets = this.targetSelector.FindCombatCleanupTargets(player);
            if (now >= this.cleanupDiagnosticAt)
            {
                this.cleanupDiagnosticAt = now.AddSeconds(2);
                IBattleNpc[] nearbyCandidates = this.objectTable
                    .OfType<IBattleNpc>()
                    .Where(candidate => candidate.IsValid() && !candidate.IsDead && candidate.IsTargetable
                        && candidate.BattleNpcKind == BattleNpcSubKind.Combatant
                        && Vector3.DistanceSquared(candidate.Position, player.Position) <= 80f * 80f)
                    .OrderBy(candidate => Vector3.DistanceSquared(candidate.Position, player.Position))
                    .Take(12)
                    .ToArray();
                string details = string.Join(
                    " | ",
                    nearbyCandidates.Select(target =>
                        $"{target.Name}({target.GameObjectId:X},fate={FateTargetSelector.GetFateId(target)}," +
                        $"target={target.TargetObjectId:X},nativeCombat={FateTargetSelector.IsNativeInCombat(target)}," +
                        $"friendly={FateTargetSelector.IsFriendly(target)},namePlateKind={FateTargetSelector.GetNamePlateKind(target)},attackable={this.targetSelector.IsAttackableCleanupTarget(target)})"));
                this.AddDiagnostic(
                    targets.Count > 0 ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
                    $"战后仇恨检测：InCombat=True，直接目标列表=0，备用 native InCombat 目标数={targets.Count}，" +
                    $"LocalPlayer={player.GameObjectId:X}；" +
                    (details.Length == 0 ? "未找到 80 yalms 内可清理战斗 NPC" : $"附近候选：{details}"));
            }
        }
        this.aggroCount = targets.Count;
        if (this.cleanupContinuation == CleanupContinuation.PreemptTravel)
        {
            if (this.preemptSyncFateId is { } syncFateId)
            {
                if (this.levelSync.IsSyncedTo(syncFateId))
                {
                    this.ClearTarget();
                    bool sent = this.levelSync.TryCancelSync(syncFateId);
                    this.statusReason = sent
                        ? $"抢占跑路前取消 FATE #{syncFateId} 等级同步"
                        : $"等待取消 FATE #{syncFateId} 等级同步请求被接受";
                    this.AddDiagnostic(
                        sent ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
                        $"抢占跑路前取消等级同步：FATE=#{syncFateId}，请求结果={sent}");
                    this.nextActionAt = now.AddMilliseconds(250);
                    return;
                }

                this.preemptSyncFateId = null;
            }

            // A preemption must not stay at the old FATE to finish enemies. Clear the target on
            // every tick and move away immediately; otherwise the external combat plugin can
            // keep selecting freshly spawned enemies and prolong the combat indefinitely.
            if (this.IsCombatEngaged())
            {
                this.ClearTarget();
                this.HandleCombatEscape(now, player, immediate: true);
                return;
            }

            // Once the combat flag is gone, do not fall through to the ordinary
            // post-FATE cleanup selector.  Any remaining objects belong to the old
            // encounter and selecting them here would pull us back into combat.
            this.ClearTarget();
            this.StopNavigationOperation();
            this.ResetCombatEscapeState();
            this.aggroCount = 0;
            this.ResetNavigationProgress();
            AutomationState resumeState = this.activeFateId is null
                ? AutomationState.ScanningFates
                : this.fateAetheryteTeleportPlan is not null
                    ? AutomationState.Teleporting
                    : this.ActiveFateTravelState;
                this.Transition(
                    resumeState,
                "指定 FATE 抢占清场已脱战，忽略旧区域残留目标并继续前往新目标");
            if (this.activeFateId is not null)
                this.BeginTravelObservationDelay();
            return;
        }

        IBattleNpc? current = this.cleanupTargetId is { } rememberedId
            ? targets.FirstOrDefault(target => target.GameObjectId == rememberedId)
            : null;
        current ??= targets.FirstOrDefault();
        if (current is not null)
        {
            this.ResetCombatEscapeState();
            if (this.cleanupTargetId != current.GameObjectId)
                this.AddDiagnostic(DiagnosticSeverity.Information, $"战后清场目标 → {current.GameObjectId:X}");
            this.cleanupTargetId = current.GameObjectId;
            if (!this.TrySelectCombatTarget(current, now))
                return;
            this.StopNavigationOperation();
            this.statusReason = $"战后清场：等待外部战斗插件击杀 {current.Name}，剩余 {targets.Count} 个目标";
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        this.cleanupTargetId = null;
        this.ClearTarget();
        if (this.condition[ConditionFlag.InCombat])
        {
            // Collection interactions are short and are cancelled by the first incoming hit.
            // Do not apply the general post-combat five-second grace period here: if the hater
            // list is temporarily one frame behind the combat flag, the collection loop must
            // still react immediately instead of appearing frozen beside the object.
            this.HandleCombatEscape(
                now,
                player,
                immediate: this.cleanupContinuation == CleanupContinuation.ResumeCollection);
            return;
        }

        this.StopNavigationOperation();
        this.ResetCombatEscapeState();
        this.aggroCount = 0;
        switch (this.cleanupContinuation)
        {
            case CleanupContinuation.FinalizeFate:
                this.FinalizePendingFateResult();
                break;
            case CleanupContinuation.RevalidatePlan:
                this.Transition(
                    AutomationState.ValidatingPlan,
                    "残余敌人已清除且已脱战，重新校验预设索引与目标地图");
                break;
            case CleanupContinuation.ResumeCompanionCheckpoint:
                this.Transition(
                    AutomationState.CheckingCompanions,
                    "残余敌人已清除且已脱战，返回伙伴检查点");
                break;
            case CleanupContinuation.ResumeTravel:
            case CleanupContinuation.PreemptTravel:
                this.ResetNavigationProgress();
                AutomationState resumeState = this.activeFateId is null
                    ? AutomationState.ScanningFates
                    : this.fateAetheryteTeleportPlan is not null
                        ? AutomationState.Teleporting
                        : this.ActiveFateTravelState;
                this.Transition(
                    resumeState,
                    "残余敌人已清除且已脱战，继续前往目标");
                break;
            case CleanupContinuation.ResumeCollection:
                this.ResetNavigationProgress();
                this.collectionCombatFallback = false;
                this.Transition(
                    this.collectionTurnInUrgent || this.collectionTurnInPhase
                        ? AutomationState.TurningInCollectionFate
                        : AutomationState.CollectingFateItems,
                    "收集流程中的接战目标已清除且已脱战，恢复收集流程");
                break;
            default:
                this.Transition(AutomationState.ScanningFates, "残余敌人已清除且已脱战，继续扫描 FATE");
                break;
        }
    }

    private void HandleCombatEscape(DateTime now, IPlayerCharacter player, bool immediate = false)
    {
        if (this.cleanupNoTargetSince == DateTime.MinValue)
        {
            this.cleanupNoTargetSince = immediate ? now - CombatEscapeGracePeriod : now;
            this.StopNavigationOperation();
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                immediate
                    ? this.cleanupContinuation == CleanupContinuation.ResumeCollection
                        ? "收集流程：未发现当前仇恨对象，立即跑离尝试脱战"
                        : "指定 FATE 抢占：未发现当前仇恨对象，立即跑离尝试脱战"
                    : "清场未发现仇恨对象但仍在战斗，先原地等待 5 秒尝试自然脱战");
        }

        TimeSpan graceElapsed = now - this.cleanupNoTargetSince;
        if (graceElapsed < CombatEscapeGracePeriod)
        {
            this.StopNavigationOperation();
            double graceRemaining = CombatEscapeGracePeriod.TotalSeconds - graceElapsed.TotalSeconds;
            this.statusReason = $"未发现仇恨对象，原地等待自然脱战；{Math.Ceiling(graceRemaining)} 秒后开始跑离";
            this.nextActionAt = now.AddMilliseconds(250);
            return;
        }

        if (this.cleanupEscapeStartedAt == DateTime.MinValue)
        {
            this.cleanupEscapeStartedAt = now;
            this.cleanupEscapeDestination = this.FindCombatEscapeDestination(player);
            this.ResetNavigationProgress();
            this.AddDiagnostic(
                DiagnosticSeverity.Warning,
                immediate
                    ? $"收集流程未发现可清理目标，立即开始跑离 {this.configuration.CombatEscapeDistance:0} yalms"
                    : $"原地等待 5 秒后仍在战斗，开始跑离 {this.configuration.CombatEscapeDistance:0} yalms");
        }

        TimeSpan escapeElapsed = now - this.cleanupEscapeStartedAt;
        if (escapeElapsed >= TimeSpan.FromSeconds(this.configuration.CombatEscapeTimeoutSeconds))
        {
            this.StopNavigationOperation();
            this.BeginAggroDutyReset(now);
            return;
        }

        if (!this.vnavmesh.IsAvailable)
        {
            this.statusReason = "清场跑离等待 vnavmesh 恢复";
            this.nextActionAt = now.AddSeconds(2);
            return;
        }

        Vector3 destination = this.cleanupEscapeDestination ?? player.Position;
        float distance = Vector3.Distance(player.Position, destination);
        if (distance > 3f)
        {
            _ = this.TryIssueNavigationRequest(now, NavigationPurpose.CombatEscape, destination, fly: false);
        }

        double remaining = this.configuration.CombatEscapeTimeoutSeconds - escapeElapsed.TotalSeconds;
        this.statusReason = $"未发现仇恨对象，正在跑离尝试脱战；{Math.Ceiling(remaining)} 秒后使用泰坦歼灭战重置";
        this.nextActionAt = now.AddMilliseconds(250);
    }

    private Vector3 FindCombatEscapeDestination(IPlayerCharacter player)
    {
        // During a priority-FATE preemption activeFateId already points at the new
        // destination.  Keep using the old combat area's origin so the escape vector
        // cannot accidentally send the player toward the newly selected FATE.
        Vector3 origin = this.cleanupEscapeOrigin
            ?? this.pendingFateResult?.Fate.Position
            ?? (this.activeFateId is { } fateId ? this.fates.Find(fateId)?.Position : null)
            ?? player.Position;
        Vector2 away = new(player.Position.X - origin.X, player.Position.Z - origin.Z);
        if (away.LengthSquared() < 0.01f)
        {
            float angle = ((this.pendingFateResult?.Fate.FateId ?? this.activeFateId ?? 1) % 16) * (MathF.PI / 8f);
            away = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
        else
        {
            away = Vector2.Normalize(away);
        }

        Vector3 requested = new(
            player.Position.X + (away.X * this.configuration.CombatEscapeDistance),
            player.Position.Y,
            player.Position.Z + (away.Y * this.configuration.CombatEscapeDistance));
        return this.vnavmesh.FindNearestReachablePoint(requested, 30f, 100f) ?? requested;
    }

    private void BeginAggroDutyReset(DateTime now, bool manual = false)
    {
        if (this.partyList.Length > 1)
        {
            const string reason = "当前处于小队中；为避免带其他玩家进入副本，已拒绝泰坦仇恨重置。";
            if (manual)
                this.FinishManualDutyRoundTrip(false, reason);
            else
                this.Pause($"残余战斗无法解除，但{reason}");
            return;
        }

        if (!this.dutyAggroReset.Validate(out string validationError))
        {
            if (manual)
                this.FinishManualDutyRoundTrip(false, $"无法执行泰坦副本进退：{validationError}");
            else
                this.Pause($"无法执行泰坦仇恨重置：{validationError}");
            return;
        }

        this.ClearTarget();
        // A duty reset owns the next territory transition. Any pending FATE aetheryte plan
        // belongs to the interrupted travel flow and must not resume inside The Navel.
        if (this.teleportIssuedByUs && this.lifestream.IsAvailable)
            this.lifestream.Abort();
        this.fateAetheryteTeleportPlan = null;
        this.teleportArrival.CancelRequest();
        this.teleportIssuedByUs = false;
        this.teleportBusyObserved = false;
        this.dutyResetOriginTerritory = this.clientState.TerritoryType;
        this.dutyResetStartedAt = now;
        this.dutyResetLastActionAt = DateTime.MinValue;
        this.dutyResetPhase = AggroDutyResetPhase.Queueing;
        this.Transition(
            AutomationState.ResettingAggroViaDuty,
            manual
                ? "手动执行：准备以解除限制进入泰坦歼灭战并立即退出"
                : "跑离后仍未脱战，准备以解除限制进入泰坦歼灭战",
            DiagnosticSeverity.Warning);
    }

    private void HandleAggroDutyReset(DateTime now)
    {
        if (now - this.dutyResetStartedAt > TimeSpan.FromSeconds(180))
        {
            this.dutyAggroReset.CancelOwnedQueue();
            if (this.manualDutyRoundTrip)
                this.FinishManualDutyRoundTrip(false, "手动泰坦副本进退在 180 秒内未完成");
            else
                this.Pause("泰坦仇恨重置在 180 秒内未完成。");
            return;
        }

        if (this.IsBetweenAreas() || now < this.territoryStableAfter)
        {
            this.statusReason = "泰坦仇恨重置等待区域加载稳定";
            return;
        }

        if (this.dutyAggroReset.IsInTitanDuty)
        {
            if (this.dutyResetPhase is not AggroDutyResetPhase.LeavingDuty and not AggroDutyResetPhase.WaitingForReturn)
            {
                this.dutyAggroReset.MarkEnteredDuty();
                this.dutyResetPhase = AggroDutyResetPhase.LeavingDuty;
                this.dutyResetLastActionAt = now;
            }
            if (this.dutyResetPhase == AggroDutyResetPhase.LeavingDuty
                && now - this.dutyResetLastActionAt >= TimeSpan.FromSeconds(2))
            {
                bool sent = this.dutyAggroReset.TryLeaveTitan();
                this.dutyResetLastActionAt = now;
                string commandResults = $"816={this.dutyAggroReset.LastFinishTerritoryTransportResult}, 819={this.dutyAggroReset.LastLeaveDutyResult}";
                this.statusReason = sent
                    ? $"已请求退出泰坦歼灭战，等待返回原区域（{commandResults}）"
                    : $"退出副本请求未被接受，准备重试（{commandResults}）";
                this.AddDiagnostic(sent ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning, this.statusReason);
                if (sent)
                    this.dutyResetPhase = AggroDutyResetPhase.WaitingForReturn;
            }
            else if (this.dutyResetPhase == AggroDutyResetPhase.WaitingForReturn)
            {
                if (now - this.dutyResetLastActionAt >= TimeSpan.FromSeconds(3))
                {
                    // Command 819 can be accepted by the client while the duty UI is still
                    // closing. Retry it until the territory actually unloads; previously the
                    // state stayed in WaitingForReturn forever after a single accepted command.
                    bool retrySent = this.dutyAggroReset.TryLeaveTitan();
                    this.dutyResetLastActionAt = now;
                    string retryResults = $"816={this.dutyAggroReset.LastFinishTerritoryTransportResult}, 819={this.dutyAggroReset.LastLeaveDutyResult}";
                    this.AddDiagnostic(
                        retrySent ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
                        $"泰坦副本仍未退出，重试退出命令：结果={retrySent}（{retryResults}，Territory={this.clientState.TerritoryType}）");
                }
                this.statusReason = "退出命令已发送，等待泰坦副本区域卸载";
            }
            return;
        }

        if (this.dutyResetPhase == AggroDutyResetPhase.LeavingDuty)
            this.dutyResetPhase = AggroDutyResetPhase.WaitingForReturn;

        if (this.dutyResetPhase == AggroDutyResetPhase.WaitingForReturn)
        {
            if (this.clientState.TerritoryType != this.dutyResetOriginTerritory)
            {
                this.statusReason = $"已退出泰坦歼灭战，等待返回原 Territory {this.dutyResetOriginTerritory}";
                return;
            }

            this.dutyResetPhase = AggroDutyResetPhase.None;
            this.ResetCombatEscapeState();
            if (this.manualDutyRoundTrip)
                this.FinishManualDutyRoundTrip(true, "手动副本进退已完成");
            else
                this.Transition(AutomationState.CleaningUpCombat, "已通过副本进出重置仇恨，重新确认脱战状态");
            return;
        }

        if (!this.manualDutyRoundTrip && !this.condition[ConditionFlag.InCombat])
        {
            this.dutyAggroReset.CancelOwnedQueue();
            this.dutyResetPhase = AggroDutyResetPhase.None;
            this.ResetCombatEscapeState();
            this.Transition(AutomationState.CleaningUpCombat, "副本进入前已自然脱战，取消泰坦重置");
            return;
        }

        ContentsFinderQueueState queueState = this.dutyAggroReset.QueueState;
        if (!this.dutyAggroReset.QueueIssuedByUs)
        {
            if (queueState != ContentsFinderQueueState.None)
            {
                string queueConflictReason = $"检测到非 AutoFatre 的副本队列（{queueState}），已拒绝执行泰坦仇恨重置。";
                if (this.manualDutyRoundTrip)
                    this.FinishManualDutyRoundTrip(false, queueConflictReason);
                else
                    this.Pause(queueConflictReason);
                return;
            }

            if (!this.dutyAggroReset.TryQueueUnrestricted(out string reason))
            {
                this.statusReason = $"泰坦解限进入请求暂未成功：{reason}";
                this.nextActionAt = now.AddSeconds(2);
                return;
            }

            this.dutyResetLastActionAt = now;
            this.dutyResetPhase = AggroDutyResetPhase.WaitingForEntry;
            this.AddDiagnostic(DiagnosticSeverity.Information, reason);
            this.nextActionAt = now.AddSeconds(1);
            return;
        }

        if (this.dutyAggroReset.TryCommence())
        {
            this.statusReason = "已确认进入泰坦歼灭战，等待区域切换";
            this.dutyResetLastActionAt = now;
            this.nextActionAt = now.AddSeconds(1);
            return;
        }

        if (queueState == ContentsFinderQueueState.None
            && now - this.dutyResetLastActionAt >= TimeSpan.FromSeconds(3))
        {
            this.dutyAggroReset.TryQueueUnrestricted(out string retryReason);
            this.dutyResetLastActionAt = now;
            this.statusReason = $"泰坦解限队列尚未建立，正在重试：{retryReason}";
            this.nextActionAt = now.AddSeconds(1);
            return;
        }

        this.statusReason = $"等待泰坦歼灭战进入确认（队列状态 {queueState}）";
        this.nextActionAt = now.AddMilliseconds(500);
    }

    private void FinishManualDutyRoundTrip(bool succeeded, string reason)
    {
        bool shouldResumeAutomation = this.resumeAutomationAfterManualDuty && this.configuration.Enabled;
        this.ResetCurrentActivity();
        this.Transition(
            shouldResumeAutomation ? AutomationState.ValidatingPlan : AutomationState.Stopped,
            shouldResumeAutomation ? $"{reason}，重新验证并继续自动化" : reason,
            succeeded ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning);
    }

    private void FinalizePendingFateResult()
    {
        PendingFateResult? result = this.pendingFateResult;
        if (result is null)
        {
            this.Transition(AutomationState.ScanningFates, "没有待结算 FATE，继续扫描");
            return;
        }

        ushort completedId = result.Fate.FateId;
        bool temporaryTargetCompleted = this.temporaryTargetActive
            && this.temporaryTargetFateId == completedId;
        if (result.Succeeded)
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"FATE #{completedId} {result.Fate.Name} 成功完成并已脱战；当前地图完成计数 = {this.presetCompletedFates}");
            this.PlaySoundAlert("FATE 完成");
        }
        else
        {
            this.AddDiagnostic(DiagnosticSeverity.Warning, $"FATE #{completedId} {result.Fate.Name} 失败并已脱战，不计入完成数");
        }

        if (temporaryTargetCompleted)
            this.RestoreTemporaryTarget("临时目标 FATE 已完成并从列表移除");

        if (temporaryTargetCompleted && !this.configuration.Enabled)
        {
            this.ResetCurrentActivity();
            this.Transition(AutomationState.Stopped, "临时目标 FATE 已完成，已恢复原模式并保持停止");
            return;
        }

        bool hasItemStopCondition = this.GetCurrentPresetStopConditions()
            .Any(stop => stop.Kind == StopConditionKind.ItemCount);
        this.ResetCurrentActivity();
        if (result.Succeeded && hasItemStopCondition)
        {
            this.presetConditionRecheckUntil = DateTime.UtcNow.AddSeconds(3);
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                "FATE 已结算；物品停止条件将在背包结算窗口内持续复查 3 秒");
        }
        this.BeginCompanionCheckpoint(
            CompanionCheckpointKind.PostFate,
            AutomationState.ScanningFates,
            result.Succeeded
                ? "FATE 结算完成；检查宠物、指定武器与陆行鸟伙伴，再检查停止条件并选择下一目标"
                : "FATE 失败并已脱战；检查宠物、指定武器与陆行鸟伙伴，再重新扫描");
    }

    private IReadOnlyList<StopCondition> GetCurrentPresetStopConditions()
    {
        IReadOnlyList<MapPreset> maps = this.GetPresetMaps();
        if (this.configuration.Mode != AutomationMode.PresetSequence || maps.Count == 0)
            return Array.Empty<StopCondition>();

        return maps[Math.Clamp(this.presetIndex, 0, maps.Count - 1)].StopConditions;
    }

    private bool CheckPresetStopCondition(ushort? justCompletedFateId = null, bool logUnmet = false)
    {
        IReadOnlyList<MapPreset> maps = this.GetPresetMaps();
        if (this.configuration.Mode != AutomationMode.PresetSequence || maps.Count == 0)
            return false;

        IReadOnlyList<StopCondition> conditions = this.GetCurrentPresetStopConditions();
        if (conditions.Count == 0)
            return false;

        bool met = conditions.All(stop =>
        {
            int target = Math.Max(1, stop.Kind == StopConditionKind.ItemCount ? stop.ItemCount : stop.FateCount);
            int actual;
            bool satisfied;
            switch (stop.Kind)
            {
                case StopConditionKind.FateCount:
                    actual = this.presetCompletedFates;
                    satisfied = actual >= target;
                    break;
                case StopConditionKind.ItemCount:
                    actual = stop.ItemId == 0 ? 0 : this.inventoryCounter.Count(stop.ItemId);
                    satisfied = stop.ItemId != 0 && actual >= target;
                    break;
                case StopConditionKind.TargetFate:
                    actual = this.ExpandFateSelection(stop.TargetFateId)
                        .Sum(fateId => this.presetTargetFateCounts.GetValueOrDefault(fateId));
                    satisfied = actual >= target;
                    break;
                default:
                    actual = 0;
                    satisfied = false;
                    break;
            }

            if (logUnmet)
            {
                this.AddDiagnostic(
                    DiagnosticSeverity.Debug,
                    $"预设停止条件检查：类型={stop.Kind} itemId={stop.ItemId} fateId={stop.TargetFateId} 当前={actual} 目标={target} 结果={(satisfied ? "满足" : "未满足")}");
            }

            return satisfied;
        });
        if (!met)
        {
            if (logUnmet)
            {
                this.AddDiagnostic(
                    DiagnosticSeverity.Debug,
                    $"当前地图停止条件未满足：地图 {this.presetIndex + 1}/{maps.Count}，已完成 FATE {this.presetCompletedFates}，条件数 {conditions.Count}");
            }
            return false;
        }

        this.AddDiagnostic(
            DiagnosticSeverity.Information,
            $"当前地图停止条件已满足：地图 {this.presetIndex + 1}/{maps.Count}，准备切换到下一张地图");
        this.AdvancePreset();
        return true;
    }

    private void AdvancePreset()
    {
        this.CancelOwnedActions();
        this.ResetCurrentActivity();
        this.ClearCurrentMapTerritory();
        // A subsequent preset entry may target the same territory but a different pet; do not
        // let the previous map entry suppress the new summon attempt.
        this.entryPetSummonedTerritory = null;
        this.entryPetRetryAt = DateTime.MinValue;
        this.presetCompletedFates = 0;
        this.presetTargetFateCounts.Clear();
        this.presetIndex++;
        if (this.presetIndex < this.GetPresetMaps().Count)
        {
            MapPreset next = this.GetPresetMaps()[this.presetIndex];
            string mapName = string.IsNullOrWhiteSpace(next.Name) ? $"地图 {this.presetIndex + 1}" : next.Name;
            this.Transition(
                AutomationState.ValidatingPlan,
                $"当前地图条件完成，进入{mapName}（Territory {next.TerritoryId}）");
            return;
        }

        if (this.configuration.SequenceCompletion == SequenceCompletionPolicy.Loop)
        {
            this.presetIndex = 0;
            this.Transition(AutomationState.ValidatingPlan, "预设列表完成，开始下一轮");
        }
        else
        {
            this.Stop();
            this.statusReason = "预设列表已全部完成";
        }
    }

    private void FailRecoverableAction(string reason, bool required)
    {
        this.recoveryResumeState = this.state;
        this.recoveryAttempts++;
        this.AddDiagnostic(DiagnosticSeverity.Warning, $"{reason}（恢复 {this.recoveryAttempts}/{this.configuration.MaxRecoveryAttempts}）");
        if (this.recoveryAttempts > this.configuration.MaxRecoveryAttempts)
        {
            if (!required && this.activeFateId is { } fateId)
            {
                if (this.ResolveActiveFate() is { } failedFate)
                    this.RecordFateFailure(failedFate, reason.Contains("导航", StringComparison.Ordinal) ? "寻路失败" : "流程失败", reason);
                this.skippedFates[fateId] = DateTime.UtcNow.AddSeconds(60);
                this.AbandonActiveFate($"普通 FATE 恢复耗尽，跳过 60 秒：{reason}");
            }
            else
            {
                if (this.ResolveActiveFate() is { } failedFate)
                    this.RecordFateFailure(failedFate, reason.Contains("导航", StringComparison.Ordinal) ? "寻路失败" : "流程失败", reason);
                this.Pause($"必要目标恢复次数耗尽：{reason}");
            }
            return;
        }

        this.Transition(AutomationState.Recovering, reason, DiagnosticSeverity.Warning);
        this.nextActionAt = DateTime.UtcNow.AddSeconds(Math.Min(5, this.recoveryAttempts));
    }

    private void RecoverCurrentActivity()
    {
        this.StopNavigationOperation();
        this.teleportIssuedByUs = false;
        this.teleportBusyObserved = false;
        this.ResetNavigationProgress();
        AutomationState next = this.pendingFateResult is not null
            ? AutomationState.CleaningUpCombat
            : this.activeFateId is null
                ? AutomationState.ValidatingPlan
                : this.recoveryResumeState switch
                {
                    AutomationState.MountingForTravel => AutomationState.MountingForTravel,
                    AutomationState.LandingForFate => AutomationState.LandingForFate,
                    AutomationState.DismountingForFate => AutomationState.DismountingForFate,
                    AutomationState.ReturningToFateArea => AutomationState.ReturningToFateArea,
                    _ => AutomationState.NavigatingToFate,
                };
        this.Transition(next, "执行恢复重试");
    }

    private FateSnapshot? ResolveActiveFate()
    {
        if (this.activeFateId is not { } id)
            return null;
        FateSnapshot? fate = this.fates.Find(id);
        if (fate is null)
            return null;
        if (IsPreparingFate(fate)
            && this.state is AutomationState.MountingForTravel
                or AutomationState.NavigatingToFate
                or AutomationState.LandingForFate
                or AutomationState.DismountingForFate
                or AutomationState.CheckingCompanions
                or AutomationState.OpeningPreparingFate
                or AutomationState.RecoveringNavigation
                or AutomationState.Recovering)
            return fate;
        return fate.State == FateState.Running && fate.TimeRemaining > 0 ? fate : null;
    }

    private IReadOnlyList<FateSnapshot> GetEligibleFates(DateTime now) => this.fates.Snapshot
        .Where(f => this.IsEligible(f, now))
        .ToArray();

    private bool IsEligible(FateSnapshot fate, DateTime now) =>
        (fate.State == FateState.Running && fate.TimeRemaining > 0 || IsPreparingFate(fate))
        && !this.configuration.FateBlacklist.Contains(fate.FateId)
        && fate.IsAutomationSupported
        && FateRepository.HasValidPosition(fate)
        && (!this.skippedFates.TryGetValue(fate.FateId, out DateTime until) || until <= now);

    private float ScoreFate(FateSnapshot fate)
    {
        float distance = this.DistanceToPlayer(fate.Position);
        float distanceWeight = CalculateDistanceWeight(distance);
        if (IsPreparingFate(fate))
        {
            // Preparing events use distance only because their native time is a pre-start
            // sentinel. They can still beat a started FATE whose remaining window is too short.
            return 700f + distanceWeight;
        }

        // A started FATE is normally preferred. Distance uses a linear, bounded penalty so that
        // a 50-100 yalm difference remains meaningful even when both events are far away.
        float score = 850f + distanceWeight;
        score += Math.Max(0, 100 - fate.Progress) * 0.75f;
        score += CalculateTimeWeight(fate.TimeRemaining, distance);
        if (fate.HasBonus)
            score += 25f;
        return score;
    }

    private static float CalculateDistanceWeight(float distance) =>
        -Math.Clamp(distance * 0.35f, 0f, 350f);

    /// <summary>
    /// Converts remaining time into a risk penalty. Once the usable window is comfortably long
    /// (six minutes after travel buffer), time contributes zero, so distance decides between two
    /// otherwise healthy choices. Only below that plateau does the penalty grow, accelerating in
    /// the final three minutes because an unfinished FATE has no completion reward.
    /// </summary>
    private static float CalculateTimeWeight(long timeRemaining, float distance)
    {
        const float safeWindowSeconds = 360f;
        const float riskWindowSeconds = 180f;
        const float travelSecondsPerYalm = 5f;
        float travelBuffer = Math.Clamp(distance / travelSecondsPerYalm, 0, 90);
        float seconds = Math.Clamp(timeRemaining - travelBuffer, 0, 900);
        if (seconds >= safeWindowSeconds)
            return 0f;

        if (seconds >= riskWindowSeconds)
        {
            float transitionRisk = (safeWindowSeconds - seconds) / (safeWindowSeconds - riskWindowSeconds);
            return -100f * transitionRisk * transitionRisk;
        }

        float risk = (riskWindowSeconds - seconds) / riskWindowSeconds;
        return -100f - (1600f * risk * risk) - (300f * risk);
    }

    private bool IsRequiredFate(ushort fateId) => this.GetPriorityTargetFateIds().Contains(fateId);

    private IReadOnlyList<ushort> GetPriorityTargetFateIds() => this.GetCurrentPlan().TargetFateIds
        .Where(fateId => fateId != 0 && !this.configuration.FateBlacklist.Contains(fateId))
        .ToArray();

    private PlanContext GetCurrentPlan()
    {
            if (this.configuration.Mode == AutomationMode.PresetSequence && this.GetPresetMaps().Count > 0)
            {
            int index = Math.Clamp(this.presetIndex, 0, this.GetPresetMaps().Count - 1);
            MapPreset preset = this.GetPresetMaps()[index];
            ushort[] stopTargets = preset.StopConditions
                .Where(stop => stop.Kind == StopConditionKind.TargetFate && stop.TargetFateId != 0)
                .SelectMany(stop => this.ExpandFateSelection(stop.TargetFateId))
                .Distinct()
                .ToArray();
            TargetFateFallbackPolicy fallback = preset.StopConditions.Any(stop => stop.Kind != StopConditionKind.TargetFate)
                ? TargetFateFallbackPolicy.FarmOtherFates
                : preset.TargetFallback;
            return new PlanContext(
                this.ResolvePlanTerritory(preset.TerritoryId, index),
                (preset.TargetFateId is { } legacy && legacy != 0
                    ? this.ExpandFateSelection(legacy)
                    : Array.Empty<ushort>()).Concat(stopTargets).Distinct().ToArray(),
                fallback);
        }

        return new PlanContext(
            this.ResolvePlanTerritory(this.configuration.SingleMapTerritoryId, -1),
            this.configuration.Mode == AutomationMode.TargetFate
                ? this.configuration.TargetFateIds
                    .Where(id => id != 0)
                    .SelectMany(this.ExpandFateSelection)
                    .Distinct()
                    .ToArray()
                : Array.Empty<ushort>(),
            this.configuration.TargetFateFallback);
    }

    private uint ResolvePlanTerritory(uint configuredTerritory, int presetIndex)
    {
        if (configuredTerritory != 0)
        {
            this.ClearCurrentMapTerritory();
            return configuredTerritory;
        }

        // While stopped, this is only a display/validation query. Do not capture a map
        // before Start() has established a new run.
        if (!this.configuration.Enabled && this.state == AutomationState.Stopped)
            return this.clientState.TerritoryType;

        if (this.currentMapTerritoryMode != this.configuration.Mode
            || this.currentMapTerritoryPresetIndex != presetIndex)
        {
            this.currentMapTerritory = null;
            this.currentMapTerritoryMode = this.configuration.Mode;
            this.currentMapTerritoryPresetIndex = presetIndex;
        }

        if (this.currentMapTerritory is null && this.clientState.TerritoryType != 0)
        {
            this.currentMapTerritory = this.clientState.TerritoryType;
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"“当前地图”目标已锁定为 Territory={this.currentMapTerritory.Value}，本轮运行期间保持不变");
        }

        return this.currentMapTerritory ?? this.clientState.TerritoryType;
    }

    private void ClearCurrentMapTerritory()
    {
        this.currentMapTerritory = null;
        this.currentMapTerritoryMode = null;
        this.currentMapTerritoryPresetIndex = -1;
    }

    private IReadOnlyList<ushort> ExpandFateSelection(ushort fateId)
    {
        if (!this.staticFateCatalog.TryGet(fateId, out StaticFateCatalogEntry entry)
            || entry.CollectionIds.Count == 0)
        {
            return [fateId];
        }

        ushort[] members = entry.CollectionIds
            .SelectMany(this.staticFateCatalog.GetCollectionMembers)
            .Append(fateId)
            .Where(memberId => !this.staticFateCatalog.TryGet(memberId, out StaticFateCatalogEntry member)
                               || string.Equals(member.MapName, entry.MapName, StringComparison.Ordinal))
            .Distinct()
            .Order()
            .ToArray();
        return members.Length == 0 ? [fateId] : members;
    }

    private IReadOnlyList<MapPreset> GetPresetMaps() => this.configuration.GetActivePresetSequence().Maps;

    private void RefreshCandidateSnapshot()
    {
        DateTime now = DateTime.UtcNow;
        this.candidateSnapshot = this.fates.Snapshot
            .Select(f =>
            {
                bool eligible = this.IsEligible(f, now);
                float timeWeight = eligible && !IsPreparingFate(f)
                    ? CalculateTimeWeight(f.TimeRemaining, this.DistanceToPlayer(f.Position))
                    : 0;
                string reason = eligible
                    ? f.EligibilityReason
                    : !f.IsAutomationSupported ? f.EligibilityReason
                    : this.configuration.FateBlacklist.Contains(f.FateId) ? "黑名单中"
                    : IsPreparingFate(f) ? "准备中，倒计时尚未开始；需要 NPC/事件触发"
                    : !FateRepository.IsActive(f) ? $"状态 {f.State} 不可选"
                    : f.TimeRemaining <= 0 ? "已无剩余时间"
                    : this.skippedFates.ContainsKey(f.FateId) ? "恢复冷却中"
                    : "位置无效";
                return new FateCandidateSnapshot(
                    f.FateId,
                    f.Name,
                    f.State.ToString(),
                    f.Progress,
                    f.TimeRemaining,
                    this.DistanceToPlayer(f.Position),
                    eligible ? this.ScoreFate(f) : 0,
                    eligible ? CalculateDistanceWeight(this.DistanceToPlayer(f.Position)) : 0,
                    timeWeight,
                    f.HasBonus,
                    f.Rule,
                    f.IconId,
                    f.CombatKind,
                    eligible,
                    reason);
            })
            .OrderByDescending(f => f.IsEligible)
            .ThenByDescending(f => f.Score)
            .ToArray();
    }

    private static bool IsPreparingFate(FateSnapshot fate) =>
        fate.State == FateState.Preparing
        || string.Equals(fate.State.ToString(), "Preparation", StringComparison.OrdinalIgnoreCase)
        || fate.State.ToString().Contains("Prepar", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records every newly encountered FATE target identity once per plugin session. The object
    /// table only contains nearby loaded objects, so subsequent 500 ms scans naturally discover
    /// identities as the player travels without repeating every spawned instance.
    /// </summary>
    private void LogNewFateTargetData()
    {
        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        if (player is null)
            return;

        uint territoryId = this.clientState.TerritoryType;
        foreach (FateSnapshot fate in this.fates.Snapshot.Where(FateRepository.IsActive))
        {
            var contextKey = new LoggedFateContext(
                territoryId,
                fate.FateId,
                fate.ObjectiveNpc,
                fate.MotivationNpc);
            if (this.loggedFateContexts.Add(contextKey))
            {
                this.log.Information(
                    "Observed FATE {Id} '{Name}' state={State} objectiveNpc={ObjectiveNpc} motivationNpc={MotivationNpc} rule={Rule} icon={IconId} startTimeEpoch={StartTimeEpoch} duration={Duration}s timeRemaining={TimeRemaining}s timerStarted={TimerStarted}.",
                    fate.FateId,
                    fate.Name,
                    fate.State,
                    fate.ObjectiveNpc,
                    fate.MotivationNpc,
                    fate.Rule,
                    fate.IconId,
                    fate.StartTimeEpoch,
                    fate.Duration,
                    fate.TimeRemaining,
                    fate.HasStartedTimer);
                foreach (Vector3 marker in fate.MapMarkerPositions)
                    this.log.Information(
                        "Observed FATE {FateId} map marker position=({X:0.00},{Y:0.00},{Z:0.00}) distanceFromCenter={Distance:0.0}.",
                        fate.FateId,
                        marker.X,
                        marker.Y,
                        marker.Z,
                        Vector3.Distance(marker, fate.Position));
            }

            foreach (FateTargetDiagnostic target in this.targetSelector.DescribeTargets(
                         fate.FateId,
                         player.Position,
                         player))
            {
                var targetKey = new LoggedTargetIdentity(
                    territoryId,
                    fate.FateId,
                    target.NameId,
                    target.BaseId,
                    target.LayoutId,
                    target.NamePlateKind,
                    target.IsFriendly,
                    target.IsAttackable);
                if (!this.loggedTargetIdentities.Add(targetKey))
                    continue;

                this.log.Information(
                    "Observed FATE {FateId} target type '{Name}' nameId={NameId} baseId={BaseId} layoutId={LayoutId} hp={CurrentHp}/{MaxHp} friendly={IsFriendly} namePlateKind={NamePlateKind} attackable={IsAttackable} objectiveNpc={ObjectiveNpc}.",
                    fate.FateId,
                    target.Name,
                    target.NameId,
                    target.BaseId,
                    target.LayoutId,
                    target.CurrentHp,
                    target.MaxHp,
                    target.IsFriendly,
                    target.NamePlateKind,
                    target.IsAttackable,
                    fate.ObjectiveNpc);
            }
        }
    }

    private void RemoveExpiredCooldowns(DateTime now)
    {
        foreach (ushort id in this.skippedFates.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
            this.skippedFates.Remove(id);
        foreach (ulong id in this.skippedTargets.Where(x => x.Value <= now).Select(x => x.Key).ToArray())
            this.skippedTargets.Remove(id);
    }

    private void ResetCurrentActivity()
    {
        this.deathRecordedFateId = null;
        this.activeFateId = null;
        this.groundTravelToActiveFate = false;
        this.lastActiveFateSnapshot = null;
        this.activeFateMissingSince = DateTime.MinValue;
        this.preparationTalkStartedAt = DateTime.MinValue;
        this.preparationTalkCallbackAttempts = 0;
        this.preparationTextAdvanceEnabled = false;
        this.pullTargetId = null;
        this.killTargetId = null;
        this.lostPriorityTargetId = null;
        this.lostPriorityTargetKind = null;
        this.priorityDestroyTargetId = null;
        this.destroySession.Reset();
        this.bossTargetId = null;
        this.bossCleanupFateId = null;
        this.bossCleanupTargetId = null;
        this.bossCleanupNoTargetSince = DateTime.MinValue;
        this.bossCancelSyncStartedAt = DateTime.MinValue;
        this.bossEnvironmentLogAt = DateTime.MinValue;
        this.bossPreCombatCleanupChecked = false;
        this.ResetPullBatch();
        this.escortFollowLastRequestAt = DateTime.MinValue;
        this.escortFollowLastPosition = null;
        this.cleanupTargetId = null;
        this.pendingFateResult = null;
        this.ResetFailedTeleportCandidates();
        this.fateAetheryteTeleportPlan = null;
        this.aethernetTeleportIssuedByUs = false;
        this.aethernetDestinationPlaceNameId = 0;
        this.aethernetTeleportStartedAt = DateTime.MinValue;
        this.landing.StopDescending();
        this.aggroCount = 0;
        this.recoveryAttempts = 0;
        this.levelSyncAttempts = 0;
        this.outsideFateAreaSince = DateTime.MinValue;
        this.travelObservationDelayPending = false;
        this.presetConditionRecheckUntil = DateTime.MinValue;
        this.fateParticipationSince = DateTime.MinValue;
        this.fallbackLandingForParticipation = false;
        this.completedProgressObservedAt = DateTime.MinValue;
        this.activeFateParticipationObserved = false;
        this.terminalFateObservedAt = DateTime.MinValue;
        this.terminalFateState = null;
        this.completedCollectionObjects.Clear();
        this.collectionFateId = null;
        this.collectionItemId = 0;
        this.collectionInteractionCount = 0;
        this.collectionObjectId = null;
        this.collectionTurnInObjectId = null;
        this.collectionInteractionStartedAt = DateTime.MinValue;
        this.collectionTurnInStartedAt = DateTime.MinValue;
        this.collectionLastDiagnosticAt = DateTime.MinValue;
        this.collectionInteractionDiagnosticAt = DateTime.MinValue;
        this.collectionDialogueLastActionAt = DateTime.MinValue;
        this.collectionCastObserved = false;
        this.collectionTurnInUrgent = false;
        this.collectionTurnInPhase = false;
        this.collectionTurnInInteractionIssued = false;
        this.collectionTurnInDialogueSeen = false;
        this.collectionSprintIssued = false;
        this.collectionTurnInItemCountBefore = 0;
        this.collectionTurnInProgressBefore = 0;
        this.collectionCombatFallback = false;
        this.collectionInventoryBefore = new Dictionary<uint, int>();
        this.collectionObservedInventory = new Dictionary<uint, int>();
        this.textAdvance.Disable();
        this.preparationTextAdvanceEnabled = false;
        this.ResetCompanionCheckpoint();
        this.ResetCombatEscapeState();
        this.preemptSyncFateId = null;
        this.dutyResetPhase = AggroDutyResetPhase.None;
        this.ResetManualDutyRoundTripState();
        this.ResetNavigationProgress();
        this.selectingTargetId = null;
        this.targetSelectionMoving = false;
        this.targetSelectionFailedAt = DateTime.MinValue;
        this.preparingGroundArrivalPending = false;
        this.navigationOnlyRequested = false;
        this.navigationOnlyFateId = null;
        this.ClearTarget();
    }

    /// <summary>
    /// Stops the current shared travel session. State handlers must use this instead of stopping
    /// vnavmesh directly so a later request cannot inherit an old mode, destination, landing
    /// window, or watchdog sample.
    /// </summary>
    private void StopNavigationOperation() => this.ResetNavigationProgress();

    private void ResetNavigationProgress()
    {
        this.EndTravelSession();
    }

    private void ResetPullBatch()
    {
        this.pullBatchTargets.Clear();
        this.pullBatchEmptySince = DateTime.MinValue;
        this.pullTargetKind = PullTargetKind.Normal;
        this.ResetPullApproach();
    }

    private void ResetCombatEscapeState()
    {
        this.cleanupNoTargetSince = DateTime.MinValue;
        this.cleanupEscapeStartedAt = DateTime.MinValue;
        this.cleanupEscapeDestination = null;
        this.cleanupEscapeOrigin = null;
    }

    private void ResetManualDutyRoundTripState()
    {
        this.manualDutyRoundTrip = false;
        this.resumeAutomationAfterManualDuty = false;
    }

    private void AbandonActiveFate(string reason)
    {
        this.StopNavigationOperation();
        if (this.navigationOnlyRequested)
        {
            this.CompleteNavigationOnly(reason);
            return;
        }
        bool temporaryTargetLost = this.temporaryTargetActive
            && this.activeFateId == this.temporaryTargetFateId;
        bool needsCleanup = this.condition[ConditionFlag.InCombat]
            || (this.objectTable.LocalPlayer is { } player
                && this.targetSelector.FindTargetsAggroedOnPlayer(player).Count > 0);
        this.ResetCurrentActivity();
        if (temporaryTargetLost)
        {
            this.RestoreTemporaryTarget("临时目标 FATE 已从当前列表消失");
            if (!this.configuration.Enabled)
            {
                this.Transition(AutomationState.Stopped, "临时目标 FATE 已消失，已恢复原运行状态");
                return;
            }
        }
        if (needsCleanup)
            this.BeginCombatCleanup(CleanupContinuation.ScanFates, $"{reason}；先清理残余战斗");
        else
            this.Transition(AutomationState.ScanningFates, reason, DiagnosticSeverity.Warning);
    }

    private void CancelOwnedActions()
    {
        this.StopNavigationOperation();
        this.landing.StopDescending();
        this.dutyAggroReset.CancelOwnedQueue();
        if (this.state == AutomationState.ResettingAggroViaDuty && this.dutyAggroReset.IsInTitanDuty)
            this.dutyAggroReset.TryLeaveTitan();
        if (this.teleportIssuedByUs)
        {
            this.lifestream.Abort();
            this.teleportArrival.CancelRequest();
            this.teleportIssuedByUs = false;
            this.teleportBusyObserved = false;
        }
        this.fateAetheryteTeleportPlan = null;
        if (this.aethernetTeleportIssuedByUs)
        {
            this.lifestream.Abort();
            this.teleportArrival.CancelRequest();
            this.aethernetTeleportIssuedByUs = false;
            this.aethernetDestinationPlaceNameId = 0;
            this.aethernetTeleportStartedAt = DateTime.MinValue;
        }
    }

    private void ClearTarget()
    {
        try
        {
            this.targetManager.Target = null;
        }
        catch
        {
            // Target manager can become unavailable during logout/territory transitions.
        }
    }

    private bool IsBetweenAreas() =>
        this.condition[ConditionFlag.BetweenAreas] || this.condition[ConditionFlag.BetweenAreas51];

    private bool IsCombatEngaged()
    {
        if (this.condition[ConditionFlag.InCombat])
            return true;

        IPlayerCharacter? player = this.objectTable.LocalPlayer;
        return player is not null
            && this.targetSelector.FindTargetsAggroedOnPlayer(player).Count > 0;
    }

    private float DistanceToPlayer(Vector3 position) =>
        this.objectTable.LocalPlayer is { } player ? Vector3.Distance(player.Position, position) : float.MaxValue;

    private float HorizontalDistanceToPlayer(Vector3 position)
    {
        if (this.objectTable.LocalPlayer is not { } player)
            return float.MaxValue;

        float deltaX = player.Position.X - position.X;
        float deltaZ = player.Position.Z - position.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private static float HorizontalDistance(Vector3 left, Vector3 right)
    {
        float deltaX = left.X - right.X;
        float deltaZ = left.Z - right.Z;
        return MathF.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
    }

    private bool IsInsideFateCombatRange(IBattleNpc target, FateSnapshot fate)
    {
        float radius = Math.Max(6f, fate.Radius) + FateCombatRangePadding;
        return HorizontalDistance(target.Position, fate.Position) <= radius;
    }

    private void LogOutOfRangeFateTargets(
        DateTime now,
        FateSnapshot fate,
        IReadOnlyList<IBattleNpc> allEnemies,
        IReadOnlyList<IBattleNpc> inRangeEnemies)
    {
        if (allEnemies.Count == inRangeEnemies.Count || now < this.outOfRangeTargetDiagnosticAt)
            return;

        this.outOfRangeTargetDiagnosticAt = now.AddSeconds(2);
        HashSet<ulong> inRangeIds = inRangeEnemies.Select(enemy => enemy.GameObjectId).ToHashSet();
        string excluded = string.Join(
            " | ",
            allEnemies
                .Where(enemy => !inRangeIds.Contains(enemy.GameObjectId))
                .Take(12)
                .Select(enemy =>
                    $"{enemy.Name}({enemy.GameObjectId:X},dist={HorizontalDistance(enemy.Position, fate.Position):0.0}," +
                    $"radius={fate.Radius:0.0})"));
        this.AddDiagnostic(
            DiagnosticSeverity.Debug,
            $"FATE #{fate.FateId} 拉怪候选范围过滤：全部敌人={allEnemies.Count}，范围内={inRangeEnemies.Count}，" +
            $"允许范围={Math.Max(6f, fate.Radius) + FateCombatRangePadding:0.0}（FATE半径+{FateCombatRangePadding:0}）；范围外不纳入候选；{excluded}");
    }

    private static unsafe bool IsPlayerInFate(ushort fateId)
    {
        NativeFateManager* fateManager = NativeFateManager.Instance();
        return fateManager is not null
            && fateManager->CurrentFate is not null
            && fateManager->GetCurrentFateId() == fateId;
    }

    private void Transition(
        AutomationState next,
        string reason,
        DiagnosticSeverity severity = DiagnosticSeverity.Information,
        bool logTransition = true)
    {
        bool changed = this.state != next;
        if (!changed && this.statusReason == reason)
            return;
        this.state = next;
        this.statusReason = reason;
        if (!changed && !logTransition)
            return;

        this.stateEnteredAt = DateTime.UtcNow;
        if (logTransition)
            this.AddDiagnostic(severity, $"状态 → {next}: {reason}");
    }

    private void AddDiagnostic(DiagnosticSeverity severity, string message)
    {
        this.diagnostics.Enqueue(new DiagnosticEntry(DateTime.Now, severity, message));
        while (this.diagnostics.Count > 500)
            this.diagnostics.Dequeue();

        switch (severity)
        {
            case DiagnosticSeverity.Error:
                this.log.Error("{Message}", message);
                break;
            case DiagnosticSeverity.Warning:
                this.log.Warning("{Message}", message);
                break;
            case DiagnosticSeverity.Information:
                this.log.Information("{Message}", message);
                break;
            default:
                this.log.Debug("{Message}", message);
                break;
        }
    }

    private void PlaySoundAlert(string reason)
    {
        if (!this.configuration.EnableSoundAlerts)
            return;

        DateTime now = DateTime.UtcNow;
        if (now < this.soundAlertCooldownUntil)
            return;

        try
        {
            uint effectId = reason switch
            {
                var value when value.Contains("指定 FATE 出现", StringComparison.Ordinal) => this.configuration.SoundAlertTargetAppearedEffectId,
                var value when value.Contains("FATE 完成", StringComparison.Ordinal) => this.configuration.SoundAlertFateCompletedEffectId,
                var value when value.Contains("角色死亡", StringComparison.Ordinal) => this.configuration.SoundAlertDeathEffectId,
                var value when value.Contains("导航恢复耗尽", StringComparison.Ordinal) => this.configuration.SoundAlertNavigationSkippedEffectId,
                _ => this.configuration.SoundAlertEffectId,
            };
            if (effectId == 0)
                return;

            this.soundAlerts.Play(effectId);
            this.soundAlertCooldownUntil = now.AddSeconds(this.configuration.SoundAlertCooldownSeconds);
            this.AddDiagnostic(DiagnosticSeverity.Debug, $"已播放游戏内置音效（{reason}，音效 {effectId}）");
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "播放游戏内置音效失败：{Reason}", reason);
        }
    }

    private sealed record PlanContext(
        uint TerritoryId,
        IReadOnlyList<ushort> TargetFateIds,
        TargetFateFallbackPolicy Fallback);
}
