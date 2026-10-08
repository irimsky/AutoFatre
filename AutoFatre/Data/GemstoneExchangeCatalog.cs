using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace AutoFatre;

public sealed record GemstoneVendor(uint NpcId, string Name, uint TerritoryId, string Zone, Vector3 Position, uint Expansion);
public sealed record GemstoneProduct(uint ItemId, string Name, uint Icon, uint Cost, uint ReceiveCount,
    uint ShopId, string ShopName, uint StackSize, bool Unique, uint ActionId, uint UnlockId,
    IReadOnlyList<GemstoneVendor> Vendors)
{
    public bool SupportsUsedCheck => this.UnlockId != 0
        && this.ActionId is 853 or 1322 or 2633 or 25183 or 29459 or 3357 or 20086;
}

/// <summary>Immutable client-data snapshot; parsing sheets and LGBs never runs on Framework.Update.</summary>
public sealed class GemstoneExchangeCatalog(IDataManager dataManager, IPluginLog log)
{
    private static readonly Type[] ScriptTypes = [typeof(FateShop), typeof(GilShop), typeof(SpecialShop),
        typeof(GCShop), typeof(FccShop), typeof(InclusionShop), typeof(CollectablesShop),
        typeof(TopicSelect), typeof(PreHandler), typeof(CustomTalk)];
    private static readonly int ScriptTypeHash = RowRef.CreateTypeHash(ScriptTypes);
    // Shadowbringers event scripts do not expose these shop links in ENpcBase/FateShop.
    // NPC/shop identities from GatherBuddy's AllaganLib fallback, resolved against current client data below.
    private static readonly (uint Npc, uint Shop)[] ScriptShops = [(1027998, 1769957), (1027538, 1769958),
        (1027385, 1769959), (1027497, 1769960), (1027892, 1769961), (1027665, 1769962),
        (1027709, 1769963), (1027766, 1769964)];
    public const uint CurrencyItemId = 26807;
    private IReadOnlyList<GemstoneProduct> products = [];
    public IReadOnlyList<GemstoneProduct> Products => this.products;
    public bool IsLoaded { get; private set; }
    public string? Error { get; private set; }

    public Task LoadAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            var shops = dataManager.GetExcelSheet<SpecialShop>();
            var items = dataManager.GetExcelSheet<Item>();
            var fateShops = dataManager.GetExcelSheet<FateShop>();
            Dictionary<uint, HashSet<uint>> sellers = [];
            void Add(uint shop, uint npc)
            {
                if (shop == 0) return;
                if (!sellers.TryGetValue(shop, out var set)) sellers[shop] = set = [];
                set.Add(npc);
            }
            void Visit(uint npc, RowRef entry, HashSet<uint> visited)
            {
                if (entry.RowId == 0 || !visited.Add(entry.RowId)) return;
                if (entry.Is<SpecialShop>()) Add(entry.RowId, npc);
                else if ((entry.Is<FateShop>() || entry.RowId == npc) && fateShops.TryGetRow(entry.RowId, out var fate))
                    foreach (var shop in fate.SpecialShop) Add(shop.RowId, npc);
                else if (entry.Is<PreHandler>() && dataManager.GetExcelSheet<PreHandler>().TryGetRow(entry.RowId, out var pre))
                    Visit(npc, pre.Target, visited);
                else if (entry.Is<TopicSelect>() && dataManager.GetExcelSheet<TopicSelect>().TryGetRow(entry.RowId, out var topic))
                    foreach (var child in topic.Shop) Visit(npc, child, visited);
                else if (entry.Is<CustomTalk>() && dataManager.GetExcelSheet<CustomTalk>().TryGetRow(entry.RowId, out var talk))
                {
                    Visit(npc, talk.SpecialLinks, visited);
                    foreach (var script in talk.Script)
                    {
                        if (script.ScriptArg == 0) continue;
#pragma warning disable CA1857 // Hash is computed once for the fixed sheet set, independent of client assembly versions.
                        var child = RowRef.GetFirstValidRowOrUntyped(dataManager.Excel, script.ScriptArg,
                            ScriptTypes, ScriptTypeHash, dataManager.GameData.Options.DefaultExcelLanguage);
#pragma warning restore CA1857
                        Visit(npc, child, visited);
                    }
                }
            }
            foreach (var (npc, shop) in ScriptShops)
                if (shops.TryGetRow(shop, out _) && dataManager.GetExcelSheet<ENpcBase>().TryGetRow(npc, out _)) Add(shop, npc);
            foreach (var npc in dataManager.GetExcelSheet<ENpcBase>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (fateShops.TryGetRow(npc.RowId, out var fate))
                    foreach (var shop in fate.SpecialShop) Add(shop.RowId, npc.RowId);
                foreach (var entry in npc.ENpcData) Visit(npc.RowId, entry, []);
            }

