using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace AutoFatre;

public sealed partial class AutoFatreWindow
{
    private const float ExchangeContentFontScale = 0.9f;
    private ITextureProvider? exchangeTextures;
    private string exchangeSearch = string.Empty;
    private uint? exchangeExpansionFilter;
    private uint exchangeTerritoryFilter;
    private uint exchangeNpcFilter;
    private string? exchangeDraggedRule;
    private readonly Dictionary<uint, (uint Shop, uint Npc, uint Territory)> exchangeProductChoices = [];

    public void SetExchangeTextureProvider(ITextureProvider textureProvider) => this.exchangeTextures = textureProvider;

    private void DrawExchangeTab()
    {
        var settings = this.configuration.GemstoneExchange;
        bool enabled = settings.Enabled;
        if (ImGui.Checkbox("启用自动兑换", ref enabled)) settings.Enabled = enabled;
        ImGui.SameLine();
        bool balanceLoaded = this.controller.TryGetGemstones(out uint balance, out uint cap);
        ImGui.TextUnformatted(balanceLoaded ? $"双色宝石：{balance} / {cap}" : "双色宝石：待读取");
        ImGui.SetNextItemWidth(100);
        int threshold = settings.Threshold;
        if (ImGui.InputInt("触发数量", ref threshold))
            settings.Threshold = Math.Clamp(threshold, 1, balanceLoaded ? (int)cap : 99999);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        int reserve = settings.Reserve;
        if (ImGui.InputInt("保留宝石", ref reserve)) settings.Reserve = Math.Clamp(reserve, 0, settings.Threshold - 1);
        ImGui.BeginDisabled(this.controller.IsExchanging || this.controller.ExchangePending);
        if (ImGui.Button("立即兑换")) this.controller.RequestGemstoneExchange();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!this.controller.IsExchanging && !this.controller.ExchangePending);
        if (ImGui.Button("取消兑换")) this.controller.RequestCancelGemstoneExchange();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (this.controller.IsExchanging && this.controller.State == AutomationState.Paused)
            if (ImGui.Button("继续兑换")) this.controller.Retry();
        ImGui.TextWrapped(this.controller.ExchangeStatus);
        ImGui.Separator();

