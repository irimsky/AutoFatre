using System.Numerics;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using NativePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AutoFatre;

public enum AutoRepairResult { Running, Completed, Failed }

/// <summary>Framework-thread only. Owns only windows opened during this repair operation.</summary>
public sealed unsafe class AutoRepairAdapter : IDisposable
{
    private static readonly string[] Addons = ["Repair", "RepairAuto", "SelectYesno", "SelectIconString", "SelectString", "Talk"];
    private readonly IGameGui gui;
    private readonly IDataManager data;
    private readonly IObjectTable objects;
    private readonly ITargetManager targets;
    private readonly IAddonLifecycle lifecycle;
    private readonly Action<string> diagnostic;
    private readonly Dictionary<string, ushort> owned = [];
    private readonly record struct Gear(int Slot, uint ItemId, ushort Condition);
    private List<Gear> original = [];
    private AutoRepairSettings settings = new();
    private enum Stage { Opening, Menu, Configure, Click, Confirm, Verify, Closing }
    private Stage stage;
    private DateTime deadline, nextAction;
    private ulong npcObjectId;
    private bool selfRepair, expectingWindows;
    private bool disposed;

    public AutoRepairAdapter(IGameGui gui, IDataManager data, IObjectTable objects,
        ITargetManager targets, IAddonLifecycle lifecycle, Action<string> diagnostic)
    {
        this.gui = gui; this.data = data; this.objects = objects; this.targets = targets; this.lifecycle = lifecycle;
        this.diagnostic = diagnostic;
        foreach (string name in Addons)
        {
            lifecycle.RegisterListener(AddonEvent.PostSetup, name, this.OnSetup);
            lifecycle.RegisterListener(AddonEvent.PreFinalize, name, this.OnFinalize);
        }
    }

    public bool IsActive { get; private set; }
    public bool HasTimedOut(DateTime now) => this.IsActive && now >= this.deadline;
    public string Status { get; private set; } = "尚未检查";
    public int? MinimumConditionPercent => this.ReadEquipment() is { Count: > 0 } gear
        ? (gear.Min(item => item.Condition) + 299) / 300 : null;
    public bool NeedsRepair(int threshold) => this.ReadEquipment() is not { } gear
        || gear.Any(item => item.Condition <= threshold * 300);

