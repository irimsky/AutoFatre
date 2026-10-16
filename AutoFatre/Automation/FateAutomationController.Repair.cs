using Dalamud.Game.ClientState.Conditions;

namespace AutoFatre;

public sealed unsafe partial class FateAutomationController
{
    private readonly AutoRepairAdapter autoRepair;
    private bool companionRepairHandled;
    private DateTime repairRetryAt;
    public string AutoRepairStatus => this.autoRepair.Status;
    public int? MinimumEquipmentCondition => this.clientState.IsLoggedIn && this.playerState.IsLoaded
        && this.objectTable.LocalPlayer is not null ? this.autoRepair.MinimumConditionPercent : null;

    private bool RepairRequirementSatisfied() => !this.configuration.AutoRepair.Enabled
        || DateTime.UtcNow < this.repairRetryAt
        || !this.autoRepair.NeedsRepair(this.configuration.AutoRepair.ThresholdPercent);

    // This is a step of the existing equipment/companion checkpoint, never an independent scanning loop.
    private bool HandleCompanionRepair(DateTime now)
    {
        if (this.companionRepairHandled) return false;
        var options = this.configuration.AutoRepair;
        if (!options.Enabled || this.companionCheckpointKind == CompanionCheckpointKind.CombatPeriodic)
        {
            this.autoRepair.Cancel();
            this.companionRepairHandled = true;
            return false;
        }
        if (this.autoRepair.HasTimedOut(now))
            return this.HandleRepairFailure("自动修理等待超时", now);
        if (this.condition[ConditionFlag.Casting] || this.condition[ConditionFlag.Casting87]
            || this.objectTable.LocalPlayer?.IsCasting == true)
        {
            this.statusReason = "自动修理等待当前读条结束";
            this.nextActionAt = now.AddMilliseconds(250);
            return true;
        }
        if (!this.autoRepair.IsActive)
        {
            if (this.RepairRequirementSatisfied()) { this.companionRepairHandled = true; return false; }
            if (!this.autoRepair.TryBegin(options, now, out string error))
                return this.HandleRepairFailure(error, now);
            this.noFateStopTimer.Reset();
            this.AddDiagnostic(DiagnosticSeverity.Information,
                $"装备/伙伴检查点 {this.companionCheckpointKind} 开始自动修理：最低耐久={this.MinimumEquipmentCondition}%，阈值={options.ThresholdPercent}%，方式={options.Method}");
        }
        AutoRepairResult result = this.autoRepair.Update(now);
        this.statusReason = this.autoRepair.Status;
        this.nextActionAt = now.AddMilliseconds(250);
        if (result == AutoRepairResult.Running) return true;
        if (result == AutoRepairResult.Failed) return this.HandleRepairFailure(this.autoRepair.Status, now);
        this.companionRepairHandled = true;
        this.repairRetryAt = DateTime.MinValue;
        this.AddDiagnostic(DiagnosticSeverity.Information, this.autoRepair.Status);
        return false;
    }

    private bool HandleRepairFailure(string reason, DateTime now)
    {
        this.autoRepair.Cancel();
        this.AddDiagnostic(DiagnosticSeverity.Warning,
            $"自动修理失败：{reason}；失败策略={this.configuration.AutoRepair.FailurePolicy}");
        if (this.configuration.AutoRepair.FailurePolicy == AutoRepairFailurePolicy.Pause)
        {
            // Retry() starts a fresh checkpoint; a manual resume must retry immediately.
            this.repairRetryAt = DateTime.MinValue;
            this.Pause($"自动修理失败：{reason}；请处理后继续");
            return true;
        }
        this.repairRetryAt = now.AddSeconds(this.configuration.AutoRepair.RetryIntervalSeconds);
        this.AddDiagnostic(DiagnosticSeverity.Warning,
            $"按配置跳过本次修理，继续装备/伙伴流程；{this.configuration.AutoRepair.RetryIntervalSeconds} 秒冷却后在后续检查点重试，重新启动或手动继续会立即重查");
        this.companionRepairHandled = true;
        this.statusReason = $"修理失败后继续：{reason}；冷却结束后的下个装备/伙伴检查点重试";
        return false;
    }
}
