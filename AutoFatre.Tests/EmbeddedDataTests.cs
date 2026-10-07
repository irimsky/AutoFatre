namespace AutoFatre.Tests;

public sealed class EmbeddedDataTests
{
    [Fact]
    public async Task StaticFateCatalog_LoadsEmbeddedCatalog()
    {
        StaticFateCatalog catalog = new();

        await catalog.LoadAsync(CancellationToken.None);

        Assert.True(catalog.IsAvailable, catalog.LoadError);
        Assert.True(catalog.FateCount > 0);
        Assert.NotEmpty(catalog.MapNames);
        Assert.True(catalog.TryGet(840, out StaticFateCatalogEntry entry));
        Assert.Equal((ushort)840, entry.FateId);
    }

    [Fact]
    public async Task StaticFateCatalog_ColonizationTimberIsDestroyTargetNotOrdinaryEnemy()
    {
        StaticFateCatalog catalog = new();
        await catalog.LoadAsync(CancellationToken.None);

        Assert.True(catalog.IsAvailable, catalog.LoadError);
        Assert.True(catalog.TryGet(498, out StaticFateCatalogEntry entry));
        Assert.Equal(new[] { "违法砍伐的木材" }, entry.Targets.Destroy);
        Assert.DoesNotContain("违法砍伐的木材", entry.Targets.Defeat);
        Assert.Equal(new[] { "鸟人恐惧术士", "鸟人弹翼勇士" }, entry.Targets.Defeat);
        Assert.True(FateObjectiveIdentityCatalog.TryGetDestroyObjectives(498, out var identities));
        Assert.Contains(identities, identity => identity.Matches(1293, 1730));
    }

    [Fact]
    public async Task StaticFateTerritoryCatalog_LoadsEmbeddedTerritoryMapping()
    {
        StaticFateTerritoryCatalog catalog = new();

        await catalog.LoadAsync(CancellationToken.None);

        Assert.True(catalog.IsAvailable, catalog.LoadError);
        Assert.True(catalog.FateCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(catalog.GameVersion));
        Assert.False(string.IsNullOrWhiteSpace(catalog.SnapshotId));
    }
}
