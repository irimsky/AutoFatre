using FieldNavigation;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;

namespace AutoFatre;

public sealed unsafe partial class FateAutomationController
{
    private enum ExchangePhase { Choose, Travel, WaitNavigation, Mount, Navigate, Dismount, Open, Buy, AwaitPurchase, Close, Return, ReturnInstance }
    private readonly VnavmeshStatusIpc exchangeNavigationStatus;
    private GemstoneExchangeCatalog exchangeCatalog = null!;
    private ShopExchangeAdapter exchangeShop = null!;
    private GemstoneExchangeSettings? exchangePlan;
    private ExchangePhase exchangePhase;
    private GemstoneProduct? exchangeProduct;
    private GemstoneVendor? exchangeVendor;
    private int exchangeRuleIndex;
    private bool exchangeRequested;
    private bool exchangeResumeFarm;
    private ulong exchangeCharacter;
    private uint exchangeReturnTerritory;
    private uint exchangeReturnInstance;
    private DateTime exchangePhaseStartedAt;
    private DateTime exchangeNextActionAt;
    private DateTime exchangeSettlementUntil;
    private DateTime exchangeUnavailableSince;
    private DateTime exchangeNavigationLogAt;
    private DateTime exchangeTravelStartedAt;
    private DateTime exchangeMountRetryAt;
    private ExchangePhase exchangeNavigationResumePhase;
    private bool exchangeTeleportOwned;
    private bool exchangeInstanceOwned;
    private uint exchangeHopTerritory;
    private DateTime exchangeTeleportStartedAt;
    private int exchangeMountAttempts;
    private bool exchangeGroundApproach;
    private LandingSession? exchangeLandingSession;
    private bool exchangeInteractionIssued;
    private int exchangeInteractionAttempts;
    private int exchangeBatch;
    private int exchangeOwnedBefore;
    private uint exchangeBalanceBefore;
    private int exchangeBought;
    private uint exchangeSpent;
    private string exchangeStatus = "未运行";
    private DateTime exchangeGateLogAt = DateTime.MinValue;
    private string exchangeGateLogMessage = string.Empty;
    private string? exchangeCompletedContext;
    private DateTime exchangeContextCheckAt;
    private readonly HashSet<(uint Shop, uint Npc, uint Territory)> exchangeUnavailableShops = [];

    public GemstoneExchangeCatalog ExchangeCatalog => this.exchangeCatalog;
    public bool IsExchanging => this.exchangePlan is not null;
    public bool ExchangePending => this.exchangeRequested;
    public string ExchangeStatus => this.exchangeStatus;
    public ItemUseStatus GetExchangeItemUseStatus(GemstoneProduct product) => ItemUnlockStatusReader.Read(product,
        this.clientState.IsLoggedIn && this.playerState.IsLoaded && !this.IsBetweenAreas());

    public bool TryGetGemstones(out uint balance, out uint cap)
    {
        balance = cap = 0;
        if (!this.clientState.IsLoggedIn || !this.playerState.IsLoaded || this.IsBetweenAreas()) return false;
        CurrencyManager* currency = CurrencyManager.Instance();
        if (currency == null || !currency->ItemBucket.TryGetValue(GemstoneExchangeCatalog.CurrencyItemId, out var item, copyCtor: false)) return false;
        balance = item.Count;
        cap = item.MaxCount;
        return cap > 0;
    }

    private bool IsAutomaticExchangeDue()
    {
        GemstoneExchangeSettings settings = this.configuration.GemstoneExchange;
        if (!settings.Enabled || !this.TryGetGemstones(out uint balance, out uint cap))
            return false;

        return balance >= Math.Min((uint)settings.Threshold, cap) && !this.IsExchangeContextUnchanged(settings, balance);
    }

    // After a full pass, wait for a meaningful input change rather than visiting the same
    // unavailable shops every scan. Manual requests always bypass this in-memory suppression.
    private bool IsExchangeContextUnchanged(GemstoneExchangeSettings settings, uint balance)
    {
        if (this.exchangeCompletedContext is null) return false;
        DateTime now = DateTime.UtcNow;
        if (now < this.exchangeContextCheckAt) return true;
        this.exchangeContextCheckAt = now.AddSeconds(1);
        if (this.exchangeCompletedContext == this.ExchangeAttemptContext(settings, balance)) return true;
        this.exchangeCompletedContext = null;
        return false;
    }

    private string ExchangeAttemptContext(GemstoneExchangeSettings settings, uint balance)
    {
        var context = new System.Text.StringBuilder();
        context.Append(this.playerState.ContentId).Append('|').Append(balance).Append('|')
            .Append(settings.Enabled).Append('|').Append(settings.Threshold).Append('|').Append(settings.Reserve);
        foreach (var rule in settings.Rules)
        {
            var product = this.exchangeCatalog.Find(rule);
            context.Append(';').Append(rule.Id).Append(',').Append(rule.Enabled).Append(',').Append(rule.ItemId)
                .Append(',').Append(rule.ShopId).Append(',').Append(rule.NpcId).Append(',').Append(rule.TerritoryId)
                .Append(',').Append(rule.QuantityMode).Append(',').Append(rule.TargetQuantity).Append(',').Append(rule.SkipIfUsed)
                .Append(',').Append(this.inventoryCounter.Count(rule.ItemId));
            if (product is not null)
                context.Append(',').Append(this.inventoryCounter.PurchaseCapacity(product.ItemId, product.StackSize))
                    .Append(',').Append(this.GetExchangeItemUseStatus(product));
        }
        return context.ToString();
    }

