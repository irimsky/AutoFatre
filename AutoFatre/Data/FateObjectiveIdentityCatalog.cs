namespace AutoFatre;

/// <summary>Verified client identities for FATE destruction objectives.</summary>
public readonly record struct FateObjectiveIdentity(uint BaseId, uint NameId);

/// <summary>
/// Keeps manually verified objective identities separate from the Wiki enrichment catalog.
/// An entry matches when either its BaseId or its NameId matches the live object. This supports
/// client variants where one of the two fields changes while the other remains stable.
/// Add an entry only after confirming it with an in-game diagnostic scan.
/// </summary>
public static class FateObjectiveIdentityCatalog
{
    private static readonly IReadOnlyDictionary<ushort, FateObjectiveIdentity[]> DestroyObjectives =
        new Dictionary<ushort, FateObjectiveIdentity[]>
        {
            // 冠恐鸟窝破坏命令：冠恐鸟窝
            [840] =
            [
                new FateObjectiveIdentity(BaseId: 5027, NameId: 4010),
            ],
        };

    public static bool TryGetDestroyObjectives(
        ushort fateId,
        out IReadOnlyList<FateObjectiveIdentity> identities)
    {
        if (DestroyObjectives.TryGetValue(fateId, out FateObjectiveIdentity[]? found))
        {
            identities = found;
            return true;
        }

        identities = [];
        return false;
    }
}
