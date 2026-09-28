namespace AutoFatre;

/// <summary>Verified client identities for FATE destruction objectives.</summary>
public readonly record struct FateObjectiveIdentity(uint BaseId, uint NameId)
{
    public bool Matches(uint baseId, uint nameId) =>
        (BaseId != 0 && BaseId == baseId)
        || (NameId != 0 && NameId == nameId);
}

/// <summary>
/// Keeps manually verified objective identities separate from the Wiki enrichment catalog.
/// An entry matches when a non-zero BaseId or NameId matches the live object. A zero field means
/// that the corresponding identity is not known and must not match every object.
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

            // 山贼的武器箱
            [1184] =
            [
                new FateObjectiveIdentity(BaseId: 0, NameId: 6437),
            ],

            // 地灵族的采矿工具（国服客户端名；国际服名为采掘工具）
            [587] =
            [
                new FateObjectiveIdentity(BaseId: 0, NameId: 1722),
            ],

            // 以太收集器
            [877] =
            [
                new FateObjectiveIdentity(BaseId: 0, NameId: 3781),
            ],
            [878] =
            [
                new FateObjectiveIdentity(BaseId: 0, NameId: 3781),
            ],
            [879] =
            [
                new FateObjectiveIdentity(BaseId: 0, NameId: 3781),
            ],

            // 起风珠在当前客户端数据中有两个 BNpcName 行。
            [633] =
            [
                new FateObjectiveIdentity(BaseId: 0, NameId: 1860),
                new FateObjectiveIdentity(BaseId: 0, NameId: 5267),
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
