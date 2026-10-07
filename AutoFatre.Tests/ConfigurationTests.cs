using Newtonsoft.Json;

namespace AutoFatre.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void NewConfiguration_DefaultsToSilentAlertsAndChocoboCompanion()
    {
        AutoFatreConfiguration configuration = new();

        Assert.Equal(4, configuration.TankMaxAggroCount);
        Assert.Equal(3, configuration.DpsMaxAggroCount);
        Assert.Equal(4, configuration.HealerMaxAggroCount);
        Assert.True(configuration.AutoSummonChocoboCompanion);
        Assert.False(configuration.EnableSoundAlerts);
        Assert.Equal(0u, configuration.SoundAlertEffectId);
        Assert.Equal(0u, configuration.SoundAlertTargetAppearedEffectId);
        Assert.Equal(0u, configuration.SoundAlertFateCompletedEffectId);
        Assert.Equal(0u, configuration.SoundAlertDeathEffectId);
        Assert.Equal(0u, configuration.SoundAlertNavigationSkippedEffectId);
        Assert.Equal(0u, configuration.SoundAlertPlayerEnteredEffectId);
        Assert.Equal(0u, configuration.SoundAlertGemstonesFullEffectId);
        Assert.False(configuration.PrioritizeLostGirlAndLostOne);
        Assert.Equal(180, configuration.LostGirlRemainingTimeThresholdSeconds);
        Assert.Equal(240, configuration.LostOneRemainingTimeThresholdSeconds);
    }

    [Fact]
    public void Normalize_ClampsValuesAndRemovesInvalidIds()
    {
        AutoFatreConfiguration configuration = new()
        {
            SingleMapAetheryteId = 123,
            TankMaxAggroCount = 99,
            DpsMaxAggroCount = -1,
            HealerMaxAggroCount = 7,
            MaxRecoveryAttempts = -1,
            AggroConfirmationTimeoutSeconds = 999,
            LostGirlRemainingTimeThresholdSeconds = -1,
            LostOneRemainingTimeThresholdSeconds = 999,
            FateBlacklist = [0, 840, 840, 398],
            RecentTargetFateIds = [0, 852, 852, 1949],
            TargetFateIds = [0, 840, 840],
            TargetFateId = 999,
            PresetSequences =
            [
                new MapPresetSequence
                {
                    Maps =
                    [
                        new MapPreset
                        {
                            AetheryteId = 456,
                            StopConditions = [new StopCondition()],
                        },
                    ],
                },
            ],
            ActivePresetSequenceIndex = 99,
        };

        configuration.Normalize();

        Assert.Equal(0u, configuration.SingleMapAetheryteId);
        Assert.Equal(6, configuration.TankMaxAggroCount);
        Assert.Equal(1, configuration.DpsMaxAggroCount);
        Assert.Equal(6, configuration.HealerMaxAggroCount);
        Assert.Equal(0, configuration.MaxRecoveryAttempts);
        Assert.Equal(60, configuration.AggroConfirmationTimeoutSeconds);
        Assert.Equal(0, configuration.LostGirlRemainingTimeThresholdSeconds);
        Assert.Equal(600, configuration.LostOneRemainingTimeThresholdSeconds);
        Assert.Equal(new ushort[] { 398, 840 }, configuration.FateBlacklist);
        Assert.Equal(new ushort[] { 852, 1949 }, configuration.RecentTargetFateIds);
        Assert.Equal(new ushort[] { 840 }, configuration.TargetFateIds);
        Assert.Equal((ushort)840, configuration.TargetFateId);
        Assert.Equal(0u, configuration.PresetSequence.Maps[0].AetheryteId);
        Assert.Same(configuration.PresetSequences[0], configuration.PresetSequence);
    }

    [Fact]
    public void Migrate_CopiesLegacyTargetFateAndMapStopCondition()
    {
        AutoFatreConfiguration configuration = new()
        {
            Version = 5,
            TargetFateId = 852,
            Presets =
            [
                new MapPreset
                {
                    TargetFateId = 840,
                    StopCondition = new StopCondition { Kind = StopConditionKind.FateCount, FateCount = 3 },
                },
            ],
        };

        configuration.Migrate();

        Assert.Equal(new ushort[] { 852 }, configuration.TargetFateIds);
        Assert.Single(configuration.PresetSequence.Maps);
        MapPreset migratedMap = configuration.PresetSequence.Maps[0];
        Assert.Contains(migratedMap.StopConditions, condition => condition.Kind == StopConditionKind.FateCount);
        Assert.Contains(migratedMap.StopConditions, condition => condition.Kind == StopConditionKind.TargetFate && condition.TargetFateId == 840);
        Assert.Null(migratedMap.StopCondition);
        Assert.Equal(20, configuration.Version);
    }

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    [InlineData(4, 5)]
    [InlineData(0, 3)]
    [InlineData(255, 3)]
    public void RolePullLimits_SelectCorrectSettingIncludingBothDpsRoles(byte role, int expected)
    {
        AutoFatreConfiguration configuration = new()
        {
            TankMaxAggroCount = 6, DpsMaxAggroCount = 3, HealerMaxAggroCount = 5,
        };
        Assert.Equal(expected, configuration.GetMaxAggroCount(role));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(6, 6)]
    [InlineData(7, 6)]
    public void Normalize_RolePullLimitsAllowOneThroughSix(int value, int expected)
    {
        AutoFatreConfiguration configuration = new()
        {
            TankMaxAggroCount = value, DpsMaxAggroCount = value, HealerMaxAggroCount = value,
        };
        configuration.Normalize();
        Assert.Equal(expected, configuration.TankMaxAggroCount);
        Assert.Equal(expected, configuration.DpsMaxAggroCount);
        Assert.Equal(expected, configuration.HealerMaxAggroCount);
    }

    [Fact]
    public void Migrate_OldDefaultSharedLimitAdoptsRoleDefaults()
    {
        AutoFatreConfiguration configuration = JsonConvert.DeserializeObject<AutoFatreConfiguration>(
            "{\"Version\":18,\"MaxAggroCount\":4}")!;
        configuration.Migrate();
        configuration.Normalize();
        Assert.Equal(20, configuration.Version);
        Assert.Equal(4, configuration.TankMaxAggroCount);
        Assert.Equal(3, configuration.DpsMaxAggroCount);
        Assert.Equal(4, configuration.HealerMaxAggroCount);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(-1, 1)]
    public void Migrate_LowerLegacyLimitIsPreservedForAllRoles(int legacy, int expected)
    {
        AutoFatreConfiguration configuration = JsonConvert.DeserializeObject<AutoFatreConfiguration>(
            $"{{\"Version\":18,\"MaxAggroCount\":{legacy}}}")!;
        configuration.Migrate();
        configuration.Normalize();
        Assert.Equal(expected, configuration.TankMaxAggroCount);
        Assert.Equal(expected, configuration.DpsMaxAggroCount);
        Assert.Equal(expected, configuration.HealerMaxAggroCount);
        configuration.DpsMaxAggroCount = 6;
        configuration.Migrate();
        Assert.Equal(6, configuration.DpsMaxAggroCount);
    }

    [Fact]
    public void Migrate_OldConfigurationWithoutSharedLimitUsesRoleDefaults()
    {
        AutoFatreConfiguration configuration = JsonConvert.DeserializeObject<AutoFatreConfiguration>(
            "{\"Version\":18}")!;
        configuration.Migrate();
        Assert.Equal(4, configuration.TankMaxAggroCount);
        Assert.Equal(3, configuration.DpsMaxAggroCount);
        Assert.Equal(4, configuration.HealerMaxAggroCount);
    }

    [Fact]
    public void RolePullLimits_RoundTripIndependentlyAndDoNotSerializeLegacyLimit()
    {
        AutoFatreConfiguration configuration = new()
        {
            TankMaxAggroCount = 6, DpsMaxAggroCount = 2, HealerMaxAggroCount = 5,
        };
        string json = JsonConvert.SerializeObject(configuration);
        Assert.False(Newtonsoft.Json.Linq.JObject.Parse(json).ContainsKey("MaxAggroCount"));
        AutoFatreConfiguration restored = JsonConvert.DeserializeObject<AutoFatreConfiguration>(json)!;
        restored.Migrate();
        restored.Normalize();
        Assert.Equal(6, restored.TankMaxAggroCount);
        Assert.Equal(2, restored.DpsMaxAggroCount);
        Assert.Equal(5, restored.HealerMaxAggroCount);
    }

    [Fact]
    public void Migrate_CurrentVersionDoesNotApplyObsoleteSharedLimit()
    {
        AutoFatreConfiguration configuration = JsonConvert.DeserializeObject<AutoFatreConfiguration>(
            "{\"Version\":19,\"MaxAggroCount\":1,\"TankMaxAggroCount\":6,\"DpsMaxAggroCount\":2,\"HealerMaxAggroCount\":5}")!;
        configuration.Migrate();
        Assert.Equal(6, configuration.TankMaxAggroCount);
        Assert.Equal(2, configuration.DpsMaxAggroCount);
        Assert.Equal(5, configuration.HealerMaxAggroCount);
    }

    [Fact]
    public void Enabled_IsRuntimeOnlyDuringNewtonsoftSerialization()
    {
        AutoFatreConfiguration configuration = new() { Enabled = true };

        string json = JsonConvert.SerializeObject(configuration);
        AutoFatreConfiguration restored = JsonConvert.DeserializeObject<AutoFatreConfiguration>(json)!;

        Assert.DoesNotContain("Enabled", json, StringComparison.Ordinal);
        Assert.False(restored.Enabled);
    }

    [Fact]
    public void Migrate_PreviousVersionPreservesExistingAlertsAndStopConditions()
    {
        AutoFatreConfiguration configuration = JsonConvert.DeserializeObject<AutoFatreConfiguration>(
            """
            {"Version":19,"EnableSoundAlerts":true,"SoundAlertFateCompletedEffectId":7,
             "PresetSequence":{"Maps":[{"StopConditions":[{"Kind":0,"FateCount":4},{"Kind":1,"ItemId":100,"ItemCount":8}]}]}}
            """)!;
        configuration.Migrate();
        configuration.Normalize();
        Assert.Equal(20, configuration.Version);
        Assert.True(configuration.EnableSoundAlerts);
        Assert.Equal(7u, configuration.SoundAlertFateCompletedEffectId);
        Assert.Equal(0u, configuration.SoundAlertPlayerEnteredEffectId);
        Assert.Equal(0u, configuration.SoundAlertGemstonesFullEffectId);
        Assert.Equal(2, configuration.PresetSequence.Maps[0].StopConditions.Count);
        Assert.Equal(4, configuration.PresetSequence.Maps[0].StopConditions[0].FateCount);
        Assert.Equal(8, configuration.PresetSequence.Maps[0].StopConditions[1].ItemCount);
    }

    [Fact]
    public void NewAlertsAndNoFatesTimeoutRoundTrip()
    {
        AutoFatreConfiguration configuration = new()
        {
            SoundAlertPlayerEnteredEffectId = 4,
            SoundAlertGemstonesFullEffectId = 12,
            PresetSequence = new() { Maps = [new() { StopConditions = [new() { Kind = StopConditionKind.NoFates, NoFateSeconds = 9 }] }] },
        };
        string json = JsonConvert.SerializeObject(configuration);
        Assert.DoesNotContain("RequiredProgress", json, StringComparison.Ordinal);
        AutoFatreConfiguration restored = JsonConvert.DeserializeObject<AutoFatreConfiguration>(json)!;
        restored.Migrate();
        restored.Normalize();
        Assert.Equal(4u, restored.SoundAlertPlayerEnteredEffectId);
        Assert.Equal(12u, restored.SoundAlertGemstonesFullEffectId);
        Assert.Equal(9, Assert.Single(restored.PresetSequence.Maps[0].StopConditions).RequiredProgress);
    }

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(3, 3)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(20, 10)]
    public void Normalize_NoFatesTimeoutIsExclusiveAndClamped(int seconds, int expected)
    {
        List<StopCondition> original = [new() { Kind = StopConditionKind.FateCount },
            new() { Kind = StopConditionKind.NoFates, NoFateSeconds = seconds },
            new() { Kind = StopConditionKind.ItemCount }, new() { Kind = StopConditionKind.NoFates }];
        MapPreset map = new() { StopConditions = original };
        AutoFatreConfiguration configuration = new() { PresetSequence = new() { Maps = [map] } };
        configuration.Normalize();
        StopCondition timeout = Assert.Single(map.StopConditions);
        Assert.Equal(StopConditionKind.NoFates, timeout.Kind);
        Assert.Equal(expected, timeout.NoFateSeconds);
        Assert.NotSame(original, map.StopConditions);
        Assert.Equal(4, original.Count);
    }

    [Fact]
    public void SelectingNoFatesClearsOtherConditionsAndCanBeDisabled()
    {
        MapPreset map = new() { StopConditions = [new(), new() { Kind = StopConditionKind.TargetFate }] };
        map.SetNoFatesStopCondition(true);
        Assert.Equal(5, Assert.Single(map.StopConditions).NoFateSeconds);
        map.SetNoFatesStopCondition(false);
        Assert.Empty(map.StopConditions);
        map.StopConditions.Add(new() { Kind = StopConditionKind.ItemCount });
        map.SetNoFatesStopCondition(false);
        Assert.Equal(StopConditionKind.ItemCount, Assert.Single(map.StopConditions).Kind);
    }

    [Fact]
    public void Normalize_ClampsNewSoundEffects()
    {
        AutoFatreConfiguration configuration = new()
        {
            SoundAlertPlayerEnteredEffectId = 99, SoundAlertGemstonesFullEffectId = uint.MaxValue,
        };
        configuration.Normalize();
        Assert.Equal(16u, configuration.SoundAlertPlayerEnteredEffectId);
        Assert.Equal(16u, configuration.SoundAlertGemstonesFullEffectId);
    }
}
