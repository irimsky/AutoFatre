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