    public void RequestGemstoneExchange() => _ = this.framework.RunOnFrameworkThread(() =>
    {
        if (this.disposed || this.framework.IsFrameworkUnloading) return;
        if (this.IsExchanging) return;
        if (this.condition[ConditionFlag.BoundByDuty] || this.condition[ConditionFlag.BoundByDuty56]
            || this.condition[ConditionFlag.BoundByDuty95]) { this.exchangeStatus = "副本内不能兑换双色宝石"; return; }
        if ((this.itemFarmMaps is not null || this.temporaryTargetActive) && !this.allowExchangeForExternalFarm
            || this.navigationOnlyRequested || this.manualDutyRoundTrip)
        { this.exchangeStatus = "临时任务或仅导航期间不能兑换"; return; }
        if (this.state is AutomationState.Paused or AutomationState.Faulted)
        { this.exchangeStatus = "请先继续或停止当前任务，再立即兑换"; return; }
        if (!this.exchangeCatalog.IsLoaded) { this.exchangeStatus = this.exchangeCatalog.Error ?? "商品目录尚未加载"; return; }
        this.exchangeRequested = true;
        this.exchangeStatus = "已请求兑换，等待角色可操作并完成本场 FATE、脱战及奖励结算";
        if (!this.configuration.Enabled && this.state == AutomationState.Stopped)
            this.TryBeginExchange(DateTime.UtcNow, manual: true);
    });

    public void RequestCancelGemstoneExchange() => _ = this.framework.RunOnFrameworkThread(() =>
    {
        if (this.disposed || this.framework.IsFrameworkUnloading) return;
        if (!this.IsExchanging) { this.exchangeRequested = false; this.exchangeStatus = "已取消待兑换请求"; return; }
        this.Stop("已取消自动兑换，保持停止");
    });

    private bool TryBeginExchange(DateTime now, bool manual = false)
    {
        var settings = this.configuration.GemstoneExchange;
        bool hasGemstones = this.TryGetGemstones(out uint balance, out uint cap);
        bool due = hasGemstones && balance >= Math.Min((uint)settings.Threshold, cap);
        string? gate = null;
        if (this.IsExchanging) gate = "已经在兑换流程中";
        else if ((this.itemFarmMaps is not null || this.temporaryTargetActive) && !this.allowExchangeForExternalFarm) gate = "当前是临时任务且 IPC 未授权兑换";
        else if (this.navigationOnlyRequested) gate = "当前是仅导航模式";
        else if (this.manualDutyRoundTrip) gate = "当前正在执行副本进退";
        else if (this.activeFateId is not null) gate = $"当前 FATE 尚未清理（FATE={this.activeFateId.Value}）";
        else if (this.pendingFateResult is not null) gate = "当前仍有待结算 FATE";
        else if (!this.clientState.IsLoggedIn || !this.playerState.IsLoaded || this.objectTable.LocalPlayer is null) gate = "角色数据尚未加载";
        else if (this.IsBetweenAreas()) gate = "当前正在切图";
        else if (now < this.territoryStableAfter) gate = $"区域尚未稳定（还需 {(this.territoryStableAfter - now).TotalSeconds:0.0}s）";
        else if (this.condition[ConditionFlag.InCombat]) gate = "当前仍在战斗";
        else if (this.condition[ConditionFlag.Unconscious]) gate = "当前角色已倒地";
        else if (this.condition[ConditionFlag.BoundByDuty] || this.condition[ConditionFlag.BoundByDuty56] || this.condition[ConditionFlag.BoundByDuty95]) gate = "当前在副本内";
        else if (this.condition[ConditionFlag.Casting] || this.condition[ConditionFlag.Casting87]) gate = "当前正在施法";
        else if (now < this.exchangeSettlementUntil) gate = $"等待 FATE 奖励结算（还需 {(this.exchangeSettlementUntil - now).TotalSeconds:0.0}s）";
        else if (this.lifestream.IsBusy) gate = "Lifestream 正忙";
        else if (!this.exchangeRequested && !(settings.Enabled || this.allowExchangeForExternalFarm)) gate = "自动兑换未启用，且 IPC 未授权兑换";
        else if (!this.exchangeRequested && !hasGemstones) gate = "无法读取双色宝石数量";
        else if (!this.exchangeRequested && !due) gate = hasGemstones
            ? $"未达到门槛（余额={balance}，门槛={Math.Min((uint)settings.Threshold, cap)}，上限={cap}）"
            : "无法读取双色宝石数量";
        else if (!this.exchangeCatalog.IsLoaded) gate = $"商品目录未加载：{this.exchangeCatalog.Error ?? "无错误信息"}";
        else if (!hasGemstones) gate = "无法读取双色宝石数量";
        else if (!this.exchangeRequested && this.IsExchangeContextUnchanged(settings, balance)) gate = "本轮兑换项目已检查，等待余额、计划或背包状态变化；可立即兑换重新检查解锁状态";
        else if (this.exchangeShop.HasBlockingUi || this.condition[ConditionFlag.OccupiedInQuestEvent]) gate = "已有对话/商店界面占用";

        if (gate is not null)
        {
            this.LogExchangeGate(now, gate, due || this.exchangeRequested);
            if (this.exchangeShop.HasBlockingUi || this.condition[ConditionFlag.OccupiedInQuestEvent])
                this.exchangeStatus = "等待已有对话或商店关闭后兑换";
            return false;
        }

        uint currencyCap = cap;
        if (this.exchangeShop.HasBlockingUi || this.condition[ConditionFlag.OccupiedInQuestEvent])
        { this.exchangeStatus = "等待已有对话或商店关闭后兑换"; return false; }
        this.exchangeRequested = false;
        this.exchangeCompletedContext = null;
        this.exchangeUnavailableShops.Clear();
        this.exchangePlan = settings.Copy();
        this.exchangePlan.Threshold = Math.Min(this.exchangePlan.Threshold, (int)currencyCap);
        this.exchangePlan.Normalize();
        this.exchangeResumeFarm = !manual && this.configuration.Enabled;
        // Capture the implicit current-map plan before visiting a vendor in another territory.
        if (this.exchangeResumeFarm) this.GetCurrentPlan();
        this.exchangeReturnTerritory = this.clientState.TerritoryType;
        this.exchangeReturnInstance = GetPublicInstanceId();
        this.exchangeCharacter = this.playerState.ContentId;
        this.exchangeRuleIndex = this.exchangeBought = 0;
        this.exchangeSpent = 0;
        this.exchangeProduct = null;
        this.exchangeVendor = null;
        this.exchangeBatch = 0;
        this.exchangeUnavailableSince = DateTime.MinValue;
        this.CancelOwnedActions();
        this.textAdvance.Disable();
        this.textAdvance.EnableForExchange();
        this.ClearTarget();
        this.startupFateRangeSelectionPending = false;
        this.noFateStopTimer.Reset();
        this.SetExchangePhase(ExchangePhase.Choose, now);
        this.Transition(AutomationState.ExchangingGemstones, "开始按列表顺序兑换双色宝石");
        return true;
    }

