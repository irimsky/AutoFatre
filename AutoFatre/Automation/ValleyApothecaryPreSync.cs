using System.Numerics;

namespace AutoFatre;

/// <summary>Fixed ground staging point after pre-combat cleanup and before melee sync.</summary>
public static class ValleyApothecaryPreSync
{
    public const ushort FateId = 616;
    public const uint TerritoryId = 152;
    public const uint MapId = 5;
    public const float GroundSearchRadius = 3f;
    public const float GroundSearchHeight = 100f;

    public static bool AppliesTo(ushort fateId, uint territoryId, byte role) =>
        fateId == FateId && territoryId == TerritoryId && role is 1 or 2;

    public static Vector3 GetWaypoint(uint sizeFactor, int offsetX, int offsetY)
    {
        if (sizeFactor == 0)
            throw new ArgumentOutOfRangeException(nameof(sizeFactor));
        // Inverse of current Dalamud MapUtil.ConvertWorldCoordXZToMapCoord.
        // Map Y is world Z; vnavmesh resolves the actual ground height separately.
        return new((32f - 1f - 2048f / sizeFactor) / 0.02f - offsetX,
            0, (14f - 1f - 2048f / sizeFactor) / 0.02f - offsetY);
    }

    public static bool IsResolvedPointValid(Vector3 waypoint, Vector3 resolved) =>
        float.IsFinite(resolved.X) && float.IsFinite(resolved.Y) && float.IsFinite(resolved.Z)
        && Vector2.DistanceSquared(new(waypoint.X, waypoint.Z), new(resolved.X, resolved.Z)) <= GroundSearchRadius * GroundSearchRadius;

    public static bool TryResolveGround(Vector3 waypoint, float playerHeight,
        Func<Vector3, float, float, Vector3?> findNearestReachable, out Vector3? destination)
    {
        // Unlike moveflag's top-down highest-floor query, prefer the walkable mesh nearest
        // to the player's height at the requested X/Z, not a higher ledge beside the point.
        destination = findNearestReachable(new(waypoint.X, playerHeight, waypoint.Z), GroundSearchRadius, GroundSearchHeight);
        return destination is { } ground && IsResolvedPointValid(waypoint, ground);
    }

    public static bool HasArrived(Vector3 player, Vector3 destination) =>
        Vector3.DistanceSquared(player, destination) <= 9f;
}