    private List<Gear>? ReadEquipment()
    {
        InventoryManager* manager = InventoryManager.Instance();
        InventoryContainer* container = manager == null ? null : manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || !container->IsLoaded) return null;
        List<Gear> result = [];
        for (int i = 0; i < container->Size; i++)
        {
            InventoryItem* slot = container->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0) continue;
            if (this.data.GetExcelSheet<Item>().TryGetRow(slot->ItemId, out var item) && item.ItemRepair.RowId != 0)
                result.Add(new(i, slot->ItemId, slot->Condition));
        }
        return result;
    }

    public bool TryBegin(AutoRepairSettings options, DateTime now, out string error)
    {
        this.Cancel();
        this.settings = options.Copy();
        this.settings.Normalize();
        this.original = this.ReadEquipment() ?? [];
        error = string.Empty;
        if (this.original.Count == 0) { this.Status = error = "无法读取身上可修理装备"; return false; }
        string selfError = string.Empty;
        this.selfRepair = options.Method != AutoRepairMethod.NearbyNpc && this.CanSelfRepair(out selfError);
        if (!this.selfRepair)
        {
            if (options.Method == AutoRepairMethod.SelfRepair) { this.Status = error = selfError; return false; }
            IGameObject? npc = this.FindNearbyMender();
            if (npc is null)
            {
                error = string.IsNullOrEmpty(selfError) ? "交互范围内没有修理 NPC" : $"{selfError}；交互范围内也没有修理 NPC";
                this.Status = error;
                return false;
            }
            this.npcObjectId = npc.GameObjectId;
        }
        this.IsActive = true;
        this.stage = Stage.Opening;
        this.deadline = now.AddSeconds(this.settings.TimeoutSeconds);
        this.nextAction = now;
        this.Status = this.selfRepair ? "准备自修身上装备" : "准备请附近 NPC 修理身上装备";
        return true;
    }

    private bool CanSelfRepair(out string error)
    {
        error = string.Empty;
        NativePlayerState* player = NativePlayerState.Instance();
        InventoryManager* inventory = InventoryManager.Instance();
        if (player == null || inventory == null) { error = "角色或背包数据未就绪"; return false; }
        var resources = this.data.GetExcelSheet<ItemRepairResource>()
            .Where(row => row.Item.RowId != 0).OrderByDescending(row => row.RowId)
            .Select(row => (row.RowId, ItemId: row.Item.RowId)).ToArray();
        var remaining = resources.ToDictionary(row => row.ItemId, row => inventory->GetInventoryItemCount(row.ItemId));
        // Repair All may consume materials for any damaged equipped item. Budget for the entire selected container.
        foreach (var gear in this.original.Where(gear => gear.Condition < 30000)
                     .OrderByDescending(gear => this.data.GetExcelSheet<Item>().GetRow(gear.ItemId).ItemRepair.RowId))
        {
            var item = this.data.GetExcelSheet<Item>().GetRow(gear.ItemId);
            int jobIndex = item.ClassJobRepair.RowId == 0 ? -1 : item.ClassJobRepair.Value.ExpArrayIndex;
            if (item.ClassJobRepair.RowId == 0 || jobIndex < 0 || jobIndex >= player->ClassJobLevels.Length
                || player->ClassJobLevels[jobIndex] < Math.Max(1, (int)item.LevelEquip - 10))
            { error = $"{item.Name} 的修理职业等级不足"; return false; }
            var resource = resources.FirstOrDefault(row => row.RowId >= item.ItemRepair.RowId && remaining[row.ItemId] > 0);
            if (resource.ItemId == 0) { error = $"{item.Name} 所需暗物质不足（支持更高等级暗物质）"; return false; }
            remaining[resource.ItemId]--;
        }
        return true;
    }

    private IGameObject? FindNearbyMender() => this.objects.LocalPlayer is { } player
        ? this.objects.Where(obj => obj.ObjectKind == ObjectKind.EventNpc && obj.IsTargetable
                && Vector3.Distance(obj.Position, player.Position) <= 6f
                && this.data.GetExcelSheet<ENpcBase>().TryGetRow(obj.BaseId, out var row)
                && row.ENpcData.Any(entry => entry.RowId == 720915))
            .OrderBy(obj => Vector3.DistanceSquared(obj.Position, player.Position)).FirstOrDefault()
        : null;

    private bool ExpectedNpc => this.npcObjectId != 0 && this.targets.Target?.GameObjectId == this.npcObjectId;
    private AtkUnitBase* Visible(string name)
    {
        AtkUnitBase* addon = this.gui.GetAddonByName<AtkUnitBase>(name);
        return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
    }
    private AtkUnitBase* Owned(string name)
    {
        AtkUnitBase* addon = this.Visible(name);
        return addon != null && this.owned.TryGetValue(name, out ushort id) && id == addon->Id ? addon : null;
    }
    private static bool IsRepairText(string text) => new[] { "修理", "修复", "修復", "repair", "répar", "repar" }
        .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    private bool MatchesRepairPrompt(AddonSelectYesno* addon) => addon != null && addon->PromptText != null
        && IsRepairText(addon->PromptText->NodeText.ToString());

    private void OnSetup(AddonEvent _, AddonArgs args)
    {
        if (!this.IsActive || !this.expectingWindows || args.Addon.IsNull) return;
        if (!this.selfRepair && !this.ExpectedNpc) return;
        if (args.AddonName == "SelectYesno"
            && (this.stage != Stage.Confirm || this.Owned("Repair") == null
                || !this.MatchesRepairPrompt((AddonSelectYesno*)args.Addon.Address))) return;
        if (this.selfRepair && args.AddonName is "Talk" or "SelectString" or "SelectIconString") return;
        this.owned[args.AddonName] = args.Addon.Id;
    }
    private void OnFinalize(AddonEvent _, AddonArgs args) => this.owned.Remove(args.AddonName);

    public AutoRepairResult Update(DateTime now)
    {
        if (!this.IsActive) return AutoRepairResult.Failed;
        if (now >= this.deadline) return this.Fail($"自动修理超过 {this.settings.TimeoutSeconds} 秒；{this.Status}");
        if (now < this.nextAction) return AutoRepairResult.Running;
        this.nextAction = now.AddMilliseconds(300);
        if (!this.selfRepair && this.stage != Stage.Opening && !this.ExpectedNpc)
            return this.Fail("修理 NPC 交互目标已改变");
        try { return this.UpdateCore(now); }
        catch (Exception ex) { return this.Fail($"自动修理异常：{ex.Message}"); }
    }

    private AutoRepairResult UpdateCore(DateTime now)
    {
        if (this.stage == Stage.Closing)
        {
            this.CloseOwnedWindows();
            if (Addons.Any(name => this.Owned(name) != null)) return AutoRepairResult.Running;
            this.Status = "自动修理完成，已确认身上受损装备恢复至至少 100%";
            this.Cancel();
            return AutoRepairResult.Completed;
        }
        if (this.stage == Stage.Opening)
        {
            if (Addons.Concat(["Shop", "ShopExchangeCurrency", "ShopExchangeItem"])
                .Any(name => this.Visible(name) != null))
            { this.Status = "等待已有对话或修理窗口关闭"; return AutoRepairResult.Running; }
            this.expectingWindows = true;
            if (this.selfRepair)
            {
                ActionManager* actions = ActionManager.Instance();
                if (actions == null || !actions->UseAction(ActionType.GeneralAction, 6))
                { this.expectingWindows = false; return AutoRepairResult.Running; }
            }
            else
            {
                var npc = this.objects.FirstOrDefault(obj => obj.GameObjectId == this.npcObjectId);
                if (npc == null || !npc.IsTargetable || this.objects.LocalPlayer is not { } player
                    || Vector3.Distance(npc.Position, player.Position) > 6f) return this.Fail("修理 NPC 已离开交互范围");
                this.targets.Target = npc;
                TargetSystem.Instance()->OpenObjectInteraction((NativeGameObject*)npc.Address);
            }
            this.stage = Stage.Menu;
            this.Status = "等待修理窗口";
            return AutoRepairResult.Running;
        }
        AddonRepair* repair = (AddonRepair*)this.Owned("Repair");
        AgentRepair* agent = AgentRepair.Instance();
        if (this.stage == Stage.Menu)
        {
            if (repair != null)
            {
                this.stage = Stage.Configure;
                this.LogWindowState("修理窗口已打开");
                return AutoRepairResult.Running;
            }
            if (!this.selfRepair)
            {
                foreach (string name in new[] { "SelectIconString", "SelectString" })
                {
                    AtkUnitBase* addon = this.Owned(name);
                    if (addon == null) continue;
                    PopupMenu* menu = name == "SelectString" ? &((AddonSelectString*)addon)->PopupMenu.PopupMenu
                        : &((AddonSelectIconString*)addon)->PopupMenu.PopupMenu;
                    if (menu->EntryNames == null || menu->EntryCount is < 1 or > 64) continue;
                    List<int> matches = [];
                    for (int i = 0; i < menu->EntryCount; i++)
                    {
                        string text = MemoryHelper.ReadSeStringNullTerminated((nint)menu->EntryNames[i].Value).TextValue;
                        if (IsRepairText(text)) matches.Add(i);
                    }
                    if (matches.Count > 1) return this.Fail("NPC 菜单中存在多个修理选项，无法唯一确认");
                    if (matches.Count == 1)
                    {
                        addon->FireCallbackInt(matches[0]);
                        return AutoRepairResult.Running;
                    }
                }
            }
            return AutoRepairResult.Running;
        }
        if (repair == null || agent == null) return this.Fail("本次打开的修理窗口已关闭");
        // IsSelfRepairOpen describes the open window. UseSelfRepair is a separate native
        // operation flag and must not reject a freshly opened self-repair window.
        if (this.stage is Stage.Configure or Stage.Click or Stage.Confirm
            && agent->IsSelfRepairOpen != this.selfRepair)
        {
            if (this.stage == Stage.Configure)
            {
                this.Status = "等待修理窗口方式就绪";
                return AutoRepairResult.Running;
            }
            return this.Fail("修理窗口方式已改变");
        }
        var current = this.ReadEquipment();
        if (current == null) return AutoRepairResult.Running;
        if (current.Count != this.original.Count || this.original.Any(before => !current.Any(after => after.Slot == before.Slot && after.ItemId == before.ItemId)))
            return this.Fail("修理期间身上装备发生变化，请重新检查");
        if (this.stage == Stage.Configure)
        {
            agent->Filter = AgentRepair.ItemFilter.Equipped;
            agent->ChangeRepairInventory(false);
            this.stage = Stage.Click;
            this.LogWindowState("已请求切换至身上装备");
            this.Status = "检查身上装备修理费用和修理按钮";
            return AutoRepairResult.Running;
        }
        if (agent->Filter != AgentRepair.ItemFilter.Equipped) return this.Fail("修理窗口装备范围已被改变");
        if (agent->IsAddonRefreshPending) return AutoRepairResult.Running;
        if (this.stage == Stage.Click)
        {
            if (this.Visible("SelectYesno") != null) return AutoRepairResult.Running;
            InventoryManager* inventory = InventoryManager.Instance();
            if (inventory == null) return AutoRepairResult.Running;
            if (!this.selfRepair && (agent->TotalRepairCost <= 0 || agent->TotalRepairCost > this.settings.MaxNpcRepairCost
                || inventory->GetInventoryItemCount(1) < agent->TotalRepairCost))
                return this.Fail($"NPC 修理费用 {agent->TotalRepairCost} 超出上限 {this.settings.MaxNpcRepairCost} 或金币不足");
            // Set before clicking: PostSetup may fire synchronously from ReceiveEvent.
            this.stage = Stage.Confirm;
            if (!ClickButton(&repair->AtkUnitBase, repair->RepairAllButton)) this.stage = Stage.Click;
            else this.LogWindowState("已点击全部修理");
            this.Status = "等待本次修理确认";
            return AutoRepairResult.Running;
        }
        if (this.stage == Stage.Confirm)
        {
            AddonSelectYesno* prompt = (AddonSelectYesno*)this.Owned("SelectYesno");
            if (prompt == null) return AutoRepairResult.Running;
            if (!this.MatchesRepairPrompt(prompt)) return this.Fail("修理确认内容已改变");
            if (!this.selfRepair && (agent->TotalRepairCost <= 0 || agent->TotalRepairCost > this.settings.MaxNpcRepairCost))
                return this.Fail("确认时 NPC 修理费用超出上限");
            if (ClickButton(&prompt->AtkUnitBase, prompt->YesButton))
            {
                this.stage = Stage.Verify;
                this.LogWindowState("已确认修理");
                this.nextAction = now.AddSeconds(1);
                this.Status = "等待实际装备耐久恢复";
            }
            return AutoRepairResult.Running;
        }
        if (this.original.Where(before => before.Condition < 30000)
            .Any(before => !current.Any(after => after.Slot == before.Slot && after.Condition >= 30000)))
            return AutoRepairResult.Running;
        this.stage = Stage.Closing;
        this.Status = "装备耐久已恢复，等待本次修理窗口关闭";
        this.CloseOwnedWindows();
        this.nextAction = now.AddMilliseconds(500);
        return AutoRepairResult.Running;
    }

    private static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (button == null || !button->IsEnabled) return false;
        AtkComponentNode* owner = button->AtkComponentBase.OwnerNode;
        if (owner == null || !owner->AtkResNode.IsVisible()) return false;
        AtkEvent* click = owner->AtkResNode.AtkEventManager.Event;
        if (click == null) return false;
        addon->ReceiveEvent(click->State.EventType, (int)click->Param, click);
        return true;
    }
    private void LogWindowState(string reason)
    {
        AgentRepair* agent = AgentRepair.Instance();
        AddonRepair* repair = (AddonRepair*)this.Owned("Repair");
        string state = agent == null ? "agent=unavailable"
            : $"IsSelfRepairOpen={agent->IsSelfRepairOpen}; UseSelfRepair={agent->UseSelfRepair}; NpcCallback={agent->NpcRepairCallback != null}; Filter={agent->Filter}; RefreshPending={agent->IsAddonRefreshPending}; Cost={agent->TotalRepairCost}";
        this.diagnostic($"[REPAIR_SNAPSHOT] {reason}; Stage={this.stage}; ExpectedSelfRepair={this.selfRepair}; RepairAddonId={(repair == null ? 0 : repair->AtkUnitBase.Id)}; RepairAllEnabled={repair != null && repair->RepairAllButton != null && repair->RepairAllButton->IsEnabled}; {state}");
    }

    private AutoRepairResult Fail(string reason)
    {
        this.LogWindowState(reason);
        this.Status = reason;
        this.Cancel();
        return AutoRepairResult.Failed;
    }

    private void CloseOwnedWindows()
    {
        foreach (string name in new[] { "SelectYesno", "RepairAuto", "Repair", "SelectIconString", "SelectString", "Talk" })
        {
            AtkUnitBase* addon = this.Owned(name);
            if (addon != null) addon->Close(true);
        }
    }
    public void Cancel()
    {
        this.expectingWindows = false;
        this.CloseOwnedWindows();
        if (this.ExpectedNpc) this.targets.Target = null;
        this.IsActive = false;
        this.npcObjectId = 0;
        this.owned.Clear();
        this.original.Clear();
    }
    public void Dispose()
    {
        if (this.disposed) return;
        this.disposed = true;
        this.Cancel();
        foreach (string name in Addons)
        {
            this.lifecycle.UnregisterListener(AddonEvent.PostSetup, name, this.OnSetup);
            this.lifecycle.UnregisterListener(AddonEvent.PreFinalize, name, this.OnFinalize);
        }
    }
}
