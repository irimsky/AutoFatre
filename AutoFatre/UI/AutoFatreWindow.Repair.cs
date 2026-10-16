using Dalamud.Bindings.ImGui;

namespace AutoFatre;

public sealed partial class AutoFatreWindow
{
    private void DrawAutoRepairSettings()
    {
        var settings = this.configuration.AutoRepair;
        bool enabled = settings.Enabled;
        if (DrawSettingCheckbox("启用自动修理", "repair-enabled",
                "在装备与伙伴检查流程内检查身上装备耐久，指定武器换装完成后再修理。战斗中的伙伴定期检查跳过修理。", ref enabled))
            settings.Enabled = enabled;
        ImGui.TextWrapped("在启动、传送到达、FATE 落地及战后原有的装备/伙伴检查点执行。飞行时延后至地面检查点；不会为了修理中断当前 FATE 战斗。");
        ImGui.Spacing();
        ImGui.TextUnformatted(this.controller.MinimumEquipmentCondition is { } percent
            ? $"身上装备最低耐久：{percent}%" : "身上装备最低耐久：待读取");
        ImGui.TextWrapped($"修理状态：{this.controller.AutoRepairStatus}");
        ImGui.Separator();

        int threshold = settings.ThresholdPercent;
        if (DrawSettingSliderInt("触发耐久（%）", "repair-threshold",
                "任意一件身上可修理装备耐久小于或等于此值时触发，修理范围固定为身上装备。", ref threshold, 1, 99))
            settings.ThresholdPercent = threshold;
        string[] methods = ["自行修理", "附近 NPC 修理", "优先自修，条件不足时找附近 NPC"];
        int method = (int)settings.Method;
        if (ImGui.Combo("修理方式###repair-method", ref method, methods, methods.Length))
            settings.Method = (AutoRepairMethod)method;
        ImGui.TextWrapped("自行修理要求对应生产职业达到装备等级减 10（最低 1 级），并准备足够的对应或更高等级暗物质；无需切换生产职业。附近 NPC 修理仅使用 6 码内可交互的修理工，不安排额外移动或传送。");
        if (settings.Method != AutoRepairMethod.SelfRepair)
        {
            int maxCost = settings.MaxNpcRepairCost;
            if (DrawSettingSliderInt("单次 NPC 修理费用上限", "repair-npc-cost",
                    "使用修理窗口中身上装备的实际总价，超过此上限或金币不足时按失败策略处理。", ref maxCost, 1, 100000))
                settings.MaxNpcRepairCost = maxCost;
        }
        int timeout = settings.TimeoutSeconds;
        if (DrawSettingSliderInt("单次修理超时（秒）", "repair-timeout",
                "包括等待现有对话关闭、打开窗口、确认修理和等待实际耐久恢复；超时后按失败策略处理。", ref timeout, 10, 120))
            settings.TimeoutSeconds = timeout;
        string[] policies = ["暂停自动化，处理后手动继续", "记录失败并继续，下个检查点再尝试"];
        int policy = (int)settings.FailurePolicy;
        if (ImGui.Combo("修理失败时###repair-failure", ref policy, policies, policies.Length))
            settings.FailurePolicy = (AutoRepairFailurePolicy)policy;
        if (settings.FailurePolicy == AutoRepairFailurePolicy.Continue)
        {
            int interval = settings.RetryIntervalSeconds;
            if (DrawSettingSliderInt("失败重试间隔（秒）", "repair-retry",
                    "间隔内跳过修理；计时结束后，在下一次装备/伙伴检查点重试。", ref interval, 10, 600))
                settings.RetryIntervalSeconds = interval;
        }
    }
}
