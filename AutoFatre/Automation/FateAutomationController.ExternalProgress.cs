using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;

namespace AutoFatre;

public sealed unsafe partial class FateAutomationController
{
    private DateTime externalProgressAt, externalNextSample;
    private Vector3 externalPosition;
    private uint externalTerritory;
    private ushort? externalFate;
    private int externalFateProgress, externalCompleted, externalEventItems;
    private ulong externalTarget;
    private uint externalTargetHp;
    private int externalRecoveryAttempts;

    private void ResetExternalProgress()
    {
        this.externalProgressAt = this.externalNextSample = DateTime.MinValue;
        this.externalRecoveryAttempts = 0;
    }

    // Only opt-in, token-owned IPC runs use this guard. Waiting for a spawn,
    // preparation NPC or resurrection is governed by the existing AF policies.
    private bool CheckExternalProgress(DateTime now)
    {
        if (this.itemFarmMaps is null || this.itemFarmResult != "Running" || now < this.externalNextSample)
            return false;
        this.externalNextSample = now.AddSeconds(1);
        var player = this.objectTable.LocalPlayer;
        var fate = this.ResolveActiveFate();
        bool legitimateWait = player is null || this.IsBetweenAreas() || now < this.territoryStableAfter
            || this.state is AutomationState.WaitingForLogin or AutomationState.WaitingForTerritory
                or AutomationState.ScanningFates or AutomationState.DeadWaitingForRaise or AutomationState.DeadReturning
                or AutomationState.ResettingAggroViaDuty
            || fate is not null && IsPreparingFate(fate);
        if (legitimateWait)
        {
            this.externalProgressAt = DateTime.MinValue;
            return false;
        }
        var target = this.targetManager.Target as IBattleChara;
        int eventItems = fate is { EventItem: > 0 } ? this.inventoryCounter.Count(fate.EventItem) : 0;
        bool actualProgress = Vector3.DistanceSquared(this.externalPosition, player!.Position) >= 1
            || this.externalCompleted != this.totalCompletedFates
            || this.externalFate == this.activeFateId && this.externalFateProgress != (fate?.Progress ?? -1)
            || this.externalEventItems != eventItems
            || target is not null && target.GameObjectId == this.externalTarget && target.CurrentHp < this.externalTargetHp;
        bool progressed = this.externalProgressAt == DateTime.MinValue
            || this.externalTerritory != this.clientState.TerritoryType
            || this.externalFate != this.activeFateId
            || actualProgress;
        if (progressed)
        {
            // Entering a new monitored interval alone doesn't forgive failed retries.
            if (this.externalProgressAt != DateTime.MinValue && actualProgress) this.externalRecoveryAttempts = 0;
            this.externalProgressAt = now;
            this.externalPosition = player!.Position;
            this.externalTerritory = this.clientState.TerritoryType;
            this.externalFate = this.activeFateId;
            this.externalFateProgress = fate?.Progress ?? -1;
            this.externalCompleted = this.totalCompletedFates;
            this.externalEventItems = eventItems;
        }
        this.externalTarget = target?.GameObjectId ?? 0;
        this.externalTargetHp = target?.CurrentHp ?? 0;
        if (now - this.externalProgressAt < TimeSpan.FromSeconds(120)) return false;
        this.externalProgressAt = now;
        this.externalRecoveryAttempts++;
        string reason = $"所属 IPC 请求120秒没有位移、FATE进度、击杀伤害或交付物变化；状态={this.state}，FATE={this.activeFateId}，恢复={this.externalRecoveryAttempts}/3";
        this.AddDiagnostic(DiagnosticSeverity.Warning, reason);
        if (this.externalRecoveryAttempts > 3)
        {
            this.Stop(reason + "；连续恢复无效，已停止，未标记目标完成");
            this.itemFarmResult = "Failed";
            return true;
        }
        // Keep the original preset and counters. Combat must be cleared before
        // replanning; outside combat cancel owned movement and retry the map plan.
        this.CancelOwnedActions();
        if (this.pendingFateResult is not null)
            this.BeginCombatCleanup(CleanupContinuation.FinalizeFate, reason);
        else if (this.IsCombatEngaged())
            this.BeginCombatCleanup(CleanupContinuation.RevalidatePlan, reason);
        else
        {
            this.ResetCurrentActivity(preserveMapReentry: true);
            this.Transition(AutomationState.ValidatingPlan, reason, DiagnosticSeverity.Warning);
            this.nextActionAt = DateTime.MinValue;
        }
        return true;
    }
}
