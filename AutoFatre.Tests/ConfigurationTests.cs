using Newtonsoft.Json;

namespace AutoFatre.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void Normalize_ClampsValuesAndRemovesInvalidIds()
    {
        AutoFatreConfiguration configuration = new()
        {
            SingleMapAetheryteId = 123,
            MaxAggroCount = 99,
            MaxRecoveryAttempts = -1,
            AggroConfirmationTimeoutSeconds = 999,
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
        Assert.Equal(4, configuration.MaxAggroCount);
        Assert.Equal(0, configuration.MaxRecoveryAttempts);
        Assert.Equal(60, configuration.AggroConfirmationTimeoutSeconds);
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
        Assert.Equal(17, configuration.Version);
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
}
