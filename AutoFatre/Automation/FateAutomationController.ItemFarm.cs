namespace AutoFatre;

public sealed unsafe partial class FateAutomationController
{
    private IReadOnlyList<MapPreset>? itemFarmMaps;
    private string itemFarmToken = "", itemFarmResult = "Unknown";
    private bool itemFarmCleanupEnabled;
    private DateTime itemFarmCleanupDeadline;
    private AutomationMode EffectiveMode => this.itemFarmMaps is null ? this.configuration.Mode : AutomationMode.PresetSequence;
    public bool IsItemFarmActive => this.itemFarmMaps is not null;
    public string TemporaryFarmLabel => this.itemFarmMaps?[0].StopConditions[0].Kind == StopConditionKind.TargetFate
        ? "临时指定FATE" : "临时刷物品";
    public string ItemFarmStatus
    {
        get
        {
            if (this.itemFarmMaps is null) return "";
            var map = this.itemFarmMaps[0];
            var goal = map.StopConditions[0];
            return goal.Kind == StopConditionKind.TargetFate
                ? $"{map.Name} · 完成 {this.GetPresetStopConditionProgress(0, goal)}/{goal.RequiredProgress}"
                : $"{map.Name} · 背包 {this.inventoryCounter.Count(goal.ItemId)}/{goal.ItemCount}";
        }
    }

    // Synchronous Framework-only contract. No queued start survives an accepted stop.
    // Target is an absolute inventory count; callers can request baseline + desired gain.
    public string StartItemFarm(uint territory, uint item, int target)
    {
        if (!this.framework.IsInFrameworkUpdateThread || this.disposed || this.framework.IsFrameworkUnloading
            || territory == 0 || item == 0 || target <= 0 || this.IsRunning
            || this.state != AutomationState.Stopped || this.temporaryTargetActive || this.itemFarmMaps is not null)
            return "";
        var itemRow = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(item);
        var territoryRow = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory);
        if (itemRow is null || territoryRow is null || string.IsNullOrEmpty(itemRow.Value.Name.ToString())) return "";
        this.itemFarmToken = Guid.NewGuid().ToString("N");
        this.itemFarmResult = "Running";
        this.itemFarmCleanupEnabled = false;
        this.ResetExternalProgress();
        this.itemFarmMaps = new[] { new MapPreset {
            Name = $"{territoryRow.Value.PlaceName.Value.Name} · {itemRow.Value.Name}", TerritoryId = territory,
            StopConditions = [new StopCondition { Kind = StopConditionKind.ItemCount, ItemId = item, ItemCount = target }]
        } };
        try { this.StartCore(); }
        catch (Exception ex)
        {
            this.itemFarmResult = "Failed";
            this.statusReason = $"临时物品目标启动失败：{ex.Message}";
            // Return the reserved token even on failure so the caller can retry owned cleanup.
        }
        return this.itemFarmToken;
    }

    // Uses the same owned request/result/cleanup contract as item farming.
    // A private preset keeps the user's targets, mode and completion policy intact.
    public string StartFateFarm(ushort fateId, int requiredCount)
    {
        if (!this.framework.IsInFrameworkUpdateThread || this.disposed || this.framework.IsFrameworkUnloading
            || fateId == 0 || requiredCount <= 0 || this.IsRunning || this.state != AutomationState.Stopped
            || this.temporaryTargetActive || this.itemFarmMaps is not null
            || !this.staticFateTerritoryCatalog.TryGet(fateId, out FateTerritoryEntry? entry) || entry is null)
            return "";
        this.itemFarmToken = Guid.NewGuid().ToString("N");
        this.itemFarmResult = "Running";
        this.itemFarmCleanupEnabled = false;
        this.ResetExternalProgress();
        this.itemFarmMaps = new[] { new MapPreset {
            Name = $"指定 FATE #{fateId}", TerritoryId = entry.TerritoryId,
            TargetFallback = this.configuration.TargetFateFallback,
            StopConditions = [new StopCondition { Kind = StopConditionKind.TargetFate,
                TargetFateId = fateId, FateCount = requiredCount }]
        } };
        try { this.StartCore(); }
        catch (Exception ex)
        {
            this.itemFarmResult = "Failed";
            this.statusReason = $"临时 FATE 目标启动失败：{ex.Message}";
        }
        return this.itemFarmToken;
    }

    public string GetItemFarmResult(string token) => !string.IsNullOrEmpty(token) && token == this.itemFarmToken
        ? this.itemFarmResult : "Unknown";

    public bool StopItemFarm(string token)
    {
        if (!this.framework.IsInFrameworkUpdateThread || string.IsNullOrEmpty(token) || token != this.itemFarmToken)
            return false;
        if (this.itemFarmMaps is not null) this.Stop("所属 IPC 物品目标已停止");
        return true;
    }

    // Opt-in barrier: the caller cleans drops only after AF's real combat cleanup.
    public bool EnableItemFarmCleanup(string token)
    {
        if (!this.framework.IsInFrameworkUpdateThread || this.itemFarmMaps is null
            || token != this.itemFarmToken || this.itemFarmResult != "Running") return false;
        this.itemFarmCleanupEnabled = true;
        return true;
    }

    public bool ResumeItemFarmAfterCleanup(string token)
    {
        if (!this.framework.IsInFrameworkUpdateThread || this.itemFarmMaps is null
            || token != this.itemFarmToken || this.itemFarmResult != "WaitingForCleanup"
            || !this.configuration.Enabled || this.state != AutomationState.ScanningFates
            || this.IsCombatEngaged() || this.condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.Unconscious]
            || this.IsBetweenAreas() || DateTime.UtcNow < this.territoryStableAfter) return false;
        this.itemFarmResult = "Running";
        this.externalProgressAt = DateTime.UtcNow;
        if (this.clientState.TerritoryType != this.GetCurrentPlan().TerritoryId)
        {
            this.Transition(AutomationState.ValidatingPlan, "调用方清理完成；恢复后重新校验目标地图");
            return true;
        }
        this.presetConditionRecheckUntil = DateTime.UtcNow.AddSeconds(3);
        this.BeginCompanionCheckpoint(CompanionCheckpointKind.PostFate,
            AutomationState.ScanningFates, "调用方掉落清理完成，继续检查伙伴和物品目标");
        return true;
    }

    private bool WaitForItemFarmCleanup(DateTime now)
    {
        if (this.itemFarmMaps is null || this.itemFarmResult != "WaitingForCleanup") return false;
        if (now >= this.itemFarmCleanupDeadline)
        {
            this.Stop("等待调用方掉落清理超时，临时刷物品已停止");
            this.itemFarmResult = "Failed";
            return true;
        }
        // Keep native death/aggro recovery alive while preventing the next FATE.
        if (this.state is AutomationState.CleaningUpCombat or AutomationState.ResettingAggroViaDuty)
            return false;
        if (this.IsCombatEngaged())
        {
            this.BeginCombatCleanup(CleanupContinuation.RevalidatePlan, "清理等待期间重新接战，先脱战再交回调用方");
            return false;
        }
        if (this.IsBetweenAreas() || now < this.territoryStableAfter) return true;
        if (this.state != AutomationState.ScanningFates)
            this.Transition(AutomationState.ScanningFates, "恢复已结束，继续等待调用方清理掉落物");
        return true;
    }
}
