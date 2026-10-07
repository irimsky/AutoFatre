using Dalamud.Game.ClientState.Buddy;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Dalamud.Utility;

namespace AutoFatre;

/// <summary>Small framework-thread adapter for the player's chocobo companion and minion items.</summary>
public sealed unsafe class CompanionAdapter(
    IBuddyList buddyList,
    InventoryCounter inventory,
    IDataManager dataManager,
    IObjectTable objectTable)
{
    public const uint GysahlGreensItemId = 4868;
    private DateTime nextChocoboUseAt = DateTime.MinValue;
    private DateTime nextMinionUseAt = DateTime.MinValue;

    /// <summary>Human-readable details of the most recent companion action/check.</summary>
    public string LastActionReason { get; private set; } = "尚未执行伙伴操作";

    /// <summary>Raw result returned by AgentInventoryContext.UseItem for the last attempt.</summary>
    public long? LastUseItemResult { get; private set; }

    public bool HasChocobo => buddyList.CompanionBuddy is not null;

    public float ChocoboTimeLeft
    {
        get
        {
            UIState* ui = UIState.Instance();
            return ui is null ? 0f : Math.Max(0f, ui->Buddy.CompanionInfo.TimeLeft);
        }
    }

    /// <summary>Companion row id currently summoned as the player's minion, if any.</summary>
    public uint CurrentMinionId => objectTable.LocalPlayer is ICharacter character
        ? character.CurrentMinion?.RowId ?? 0
        : 0;

    public string CurrentMinionName => this.CurrentMinionId is var id and not 0
        ? dataManager.GetExcelSheet<Companion>().GetRowOrDefault(id)?.Singular.ToString() ?? $"宠物 {id}"
        : "未召唤";

    public uint CurrentMainHandItemId
    {
        get
        {
            InventoryManager* manager = InventoryManager.Instance();
            if (manager is null) return 0;
            InventoryContainer* equipped = manager->GetInventoryContainer(InventoryType.EquippedItems);
            if (equipped is null) return 0;
            InventoryItem* item = equipped->GetInventorySlot(0);
            return item is null ? 0 : item->ItemId;
        }

    }
    public string CurrentMainHandName => this.CurrentMainHandItemId is var id and not 0
        ? dataManager.GetExcelSheet<Item>().GetRowOrDefault(ItemUtil.GetBaseId(id).ItemId)?.Name.ToString() ?? $"武器 {id}"
        : "未装备武器";

    public int GysahlGreensCount => inventory.Count(GysahlGreensItemId);

    public bool NeedsChocobo => !this.HasChocobo || this.ChocoboTimeLeft <= 300f;

    public bool TrySummonOrExtendChocobo(DateTime now)
    {
        this.LastUseItemResult = null;
        bool hasChocobo = this.HasChocobo;
        float timeLeft = this.ChocoboTimeLeft;
        int gysahlCount = inventory.Count(GysahlGreensItemId);
        if (now < this.nextChocoboUseAt)
        {
            this.LastActionReason = $"使用冷却中（还需 {(this.nextChocoboUseAt - now).TotalSeconds:0.0}s）；HasChocobo={hasChocobo}，TimeLeft={timeLeft:0.0}s，基萨尔野菜={gysahlCount}";
            return false;
        }

        if (hasChocobo && timeLeft > 300f)
        {
            this.LastActionReason = $"无需召唤：伙伴剩余 {timeLeft:0.0}s（阈值 300s）";
            return false;
        }

        if (gysahlCount <= 0)
        {
            this.LastActionReason = $"背包中没有基萨尔野菜（ItemId={GysahlGreensItemId}）；HasChocobo={hasChocobo}，TimeLeft={timeLeft:0.0}s";
            return false;
        }

        this.nextChocoboUseAt = now.AddSeconds(5);
        AgentInventoryContext* agent = AgentInventoryContext.Instance();
        if (agent is null)
        {
            this.LastActionReason = $"AgentInventoryContext.Instance() 为空；基萨尔野菜={gysahlCount}";
            return false;
        }

        long result = agent->UseItem(GysahlGreensItemId);
        this.LastUseItemResult = result;
        // Dalamud/Questionable's native wrapper treats result == 0 as success.
        bool success = result == 0;
        this.LastActionReason = success
            ? $"UseItem 成功（返回值 0）；召唤前 HasChocobo={hasChocobo}，TimeLeft={timeLeft:0.0}s，基萨尔野菜={gysahlCount}"
            : $"UseItem 被拒绝（返回值 {result}）；召唤前 HasChocobo={hasChocobo}，TimeLeft={timeLeft:0.0}s，基萨尔野菜={gysahlCount}";
        return success;
    }

    public bool TryUseMinionItem(uint itemId, DateTime now)
    {
        this.LastUseItemResult = null;
        if (itemId == 0)
        {
            this.LastActionReason = "未配置地图进入宠物物品（ItemId=0）";
            return false;
        }

        if (now < this.nextMinionUseAt)
        {
            this.LastActionReason = $"宠物召唤使用冷却中（还需 {(this.nextMinionUseAt - now).TotalSeconds:0.0}s）；ItemId={itemId}";
            return false;
        }

        // Minion consumables are one-time unlock items. Once unlocked they disappear from the
        // inventory, so invoking AgentInventoryContext.UseItem(itemId) cannot summon the pet
        // again. ItemAction.Data[0] stores the Companion row id; use the native Companion action
        // with that id, as the game does for the minion book/hotbar.
        Companion? companionRow = dataManager.GetExcelSheet<Item>()
            .GetRowOrDefault(itemId)
            ?.ItemAction.ValueNullable is { } itemAction
            ? dataManager.GetExcelSheet<Companion>().GetRowOrDefault(itemAction.Data[0])
            : null;
        uint companionId = companionRow?.RowId ?? 0;
        UIState* ui = UIState.Instance();
        bool unlocked = ui is not null && companionId != 0 && ui->IsCompanionUnlocked(companionId);
        if (companionId == 0)
        {
            this.LastActionReason = $"无法从 ItemAction.Data[0] 解析宠物 ID；ItemId={itemId}";
            return false;
        }

        uint currentMinionId = this.CurrentMinionId;
        if (currentMinionId == companionId)
        {
            this.LastActionReason = $"目标宠物已经召唤，无需再次调用 Companion 动作；ItemId={itemId}，CompanionId={companionId}，CurrentMinionId={currentMinionId}";
            return true;
        }

        this.nextMinionUseAt = now.AddMilliseconds(200);
        if (unlocked)
        {
            ActionManager* actionManager = ActionManager.Instance();
            if (actionManager is null)
            {
                this.LastActionReason = $"ActionManager.Instance() 为空；ItemId={itemId}，CompanionId={companionId}，已解锁={unlocked}，CurrentMinionId={currentMinionId}";
                return false;
            }

            bool actionAccepted = actionManager->UseAction(ActionType.Companion, companionId);
            this.LastUseItemResult = actionAccepted ? 0 : 1;
            this.LastActionReason = actionAccepted
                ? $"Companion 动作已请求；ItemId={itemId}，CompanionId={companionId}，已解锁={unlocked}，CurrentMinionId={currentMinionId}"
                : $"Companion 动作被拒绝；ItemId={itemId}，CompanionId={companionId}，已解锁={unlocked}，CurrentMinionId={currentMinionId}";
            return actionAccepted;
        }

        int itemCount = inventory.Count(itemId);
        if (itemCount <= 0)
        {
            this.LastActionReason = $"宠物尚未解锁且背包中没有召唤物品；ItemId={itemId}，CompanionId={companionId}，背包数量=0，已解锁=False";
            return false;
        }

        AgentInventoryContext* agent = AgentInventoryContext.Instance();
        if (agent is null)
        {
            this.LastActionReason = $"AgentInventoryContext.Instance() 为空；ItemId={itemId}，CompanionId={companionId}，背包数量={itemCount}";
            return false;
        }

        long result = agent->UseItem(itemId);
        this.LastUseItemResult = result;
        bool success = result == 0;
        this.LastActionReason = success
            ? $"首次解锁宠物 UseItem 成功（返回值 0）；ItemId={itemId}，CompanionId={companionId}，背包数量={itemCount}"
            : $"首次解锁宠物 UseItem 被拒绝（返回值 {result}）；ItemId={itemId}，CompanionId={companionId}，背包数量={itemCount}";
        return success;
    }

    /// <summary>Returns whether the companion represented by a configured item is already active.</summary>
    public bool IsMinionItemActive(uint itemId)
    {
        if (itemId == 0)
            return false;

        Companion? companionRow = dataManager.GetExcelSheet<Item>()
            .GetRowOrDefault(itemId)
            ?.ItemAction.ValueNullable is { } itemAction
            ? dataManager.GetExcelSheet<Companion>().GetRowOrDefault(itemAction.Data[0])
            : null;
        return companionRow is { } row && this.CurrentMinionId == row.RowId;
    }

    public bool TryEquipRecommended()
    {
        RecommendEquipModule* module = RecommendEquipModule.Instance();
        if (module is null || module->IsUpdating)
            return false;
        module->EquipRecommendedGear();
        return true;
    }

    public bool TryEquipItem(uint itemId)
    {
        if (itemId == 0)
            return true;
        Item? item = dataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        if (item is null)
            return false;
        List<ushort>? slots = item.Value.EquipSlotCategory.RowId switch
        {
            >= 1 and <= 11 => [(ushort)(item.Value.EquipSlotCategory.RowId - 1)],
            12 => [11, 12],
            13 => [0],
            17 => [13],
            _ => null,
        };
        if (slots is null)
            return false;
        InventoryManager* manager = InventoryManager.Instance();
        if (manager is null)
            return false;
        InventoryType[] sources = [InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
            InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
            InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
            InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
            InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
        foreach (InventoryType source in sources)
        {
            InventoryContainer* container = manager->GetInventoryContainer(source);
            if (container is null) continue;
            for (ushort slot = 0; slot < container->Size; slot++)
            {
                InventoryItem* found = container->GetInventorySlot(slot);
                if (found is not null && (found->ItemId == itemId || ItemUtil.GetBaseId(found->ItemId).ItemId == itemId))
                {
                    int result = manager->MoveItemSlot(source, slot, InventoryType.EquippedItems, slots[0], true);
                    this.LastUseItemResult = result;
                    return result == 0;
                }
            }
        }
        return false;
    }

    public bool IsItemEquipped(uint itemId)
    {
        InventoryManager* manager = InventoryManager.Instance();
        if (manager is null) return false;
        InventoryContainer* equipped = manager->GetInventoryContainer(InventoryType.EquippedItems);
        if (equipped is null) return false;
        for (int i = 0; i < equipped->Size; i++)
        {
            InventoryItem* item = equipped->GetInventorySlot(i);
            if (item is not null && (item->ItemId == itemId || ItemUtil.GetBaseId(item->ItemId).ItemId == itemId))
                return true;
        }
        return false;
    }
}
