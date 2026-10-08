using Dalamud.Memory;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoFatre;

public enum ExchangeShopIssue { None, NotReady, Unavailable, InvalidData }

/// <summary>Framework-thread only. Keeps addon IDs, never retains native addon pointers across ticks.</summary>
public sealed unsafe class ShopExchangeAdapter : IDisposable
{
    private static readonly string[] Addons = ["Talk", "SelectString", "SelectIconString", "SelectYesno",
        "ShopExchangeCurrency", "ShopExchangeCurrencyDialog"];
    private readonly IGameGui gameGui;
    private readonly ITargetManager targetManager;
    private readonly IAddonLifecycle lifecycle;
    private readonly IPluginLog log;
    private readonly uint currencyIcon;
    private readonly Dictionary<string, ushort> owned = [];
    private readonly HashSet<string> confirmedStages = [];
    private uint npcId;
    private ulong objectId;
    private int pendingQuantity;
    private uint pendingItemId;
    private uint pendingCost;
    private string pendingItemName = string.Empty;
    private ushort promptDiagnosticAddonId;
    private bool closing;

    public ShopExchangeAdapter(IGameGui gameGui, ITargetManager targetManager, IAddonLifecycle lifecycle, IPluginLog log, uint currencyIcon)
    {
        this.gameGui = gameGui;
        this.targetManager = targetManager;
        this.lifecycle = lifecycle;
        this.log = log;
        this.currencyIcon = currencyIcon;
        foreach (string name in Addons)
        {
            lifecycle.RegisterListener(AddonEvent.PostSetup, name, this.OnSetup);
            lifecycle.RegisterListener(AddonEvent.PreFinalize, name, this.OnFinalize);
        }
    }

    private void OnSetup(AddonEvent _, AddonArgs args)
    {
        if (this.closing || !this.IsExpectedTarget || args.Addon.IsNull) return;
        if (args.AddonName is "SelectYesno" or "ShopExchangeCurrencyDialog")
        {
            if (this.pendingQuantity <= 0 || this.GetOwned("ShopExchangeCurrency") == null || !this.IsPendingItemSelected()) return;
            if (args.AddonName == "SelectYesno" && !MatchesPrompt((AddonSelectYesno*)args.Addon.Address)) return;
        }
        this.owned[args.AddonName] = args.Addon.Id;
    }

    private void OnFinalize(AddonEvent _, AddonArgs args) => this.owned.Remove(args.AddonName);
    public bool IsExpectedTarget => this.npcId != 0 && this.targetManager.Target is { } target
        && target.BaseId == this.npcId && target.GameObjectId == this.objectId;
    public bool HasOwnership => this.npcId != 0;
    public bool HasShop => this.IsExpectedTarget && this.GetOwned("ShopExchangeCurrency") != null;
    private AtkUnitBase* Get(string name)
    {
        AtkUnitBase* addon = this.gameGui.GetAddonByName<AtkUnitBase>(name);
        return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
    }

    public bool HasBlockingUi => new[] { "Talk", "SelectString", "SelectIconString", "SelectYesno",
        "ShopExchangeCurrency", "ShopExchangeCurrencyDialog", "ShopExchangeItem", "ShopExchangeItemDialog", "Shop" }
        .Any(name => this.Get(name) != null);

    public void Begin(uint npc, ulong objectId)
    {
        this.npcId = npc; this.objectId = objectId; this.owned.Clear();
        this.confirmedStages.Clear(); this.pendingQuantity = 0; this.pendingCost = 0;
        this.pendingItemName = string.Empty; this.promptDiagnosticAddonId = 0; this.closing = false;
    }

    private AtkUnitBase* GetOwned(string name)
    {
        AtkUnitBase* addon = this.Get(name);
        return addon != null && this.owned.TryGetValue(name, out ushort id) && id == addon->Id ? addon : null;
    }

    private bool Observe(string name, uint targetNpc)
    {
        AtkUnitBase* addon = this.Get(name);
        if (addon == null || this.closing || !this.IsExpectedTarget || targetNpc != this.npcId) return false;
        if (this.owned.TryGetValue(name, out ushort id)) return id == addon->Id;
        this.owned[name] = addon->Id;
        return true;
    }

