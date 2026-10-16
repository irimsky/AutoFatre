using System.Numerics;
using FieldNavigation;
namespace AutoFatre;
internal sealed class FateLandingPlanner
{
    private readonly AreaLandingPlanner shared;
    public FateLandingPlanner(VnavmeshIpc navigation) => shared = new(navigation, navigation.FindPath);
    public Vector3? Floor => shared.Floor;
    public bool IsActive => shared.IsActive;
    public bool Exhausted => shared.Exhausted;
    public string Status => shared.Status;
    public Vector3? Update(DateTime now, FateSnapshot fate, Vector3 origin, Func<Vector3, bool> flyToFloor, IEnumerable<Vector3> targets) =>
        shared.Update(now, fate.FateId, fate.Position, fate.Radius, origin, flyToFloor, targets);
    public static Vector3 Approach(Vector3 floor, bool fly) => AreaLandingPlanner.Approach(floor, fly);
    public void RejectDestination(DateTime now) => shared.RejectDestination(now);
    public void CancelQuery(bool retry = false) => shared.CancelQuery(retry);
    public void Reset() => shared.Reset();
}