    private void LogExchangeGate(DateTime now, string reason, bool important)
    {
        if (!important && now - this.exchangeGateLogAt < TimeSpan.FromSeconds(10))
            return;
        if (reason == this.exchangeGateLogMessage && now - this.exchangeGateLogAt < TimeSpan.FromSeconds(3))
            return;
        this.exchangeGateLogAt = now;
        this.exchangeGateLogMessage = reason;
        this.AddDiagnostic(important ? DiagnosticSeverity.Warning : DiagnosticSeverity.Debug,
            $"自动兑换入口未启动：{reason}；Enabled={this.configuration.GemstoneExchange.Enabled}，" +
            $"IPC授权={this.allowExchangeForExternalFarm}，Requested={this.exchangeRequested}，" +
            $"State={this.state}，余额读取={(this.TryGetGemstones(out uint balance, out uint cap) ? $"{balance}/{cap}" : "失败")}，" +
            $"阈值={this.configuration.GemstoneExchange.Threshold}");
    }

    private void SetExchangePhase(ExchangePhase phase, DateTime now)
    {
        this.exchangePhase = phase;
        this.exchangePhaseStartedAt = now;
        this.exchangeNextActionAt = now.AddMilliseconds(400);
        this.exchangeNavigationLogAt = DateTime.MinValue;
    }

    /// <summary>Owns the entire tick while exchanging, including manual runs with Enabled=false.</summary>
    private bool HandleExchangeExclusive(DateTime now)
    {
        if (!this.IsExchanging)
        {
            // Complete cancellation cleanup without ever advancing an unowned dialogue.
            if (this.exchangeShop?.HasOwnership == true) this.exchangeShop.CloseOwned();
            if (this.exchangeRequested && !this.configuration.Enabled && this.state == AutomationState.Stopped)
                this.TryBeginExchange(now, manual: true);
            return this.IsExchanging;
        }
        if (this.state is AutomationState.Paused or AutomationState.Faulted)
        { this.exchangeShop.CloseOwned(); return true; }
        try
        {
            if (!this.clientState.IsLoggedIn || !this.playerState.IsLoaded || this.objectTable.LocalPlayer is null
                || this.IsBetweenAreas() || now < this.territoryStableAfter)
            {
                if (this.exchangeUnavailableSince == DateTime.MinValue) this.exchangeUnavailableSince = now;
                if (now - this.exchangeUnavailableSince > TimeSpan.FromSeconds(120)) this.FailExchange("等待角色或区域加载超时");
                return true;
            }
            this.exchangeUnavailableSince = DateTime.MinValue;
            if (this.playerState.ContentId != this.exchangeCharacter) { this.Stop("角色已改变，已取消原角色兑换"); return true; }
            if (this.condition[ConditionFlag.Unconscious])
            {
                if (this.state is not AutomationState.DeadWaitingForRaise and not AutomationState.DeadReturning) this.EnterDeadState();
                this.HandleDeath(now);
                return true;
            }
            if (this.state is AutomationState.DeadWaitingForRaise or AutomationState.DeadReturning)
            {
                this.exchangeShop.ForgetOwnership();
                this.textAdvance.EnableForExchange();
                this.SetExchangePhase(this.ExchangeRecoveryPhase(), now);
                this.Transition(AutomationState.ExchangingGemstones, "复活后核对剩余兑换计划");
            }
            if (this.condition[ConditionFlag.InCombat]) { this.FailExchange("兑换途中接战，已停止移动和购买，请脱战后继续"); return true; }
            if (this.condition[ConditionFlag.Casting] || this.condition[ConditionFlag.Casting87])
            { this.StopNavigationOperation(); return true; }
            if (!this.TryGetGemstones(out uint balance, out _))
            {
                if (now - this.exchangePhaseStartedAt > TimeSpan.FromSeconds(15)) this.FailExchange("双色宝石数据未就绪");
                return true;
            }
            if (now < this.exchangeNextActionAt) return true;
            this.exchangeNextActionAt = now.AddMilliseconds(400);
            if (this.IsExchangeTravelPhase && now - this.exchangeTravelStartedAt > TimeSpan.FromSeconds(180))
            { this.FailExchange($"前往兑换商人超过 180 秒，当前步骤={this.exchangePhase}"); return true; }
            if (!this.IsExchangeTravelPhase && this.exchangePhase is not ExchangePhase.Return and not ExchangePhase.ReturnInstance
                && now - this.exchangePhaseStartedAt > TimeSpan.FromSeconds(30))
            { this.FailExchange("兑换步骤超时，未自动重复购买"); return true; }
            switch (this.exchangePhase)
            {
                case ExchangePhase.Choose: this.ChooseExchangeRule(now, balance); break;
                case ExchangePhase.Travel: this.TravelToExchangeVendor(now); break;
                case ExchangePhase.WaitNavigation: this.WaitForExchangeNavigation(now); break;
                case ExchangePhase.Mount: this.MountForExchangeVendor(now); break;
                case ExchangePhase.Navigate: this.NavigateToExchangeVendor(now); break;
                case ExchangePhase.Dismount: this.DismountAtExchangeVendor(now); break;
                case ExchangePhase.Open: this.OpenExchangeShop(now); break;
                case ExchangePhase.Buy: this.BuyExchangeBatch(now, balance); break;
                case ExchangePhase.AwaitPurchase: this.ObserveExchangePurchase(now, balance); break;
                case ExchangePhase.Close:
                    if (this.exchangeShop.CloseOwned() && !this.condition[ConditionFlag.OccupiedInQuestEvent])
                        this.SetExchangePhase(ExchangePhase.Choose, now);
                    break;
                case ExchangePhase.Return:
                    if (this.ExchangeEnsureTerritory(now, this.exchangeReturnTerritory))
                        this.SetExchangePhase(ExchangePhase.ReturnInstance, now);
                    break;
                case ExchangePhase.ReturnInstance:
                    this.RestoreExchangeInstance(now);
                    break;
            }
            this.statusReason = this.exchangeStatus;
        }
        catch (Exception ex) { this.FailExchange($"兑换异常：{ex.Message}"); }
        return true;
    }