            List<(SpecialShop Shop, uint Item, uint Cost, uint Count)> listings = [];
            HashSet<uint> neededNpcs = [];
            foreach (var shop in shops)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!sellers.TryGetValue(shop.RowId, out var npcs)) continue;
                foreach (var row in shop.Item)
                {
                    var costs = row.ItemCosts.Where(cost => cost.ItemCost.RowId != 0 && cost.CurrencyCost > 0).ToArray();
                    var receive = row.ReceiveItems.Where(value => value.Item.RowId != 0 && value.ReceiveCount > 0).ToArray();
                    // Only direct, single-currency / single-output exchanges belong to this feature.
                    if (costs.Length != 1 || receive.Length != 1 || receive[0].ReceiveHq) continue;
                    uint currency = costs[0].ItemCost.RowId;
                    if (shop.UseCurrencyType == 16 && currency == 23) currency = CurrencyItemId;
                    if (currency != CurrencyItemId) continue;
                    listings.Add((shop, receive[0].Item.RowId, costs[0].CurrencyCost, receive[0].ReceiveCount));
                    neededNpcs.UnionWith(npcs);
                }
            }

            Dictionary<uint, List<GemstoneVendor>> locations = [];
            var names = dataManager.GetExcelSheet<ENpcResident>();
            foreach (var territory in dataManager.GetExcelSheet<TerritoryType>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!territory.IsInUse || territory.ContentFinderCondition.RowId != 0 || territory.QuestBattle.RowId != 0
                    || territory.TerritoryIntendedUse.RowId is not 0 and not 1) continue;
                string bg = territory.Bg.ExtractText();
                int level = bg.IndexOf("/level/", StringComparison.Ordinal);
                if (level < 0) continue;
                LgbFile? lgb;
                try { lgb = dataManager.GetFile<LgbFile>($"bg/{bg[..(level + 1)]}level/planevent.lgb"); }
                catch (Exception ex)
                {
                    log.Debug(ex, "自动兑换：跳过无法读取的地图资源 {Territory}", territory.RowId);
                    continue;
                }
                if (lgb is null) continue;
                foreach (var layer in lgb.Layers)
                foreach (var obj in layer.InstanceObjects)
                {
                    if (obj.AssetType != LayerEntryType.EventNPC) continue;
                    uint npcId = ((LayerCommon.ENPCInstanceObject)obj.Object).ParentData.ParentData.BaseId;
                    if (!neededNpcs.Contains(npcId)) continue;
                    var t = obj.Transform.Translation;
                    Vector3 position = new(t.X, t.Y, t.Z);
                    if (!locations.TryGetValue(npcId, out var list)) locations[npcId] = list = [];
                    if (list.Any(v => v.TerritoryId == territory.RowId && Vector3.DistanceSquared(v.Position, position) < 1)) continue;
                    list.Add(new(npcId, names.GetRow(npcId).Singular.ExtractText(), territory.RowId,
                        territory.PlaceName.Value.Name.ExtractText(), position, territory.ExVersion.RowId));
                }
            }
            List<GemstoneProduct> result = [];
            foreach (var (shop, itemId, cost, count) in listings)
            {
                if (!items.TryGetRow(itemId, out var item)) continue;
                uint action = item.ItemAction.RowId == 0 ? 0 : item.ItemAction.Value.Action.RowId;
                uint unlock = action is 25183 or 29459 or 3357 ? item.AdditionalData.RowId
                    : item.ItemAction.RowId == 0 ? 0u : (uint)item.ItemAction.Value.Data[0];
                var vendors = sellers[shop.RowId].SelectMany(npc => locations.GetValueOrDefault(npc) ?? [])
                    .OrderBy(v => v.TerritoryId).ThenBy(v => v.NpcId).ToArray();
                result.Add(new(itemId, item.Name.ExtractText(), item.Icon, cost, count, shop.RowId,
                    shop.Name.ExtractText(), item.StackSize, item.IsUnique, action, unlock, vendors));
            }
            this.products = result.DistinctBy(p => (p.ShopId, p.ItemId)).OrderBy(p => p.Name).ToArray();
            this.IsLoaded = true;
            log.Information("自动兑换目录：{Products} 个商品条目，{Vendors} 个商人位置，{Missing} 个条目未解析位置",
                this.products.Count, locations.Values.Sum(v => v.Count), this.products.Count(p => p.Vendors.Count == 0));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { this.Error = ex.Message; log.Error(ex, "自动兑换目录加载失败"); }
    }, cancellationToken);

    public GemstoneProduct? Find(GemstoneExchangeRule rule) => this.Products.FirstOrDefault(p =>
        p.ItemId == rule.ItemId && p.ShopId == rule.ShopId
        && p.Vendors.Any(v => v.NpcId == rule.NpcId && v.TerritoryId == rule.TerritoryId));
}