    public bool AdvanceOpening(GemstoneProduct product, uint targetNpc, out string? error, out bool unavailable)
    {
        error = null;
        unavailable = false;
        if (!this.IsExpectedTarget) { error = "兑换交互目标已改变，停止对话"; return false; }
        if (this.Observe("ShopExchangeCurrency", targetNpc)) return true;
        foreach (string name in new[] { "SelectString", "SelectIconString" })
        {
            if (!this.Observe(name, targetNpc)) continue;
            AtkUnitBase* addon = this.Get(name);
            PopupMenu* menu = name == "SelectString"
                ? &((AddonSelectString*)addon)->PopupMenu.PopupMenu
                : &((AddonSelectIconString*)addon)->PopupMenu.PopupMenu;
            if (menu->EntryNames == null || menu->EntryCount is < 1 or > 64) return false;
            List<int> matches = [];
            for (int i = 0; i < menu->EntryCount; i++)
            {
                string text = MemoryHelper.ReadSeStringNullTerminated((nint)menu->EntryNames[i].Value).TextValue;
                if ((!string.IsNullOrWhiteSpace(product.ShopName) && text == product.ShopName) || text.Contains("双色宝石", StringComparison.Ordinal)
                    || text.Contains("Bicolor Gemstone", StringComparison.OrdinalIgnoreCase)) matches.Add(i);
            }
            if (matches.Count != 1)
            {
                unavailable = matches.Count == 0;
                error = "无法唯一识别双色宝石商店选项，请检查商人和商店解锁状态";
                return false;
            }
            addon->FireCallbackInt(matches[0]);
            return true;
        }
        if (this.Observe("Talk", targetNpc)) { this.Get("Talk")->FireCallbackInt(0); return true; }
        return false;
    }

    private static uint UInt(AtkUnitBase* addon, int index) => addon != null && addon->AtkValues != null
        && index >= 0 && index < addon->AtkValuesCount
        ? addon->AtkValues[index].Type == AtkValueType.UInt ? addon->AtkValues[index].UInt
        : addon->AtkValues[index].Type == AtkValueType.Int ? (uint)Math.Max(0, addon->AtkValues[index].Int) : 0
        : 0;

    private static string DescribeAgentShop(AgentShop* agent, int maxItems = 8)
    {
        if (agent == null) return "agent=null";
        string name = agent->ShopName.ToString();
        if (agent->ItemReceive == null || agent->ItemReceiveCount is < 0 or > 200)
            return $"agentAddon={agent->AddonId},shopName={name},items={agent->ItemReceiveCount},itemsData=null";
        int count = Math.Min(maxItems, agent->ItemReceiveCount);
        List<string> items = [];
        for (int i = 0; i < count; i++)
        {
            var item = agent->ItemReceive[i];
            items.Add($"{i}:{item.ItemId}x{item.ItemCount}");
        }
        if (agent->ItemReceiveCount > count) items.Add($"+{agent->ItemReceiveCount - count}");
        return $"agentAddon={agent->AddonId},shopName={name},selected={agent->SelectedItemIndex}/{agent->SelectedItemStackSize},items=[{string.Join(',', items)}]";
    }