    private void ChooseExchangeRule(DateTime now, uint balance)
    {
        if (this.exchangePlan is not { } plan) return;
        while (this.exchangeRuleIndex < plan.Rules.Count)
        {
            var rule = plan.Rules[this.exchangeRuleIndex];
            var product = this.exchangeCatalog.Find(rule);
            if (!rule.Enabled) { this.exchangeRuleIndex++; continue; }
            if (product is null)
            {
                this.SkipExchangeRule(now, "商品或商人数据失效，请重新选择");
                continue;
            }
            string? skipReason = this.ExchangeRuleSkipReason(plan, rule, product, balance, out bool waiting);
            if (waiting) { this.exchangeStatus = "等待角色收藏解锁状态"; return; }
            if (skipReason is not null) { this.SkipExchangeRule(now, skipReason); continue; }
            var vendor = product.Vendors.First(v => v.NpcId == rule.NpcId && v.TerritoryId == rule.TerritoryId);
            bool reuse = this.exchangeShop.HasShop && this.exchangeVendor?.NpcId == vendor.NpcId
                && this.exchangeVendor.TerritoryId == vendor.TerritoryId;
            if (this.exchangeShop.HasOwnership && !reuse)
            { this.SetExchangePhase(ExchangePhase.Close, now); return; }
            this.exchangeProduct = product;
            this.exchangeVendor = vendor;
            this.exchangeInteractionIssued = false;
            this.exchangeInteractionAttempts = 0;
            this.exchangeMountAttempts = 0;
            this.exchangeMountRetryAt = DateTime.MinValue;
            this.exchangeGroundApproach = false;
            this.exchangeTravelStartedAt = now;
            this.SetExchangePhase(reuse ? ExchangePhase.Buy : ExchangePhase.Travel, now);
            this.exchangeStatus = reuse ? $"继续在当前商店兑换 {product.Name}"
                : $"前往 {vendor.Zone}·{vendor.Name}，兑换 {product.Name}";
            return;
        }
        if (this.exchangeShop.HasOwnership)
        { this.SetExchangePhase(ExchangePhase.Close, now); return; }
        this.exchangeCompletedContext = this.ExchangeAttemptContext(plan, balance);
        this.exchangeContextCheckAt = DateTime.MinValue;
        if (this.exchangeResumeFarm)
        {
            this.SetExchangePhase(ExchangePhase.Return, now);
            this.exchangeStatus = $"已兑换 {this.exchangeBought} 件，消耗 {this.exchangeSpent} 宝石，返回原刷图地图";
        }
        else this.CompleteExchange();
    }

    private string? ExchangeRuleSkipReason(GemstoneExchangeSettings plan, GemstoneExchangeRule rule,
        GemstoneProduct product, uint balance, out bool waiting)
    {
        waiting = false;
        if (this.exchangeUnavailableShops.Contains((rule.ShopId, rule.NpcId, rule.TerritoryId)))
            return "本轮已确认该商店尚未解锁";
        if (product.Cost == 0 || product.ReceiveCount == 0) return "商品价格或产出数据无效";
        if (rule.SkipIfUsed)
        {
            ItemUseStatus use = this.GetExchangeItemUseStatus(product);
            if (use == ItemUseStatus.WaitingForCharacter) { waiting = true; return null; }
            if (use == ItemUseStatus.Unsupported) return "不支持已使用检测，请调整此项设置";
            if (use == ItemUseStatus.Used) return "当前角色已使用";
        }
        int owned = this.inventoryCounter.Count(product.ItemId);
        if (rule.QuantityMode == GemstoneExchangeQuantityMode.BagTarget && owned >= rule.TargetQuantity)
            return $"背包数量已满足目标（背包={owned}，目标={rule.TargetQuantity}）";
        if (product.Unique && owned > 0) return "背包已有该唯一物品";
        uint available = balance > plan.Reserve ? balance - (uint)plan.Reserve : 0;
        if (available < product.Cost)
            return $"可用宝石不足一笔（余额={balance}，保留={plan.Reserve}，可用={available}，单笔价格={product.Cost}）";
        if (this.inventoryCounter.PurchaseCapacity(product.ItemId, product.StackSize) < product.ReceiveCount)
            return "背包没有空间容纳一笔兑换";
        return null;
    }

