using System.Numerics;

namespace AutoFatre.Tests;

public sealed class FatePolicyTests
{
    [Theory]
    [InlineData(FateSnapshot.DefeatEnemiesIconId, FateCombatStrategyKind.General, FateProtectionMode.None)]
    [InlineData(FateSnapshot.DefeatBossIconId, FateCombatStrategyKind.Boss, FateProtectionMode.None)]
    [InlineData(FateSnapshot.CollectItemsIconId, FateCombatStrategyKind.Collection, FateProtectionMode.None)]
    [InlineData(FateSnapshot.DefenseIconId, FateCombatStrategyKind.General, FateProtectionMode.Static)]
    [InlineData(FateSnapshot.EscortIconId, FateCombatStrategyKind.General, FateProtectionMode.Moving)]
    public void FateCombatProfile_ClassifiesSupportedIcons(
        uint iconId,
        FateCombatStrategyKind expectedStrategy,
        FateProtectionMode expectedProtection)
    {
        FateCombatProfile profile = FateCombatProfile.From(rule: 0, iconId);

        Assert.True(profile.IsSupported);
        Assert.Equal(expectedStrategy, profile.Strategy);
        Assert.Equal(expectedProtection, profile.ProtectionMode);
    }

    [Fact]
    public void FateCombatProfile_RejectsUnknownIcon()
    {
        FateCombatProfile profile = FateCombatProfile.From(rule: 0, iconId: 99999);

        Assert.False(profile.IsSupported);
        Assert.Equal(FateCombatStrategyKind.Unsupported, profile.Strategy);
        Assert.Equal("暂未支持：其他", profile.SupportDescription);
    }

    [Theory]
    [InlineData(1, FateSnapshot.DefeatEnemiesIconId, "消灭普通怪物")]
    [InlineData(2, FateSnapshot.CollectItemsIconId, "收集物品")]
    [InlineData(4, 99999, "防御")]
    [InlineData(0, 99999, "其他")]
    public void FateSnapshot_GetTypeNameUsesIconBeforeRule(
        byte rule,
        uint iconId,
        string expectedName)
    {
        Assert.Equal(expectedName, FateSnapshot.GetTypeName(rule, iconId));
    }

    [Fact]
    public void FateRepository_PositionAndStatePoliciesAreStable()
    {
        Assert.True(FateRepository.HasValidPosition(new Vector3(1, 2, 3)));
        Assert.False(FateRepository.HasValidPosition(Vector3.Zero));
        Assert.False(FateRepository.HasValidPosition(new Vector3(float.NaN, 0, 0)));

        Assert.True(FateRepository.IsActive(CreateSnapshot(Dalamud.Game.ClientState.Fates.FateState.Running)));
        Assert.False(FateRepository.IsActive(CreateSnapshot(Dalamud.Game.ClientState.Fates.FateState.Ended)));
    }

    private static FateSnapshot CreateSnapshot(Dalamud.Game.ClientState.Fates.FateState state) => new(
        FateId: 840,
        Name: "test",
        State: state,
        Progress: 0,
        TimeRemaining: 60,
        Level: 1,
        MaxLevel: 1,
        Position: new Vector3(1, 0, 1),
        Radius: 10,
        HasBonus: false,
        Rule: 0,
        IconId: FateSnapshot.DefeatEnemiesIconId,
        EventItem: 0,
        ObjectiveNpc: 0,
        MotivationNpc: 0,
        MapMarkerPositions: [],
        Objectives: [],
        EligibilityReason: string.Empty,
        StartTimeEpoch: 1,
        Duration: 60);
}
