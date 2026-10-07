using System.Numerics;

namespace AutoFatre;

/// <summary>Area correction policies shared by every FATE combat strategy.</summary>
public static class FateAreaReturnSession
{
    public static bool ShouldMonitor(AutomationState state, AutomationState companionResumeState, bool inCombat) =>
        state is AutomationState.PullingTargets or AutomationState.Fighting or AutomationState.BossFighting
            or AutomationState.CleaningBossNonFate
        || state == AutomationState.CheckingCompanions
            && (inCombat || companionResumeState is AutomationState.PullingTargets
                or AutomationState.Fighting or AutomationState.BossFighting)
        || inCombat && (state is AutomationState.CollectingFateItems
            or AutomationState.TurningInCollectionFate or AutomationState.WaitingForLevelSync);

    public static float GetBoundaryDistance(float radius) => Math.Max(0f, radius - 3f);

    public static bool IsNearBoundary(Vector3 position, Vector3 center, float radius)
    {
        float boundary = GetBoundaryDistance(radius);
        float distanceSquared = HorizontalDistanceSquared(position, center);
        // Very small events have no three-yalm inset; keep the exact center safe.
        return float.IsFinite(radius) && radius > 0 && distanceSquared > 0
            && distanceSquared >= boundary * boundary;
    }

    public static bool IsAtDestination(Vector3 position, Vector3 destination, Vector3 center, float radius)
    {
        float tolerance = Math.Min(3f, GetBoundaryDistance(radius) * 0.5f);
        return float.IsFinite(radius) && radius > 0 && !IsNearBoundary(position, center, radius)
            && HorizontalDistanceSquared(position, center) <= 3f * 3f
            && Vector3.DistanceSquared(position, destination) <= tolerance * tolerance;
    }

    public static bool IsInsidePullRange(Vector3 position, Vector3 center, float radius) =>
        radius > 0 && HorizontalDistanceSquared(position, center) <= (radius + 5f) * (radius + 5f);

    private static float HorizontalDistanceSquared(Vector3 left, Vector3 right) =>
        (left.X - right.X) * (left.X - right.X) + (left.Z - right.Z) * (left.Z - right.Z);
}