    private bool ExchangeEnsureTerritory(DateTime now, uint destination, Vector3? targetPosition = null)
    {
        if (!this.IsExchangeTravelPhase && now - this.exchangePhaseStartedAt > TimeSpan.FromSeconds(180)) { this.FailExchange("兑换返程传送超过 180 秒"); return false; }
        if (this.exchangeTeleportOwned)
        {
            if (!this.lifestream.IsBusy && this.clientState.TerritoryType == this.exchangeHopTerritory
                && now - this.exchangeTeleportStartedAt > TimeSpan.FromSeconds(2))
            {
                this.exchangeTeleportOwned = false;
                if (this.clientState.TerritoryType == destination)
                    return true;
            }
            else if (now - this.exchangeTeleportStartedAt > TimeSpan.FromSeconds(120)) this.FailExchange("兑换传送超时");
            return false;
        }
        if (this.clientState.TerritoryType == destination) return true;
        if (!this.lifestream.IsAvailable) { this.FailExchange("Lifestream 传送 IPC 不可用，请确认插件已加载"); return false; }
        if (this.lifestream.IsBusy || this.condition[ConditionFlag.Casting]) return false;
        uint? aethernet = this.ResolveAethernetDestination(destination);
        AetheryteTravelPlan? nearest = targetPosition is { } target
            && this.aetheryteTravelPlanner.TryFindNearest(destination, target, out AetheryteTravelPlan nearestPlan)
            ? nearestPlan
            : null;
        var entry = nearest is not null
            ? this.ResolveTeleportAetheryte(destination, nearest.AetheryteId)
            : this.ResolveTeleportAetheryte(destination, 0);
        bool accepted;
        uint hop = destination;
        if (entry is null && aethernet is { } place)
        {
            if (this.clientState.TerritoryType == this.ResolveIdyllshireTerritory())
                accepted = this.lifestream.IsAethernetAvailable && this.lifestream.AethernetTeleportByPlaceNameId(place);
            else
            {
                hop = this.ResolveIdyllshireTerritory();
                entry = this.ResolveTeleportAetheryte(hop, 0);
                accepted = entry is not null && this.lifestream.Teleport(entry.AetheryteId, entry.SubIndex);
            }
        }
        else accepted = entry is not null && this.lifestream.Teleport(entry.AetheryteId, entry.SubIndex);
        if (!accepted) { this.FailExchange("目标地图没有可用传送入口，或 Lifestream 拒绝兑换传送"); return false; }
        if (nearest is not null)
        {
            this.AddDiagnostic(
                DiagnosticSeverity.Information,
                $"自动兑换：按 NPC 位置选择最近已解锁水晶：aetheryte={nearest.AetheryteId}:{nearest.SubIndex}，"
                + $"距商人={nearest.DistanceToTarget:0.0}，目标地图={destination}，目标坐标={targetPosition}");
        }
        this.exchangeTeleportOwned = true;
        this.exchangeHopTerritory = hop;
        this.exchangeTeleportStartedAt = now;
        return false;
    }

    private void TravelToExchangeVendor(DateTime now)
    {
        if (this.exchangeVendor is not { } vendor) return;
        if (!this.ExchangeEnsureTerritory(now, vendor.TerritoryId, vendor.Position)) return;
        this.exchangeNavigationResumePhase = ExchangePhase.Mount;
        this.SetExchangePhase(ExchangePhase.WaitNavigation, now);
        this.exchangeStatus = "已到达商人地图，等待导航网格就绪";
    }

    private bool IsExchangeTravelPhase => this.exchangePhase is ExchangePhase.Travel or ExchangePhase.WaitNavigation
        or ExchangePhase.Mount or ExchangePhase.Navigate or ExchangePhase.Dismount;

    private bool TryGetExchangeDestination(out IGameObject? npc, out Vector3 destination, out bool arrived)
    {
        npc = null;
        destination = default;
        arrived = false;
        if (this.exchangeVendor is not { } vendor) return false;
        if (this.clientState.TerritoryType != vendor.TerritoryId)
        { this.FailExchange("前往兑换商人时地图发生变化"); return false; }
        if (this.exchangeShop.HasBlockingUi || this.condition[ConditionFlag.OccupiedInQuestEvent])
        { this.FailExchange("前往兑换商人时出现其他对话或商店，请关闭后继续"); return false; }
        npc = this.FindExchangeVendor(vendor);
        destination = npc?.Position ?? vendor.Position;
        arrived = npc is not null && Vector3.Distance(this.objectTable.LocalPlayer!.Position, destination) <= 3.5f;
        return true;
    }

    // Questionable WaitNavmesh and GatherBuddy VendorNavigator.WaitingForZoneLoad both
    // wait for the territory mesh before entering movement. Provider loss is a separate failure.
    private void WaitForExchangeNavigation(DateTime now)
    {
        if (!this.TryGetExchangeDestination(out _, out _, out bool arrived)) return;
        if (arrived)
        { this.BeginExchangeDismount(now); return; }
        if (!this.exchangeNavigationStatus.TryRead(out bool ready, out float? progress))
        { this.FailExchange("vnavmesh 导航 IPC 未注册或调用失败，请确认插件已加载"); return; }
        if (ready)
        {
            this.AddDiagnostic(DiagnosticSeverity.Information,
                $"自动兑换：地图 {this.clientState.TerritoryType} 导航网格已就绪，继续步骤 {this.exchangeNavigationResumePhase}");
            this.SetExchangePhase(this.exchangeNavigationResumePhase, now);
            return;
        }
        this.exchangeStatus = $"等待地图 {this.clientState.TerritoryType} 的导航网格加载"
            + (progress is { } value ? $"（{value:P0}）" : string.Empty);
        if (now >= this.exchangeNavigationLogAt)
        {
            this.exchangeNavigationLogAt = now.AddSeconds(10);
            this.AddDiagnostic(DiagnosticSeverity.Information, $"自动兑换：{this.exchangeStatus}；导航 IPC 已注册，Nav.IsReady=false");
        }
    }

    private bool ExchangeCanFly()
    {
        var territory = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
            .GetRowOrDefault(this.clientState.TerritoryType);
        var nativePlayer = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        return territory is { Mount: true, AetherCurrentCompFlgSet.RowId: > 0 } && nativePlayer != null
            && nativePlayer->IsAetherCurrentZoneComplete(territory.Value.AetherCurrentCompFlgSet.RowId);
    }