        var catalog = this.controller.ExchangeCatalog;
        if (!catalog.IsLoaded)
        { ImGui.TextWrapped(catalog.Error is { } error ? $"商品目录加载失败：{error}" : "正在加载双色宝石商品目录…"); return; }
        float productHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.70f, 250, 390);
        ImGui.SetWindowFontScale(UiFontScale * ExchangeContentFontScale);
        if (ImGui.BeginTable("ExchangeBrowserLayout", 2, ImGuiTableFlags.BordersInnerV, new Vector2(0, productHeight)))
        {
            ImGui.TableSetupColumn("筛选", ImGuiTableColumnFlags.WidthFixed, 235);
            ImGui.TableSetupColumn("商品", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            this.DrawExchangeFilters(catalog, productHeight);
            ImGui.TableNextColumn();
            this.DrawExchangeProducts(catalog, productHeight);
        }
        ImGui.EndTable();
        ImGui.SetWindowFontScale(UiFontScale);
        ImGui.Separator();
        ImGui.SetWindowFontScale(UiFontScale * ExchangeContentFontScale);
        ImGui.TextUnformatted("兑换计划（从上到下优先，拖动左侧手柄调整顺序）");
        if (this.controller.IsExchanging) ImGui.TextUnformatted("本轮使用启动时的计划，修改和排序从下一轮生效。");
        this.DrawExchangeRules(catalog);
        ImGui.SetWindowFontScale(UiFontScale);
    }

    private static string ExpansionName(uint expansion) => expansion switch
    { 3 => "5.0 漆黑的反叛者", 4 => "6.0 晓月之终途", 5 => "7.0 金曦之遗辉", _ => $"资料片 {expansion + 2}.0" };

    private static void DrawExchangeFilterTitle(string title)
    {
        ImGui.SetWindowFontScale(UiFontScale * ExchangeContentFontScale * 1.15f);
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.82f, 0.40f, 1f));
        ImGui.TextUnformatted(title);
        ImGui.PopStyleColor();
        ImGui.SetWindowFontScale(UiFontScale * ExchangeContentFontScale);
        ImGui.Separator();
    }

    private void DrawExchangeFilters(GemstoneExchangeCatalog catalog, float availableHeight)
    {
        if (!ImGui.BeginTable("ExchangeFilterTable", 1, ImGuiTableFlags.ScrollY, new Vector2(0, availableHeight))) return;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawExchangeFilterTitle("资料片");
        if (ImGui.Selectable("全部资料片", this.exchangeExpansionFilter is null))
        { this.exchangeExpansionFilter = null; this.exchangeTerritoryFilter = this.exchangeNpcFilter = 0; }
        var vendors = catalog.Products.SelectMany(p => p.Vendors).DistinctBy(v => (v.NpcId, v.TerritoryId)).ToArray();
        foreach (uint expansion in vendors.Select(v => v.Expansion).Distinct().Order())
            if (ImGui.Selectable(ExpansionName(expansion), this.exchangeExpansionFilter == expansion))
            { this.exchangeExpansionFilter = expansion; this.exchangeTerritoryFilter = this.exchangeNpcFilter = 0; }
        DrawExchangeFilterTitle("地图");
        if (ImGui.Selectable("全部地图", this.exchangeTerritoryFilter == 0)) this.exchangeTerritoryFilter = this.exchangeNpcFilter = 0;
        foreach (var vendor in vendors.Where(v => this.exchangeExpansionFilter is null || v.Expansion == this.exchangeExpansionFilter)
                     .DistinctBy(v => v.TerritoryId).OrderBy(v => v.Expansion).ThenBy(v => v.Zone, StringComparer.CurrentCulture))
            if (ImGui.Selectable($"{vendor.Zone}##zone{vendor.TerritoryId}", this.exchangeTerritoryFilter == vendor.TerritoryId))
            { this.exchangeTerritoryFilter = vendor.TerritoryId; this.exchangeNpcFilter = 0; }
        DrawExchangeFilterTitle("商人");
        if (ImGui.Selectable("全部商人", this.exchangeNpcFilter == 0)) this.exchangeNpcFilter = 0;
        foreach (var vendor in vendors.Where(this.MatchesExchangeFilter).DistinctBy(v => (v.NpcId, v.TerritoryId))
                     .OrderBy(v => v.Expansion).ThenBy(v => v.Zone, StringComparer.CurrentCulture)
                     .ThenBy(v => v.Name, StringComparer.CurrentCulture).ThenBy(v => v.NpcId))
            if (ImGui.Selectable($"{ExchangeVendorLabel(vendor)}##npc{vendor.NpcId}", this.exchangeNpcFilter == vendor.NpcId)) this.exchangeNpcFilter = vendor.NpcId;
        ImGui.EndTable();
    }

    private bool MatchesExchangeFilter(GemstoneVendor vendor) =>
        (this.exchangeExpansionFilter is null || vendor.Expansion == this.exchangeExpansionFilter)
        && (this.exchangeTerritoryFilter == 0 || vendor.TerritoryId == this.exchangeTerritoryFilter);

    private IEnumerable<(GemstoneProduct Product, GemstoneVendor Vendor)> ExchangeChoices(GemstoneExchangeCatalog catalog, uint itemId) =>
        catalog.Products.Where(p => p.ItemId == itemId).SelectMany(p => p.Vendors.Select(v => (Product: p, Vendor: v)))
            .DistinctBy(c => (c.Vendor.NpcId, c.Vendor.TerritoryId, c.Product.Cost, c.Product.ReceiveCount))
            .OrderBy(c => c.Vendor.Expansion)
            .ThenBy(c => c.Vendor.Zone, StringComparer.CurrentCulture)
            .ThenBy(c => c.Vendor.Name, StringComparer.CurrentCulture)
            .ThenBy(c => c.Product.Cost)
            .ThenBy(c => c.Vendor.NpcId);

    private static string ExchangeVendorLabel(GemstoneVendor vendor)
    {
        const string prefix = "广域交易商";
        string name = vendor.Name.StartsWith(prefix, StringComparison.Ordinal)
            ? vendor.Name[prefix.Length..].TrimStart()
            : vendor.Name;
        return $"{vendor.Zone} · {name}";
    }

    private void DrawExchangeIcon(GemstoneProduct product)
    {
        var texture = this.exchangeTextures?.GetFromGameIcon(new GameIconLookup(product.Icon)).GetWrapOrDefault();
        if (texture is not null) { ImGui.Image(texture.Handle, new Vector2(24, 24)); ImGui.SameLine(); }
    }

    private static float ExchangeItemColumnWidth(IEnumerable<string> itemNames)
    {
        float longest = itemNames.Select(name => ImGui.CalcTextSize(name).X).DefaultIfEmpty().Max();
        return MathF.Max(180f, longest + 36f);
    }

    private void DrawExchangeProducts(GemstoneExchangeCatalog catalog, float availableHeight)
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##exchangeSearch", "搜索兑换物品…", ref this.exchangeSearch, 128);
        if (!ImGui.BeginTable("ExchangeProductTable", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX
                | ImGuiTableFlags.BordersInnerV, new Vector2(0, Math.Max(100, availableHeight - ImGui.GetFrameHeightWithSpacing())))) return;
        ImGui.TableSetupColumn("物品", ImGuiTableColumnFlags.WidthFixed, ExchangeItemColumnWidth(catalog.Products.Select(p => p.Name)));
        ImGui.TableSetupColumn("单价", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("背包数量", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("使用状态", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("地图 · 商人", ImGuiTableColumnFlags.WidthFixed, 320);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();
        foreach (var group in catalog.Products.Where(p => string.IsNullOrWhiteSpace(this.exchangeSearch)
                     || p.Name.Contains(this.exchangeSearch, StringComparison.OrdinalIgnoreCase)
                     || p.ItemId.ToString() == this.exchangeSearch).GroupBy(p => p.ItemId))
        {
            var choices = this.ExchangeChoices(catalog, group.Key).Where(c => this.MatchesExchangeFilter(c.Vendor)
                && (this.exchangeNpcFilter == 0 || c.Vendor.NpcId == this.exchangeNpcFilter)).ToArray();
            if (choices.Length == 0 && (this.exchangeTerritoryFilter != 0 || this.exchangeNpcFilter != 0 || this.exchangeExpansionFilter is not null)) continue;
            var selected = choices.FirstOrDefault();
            if (this.exchangeProductChoices.TryGetValue(group.Key, out var key))
                selected = choices.FirstOrDefault(c => c.Product.ShopId == key.Shop && c.Vendor.NpcId == key.Npc && c.Vendor.TerritoryId == key.Territory,
                    selected);
            GemstoneProduct product = selected.Product ?? group.First();
            ImGui.PushID((int)group.Key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn(); this.DrawExchangeIcon(product); ImGui.TextUnformatted(product.Name);
            ImGui.TableNextColumn(); ImGui.TextUnformatted(product.Cost.ToString());
            if (product.ReceiveCount != 1 && ImGui.IsItemHovered()) ImGui.SetTooltip($"每次兑换得到 {product.ReceiveCount} 件");
            ImGui.TableNextColumn(); ImGui.TextUnformatted(this.inventoryCounter.Count(product.ItemId).ToString());
            ImGui.TableNextColumn(); this.DrawExchangeItemUseStatus(product);
            ImGui.TableNextColumn();
            string label = selected.Vendor is null ? "位置未解析" : ExchangeVendorLabel(selected.Vendor);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##vendor", label))
            {
                foreach (var choice in choices)
                    if (ImGui.Selectable($"{ExchangeVendorLabel(choice.Vendor)} · {choice.Product.Cost}宝石##{choice.Product.ShopId}-{choice.Vendor.NpcId}-{choice.Vendor.TerritoryId}"))
                    { selected = choice; product = choice.Product; this.exchangeProductChoices[group.Key] = (choice.Product.ShopId, choice.Vendor.NpcId, choice.Vendor.TerritoryId); }
                ImGui.EndCombo();
            }
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(selected.Vendor is null);
            if (ImGui.SmallButton("添加") && selected.Vendor is { } vendor)
                this.configuration.GemstoneExchange.Rules.Add(new()
                {
                    ItemId = product.ItemId, ShopId = product.ShopId, NpcId = vendor.NpcId, TerritoryId = vendor.TerritoryId,
                    SkipIfUsed = product.SupportsUsedCheck,
                });
            ImGui.EndDisabled();
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private unsafe void DrawExchangeRules(GemstoneExchangeCatalog catalog)
    {
        var rules = this.configuration.GemstoneExchange.Rules;
        if (rules.Count == 0) { ImGui.TextUnformatted("从上方添加需要兑换的物品。"); return; }
        float itemColumnWidth = ExchangeItemColumnWidth(rules.Select(rule =>
            catalog.Find(rule)?.Name ?? this.selectionCatalog.GetItemName(rule.ItemId)));
        if (!ImGui.BeginTable("ExchangeRules", 10, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX
                | ImGuiTableFlags.BordersInnerV, new Vector2(0, Math.Max(110, ImGui.GetContentRegionAvail().Y)))) return;
        ImGui.TableSetupColumn("顺序", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("启用", ImGuiTableColumnFlags.WidthFixed, 62);
        ImGui.TableSetupColumn("物品", ImGuiTableColumnFlags.WidthFixed, itemColumnWidth);
        ImGui.TableSetupColumn("已使用则跳过", ImGuiTableColumnFlags.WidthFixed, 145);
        ImGui.TableSetupColumn("地图 · 商人", ImGuiTableColumnFlags.WidthFixed, 280);
        ImGui.TableSetupColumn("兑换方式", ImGuiTableColumnFlags.WidthFixed, 150);
        ImGui.TableSetupColumn("目标数量", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("背包数量", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("使用状态", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 65);
        ImGui.TableSetupScrollFreeze(2, 1);
        ImGui.TableHeadersRow();
        string? remove = null;
        (string Source, string Target, bool After)? move = null;
        foreach (var rule in rules)
        {
            var product = catalog.Find(rule);
            ImGui.PushID(rule.Id);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.SmallButton("≡");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("拖动调整兑换顺序");
            if (ImGui.BeginDragDropSource())
            {
                this.exchangeDraggedRule = rule.Id;
                ImGui.SetDragDropPayload("AF_EXCHANGE_RULE", []);
                ImGui.TextUnformatted(product?.Name ?? this.selectionCatalog.GetItemName(rule.ItemId));
                ImGui.EndDragDropSource();
            }
            if (ImGui.BeginDragDropTarget())
            {
                var payload = ImGui.AcceptDragDropPayload("AF_EXCHANGE_RULE");
                if (!payload.IsNull && payload.IsDelivery() && this.exchangeDraggedRule is { } source && source != rule.Id)
                    move = (source, rule.Id, ImGui.GetMousePos().Y >= (ImGui.GetItemRectMin().Y + ImGui.GetItemRectMax().Y) / 2);
                ImGui.EndDragDropTarget();
            }
            ImGui.TableNextColumn();
            bool enabled = rule.Enabled;
            if (CenterExchangeCheckbox("##enabled", ref enabled)) rule.Enabled = enabled;
            ImGui.TableNextColumn();
            if (product is not null) this.DrawExchangeIcon(product);
            string itemName = product?.Name ?? this.selectionCatalog.GetItemName(rule.ItemId);
            ImGui.TextUnformatted(itemName);
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(product?.SupportsUsedCheck != true);
            bool skip = rule.SkipIfUsed;
            if (CenterExchangeCheckbox("##skipUsed", ref skip)) rule.SkipIfUsed = skip;
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(product?.SupportsUsedCheck == true ? "当前角色已学习或登记这个道具时，跳过此项兑换。" : "该物品不支持一次性解锁状态检测。");
            ImGui.TableNextColumn();
            var currentVendor = product?.Vendors.FirstOrDefault(v => v.NpcId == rule.NpcId && v.TerritoryId == rule.TerritoryId);
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##ruleVendor", currentVendor is null ? "商人数据失效" : ExchangeVendorLabel(currentVendor)))
            {
                foreach (var choice in this.ExchangeChoices(catalog, rule.ItemId))
                    if (ImGui.Selectable($"{ExchangeVendorLabel(choice.Vendor)} · {choice.Product.Cost}宝石##{choice.Product.ShopId}-{choice.Vendor.NpcId}-{choice.Vendor.TerritoryId}"))
                    { rule.ShopId = choice.Product.ShopId; rule.NpcId = choice.Vendor.NpcId; rule.TerritoryId = choice.Vendor.TerritoryId; }
                ImGui.EndCombo();
            }
            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            int quantityMode = (int)rule.QuantityMode;
            if (ImGui.Combo("##quantityMode", ref quantityMode, "补足背包数量\0消耗剩余宝石\0")) rule.QuantityMode = (GemstoneExchangeQuantityMode)quantityMode;
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(rule.QuantityMode == GemstoneExchangeQuantityMode.SpendRemaining);
            ImGui.SetNextItemWidth(-1); int target = rule.TargetQuantity;
            if (ImGui.InputInt("##quantity", ref target)) rule.TargetQuantity = Math.Clamp(target, 1, 99999);
            ImGui.EndDisabled();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(this.inventoryCounter.Count(rule.ItemId).ToString());
            ImGui.TableNextColumn();
            this.DrawExchangeItemUseStatus(product);
            ImGui.TableNextColumn(); if (ImGui.SmallButton("删除")) remove = rule.Id;
            ImGui.PopID();
        }
        ImGui.EndTable();
        if (remove is not null) rules.RemoveAll(r => r.Id == remove);
        if (move is { } reorder)
        {
            var source = rules.FirstOrDefault(r => r.Id == reorder.Source);
            var target = rules.FirstOrDefault(r => r.Id == reorder.Target);
            if (source is not null && target is not null)
            { rules.Remove(source); int index = rules.IndexOf(target); rules.Insert(index + (reorder.After ? 1 : 0), source); }
            this.exchangeDraggedRule = null;
        }
    }

    private void DrawExchangeItemUseStatus(GemstoneProduct? product)
    {
        ItemUseStatus use = product is null ? ItemUseStatus.Unsupported : this.controller.GetExchangeItemUseStatus(product);
        bool isIcon = use is ItemUseStatus.Used or ItemUseStatus.NotUsed;
        string label = product is null ? "商品失效" : use switch
        {
            ItemUseStatus.Used => FontAwesomeIcon.Check.ToIconString(),
            ItemUseStatus.NotUsed => FontAwesomeIcon.Times.ToIconString(),
            ItemUseStatus.WaitingForCharacter => "待读取",
            _ => "——",
        };
        if (isIcon) ImGui.PushFont(UiBuilder.IconFont);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(label).X) * 0.5f));
        ImGui.TextUnformatted(label);
        if (isIcon) ImGui.PopFont();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(product is null ? "商品数据失效" : use switch
            {
                ItemUseStatus.Used => "已使用",
                ItemUseStatus.NotUsed => "未使用",
                ItemUseStatus.WaitingForCharacter => "等待当前角色数据加载",
                _ => "该物品不支持一次性解锁状态检测",
            });
    }

    private static bool CenterExchangeCheckbox(string label, ref bool value)
    {
        float width = ImGui.GetColumnWidth();
        float checkboxWidth = ImGui.GetFrameHeight();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (width - checkboxWidth) * 0.5f));
        return ImGui.Checkbox(label, ref value);
    }
}
