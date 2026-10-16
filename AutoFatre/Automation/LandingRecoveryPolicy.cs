using System.Numerics;
namespace AutoFatre;
public static class LandingRecoveryPolicy
{
    public static IEnumerable<Vector3> GetCandidates(Vector3 position, Vector3 center, float radius, int attempt) =>
        FieldNavigation.LandingRecoveryPolicy.GetCandidates(position, center, radius, attempt);
    public static bool IsUsable(Vector3 point, Vector3 origin, Vector3 center, float radius) =>
        FieldNavigation.LandingRecoveryPolicy.IsUsable(point, origin, center, radius);
    public static float HorizontalDistance(Vector3 a, Vector3 b) => FieldNavigation.TravelGeometry.HorizontalDistance(a, b);
}