    private void MountForExchangeVendor(DateTime now)
    {
        if (!this.TryGetExchangeDestination(out _, out _, out bool arrived)) return;
        if (arrived) { this.BeginExchangeDismount(now); return; }
        if (this.mount.IsMountTransition || this.mount.IsJumping)
        { this.exchangeStatus = "等待坐骑切换或跳跃结束"; return; }
        var territory = this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()
            .GetRowOrDefault(this.clientState.TerritoryType);
        if (territory is null) { this.FailExchange("无法读取商人地图的坐骑许可"); return; }
        if (!this.ExchangeCanFly() || this.GetCurrentNoFlyZone() is not null)
            this.exchangeGroundApproach = true;
        if (this.mount.IsReadyForNavigation || this.mount.IsInFlight)
        { this.SetExchangePhase(ExchangePhase.Navigate, now); return; }
        if (now < this.exchangeMountRetryAt || now < this.mount.NextMountRequestAt)
        { this.exchangeStatus = "等待上坐骑结果"; return; }
        if (!territory.Value.Mount || this.exchangeMountAttempts >= 3)
        {
            this.exchangeGroundApproach = true;
            this.AddDiagnostic(DiagnosticSeverity.Information,
                $"自动兑换：改用地面导航；地图允许坐骑={territory.Value.Mount}，上坐骑尝试={this.exchangeMountAttempts}");
            this.SetExchangePhase(ExchangePhase.Navigate, now);
            return;
        }
        if (!this.mount.CanAttemptMount)
        { this.exchangeStatus = $"等待坐骑操作可用：{this.mount.MountBlockReason}"; return; }
        // Questionable waits five seconds for the mounted state before retrying a request.
        this.ClearTarget();
        bool sent = this.mount.TryMount();
        this.exchangeMountAttempts++;
        this.exchangeMountRetryAt = now.AddSeconds(5);
        this.exchangeStatus = sent ? "已请求坐骑，等待上坐骑完成" : "坐骑请求未接受，等待后重试";
        this.AddDiagnostic(DiagnosticSeverity.Information,
            $"自动兑换：请求坐骑，结果={sent}，尝试={this.exchangeMountAttempts}/3，允许飞行={this.ExchangeCanFly()}");
    }

    private void NavigateToExchangeVendor(DateTime now)
    {
        if (!this.TryGetExchangeDestination(out _, out Vector3 destination, out bool arrived)) return;
        if (arrived) { this.BeginExchangeDismount(now); return; }
        if (!this.exchangeNavigationStatus.TryRead(out bool ready, out _))
        { this.FailExchange("vnavmesh 导航 IPC 未注册或调用失败，请确认插件已加载"); return; }
        if (!ready)
        {
            this.StopNavigationOperation();
            this.exchangeNavigationResumePhase = ExchangePhase.Navigate;
            this.SetExchangePhase(ExchangePhase.WaitNavigation, now);
            return;
        }
        if (this.mount.IsMountTransition || this.mount.IsJumping && !this.travelSessionActive) return;
        if (!this.mount.IsMounted && !this.mount.IsInFlight)
            this.exchangeGroundApproach = true;
        var player = this.objectTable.LocalPlayer!;
        bool forceLanding = this.mount.IsInFlight && Vector3.Distance(player.Position, destination) <= 20f;
        if (forceLanding)
            this.exchangeGroundApproach = true;
        this.StartTravelSession(now, NavigationPurpose.GemstoneVendor, destination,
            fly: !this.exchangeGroundApproach && this.ExchangeCanFly(),
            GroundDestinationKind.Raw, horizontalProgress: false, "自动兑换商人");
        var update = this.TickTravelSession(now, destination, () => arrived && !this.mount.IsInFlight);
        if (update.Outcome == NavigationTravelOutcome.Failed || update.RequestResult == NavigationRequestResult.Rejected)
            this.FailExchange($"前往兑换商人失败：{update.Reason}");
        else this.exchangeStatus = $"前往 {this.exchangeVendor!.Name}，距离 {Vector3.Distance(player.Position, destination):0.0} 码，"
            + (this.mount.IsInFlight ? "飞行中" : this.mount.IsMounted ? "坐骑中" : "步行");
    }

    private void BeginExchangeDismount(DateTime now)
    {
        this.StopNavigationOperation();
        this.exchangeLandingSession ??= new LandingSession(this.vnavmesh, this.landing);
        this.exchangeLandingSession.BeginVerticalDescent();
        this.SetExchangePhase(ExchangePhase.Dismount, now);
    }

    private void DismountAtExchangeVendor(DateTime now)
    {
        if (!this.TryGetExchangeDestination(out var npc, out _, out bool arrived)) return;
        if (!arrived)
        {
            this.exchangeLandingSession?.Reset();
            this.SetExchangePhase(ExchangePhase.Navigate, now);
            return;
        }
        if (now - this.exchangePhaseStartedAt > TimeSpan.FromSeconds(12))
        { this.FailExchange("商人附近落地或下坐骑超过 12 秒"); return; }
        this.exchangeStatus = "已接近商人，等待落地稳定并下坐骑";
        if (this.exchangeLandingSession!.Update(now, this.objectTable.LocalPlayer!.Position,
                new FlightState(this.mount.IsInFlight, this.mount.IsJumping, this.mount.IsMountTransition)) != LandingStatus.Landed)
            return;
        if (!this.mount.IsDismounted) { this.mount.TryDismount(); return; }
        this.exchangeLandingSession.Reset();
        this.targetManager.Target = npc;
        this.exchangeShop.Begin(this.exchangeVendor!.NpcId, npc!.GameObjectId);
        this.SetExchangePhase(ExchangePhase.Open, now);
    }