    private bool TryGetRow(GemstoneProduct product, out int row, out uint liveCost, out string? error, out ExchangeShopIssue issue)
    {
        row = -1;
        liveCost = 0;
        error = null;
        issue = ExchangeShopIssue.InvalidData;
        AtkUnitBase* shop = this.GetOwned("ShopExchangeCurrency");
        if (!this.IsExpectedTarget || shop == null) { error = "所属商店已关闭或交互目标改变"; return false; }
        AgentShop* agent = AgentShop.Instance();
        if (agent == null || agent->ItemReceive == null || agent->ItemReceiveCount is < 1 or > 180)
        { issue = ExchangeShopIssue.NotReady; error = "商店商品数据尚未就绪"; return false; }
        if (agent->AddonId != shop->Id)
        { error = $"商店实例与 AgentShop 不一致（addon={shop->Id}, agent={agent->AddonId}）"; return false; }

        uint actualIcon = UInt(shop, 85);
        if (actualIcon != this.currencyIcon)
            this.log.Warning("自动兑换商店货币图标与物品表不一致，但继续使用实时 AgentShop 核验：addon={Addon}, targetNpc={Npc}, expectedIcon={Expected}, actualIcon={Actual}, product={Item}/{Name}, shop={Shop}; {Agent}",
                shop->Id, this.npcId, this.currencyIcon, actualIcon, product.ItemId, product.Name, product.ShopId, DescribeAgentShop(agent));

        int count = (int)Math.Min(60, UInt(shop, 4));
        if (count == 0)
        { issue = ExchangeShopIssue.NotReady; error = "商店商品界面尚未就绪"; return false; }
        int atkDisplay = -1;
        for (int i = 0; i < count; i++)
            if (UInt(shop, 1064 + i) == product.ItemId) { atkDisplay = i; break; }

        int agentRow = -1;
        for (int i = 0; i < agent->ItemReceiveCount; i++)
            if (agent->ItemReceive[i].ItemId == product.ItemId) { agentRow = i; break; }
        if (atkDisplay < 0 && agentRow < 0)
        { issue = ExchangeShopIssue.Unavailable; error = $"商店中没有可兑换的「{product.Name}」，可能尚未解锁；{DescribeAgentShop(agent)}"; return false; }
        if (atkDisplay < 0 || agentRow < 0)
        { error = $"商店界面与 AgentShop 商品行不一致；{DescribeAgentShop(agent)}"; return false; }
        if (agent->ItemReceive[agentRow].ItemId != product.ItemId)
        { error = "实时商品列表无法定位该物品"; return false; }
        if (agent->ItemReceive[agentRow].ItemCount != product.ReceiveCount)
        { error = "实时兑换产出数量与商品目录不一致"; return false; }
        liveCost = UInt(shop, 454 + atkDisplay);
        if (liveCost == 0)
        { error = $"商店中没有可识别的「{product.Name}」实时兑换价格；{DescribeAgentShop(agent)}"; return false; }
        if (liveCost != product.Cost)
        {
            error = $"实时兑换价格与商品目录不一致（实际={liveCost}, 目录={product.Cost}, atkRow={atkDisplay}, agentRow={agentRow}）";
            return false;
        }
        if (atkDisplay != agentRow)
            this.log.Debug("自动兑换商品行已按实时 AgentShop 重映射：商品={Item}/{Name}, atkRow={AtkRow}, agentRow={AgentRow}",
                product.ItemId, product.Name, atkDisplay, agentRow);
        row = agentRow;
        issue = ExchangeShopIssue.None;
        return true;
    }

    public bool Submit(GemstoneProduct product, int exchanges, out uint liveCost, out string? error, out ExchangeShopIssue issue)
    {
        liveCost = 0;
        if (!this.TryGetRow(product, out int row, out liveCost, out error, out issue)) return false;
        if (this.Get("SelectYesno") != null || this.Get("ShopExchangeCurrencyDialog") != null)
        { issue = ExchangeShopIssue.InvalidData; error = "存在未结束的确认窗口，暂停新购买"; return false; }
        AtkValue* values = stackalloc AtkValue[4];
        values[0] = new() { Type = AtkValueType.Int, Int = 0 };
        values[1] = new() { Type = AtkValueType.Int, Int = row };
        values[2] = new() { Type = AtkValueType.Int, Int = exchanges };
        values[3] = new() { Type = AtkValueType.Int, Int = 0 };
        this.pendingQuantity = exchanges;
        this.pendingItemId = product.ItemId;
        this.pendingCost = checked(product.Cost * (uint)exchanges);
        this.pendingItemName = product.Name;
        this.promptDiagnosticAddonId = 0;
        this.confirmedStages.Clear();
        this.Get("ShopExchangeCurrency")->FireCallback(4, values, true);
        return true;
    }

