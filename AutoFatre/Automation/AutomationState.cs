namespace AutoFatre;

public enum AutomationState
{
    Stopped,
    Paused,
    WaitingForLogin,
    ValidatingPlan,
    WaitingForDependency,
    Teleporting,
    WaitingForTerritory,
    CheckingCompanions,
    ScanningFates,
    OpeningPreparingFate,
    MountingForTravel,
    NavigatingToFate,
    LandingForFate,
    DismountingForFate,
    WaitingForLevelSync,
    CollectingFateItems,
    TurningInCollectionFate,
    PullingTargets,
    Fighting,
    BossFighting,
    CleaningBossNonFate,
    ReturningToFateArea,
    CleaningUpCombat,
    ResettingAggroViaDuty,
    RecoveringNavigation,
    DeadWaitingForRaise,
    DeadReturning,
    Recovering,
    Faulted,
}

public enum DiagnosticSeverity
{
    Debug,
    Information,
    Warning,
    Error,
}

public sealed record DiagnosticEntry(DateTime Timestamp, DiagnosticSeverity Severity, string Message);