    private void OpenExchangeShop(DateTime now)
    {
        if (this.exchangeVendor is not { } vendor || this.exchangeProduct is not { } product) return;
        if (this.clientState.TerritoryType != vendor.TerritoryId) { this.FailExchange("打开兑换商店时地图发生变化"); return; }
        var npc = this.FindExchangeVendor(vendor);
        if (npc is null || Vector3.Distance(npc.Position, this.objectTable.LocalPlayer!.Position) > 4f)
        { this.FailExchange("兑换 NPC 消失或已离开交互范围"); return; }
        if (!this.exchangeShop.IsExpectedTarget) { this.FailExchange("兑换交互目标已改变"); return; }
        if (this.exchangeInteractionIssued)
        {
            this.exchangeShop.AdvanceOpening(product, vendor.NpcId, out string? error, out bool unavailable);
            if (unavailable)
            {
                this.exchangeUnavailableShops.Add((product.ShopId, vendor.NpcId, vendor.TerritoryId));
                this.SkipExchangeRule(now, error ?? "商店尚未解锁");
                return;
            }
            if (error is not null) { this.FailExchange(error); return; }
            if (this.exchangeShop.HasShop)
            { this.SetExchangePhase(ExchangePhase.Buy, now); return; }
            if (this.exchangeShop.HasBlockingUi) return;
        }
        if (this.exchangeInteractionAttempts >= 3) return;
        this.exchangeInteractionAttempts++;
        long result = (long)TargetSystem.Instance()->InteractWithObject(
            (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address, false);
        this.exchangeInteractionIssued = result != 0;
        this.exchangeStatus = $"正在打开 {vendor.Name} 的双色宝石商店";
        this.exchangeNextActionAt = now.AddSeconds(1);
    }

    private IGameObject? FindExchangeVendor(GemstoneVendor vendor) => this.objectTable
        .Where(o => o.BaseId == vendor.NpcId && o.ObjectKind == ObjectKind.EventNpc && o.IsTargetable)
        .OrderBy(o => Vector3.DistanceSquared(o.Position, this.objectTable.LocalPlayer!.Position)).FirstOrDefault();

    private void BuyExchangeBatch(DateTime now, uint balance)
    {
        if (this.exchangePlan is not { } plan || this.exchangeProduct is not { } product) return;
        var rule = plan.Rules[this.exchangeRuleIndex];
        string? skipReason = this.ExchangeRuleSkipReason(plan, rule, product, balance, out bool waiting);
        if (waiting) { this.exchangeStatus = "购买前等待角色收藏解锁状态"; return; }
        if (skipReason is not null) { this.SkipExchangeRule(now, skipReason); return; }
        int owned = this.inventoryCounter.Count(product.ItemId);
        int affordable = balance > plan.Reserve ? (int)((balance - plan.Reserve) / product.Cost) : 0;
        int desired = rule.QuantityMode == GemstoneExchangeQuantityMode.SpendRemaining ? affordable
            : (int)(((long)rule.TargetQuantity - owned + product.ReceiveCount - 1) / product.ReceiveCount);
        int capacity = this.inventoryCounter.PurchaseCapacity(product.ItemId, product.StackSize) / (int)product.ReceiveCount;
        int batch = Math.Min(99, Math.Min(desired, Math.Min(affordable, capacity)));
        if (product.Unique) batch = Math.Min(1, batch);
        if (batch <= 0)
        {
            this.AddDiagnostic(DiagnosticSeverity.Information,
                $"自动兑换：结束第 {this.exchangeRuleIndex + 1} 项「{product.Name}」，本次可兑换数量={batch}，"
                + $"背包={owned}，余额={balance}，保留={plan.Reserve}，单笔价格={product.Cost}；继续检查下一项");
            this.FinishExchangeRule(now); return;
        }
        // Save the reconciliation context before any native callback can submit the exchange.
        this.exchangeBatch = batch;
        this.exchangeOwnedBefore = owned;
        this.exchangeBalanceBefore = balance;
        DateTime buyStartedAt = this.exchangePhaseStartedAt;
        this.SetExchangePhase(ExchangePhase.AwaitPurchase, now);
        if (this.exchangeShop.Submit(product, batch, out _, out string? error, out ExchangeShopIssue issue))
        {
            this.exchangeStatus = $"正在兑换 {product.Name} × {batch * product.ReceiveCount}，等待确认与到账";
        }
        else
        {
            this.exchangeBatch = 0;
            if (issue == ExchangeShopIssue.Unavailable)
            { this.SkipExchangeRule(now, error ?? "商品尚未解锁"); return; }
            if (issue == ExchangeShopIssue.NotReady)
            {
                // Keep the original step deadline while waiting; do not turn transient
                // addon setup into an unavailable item or reset its timeout every tick.
                this.exchangePhase = ExchangePhase.Buy;
                this.exchangePhaseStartedAt = buyStartedAt;
                this.exchangeStatus = error ?? "等待商店商品数据";
                return;
            }
            this.SetExchangePhase(ExchangePhase.Buy, now);
            if (error is not null) this.FailExchange(error);
        }
    }

    private void ObserveExchangePurchase(DateTime now, uint balance)
    {
        if (this.exchangeProduct is not { } product || this.exchangeBatch <= 0) { this.FailExchange("购买对账上下文丢失"); return; }
        int received = this.inventoryCounter.Count(product.ItemId) - this.exchangeOwnedBefore;
        long spent = (long)this.exchangeBalanceBefore - balance;
        int expected = checked(this.exchangeBatch * (int)product.ReceiveCount);
        uint expectedCost = checked((uint)this.exchangeBatch * product.Cost);
        if (received == expected && spent == expectedCost)
        {
            this.exchangeBought += received;
            this.exchangeSpent += expectedCost;
            this.exchangeBatch = 0;
            this.AddDiagnostic(DiagnosticSeverity.Information, $"自动兑换到账：{product.Name} × {received}，消耗 {expectedCost} 宝石");
            this.SetExchangePhase(this.exchangeShop.HasShop ? ExchangePhase.Buy : ExchangePhase.Choose, now);
            this.exchangeNextActionAt = now.AddSeconds(1);
            return;
        }
        // Do not submit again on partial/delayed balance or inventory updates.
        if (this.exchangeVendor is { } vendor) this.exchangeShop.Confirm(product, vendor.NpcId);
        if (now - this.exchangePhaseStartedAt > TimeSpan.FromSeconds(15))
            this.FailExchange($"「{product.Name}」到账核对超时（物品增量 {received}/{expected}，宝石减少 {spent}/{expectedCost}），未重复购买；请确认交易结果，必要时取消后重新兑换");
    }

    private void FinishExchangeRule(DateTime now)
    {
        this.exchangeRuleIndex++;
        this.SetExchangePhase(ExchangePhase.Choose, now);
        this.exchangeStatus = "当前兑换项已完成，按顺序检查下一项";
    }

    private void SkipExchangeRule(DateTime now, string reason)
    {
        var rule = this.exchangePlan!.Rules[this.exchangeRuleIndex];
        string name = this.exchangeCatalog.Find(rule)?.Name ?? $"物品 {rule.ItemId}";
        string message = $"自动兑换：跳过第 {this.exchangeRuleIndex + 1} 项「{name}」：{reason}；继续检查下一项";
        this.AddDiagnostic(DiagnosticSeverity.Information, message);
        this.FinishExchangeRule(now);
        this.exchangeStatus = message;
    }

    private void FailExchange(string reason)
    {
        this.exchangeStatus = $"自动兑换暂停：{reason}";
        this.Pause(this.exchangeStatus);
    }

    private void SuspendExchangeActions()
    {
        this.exchangeLandingSession?.Reset();
        if (this.IsExchanging) this.textAdvance.Disable();
        if (this.exchangeTeleportOwned || this.exchangeInstanceOwned) this.lifestream.Abort();
        this.exchangeTeleportOwned = false;
        this.exchangeInstanceOwned = false;
        if (this.exchangeShop?.HasOwnership == true) this.exchangeShop.CloseOwned();
    }

    private void ResetExchange()
    {
        this.SuspendExchangeActions();
        this.exchangePlan = null;
        this.exchangeRequested = false;
        this.exchangeBatch = 0;
        this.exchangeProduct = null;
        this.exchangeVendor = null;
        this.exchangeNavigationLogAt = DateTime.MinValue;
        this.exchangeTravelStartedAt = DateTime.MinValue;
        this.exchangeMountRetryAt = DateTime.MinValue;
    }

    private void CompleteExchange()
    {
        bool resume = this.exchangeResumeFarm && this.configuration.Enabled;
        this.exchangeStatus = $"本轮已兑换 {this.exchangeBought} 件，消耗 {this.exchangeSpent} 宝石";
        if (resume && GetPublicInstanceId() != this.exchangeReturnInstance)
            this.exchangeStatus += "；返回地图实例已变化，将在当前实例继续";
        this.ResetExchange();
        this.ResetCurrentActivity();
        this.mapArrivalCheckPending = resume;
        this.Transition(resume ? AutomationState.ValidatingPlan : AutomationState.Stopped,
            this.exchangeStatus + (resume ? "，重新检查并继续刷图" : "，保持停止"));
    }

    private bool ResumeExchange()
    {
        if (!this.IsExchanging) return false;
        if (this.state is not AutomationState.Paused and not AutomationState.Faulted) return true;
        this.exchangeUnavailableSince = DateTime.MinValue;
        this.exchangeShop.ForgetOwnership();
        this.textAdvance.EnableForExchange();
        this.SetExchangePhase(this.ExchangeRecoveryPhase(), DateTime.UtcNow);
        this.Transition(AutomationState.ExchangingGemstones, "继续兑换，先核对库存与宝石");
        return true;
    }

    private ExchangePhase ExchangeRecoveryPhase() => this.exchangeBatch > 0 ? ExchangePhase.AwaitPurchase
        : this.exchangePhase is ExchangePhase.Return or ExchangePhase.ReturnInstance ? ExchangePhase.Return : ExchangePhase.Choose;

    private void RestoreExchangeInstance(DateTime now)
    {
        if (this.clientState.TerritoryType != this.exchangeReturnTerritory)
        { this.FailExchange("恢复刷图实例时地图发生变化"); return; }
        if (this.exchangeInstanceOwned)
        {
            if (now - this.exchangePhaseStartedAt > TimeSpan.FromSeconds(60))
            { this.FailExchange("返回原实例超时，请确认 Lifestream 状态后继续"); return; }
            if (this.lifestream.IsBusy || now - this.exchangePhaseStartedAt < TimeSpan.FromSeconds(2)) return;
            this.exchangeInstanceOwned = false;
            this.CompleteExchange();
            return;
        }
        if (this.exchangeReturnInstance == 0 || GetPublicInstanceId() == this.exchangeReturnInstance)
        { this.CompleteExchange(); return; }
        if (!this.lifestream.IsAvailable)
        { this.FailExchange("恢复刷图实例需要 Lifestream，但传送 IPC 不可用"); return; }
        // Only request an instance which Lifestream reports as available at the arrival aetheryte.
        if (!this.lifestream.IsBusy && !this.exchangeShop.HasBlockingUi && this.lifestream.TryRestoreInstance(this.exchangeReturnInstance))
        {
            this.exchangeInstanceOwned = true;
            this.exchangePhaseStartedAt = now;
            this.exchangeStatus = $"已返回刷图地图，尝试恢复实例 {this.exchangeReturnInstance}";
        }
        else if (now - this.exchangePhaseStartedAt > TimeSpan.FromSeconds(5)) this.CompleteExchange();
    }
}