    public bool Confirm(GemstoneProduct product, uint targetNpc)
    {
        if (this.pendingQuantity <= 0 || !this.HasShop || this.pendingItemId != product.ItemId || !this.IsPendingItemSelected()) return false;
        if (!this.confirmedStages.Contains("ShopExchangeCurrencyDialog") && this.Observe("ShopExchangeCurrencyDialog", targetNpc))
        {
            AtkUnitBase* addon = this.Get("ShopExchangeCurrencyDialog");
            AtkComponentButton* button = addon->GetComponentButtonById(17);
            if (button == null || !button->IsEnabled) return false;
            if (addon->UldManager.NodeListCount <= 8 || addon->UldManager.NodeList[8] == null) return false;
            AtkComponentNumericInput* quantity = addon->UldManager.NodeList[8]->GetAsAtkComponentNumericInput();
            if (quantity == null) return false;
            quantity->SetValue(this.pendingQuantity);
            if (quantity->Value != this.pendingQuantity) return false;
            return this.ClickConfirmation(addon, button, "ShopExchangeCurrencyDialog");
        }
        AddonSelectYesno* yesno = (AddonSelectYesno*)this.Get("SelectYesno");
        if (!this.confirmedStages.Contains("SelectYesno") && this.MatchesPrompt(yesno)
            && yesno->YesButton != null && yesno->YesButton->IsEnabled && this.Observe("SelectYesno", targetNpc))
        {
            return this.ClickConfirmation(&yesno->AtkUnitBase, yesno->YesButton, "SelectYesno");
        }
        return false;
    }

    private bool ClickConfirmation(AtkUnitBase* addon, AtkComponentButton* button, string stage)
    {
        AtkComponentNode* owner = button->AtkComponentBase.OwnerNode;
        if (owner == null || !owner->AtkResNode.IsVisible()) return false;
        AtkEvent* click = owner->AtkResNode.AtkEventManager.Event;
        if (click == null) return false;
        this.confirmedStages.Add(stage);
        addon->ReceiveEvent(click->State.EventType, (int)click->Param, click);
        return true;
    }

    private bool IsPendingItemSelected()
    {
        AgentShop* agent = AgentShop.Instance();
        if (agent == null || agent->ItemReceive == null || agent->SelectedItemIndex < 0
            || agent->SelectedItemIndex >= agent->ItemReceiveCount
            || agent->ItemReceiveCount > 180 || agent->ItemReceive[agent->SelectedItemIndex].ItemId != this.pendingItemId) return false;
        return true;
    }

    private bool MatchesPrompt(AddonSelectYesno* yesno)
    {
        if (yesno == null || yesno->PromptText == null || this.pendingQuantity <= 0 || this.pendingCost == 0) return false;
        string prompt = MemoryHelper.ReadSeStringNullTerminated((nint)yesno->PromptText->NodeText.StringPtr.Value).TextValue;
        bool hasCost = prompt.Contains(this.pendingCost.ToString(), StringComparison.Ordinal);
        bool hasExchangeWord = prompt.Contains("换取", StringComparison.Ordinal)
            || prompt.Contains("兑换", StringComparison.Ordinal)
            || prompt.Contains("购买", StringComparison.Ordinal)
            || prompt.Contains("exchange", StringComparison.OrdinalIgnoreCase)
            || prompt.Contains("purchase", StringComparison.OrdinalIgnoreCase);
        bool matched = hasCost && hasExchangeWord;
        if (!matched && yesno->AtkUnitBase.Id != this.promptDiagnosticAddonId)
        {
            this.promptDiagnosticAddonId = yesno->AtkUnitBase.Id;
            this.log.Warning("自动兑换确认提示未匹配：商品={Item}/{Name}, 数量={Quantity}, 预期总价={Cost}, 提示={Prompt}",
                this.pendingItemId, this.pendingItemName, this.pendingQuantity, this.pendingCost, prompt);
        }
        return matched;
    }

    public bool CloseOwned()
    {
        this.closing = true;
        if (!this.IsExpectedTarget) { this.ForgetOwnership(); return true; }
        bool visible = false;
        foreach (var (name, id) in this.owned.OrderBy(pair => pair.Key == "ShopExchangeCurrency" ? 1 : 0).ToArray())
        {
            AtkUnitBase* addon = this.Get(name);
            if (addon == null || addon->Id != id) continue;
            visible = true;
            addon->Close(true);
        }
        if (!visible) { this.owned.Clear(); this.npcId = 0; }
        return !visible;
    }

    public void ForgetOwnership() { this.owned.Clear(); this.npcId = 0; this.objectId = 0; this.pendingQuantity = 0; }

    public void Dispose()
    {
        this.CloseOwned();
        this.ForgetOwnership();
        foreach (string name in Addons)
        {
            this.lifecycle.UnregisterListener(AddonEvent.PostSetup, name, this.OnSetup);
            this.lifecycle.UnregisterListener(AddonEvent.PreFinalize, name, this.OnFinalize);
        }
    }
}
