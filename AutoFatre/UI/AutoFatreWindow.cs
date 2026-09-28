using System.Numerics;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;

namespace AutoFatre;

public sealed class AutoFatreWindow : IDisposable
{
    private static readonly Vector4 LabelColor = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 NeutralValueColor = new(0.85f, 0.92f, 1f, 1f);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly AutoFatreConfiguration configuration;
    private readonly FateAutomationController controller;
    private readonly GameDataSelectionCatalog selectionCatalog;
    private readonly InventoryCounter inventoryCounter;
    private readonly Action<bool> setOverlayWindowVisibility;
    private readonly Dictionary<string, string> selectorSearch = new(StringComparer.Ordinal);
    private bool isOpen;
    private bool deathRecordsOpen;
    private bool diagnosticLogOpen;
    private bool diagnosticLogAutoScroll = true;
    private bool diagnosticLogJumpToBottom;
    private int presetEditorMapIndex;
    private int? pendingPresetMapDeleteIndex;
    private DateTime pendingPresetMapDeleteUntil = DateTime.MinValue;
    private bool pendingPresetSequenceDelete;
    private DateTime pendingPresetSequenceDeleteUntil = DateTime.MinValue;

    public AutoFatreWindow(
        IDalamudPluginInterface pluginInterface,
        AutoFatreConfiguration configuration,
        FateAutomationController controller,
        GameDataSelectionCatalog selectionCatalog,
        InventoryCounter inventoryCounter,
        Action<bool>? setOverlayWindowVisibility = null)
    {
        this.pluginInterface = pluginInterface;
        this.configuration = configuration;
        this.controller = controller;
        this.selectionCatalog = selectionCatalog;
        this.inventoryCounter = inventoryCounter;
        this.setOverlayWindowVisibility = setOverlayWindowVisibility ?? (_ => { });
        this.pluginInterface.UiBuilder.Draw += this.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi += this.Open;
        this.pluginInterface.UiBuilder.OpenConfigUi += this.Open;
    }

    public void Open() => this.isOpen = true;

    public void OpenDiagnosticLog() => this.diagnosticLogOpen = true;

    public void Dispose()
    {
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.Open;
        this.pluginInterface.UiBuilder.OpenMainUi -= this.Open;
        this.pluginInterface.UiBuilder.Draw -= this.Draw;
    }

    private void Draw()
    {
        if (!this.isOpen)
        {
            this.DrawDeathRecordsWindow();
            this.DrawDiagnosticLogWindow();
            return;
        }

        ImGui.SetNextWindowSize(new Vector2(760, 650), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("AutoFatre", ref this.isOpen))
        {
            ImGui.End();
            this.DrawDeathRecordsWindow();
            this.DrawDiagnosticLogWindow();
            return;
        }
        ImGui.SetWindowFontScale(1.08f);

        this.DrawHeader();
        if (ImGui.BeginTabBar("AutoFatreTabs"))
        {
            if (ImGui.BeginTabItem("运行"))
            {
                this.DrawRunTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("选择模式"))
            {
                this.DrawModeTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("设置"))
            {
                this.DrawAdvancedTab();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("诊断"))
            {
                this.DrawDiagnosticsTab();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        ImGui.End();
        this.DrawDeathRecordsWindow();
        this.DrawDiagnosticLogWindow();
    }

    public void DrawOverlayContents()
    {
        ImGui.SetWindowFontScale(1.08f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(7f, 5f));

            bool running = this.controller.IsRunning;
            float buttonWidth = MathF.Max(72f, (ImGui.GetContentRegionAvail().X - 14f) / 3f);
            bool paused = this.controller.IsPaused;
            ImGui.BeginDisabled(running && !paused);
            if (ImGui.Button(paused ? "重试/继续" : "开始", new Vector2(buttonWidth, 0)))
            {
                if (paused)
                    this.controller.Retry();
                else
                    this.controller.Start();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!running || this.controller.IsPaused);
            if (ImGui.Button("暂停", new Vector2(buttonWidth, 0)))
                this.controller.Pause();
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!running);
            if (ImGui.Button("停止", new Vector2(buttonWidth, 0)))
                this.controller.Stop();
            ImGui.EndDisabled();

            ImGui.Separator();
            ImGui.TextColored(new Vector4(0.35f, 0.8f, 1f, 1f), "模式");
            ImGui.SameLine(0f, 8f);
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.35f, 1f), ModeLabel(this.configuration.Mode));
            ImGui.Separator();

            DrawOverlayField("状态", this.controller.State.ToString(), new Vector4(0.35f, 0.85f, 1f, 1f));
            DrawOverlayField("说明", this.controller.StatusReason, new Vector4(1f, 0.8f, 0.35f, 1f));
            string currentFate = this.controller.ActiveFateIdSnapshot is { } activeFateId
                ? $"{this.controller.ActiveFateNameSnapshot ?? "未知 FATE"}（#{activeFateId}）"
                : "无";
            DrawOverlayField("当前 FATE", currentFate, NeutralValueColor);
            DrawOverlayField(
                "主手武器",
                string.IsNullOrWhiteSpace(this.controller.CurrentMainHandName) ? "无" : this.controller.CurrentMainHandName,
                NeutralValueColor);
            DrawOverlayField(
                "宠物",
                string.IsNullOrWhiteSpace(this.controller.CurrentMinionName) ? "无" : this.controller.CurrentMinionName,
                NeutralValueColor);

            if (this.configuration.Mode == AutomationMode.PresetSequence)
                this.DrawOverlayPresetProgress();
            else if (this.configuration.Mode == AutomationMode.TargetFate)
            {
                IReadOnlyList<ushort> targetFates = this.controller.ConfiguredTargetFateIds;
                string targetFate = targetFates.Count > 0
                    ? string.Join("、", targetFates.Select(id => this.selectionCatalog.GetFateDisplayName(id, includeMapName: false)))
                    : "未选择 FATE";
                int completed = this.controller.ConfiguredTargetFateCompleted;
                int total = this.controller.ConfiguredTargetFateTotal;
                DrawOverlayField("指定 FATE", targetFate, NeutralValueColor);
                DrawOverlayField("进度", $"{completed} / {total}", completed >= total && total > 0
                    ? new Vector4(0.35f, 0.9f, 0.45f, 1f)
                    : NeutralValueColor);
            }

        ImGui.PopStyleVar();
    }

    private void DrawOverlayPresetProgress()
    {
        DrawOverlayField("预设列表", this.configuration.GetActivePresetSequence().Name, NeutralValueColor);
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.35f, 0.8f, 1f, 1f), "地图与停止条件");

        IReadOnlyList<MapPreset> maps = this.configuration.GetActivePresetSequence().Maps;
        bool childVisible = ImGui.BeginChild(
            "OverlayPresetProgress",
            new Vector2(0, 0),
            true,
            ImGuiWindowFlags.AlwaysVerticalScrollbar);
        if (childVisible)
        {
            if (maps.Count == 0)
            {
                ImGui.TextDisabled("当前预设列表没有地图。");
            }
            else
            {
                for (int i = 0; i < maps.Count; i++)
                {
                    MapPreset map = maps[i];
                    bool current = i == this.controller.PresetIndex;
                    uint displayTerritory = map.TerritoryId == 0 && current
                        ? this.controller.DesiredTerritory
                        : map.TerritoryId == 0
                            ? this.controller.CurrentTerritory
                            : map.TerritoryId;
                    string mapName = this.selectionCatalog.GetTerritoryName(displayTerritory);
                    if (string.IsNullOrWhiteSpace(mapName))
                        mapName = $"地图 {i + 1}";

                    ImGui.TextColored(
                        current ? new Vector4(1f, 0.85f, 0.35f, 1f) : LabelColor,
                        $"{i + 1}. {mapName}{(current ? "（当前）" : string.Empty)}");
                    ImGui.Indent(16f);
                    if (map.StopConditions.Count == 0)
                    {
                        ImGui.TextDisabled("无停止条件");
                    }
                    else
                    {
                        foreach (StopCondition stop in map.StopConditions)
                        {
                            int target = Math.Max(1, stop.Kind == StopConditionKind.ItemCount ? stop.ItemCount : stop.FateCount);
                            int completed = this.controller.GetPresetStopConditionProgress(i, stop);
                            Vector4 progressColor = completed >= target
                                ? new Vector4(0.35f, 0.9f, 0.45f, 1f)
                                : NeutralValueColor;
                            DrawOverlayField(
                                GetOverlayStopConditionLabel(stop),
                                $"{completed} / {target}",
                                progressColor,
                                progressColor);
                        }
                    }
                    ImGui.Unindent(16f);
                    if (i + 1 < maps.Count)
                        ImGui.Separator();
                }
            }
        }
        ImGui.EndChild();
    }

    private string GetOverlayStopConditionLabel(StopCondition stop) => stop.Kind switch
    {
        StopConditionKind.FateCount => "└ 成功完成 FATE",
        StopConditionKind.ItemCount => $"└ 物品：{(stop.ItemId == 0 ? "未选择物品" : this.selectionCatalog.GetItemName(stop.ItemId))}",
        StopConditionKind.TargetFate => $"└ 指定 FATE：{(stop.TargetFateId == 0 ? "未选择 FATE" : this.selectionCatalog.GetFateDisplayName(stop.TargetFateId, includeMapName: false))}",
        _ => $"└ {StopKindLabel(stop.Kind)}",
    };

    private static void DrawOverlayField(string label, string value, Vector4 valueColor, Vector4? labelColor = null)
    {
        ImGui.TextColored(labelColor ?? LabelColor, $"{label}：");
        ImGui.SameLine(0f, 6f);
        ImGui.PushStyleColor(ImGuiCol.Text, valueColor);
        ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "无" : value);
        ImGui.PopStyleColor();
    }

    private void DrawHeader()
    {
        ImGui.Text("FATE 全自动化控制器");
        ImGui.SameLine();
        DrawLabeledValue("状态", this.controller.State.ToString(), new Vector4(0.35f, 0.85f, 1f, 1f));
        DrawLabeledValue("说明", this.controller.StatusReason, new Vector4(1f, 0.8f, 0.35f, 1f));

        if (!this.controller.IsRunning)
        {
            if (ImGui.Button("启动"))
                this.controller.Start();
        }
        else if (!this.controller.IsPaused)
        {
            if (ImGui.Button("暂停"))
                this.controller.Pause();
        }
        else if (ImGui.Button("重试/继续"))
        {
            this.controller.Retry();
        }

        ImGui.SameLine();
        if (ImGui.Button("停止"))
            this.controller.Stop();
        ImGui.SameLine();
        if (ImGui.Button("失败记录"))
            this.deathRecordsOpen = true;
        ImGui.SameLine();
        if (ImGui.Button("保存配置"))
        {
            this.configuration.Normalize();
            this.pluginInterface.SavePluginConfig(this.configuration);
        }
        ImGui.Separator();
    }

    private void DrawRunTab()
    {
        DrawSectionTitle("运行状态");
        DrawStatusGrid(
            "RunStatusGrid",
            ("状态", this.controller.State.ToString(), new Vector4(0.35f, 0.85f, 1f, 1f)),
            ("当前地图", this.selectionCatalog.GetTerritoryName(this.controller.CurrentTerritory), NeutralValueColor),
            ("目标地图", this.selectionCatalog.GetTerritoryName(this.controller.DesiredTerritory), NeutralValueColor),
            ("启动以来完成", $"{this.controller.TotalCompletedFates} 个 FATE", new Vector4(0.35f, 0.9f, 0.45f, 1f)),
            ("活动 FATE", this.controller.ActiveFateIdSnapshot is { } activeFateId
                ? $"#{activeFateId} {this.controller.ActiveFateNameSnapshot}（类型：{this.controller.ActiveFateCombatKind}）"
                : $"未选择（类型：{this.controller.ActiveFateCombatKind}）", NeutralValueColor),
            ("接战/清场目标", $"{this.controller.AggroCount} / {this.configuration.MaxAggroCount}", NeutralValueColor));

        DrawSectionTitle("依赖状态");
        DrawStatusGrid(
            "DependencyStatusGrid",
            ("vnavmesh", this.controller.IsVnavmeshAvailable ? "可用 · " + (this.controller.IsVnavmeshPathActive ? "路径运行中" : "空闲") : "不可用", DependencyValueColor(this.controller.IsVnavmeshAvailable, this.controller.IsVnavmeshPathActive)),
            ("Lifestream", this.controller.IsLifestreamAvailable ? "可用 · " + (this.controller.IsLifestreamBusy ? "忙碌" : "空闲") : "不可用", DependencyValueColor(this.controller.IsLifestreamAvailable, this.controller.IsLifestreamBusy)));

        DrawSectionTitle("角色状态");
        Vector4 mountColor = this.controller.IsMounted ? new Vector4(0.35f, 0.8f, 1f, 1f) : new Vector4(0.65f, 0.65f, 0.68f, 1f);
        DrawStatusGrid(
            "CharacterStatusGrid",
            ("坐骑", this.controller.IsMounted ? (this.controller.IsInFlight ? "飞行中" : "地面坐骑") : "步行", mountColor),
            ("等级同步", this.controller.IsLevelSynced ? "已同步" : "未同步", this.controller.IsLevelSynced ? new Vector4(0.35f, 0.9f, 0.45f, 1f) : new Vector4(0.95f, 0.7f, 0.25f, 1f)),
            ("陆行鸟", $"{(this.controller.HasChocoboCompanion ? "已召唤" : "未召唤")}，剩余 {this.controller.ChocoboTimeLeft:0} 秒，基萨尔野菜 {this.controller.GysahlGreensCount}", this.controller.HasChocoboCompanion ? new Vector4(0.35f, 0.9f, 0.45f, 1f) : new Vector4(0.95f, 0.55f, 0.35f, 1f)),
            ("宠物", this.controller.CurrentMinionName, NeutralValueColor),
            ("主手武器", this.controller.CurrentMainHandName, NeutralValueColor));

        DrawSectionTitle("当前任务");
        List<(string Label, string Value, Vector4 Color)> taskStatus = [];
        if (this.configuration.Mode == AutomationMode.TargetFate)
        {
            IReadOnlyList<ushort> targetIds = this.controller.ConfiguredTargetFateIds;
            string target = targetIds.Count > 0
                ? string.Join("、", targetIds.Select(id => this.selectionCatalog.GetFateDisplayName(id, includeMapName: true)))
                : "未选择";
            taskStatus.Add(("指定 FATE", target, NeutralValueColor));
        }
        if (this.configuration.Mode == AutomationMode.PresetSequence)
        {
            MapPresetSequence sequence = this.configuration.GetActivePresetSequence();
            taskStatus.Add(("当前预设", sequence.Name, NeutralValueColor));
            taskStatus.Add(("地图进度", $"{this.controller.PresetIndex + 1}/{Math.Max(1, sequence.Maps.Count)}", NeutralValueColor));
            taskStatus.Add(("本地图完成", $"{this.controller.PresetCompletedFates} 个 FATE", new Vector4(0.35f, 0.9f, 0.45f, 1f)));
        }
        if (this.configuration.Mode == AutomationMode.SingleMapLoop)
            taskStatus.Add(("单地图循环", this.selectionCatalog.GetTerritoryName(this.configuration.SingleMapTerritoryId), NeutralValueColor));
        DrawStatusGrid("CurrentTaskStatusGrid", taskStatus.ToArray());

        IReadOnlyList<string> validation = this.controller.ValidateConfiguration();
        if (validation.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.3f, 1f), "配置问题：");
            foreach (string error in validation)
                ImGui.BulletText(error);
        }

        ImGui.Separator();
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 6));
        DrawSectionTitle("区域 FATE");
        int preparingCount = this.controller.CandidateSnapshot.Count(f =>
            f.State.Contains("Prepar", StringComparison.OrdinalIgnoreCase));
        if (preparingCount > 0)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.8f, 0.25f, 1f),
                $"准备中 FATE：{preparingCount} 个（需要 NPC/事件触发）");
        }
        if (this.controller.CandidateSnapshot.Count == 0)
        {
            ImGui.TextDisabled("当前没有可读取的 FATE。");
            ImGui.PopStyleVar();
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(7, 5));
        if (ImGui.BeginTable("FateTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, 330)))
        {
            ImGui.TableSetupColumn("名称", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("进度", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("剩余时间", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableSetupColumn("距离", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("评分", ImGuiTableColumnFlags.WidthStretch, 2.6f);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();
            foreach (var fate in this.controller.CandidateSnapshot)
            {
                ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.PushID($"fate-{fate.FateId}");
                Vector4 titleColor = fate.IsEligible
                    ? new Vector4(0.85f, 0.92f, 1f, 1f)
                    : new Vector4(1f, 0.65f, 0.35f, 1f);
                ImGui.PushStyleColor(ImGuiCol.Text, titleColor);
                ImGui.TextWrapped(FormatFateTitle(fate));
                ImGui.PopStyleColor();
                if (ImGui.SmallButton("导航")) this.controller.NavigateToFateForUi(fate.FateId);
                ImGui.SameLine();
                if (ImGui.SmallButton("临时目标")) this.controller.SetTemporaryTargetFateForUi(fate.FateId);
                ImGui.PopID();
                ImGui.TableNextColumn(); ImGui.TextColored(NeutralValueColor, $"{fate.Progress}%"); ImGui.TableNextColumn(); ImGui.TextColored(NeutralValueColor, IsPreparingState(fate.State) ? "等待开启" : $"{fate.TimeRemaining}s"); ImGui.TableNextColumn(); ImGui.TextColored(NeutralValueColor, $"{fate.Distance:0.0}");
                ImGui.TableNextColumn(); ImGui.TextColored(NeutralValueColor, fate.CombatKind); ImGui.TableNextColumn();
                ImGui.TextColored(NeutralValueColor, $"{fate.Score:0.0}");
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
                ImGui.TextColored(new Vector4(0.7f, 0.76f, 0.82f, 1f), fate.IsEligible ? FormatScoreBreakdown(fate) : "未参与评分");
                ImGui.PopTextWrapPos();
            }
            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
        ImGui.PopStyleVar();
    }

    private static void DrawSectionTitle(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.35f, 0.8f, 1f, 1f), title);
        ImGui.Separator();
    }

    private static void DrawStatusGrid(
        string id,
        params (string Label, string Value, Vector4 Color)[] entries)
    {
        if (!ImGui.BeginTable(id, 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV))
            return;

        for (int i = 0; i < entries.Length; i += 2)
        {
            ImGui.TableNextRow();
            DrawStatusCell(entries[i]);
            if (i + 1 < entries.Length)
                DrawStatusCell(entries[i + 1]);
            else
                ImGui.TableNextColumn();
        }

        ImGui.EndTable();
    }

    private static void DrawStatusCell((string Label, string Value, Vector4 Color) entry)
    {
        ImGui.TableNextColumn();
        DrawLabeledValue(entry.Label, entry.Value, entry.Color);
    }

    private static void DrawLabeledValue(string label, string value, Vector4 valueColor)
    {
        ImGui.TextColored(LabelColor, $"{label}：");
        ImGui.SameLine(0, 4);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X);
        ImGui.TextColored(valueColor, value);
        ImGui.PopTextWrapPos();
    }

    private static Vector4 DependencyValueColor(bool available, bool busy) => available
        ? busy ? new Vector4(0.95f, 0.75f, 0.25f, 1f) : new Vector4(0.35f, 0.9f, 0.45f, 1f)
        : new Vector4(0.95f, 0.35f, 0.3f, 1f);

    private static bool IsPreparingState(string state) =>
        state.Contains("Prepar", StringComparison.OrdinalIgnoreCase);

    private static string FormatFateTitle(FateAutomationController.FateCandidateSnapshot fate) =>
        fate.IsEligible || string.IsNullOrWhiteSpace(fate.EligibilityReason)
            ? fate.Name
            : $"{fate.Name}（{fate.EligibilityReason}）";

    private static string FormatScoreBreakdown(FateAutomationController.FateCandidateSnapshot fate)
    {
        bool preparing = IsPreparingState(fate.State);
        float baseWeight = preparing ? 700f : 850f;
        float progressWeight = preparing ? 0f : Math.Max(0, 100 - fate.Progress) * 0.75f;
        float bonusWeight = preparing || !fate.HasBonus ? 0f : 25f;
        List<string> parts = [$"基{FormatSigned(baseWeight)}", $"距{FormatSigned(fate.DistanceWeight)}"];
        if (progressWeight != 0)
            parts.Add($"进{FormatSigned(progressWeight)}");
        if (fate.TimeWeight != 0)
            parts.Add($"时{FormatSigned(fate.TimeWeight)}");
        if (bonusWeight != 0)
            parts.Add($"奖{FormatSigned(bonusWeight)}");
        return string.Join(' ', parts);
    }

    private static string FormatSigned(float value) => value.ToString("+0.#;-0.#;0");

    private void DrawModeTab()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
        string fingerprintBefore = this.GetPresetFingerprint();
        AutomationMode mode = this.configuration.Mode;
        this.DrawChoiceButtons(
            "运行模式",
            ref mode,
            (AutomationMode.SingleMapLoop, ModeLabel(AutomationMode.SingleMapLoop)),
            (AutomationMode.TargetFate, ModeLabel(AutomationMode.TargetFate)),
            (AutomationMode.PresetSequence, ModeLabel(AutomationMode.PresetSequence)));
        this.configuration.Mode = mode;
        if (this.configuration.Mode == AutomationMode.SingleMapLoop)
        {
            uint territory = this.configuration.SingleMapTerritoryId;
            uint previousTerritory = territory;
            this.DrawTerritorySelector("地图", "single-map", ref territory, allowCurrent: true);
            this.configuration.SingleMapTerritoryId = territory;
            if (territory != previousTerritory)
            {
                this.configuration.TargetFateId = null;
                this.configuration.TargetFateIds.Clear();
            }
            ImGui.TextDisabled("自动从目标地图的默认已解锁以太之光中选择；不可用时依次尝试下一个。");
        }

        if (this.configuration.Mode == AutomationMode.TargetFate)
        {
            this.configuration.TargetFateIds ??= [];
            if (this.configuration.TargetFateIds.Count == 0 && this.configuration.TargetFateId is { } legacyTarget)
                this.configuration.TargetFateIds.Add(legacyTarget);

            List<ushort> targetFates = this.configuration.TargetFateIds.Distinct().ToList();
            ushort? targetFate = targetFates.FirstOrDefault() is { } firstTarget && firstTarget != 0
                ? firstTarget
                : null;
            if (targetFate is { } configuredFirst
                && this.selectionCatalog.TryGetTerritoryIdForFate(configuredFirst, out uint configuredTerritory))
            {
                targetFates = targetFates
                    .Where((id, index) => index == 0
                        || id == 0
                        || this.selectionCatalog.TryGetTerritoryIdForFate(id, out uint territory)
                           && territory == configuredTerritory)
                    .ToList();
            }
            ushort? previousTarget = targetFate;
            this.DrawFateSelector(
                "指定 FATE",
                "single-target-fate",
                null,
                ref targetFate,
                includeMapName: true,
                requireSearch: true,
                showRecent: true);
            if (targetFate != previousTarget)
            {
                if (targetFate is { } selectedFirstValue)
                    targetFates = [selectedFirstValue, ..targetFates.Skip(1)];
                else
                    targetFates.Clear();

                if (targetFate is { } selectedNew
                    && this.selectionCatalog.TryGetTerritoryIdForFate(selectedNew, out uint selectedFirstTerritory)
                    )
                {
                    targetFates = targetFates
                        .Where(id => this.selectionCatalog.TryGetTerritoryIdForFate(id, out uint territory)
                                     && territory == selectedFirstTerritory)
                        .Distinct()
                        .ToList();
                    this.configuration.SingleMapTerritoryId = selectedFirstTerritory;
                }
            }

            if (targetFates.Count > 0
                && this.selectionCatalog.TryGetTerritoryIdForFate(targetFates[0], out uint targetTerritory))
            {
                for (int index = 1; index < targetFates.Count; index++)
                {
                    ushort? additionalTarget = targetFates[index];
                    ushort? previousAdditionalTarget = additionalTarget;
                    this.DrawFateSelector(
                        $"指定 FATE {index + 1}",
                        $"target-fate-{index}",
                        targetTerritory,
                        ref additionalTarget,
                        includeMapName: false,
                        requireSearch: true,
                        showRecent: false);
                    ImGui.SameLine();
                    bool remove = ImGui.SmallButton($"移除##target-fate-remove-{index}");
                    bool clearedExisting = additionalTarget is null
                        && previousAdditionalTarget is { } previousValue
                        && previousValue != 0;
                    if (remove || clearedExisting)
                    {
                        targetFates.RemoveAt(index);
                        index--;
                    }
                    else if (additionalTarget is { } selectedAdditional
                             && selectedAdditional != previousAdditionalTarget)
                    {
                        targetFates[index] = selectedAdditional;
                    }
                }

                bool canAddTarget = !targetFates.Skip(1).Any(id => id == 0);
                ImGui.BeginDisabled(!canAddTarget);
                if (ImGui.Button("添加指定 FATE"))
                    targetFates.Add(0);
                ImGui.EndDisabled();

                if (this.selectionCatalog.TryGetFate(targetFates[0], out FateSelectionEntry selectedEntry))
                {
                    ImGui.TextDisabled(selectedEntry.MapName is { } mapName
                        ? $"目标地图：{mapName}（所有指定 FATE 必须在此地图）"
                        : "客户端开放世界地图目录无法定位该 FATE；它可能是活动、任务实例或已废弃条目，无法用于自动跨地图传送。");
                }
            }
            else
            {
                ImGui.TextDisabled("请先选择第一个指定 FATE，再添加同地图的其他 FATE。");
            }

            this.configuration.TargetFateIds = targetFates.Distinct().ToList();
            this.configuration.TargetFateId = this.configuration.TargetFateIds.FirstOrDefault() is { } firstConfigured
                && firstConfigured != 0
                ? firstConfigured
                : null;
            TargetFateFallbackPolicy fallback = this.configuration.TargetFateFallback;
            this.DrawChoiceButtons(
                "目标未出现时",
                ref fallback,
                (TargetFateFallbackPolicy.WaitOnly, FallbackLabel(TargetFateFallbackPolicy.WaitOnly)),
                (TargetFateFallbackPolicy.FarmOtherFates, FallbackLabel(TargetFateFallbackPolicy.FarmOtherFates)));
            this.configuration.TargetFateFallback = fallback;
        }

        if (this.configuration.Mode != AutomationMode.PresetSequence)
        {
            ImGui.PopStyleVar();
            return;
        }

        SequenceCompletionPolicy completion = this.configuration.SequenceCompletion;
        this.DrawChoiceButtons(
            "列表完成后",
            ref completion,
            (SequenceCompletionPolicy.Stop, SequenceLabel(SequenceCompletionPolicy.Stop)),
            (SequenceCompletionPolicy.Loop, SequenceLabel(SequenceCompletionPolicy.Loop)));
        this.configuration.SequenceCompletion = completion;
        this.DrawPresetSequenceSelector();
        MapPresetSequence sequence = this.configuration.GetActivePresetSequence();
        string sequenceName = sequence.Name;
        this.SetSelectorWidth(420f);
        if (ImGui.InputText("列表名称", ref sequenceName, 80))
            sequence.Name = sequenceName;
        this.DrawPresetMapsEditor(sequence);

        string fingerprintAfter = this.GetPresetFingerprint();
        if (!string.Equals(fingerprintBefore, fingerprintAfter, StringComparison.Ordinal))
        {
            this.configuration.Normalize();
            this.pluginInterface.SavePluginConfig(this.configuration);
        }
        ImGui.PopStyleVar();
    }

    private void DrawPresetMapsEditor(MapPresetSequence sequence)
    {
        if (sequence.Maps.Count == 0)
            this.presetEditorMapIndex = 0;
        else
            this.presetEditorMapIndex = Math.Clamp(this.presetEditorMapIndex, 0, sequence.Maps.Count - 1);

        if (!ImGui.BeginChild("PresetMapsEditor", new Vector2(0, 0), true))
        {
            ImGui.EndChild();
            return;
        }

        float listWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.58f, 380f, 460f);
        if (ImGui.BeginChild("PresetMapList", new Vector2(listWidth, 0), true))
        {
            DrawSectionTitle("地图列表");
            if (ImGui.Button("添加地图"))
            {
                sequence.Maps.Add(new MapPreset());
                this.presetEditorMapIndex = sequence.Maps.Count - 1;
            }

            ImGui.Spacing();
            for (int i = 0; i < sequence.Maps.Count; i++)
            {
                MapPreset map = sequence.Maps[i];
                string summary = this.BuildPresetMapSummary(map);
                string displaySummary = Ellipsize(summary, listWidth - 32f);
                bool selected = i == this.presetEditorMapIndex;
                float itemWidth = ImGui.GetContentRegionAvail().X;
                if (ImGui.Selectable(
                        $"{displaySummary}##preset-map-item-{i}",
                        selected,
                        ImGuiSelectableFlags.None,
                        new Vector2(itemWidth, 52f)))
                {
                    this.presetEditorMapIndex = i;
                    this.pendingPresetMapDeleteIndex = null;
                    this.pendingPresetMapDeleteUntil = DateTime.MinValue;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(summary);
            }

            if (sequence.Maps.Count == 0)
                ImGui.TextDisabled("还没有地图，请先添加地图。");

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.BeginDisabled(sequence.Maps.Count == 0 || this.presetEditorMapIndex <= 0);
            if (ImGui.Button("上移"))
            {
                int index = this.presetEditorMapIndex;
                (sequence.Maps[index - 1], sequence.Maps[index]) = (sequence.Maps[index], sequence.Maps[index - 1]);
                this.presetEditorMapIndex--;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(sequence.Maps.Count == 0 || this.presetEditorMapIndex >= sequence.Maps.Count - 1);
            if (ImGui.Button("下移"))
            {
                int index = this.presetEditorMapIndex;
                (sequence.Maps[index], sequence.Maps[index + 1]) = (sequence.Maps[index + 1], sequence.Maps[index]);
                this.presetEditorMapIndex++;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            bool mapDeleteConfirm = this.pendingPresetMapDeleteIndex == this.presetEditorMapIndex
                                     && DateTime.UtcNow < this.pendingPresetMapDeleteUntil;
            ImGui.BeginDisabled(sequence.Maps.Count == 0);
            if (ImGui.Button(mapDeleteConfirm ? "确认删除？" : "删除"))
            {
                if (!mapDeleteConfirm)
                {
                    this.pendingPresetMapDeleteIndex = this.presetEditorMapIndex;
                    this.pendingPresetMapDeleteUntil = DateTime.UtcNow.AddSeconds(2);
                }
                else
                {
                    sequence.Maps.RemoveAt(this.presetEditorMapIndex);
                    this.presetEditorMapIndex = sequence.Maps.Count == 0
                        ? 0
                        : Math.Min(this.presetEditorMapIndex, sequence.Maps.Count - 1);
                    this.pendingPresetMapDeleteIndex = null;
                    this.pendingPresetMapDeleteUntil = DateTime.MinValue;
                }
            }
            ImGui.EndDisabled();
            ImGui.EndChild();
        }

        ImGui.SameLine();
        bool detailsVisible = ImGui.BeginChild("PresetMapDetails", new Vector2(0, 0), true);
        if (detailsVisible)
        {
            if (sequence.Maps.Count == 0)
            {
                DrawSectionTitle("地图详情");
                ImGui.TextDisabled("从左侧添加地图后，在这里编辑地图和停止条件。");
            }
            else
            {
                this.DrawPresetMapDetails(sequence.Maps[this.presetEditorMapIndex], this.presetEditorMapIndex);
            }
        }
        ImGui.EndChild();

        ImGui.EndChild();
    }

    private void DrawPresetMapDetails(MapPreset preset, int index)
    {
        DrawSectionTitle($"地图 {index + 1} 设置");
        uint territory = preset.TerritoryId;
        uint previousTerritory = territory;
        this.DrawTerritorySelector("地图", $"preset-map-{index}", ref territory, allowCurrent: true);
        preset.TerritoryId = territory;
        if (territory != previousTerritory)
        {
            preset.TargetFateId = null;
            preset.StopConditions.RemoveAll(stop => stop.Kind == StopConditionKind.TargetFate);
        }

        uint entryPet = preset.EntryPetItemId;
        this.DrawCompanionItemSelector("进入地图后召唤宠物", $"preset-{index}-entry-pet", ref entryPet);
        preset.EntryPetItemId = entryPet;
        uint entryWeapon = preset.EntryWeaponItemId;
        this.DrawItemSelector("进入地图后装备武器", $"preset-{index}-entry-weapon", ref entryWeapon);
        preset.EntryWeaponItemId = entryWeapon;
        ImGui.TextDisabled("以太之光：按所选地图自动选择默认已解锁入口");

        ImGui.Spacing();
        DrawSectionTitle("停止条件");
        this.DrawMapStopConditions(preset, index);
    }

    private string BuildPresetMapSummary(MapPreset preset)
    {
        List<string> parts = [];
        string territoryName = preset.TerritoryId == 0
            ? this.selectionCatalog.GetTerritoryName(this.controller.CurrentTerritory)
            : this.selectionCatalog.GetTerritoryName(preset.TerritoryId);
        parts.Add(string.IsNullOrWhiteSpace(territoryName) ? "当前地图" : territoryName);

        if (preset.EntryWeaponItemId != 0)
            parts.Add(this.selectionCatalog.GetItemName(preset.EntryWeaponItemId));
        if (preset.EntryPetItemId != 0)
            parts.Add(this.selectionCatalog.GetItemName(preset.EntryPetItemId));

        int fateCount = preset.StopConditions?
            .Where(stop => stop.Kind == StopConditionKind.FateCount)
            .Select(stop => stop.FateCount)
            .FirstOrDefault() ?? 0;
        if (fateCount > 0)
            parts.Add($"次数{fateCount}");

        foreach (StopCondition stop in preset.StopConditions?.Where(stop => stop.Kind == StopConditionKind.ItemCount && stop.ItemId != 0) ?? [])
            parts.Add($"{this.selectionCatalog.GetItemName(stop.ItemId)}({Math.Max(1, stop.ItemCount)})");

        IEnumerable<ushort> targetFates = (preset.StopConditions?
                .Where(stop => stop.Kind == StopConditionKind.TargetFate && stop.TargetFateId != 0)
                .Select(stop => stop.TargetFateId)
                ?? [])
            .Append(preset.TargetFateId.GetValueOrDefault())
            .Where(id => id != 0)
            .Distinct();
        foreach (ushort fateId in targetFates)
            parts.Add(this.selectionCatalog.GetFateDisplayName(fateId, includeMapName: false));

        return string.Join('|', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string Ellipsize(string text, float maximumWidth)
    {
        if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maximumWidth)
            return text;

        const string suffix = "...";
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (ImGui.CalcTextSize(text[..middle] + suffix).X <= maximumWidth)
                low = middle;
            else
                high = middle - 1;
        }

        return text[..low] + suffix;
    }

    private string GetPresetFingerprint() => JsonSerializer.Serialize(new
    {
        this.configuration.ActivePresetSequenceIndex,
        this.configuration.PresetSequences,
    });

    private void DrawPresetSequenceSelector()
    {
        if (this.configuration.PresetSequences.Count == 0)
            this.configuration.PresetSequences.Add(new MapPresetSequence());
        this.configuration.ActivePresetSequenceIndex = Math.Clamp(this.configuration.ActivePresetSequenceIndex, 0, this.configuration.PresetSequences.Count - 1);
        string current = this.configuration.GetActivePresetSequence().Name;
        this.SetSelectorWidth(300f);
        if (ImGui.BeginCombo("当前预设列表", string.IsNullOrWhiteSpace(current) ? $"列表 {this.configuration.ActivePresetSequenceIndex + 1}" : current))
        {
            for (int i = 0; i < this.configuration.PresetSequences.Count; i++)
            {
                MapPresetSequence item = this.configuration.PresetSequences[i];
                bool selected = i == this.configuration.ActivePresetSequenceIndex;
                if (ImGui.Selectable(string.IsNullOrWhiteSpace(item.Name) ? $"列表 {i + 1}" : item.Name, selected))
                {
                    this.configuration.ActivePresetSequenceIndex = i;
                    this.configuration.PresetSequence = item;
                    this.pendingPresetSequenceDelete = false;
                    this.pendingPresetSequenceDeleteUntil = DateTime.MinValue;
                    if (this.controller.IsRunning)
                        this.controller.Retry();
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.Button("新建列表"))
        {
            this.configuration.PresetSequences.Add(new MapPresetSequence { Name = $"列表 {this.configuration.PresetSequences.Count + 1}" });
            this.configuration.ActivePresetSequenceIndex = this.configuration.PresetSequences.Count - 1;
            this.configuration.PresetSequence = this.configuration.PresetSequences[^1];
            this.pendingPresetSequenceDelete = false;
            this.pendingPresetSequenceDeleteUntil = DateTime.MinValue;
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(this.configuration.PresetSequences.Count <= 1);
        bool deleteConfirm = this.pendingPresetSequenceDelete
                             && DateTime.UtcNow < this.pendingPresetSequenceDeleteUntil;
        if (ImGui.Button(deleteConfirm ? "确认删除？" : "删除当前列表"))
        {
            if (!deleteConfirm)
            {
                this.pendingPresetSequenceDelete = true;
                this.pendingPresetSequenceDeleteUntil = DateTime.UtcNow.AddSeconds(2);
            }
            else
            {
                this.configuration.PresetSequences.RemoveAt(this.configuration.ActivePresetSequenceIndex);
                this.configuration.ActivePresetSequenceIndex = Math.Clamp(this.configuration.ActivePresetSequenceIndex, 0, this.configuration.PresetSequences.Count - 1);
                this.configuration.PresetSequence = this.configuration.PresetSequences[this.configuration.ActivePresetSequenceIndex];
                this.pendingPresetSequenceDelete = false;
                this.pendingPresetSequenceDeleteUntil = DateTime.MinValue;
            }
        }
        ImGui.EndDisabled();
    }

    private void DrawMapStopConditions(MapPreset preset, int presetIndex)
    {
        ImGui.TextUnformatted("停止条件（勾选的条件全部满足后进入下一地图）");
        bool fateEnabled = preset.StopConditions.Any(s => s.Kind == StopConditionKind.FateCount);
        if (ImGui.Checkbox("满足次数", ref fateEnabled))
        {
            preset.StopConditions.RemoveAll(s => s.Kind == StopConditionKind.FateCount);
            if (fateEnabled) preset.StopConditions.Add(new StopCondition { Kind = StopConditionKind.FateCount, FateCount = 1 });
        }
        if (fateEnabled)
        {
            StopCondition stop = preset.StopConditions.First(s => s.Kind == StopConditionKind.FateCount);
            int count = stop.FateCount;
            ImGui.SameLine();
            if (ImGui.InputInt("次数", ref count)) stop.FateCount = Math.Max(1, count);
        }

        bool itemEnabled = preset.StopConditions.Any(s => s.Kind == StopConditionKind.ItemCount);
        if (ImGui.Checkbox("满足物品数量", ref itemEnabled))
        {
            if (!itemEnabled) preset.StopConditions.RemoveAll(s => s.Kind == StopConditionKind.ItemCount);
            else if (!preset.StopConditions.Any(s => s.Kind == StopConditionKind.ItemCount)) preset.StopConditions.Add(new StopCondition { Kind = StopConditionKind.ItemCount });
        }
        if (itemEnabled)
        {
            int remove = -1;
            int row = 0;
            foreach (StopCondition stop in preset.StopConditions.Where(s => s.Kind == StopConditionKind.ItemCount).ToList())
            {
                ImGui.PushID($"item{row}");
                uint itemId = stop.ItemId; int count = stop.ItemCount;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 16);
                this.DrawItemSelector(string.Empty, $"preset-{presetIndex}-item-{row}", ref itemId, maximumWidth: 360f);
                int ownedCount = itemId == 0 ? 0 : this.inventoryCounter.Count(itemId);
                ImGui.SameLine();
                ImGui.Text($"{ownedCount}/");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                if (ImGui.InputInt($"##item-count-{presetIndex}-{row}", ref count))
                    stop.ItemCount = Math.Max(1, count);
                stop.ItemId = itemId; stop.ItemCount = Math.Max(1, count);
                ImGui.SameLine(); if (ImGui.SmallButton("删除")) remove = preset.StopConditions.IndexOf(stop);
                ImGui.PopID(); row++;
            }
            if (remove >= 0) preset.StopConditions.RemoveAt(remove);
            if (ImGui.Button("添加物品")) preset.StopConditions.Add(new StopCondition { Kind = StopConditionKind.ItemCount });
        }

        bool targetEnabled = preset.StopConditions.Any(s => s.Kind == StopConditionKind.TargetFate);
        if (ImGui.Checkbox("完成指定 FATE", ref targetEnabled))
        {
            if (!targetEnabled) preset.StopConditions.RemoveAll(s => s.Kind == StopConditionKind.TargetFate);
            else if (!preset.StopConditions.Any(s => s.Kind == StopConditionKind.TargetFate)) preset.StopConditions.Add(new StopCondition { Kind = StopConditionKind.TargetFate });
        }
        if (targetEnabled)
        {
            int remove = -1; int row = 0;
            foreach (StopCondition stop in preset.StopConditions.Where(s => s.Kind == StopConditionKind.TargetFate).ToList())
            {
                ImGui.PushID($"target{row}");
                ushort? fateId = stop.TargetFateId == 0 ? null : stop.TargetFateId;
                int count = stop.FateCount;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 16);
                uint effectiveTerritory = preset.TerritoryId == 0 ? this.controller.CurrentTerritory : preset.TerritoryId;
                this.DrawFateSelector(
                    "FATE",
                    $"preset-{presetIndex}-fate-{row}",
                    effectiveTerritory,
                    ref fateId,
                    maximumWidth: 360f,
                    showRecent: true);
                ImGui.SameLine(); ImGui.SetNextItemWidth(120); ImGui.InputInt("次数", ref count);
                stop.TargetFateId = fateId ?? 0; stop.FateCount = Math.Max(1, count);
                ImGui.SameLine(); if (ImGui.SmallButton("删除")) remove = preset.StopConditions.IndexOf(stop);
                ImGui.PopID(); row++;
            }
            if (remove >= 0) preset.StopConditions.RemoveAt(remove);
            if (ImGui.Button("添加指定 FATE")) preset.StopConditions.Add(new StopCondition { Kind = StopConditionKind.TargetFate, FateCount = 1 });
            TargetFateFallbackPolicy fallback = preset.TargetFallback;
            bool hasOther = fateEnabled || itemEnabled;
            if (hasOther) fallback = TargetFateFallbackPolicy.FarmOtherFates;
            this.DrawChoiceButtons(
                "指定 FATE 未出现",
                ref fallback,
                (TargetFateFallbackPolicy.WaitOnly, FallbackLabel(TargetFateFallbackPolicy.WaitOnly)),
                (TargetFateFallbackPolicy.FarmOtherFates, FallbackLabel(TargetFateFallbackPolicy.FarmOtherFates)));
            preset.TargetFallback = fallback;
            if (hasOther) ImGui.TextDisabled("同时启用其他条件时，已强制选择“刷其他的”");
        }
    }

    private void DrawAdvancedTab()
    {
        bool showOverlay = this.configuration.ShowOverlayWindow;
        if (DrawSettingCheckbox(
                "显示悬浮窗",
                "advanced-show-overlay",
                "显示一个独立的小型状态窗口，提供开始、暂停、停止按钮，以及当前状态、模式、装备和任务进度。",
                ref showOverlay))
        {
            this.configuration.ShowOverlayWindow = showOverlay;
            this.setOverlayWindowVisibility(showOverlay);
        }
        ImGui.Separator();

        DrawSectionTitle("伙伴设置");
        bool autoChocobo = this.configuration.AutoSummonChocoboCompanion;
        if (DrawSettingCheckbox(
                "自动召唤/延长陆行鸟伙伴",
                "advanced-chocobo",
                "启用后，进入地图、传送到达和前往 FATE 等安全时机会自动召唤或延长战斗陆行鸟。",
                ref autoChocobo))
            this.configuration.AutoSummonChocoboCompanion = autoChocobo;
        DrawSectionTitle("音效提醒");
        bool soundAlerts = this.configuration.EnableSoundAlerts;
        if (DrawSettingCheckbox(
                "启用游戏内置音效提醒",
                "advanced-sound-alerts",
                "启用后，在指定 FATE 出现、FATE 完成、角色死亡或普通 FATE 跳过导航时播放下方选择的游戏内置音效。",
                ref soundAlerts))
            this.configuration.EnableSoundAlerts = soundAlerts;
        uint targetAppearedSound = this.configuration.SoundAlertTargetAppearedEffectId;
        this.DrawSoundSelector(
            "指定 FATE 出现",
            "advanced-sound-target",
            "指定 FATE 出现在当前地图且可以前往时播放的音效。",
            ref targetAppearedSound);
        this.configuration.SoundAlertTargetAppearedEffectId = targetAppearedSound;
        uint completedSound = this.configuration.SoundAlertFateCompletedEffectId;
        this.DrawSoundSelector(
            "FATE 完成",
            "advanced-sound-completed",
            "自动化确认 FATE 成功完成并准备继续任务时播放的音效。",
            ref completedSound);
        this.configuration.SoundAlertFateCompletedEffectId = completedSound;
        uint deathSound = this.configuration.SoundAlertDeathEffectId;
        this.DrawSoundSelector(
            "角色死亡",
            "advanced-sound-death",
            "检测到角色死亡并进入死亡恢复流程时播放的音效。",
            ref deathSound);
        this.configuration.SoundAlertDeathEffectId = deathSound;
        uint navigationSound = this.configuration.SoundAlertNavigationSkippedEffectId;
        this.DrawSoundSelector(
            "普通 FATE 导航跳过",
            "advanced-sound-navigation",
            "普通 FATE 因距离、状态或其他条件被跳过，没有开始前往时播放的音效。",
            ref navigationSound);
        this.configuration.SoundAlertNavigationSkippedEffectId = navigationSound;
        int soundCooldown = this.configuration.SoundAlertCooldownSeconds;
        if (DrawSettingSliderInt(
                "音效提醒冷却（秒）",
                "advanced-sound-cooldown",
                "同一种提醒两次播放之间的最短间隔；设为 0 表示不额外限制播放频率。",
                ref soundCooldown,
                0,
                60))
            this.configuration.SoundAlertCooldownSeconds = soundCooldown;
        ImGui.TextDisabled("每项可选择“无音效”或游戏内置 <se.1> 至 <se.16>。");

        this.DrawFateBlacklist();

        DrawSectionTitle("前往 FATE 与拉怪");
        bool fly = this.configuration.FlyToFates;
        if (DrawSettingCheckbox(
                "上坐骑并飞行前往 FATE",
                "advanced-fly-to-fates",
                "启用后，脱战时优先骑乘并飞行前往目标 FATE；接近 FATE 后会落地、下坐骑并进行等级同步。",
                ref fly))
            this.configuration.FlyToFates = fly;
        int nextFateDelay = this.configuration.NextFateDelaySeconds;
        if (DrawSettingSliderInt(
                "前往下一个 FATE 前延迟（秒）",
                "advanced-next-fate-delay",
                "选择下一个 FATE 后、开始导航前等待多久；0 表示不延迟，1 表示固定延迟 1 秒，2 以上表示在 2 秒到当前值之间随机延迟。",
                ref nextFateDelay,
                0,
                60))
            this.configuration.NextFateDelaySeconds = nextFateDelay;
        int aggro = this.configuration.MaxAggroCount;
        if (DrawSettingSliderInt(
                "主动拉怪上限",
                "advanced-max-aggro",
                "普通怪物类 FATE 中，主动吸引并保持仇恨的目标数量上限；BOSS 类 FATE 不使用此设置。",
                ref aggro,
                1,
                4))
            this.configuration.MaxAggroCount = aggro;
        bool prioritizeLost = this.configuration.PrioritizeLostGirlAndLostOne;
        if (DrawSettingCheckbox(
                "优先攻击迷失少女/迷失者",
                "advanced-prioritize-lost",
                "勾选后，当前 FATE 中出现迷失少女或迷失者时会暂时锁定并优先击杀，目标死亡或消失后恢复原有选怪逻辑。仅在 FATE 剩余时间达到对应阈值时触发。",
                ref prioritizeLost))
        {
            this.configuration.PrioritizeLostGirlAndLostOne = prioritizeLost;
        }
        if (this.configuration.PrioritizeLostGirlAndLostOne)
        {
            int lostGirlThreshold = this.configuration.LostGirlRemainingTimeThresholdSeconds;
            if (DrawSettingSliderInt(
                    "迷失少女击杀剩余时间阈值（秒）",
                    "advanced-lost-girl-threshold",
                    "当前 FATE 剩余时间不低于此值时才会优先击杀迷失少女；设为 0 表示不限制剩余时间。默认 180 秒（3 分钟）。",
                    ref lostGirlThreshold,
                    0,
                    600))
            {
                this.configuration.LostGirlRemainingTimeThresholdSeconds = lostGirlThreshold;
            }
            int lostOneThreshold = this.configuration.LostOneRemainingTimeThresholdSeconds;
            if (DrawSettingSliderInt(
                    "迷失者击杀剩余时间阈值（秒）",
                    "advanced-lost-one-threshold",
                    "当前 FATE 剩余时间不低于此值时才会优先击杀迷失者；设为 0 表示不限制剩余时间。默认 240 秒（4 分钟）。",
                    ref lostOneThreshold,
                    0,
                    600))
            {
                this.configuration.LostOneRemainingTimeThresholdSeconds = lostOneThreshold;
            }
        }
        PullRefillPolicy refillPolicy = this.configuration.PullRefillPolicy;
        this.DrawEnumCombo(
            "补充拉怪时机",
            "advanced-pull-refill",
            "决定当前一批目标减少后何时再次主动拉怪；可选择剩余目标较少时补充，或整批清空后再拉。",
            ref refillPolicy,
            PullRefillPolicyLabel);
        this.configuration.PullRefillPolicy = refillPolicy;
        ImGui.TextDisabled(refillPolicy == PullRefillPolicy.RefillAtHalf
            ? $"当前战斗目标不多于 {this.configuration.MaxAggroCount / 2} 只时补充至上限。"
            : "本批目标全部死亡后才开始下一批。");
        float approach = this.configuration.PullApproachDistance;
        if (DrawSettingSliderFloat(
                "拉怪接近距离",
                "advanced-pull-approach",
                "主动拉怪时接近目标到多少 yalms 后停止移动并等待建立仇恨；数值越小越接近目标。",
                ref approach,
                1f,
                20f,
                "%.1f yalms"))
            this.configuration.PullApproachDistance = approach;
        int aggroTimeout = this.configuration.AggroConfirmationTimeoutSeconds;
        if (DrawSettingSliderInt(
                "近距离仇恨确认超时（秒）",
                "advanced-aggro-timeout",
                "到达拉怪接近距离后，等待目标确认正在攻击玩家的最长时间；超时后会尝试处理或跳过目标。",
                ref aggroTimeout,
                2,
                60))
            this.configuration.AggroConfirmationTimeoutSeconds = aggroTimeout;
        int targetCooldown = this.configuration.SkippedTargetCooldownSeconds;
        if (DrawSettingSliderInt(
                "目标跳过冷却（秒）",
                "advanced-target-cooldown",
                "某个目标暂时无法处理而被跳过后，在这段时间内不会立即重复选择它。",
                ref targetCooldown,
                5,
                300))
            this.configuration.SkippedTargetCooldownSeconds = targetCooldown;

        DrawSectionTitle("死亡恢复");
        bool autoRaise = this.configuration.AutoAcceptRaise;
        if (DrawSettingCheckbox(
                "自动接受其他玩家的复活",
                "advanced-auto-raise",
                "检测到其他玩家发起的复活时自动接受，减少死亡后长时间等待。",
                ref autoRaise))
            this.configuration.AutoAcceptRaise = autoRaise;
        bool autoReturn = this.configuration.AutoReturnAfterDeathTimeout;
        if (DrawSettingCheckbox(
                "等待超时后自动返回复活点",
                "advanced-auto-return",
                "等待复活超过下方时间后自动返回最近的复活点，并继续死亡恢复流程；关闭后会一直等待复活或手动操作。",
                ref autoReturn))
            this.configuration.AutoReturnAfterDeathTimeout = autoReturn;
        if (this.configuration.AutoReturnAfterDeathTimeout)
        {
            int deathWait = this.configuration.DeathRaiseWaitSeconds;
            if (DrawSettingSliderInt(
                    "等待复活时间（秒）",
                    "advanced-death-wait",
                    "死亡后等待其他玩家复活的时间；计时结束且仍未复活时执行自动返回复活点。",
                    ref deathWait,
                    0,
                    120))
                this.configuration.DeathRaiseWaitSeconds = deathWait;
        }
        else
        {
            ImGui.TextDisabled("自动返回已关闭：死亡后会一直等待其他玩家复活或手动操作。");
        }
        int deathCooldown = this.configuration.DeathFateCooldownSeconds;
        if (DrawSettingSliderInt(
                "死亡后 FATE 冷却（秒）",
                "advanced-death-cooldown",
                "角色在某个 FATE 中死亡后，暂时降低再次选择该 FATE 的优先级，避免反复进入同一失败目标。",
                ref deathCooldown,
                30,
                3600))
            this.configuration.DeathFateCooldownSeconds = deathCooldown;

        DrawSectionTitle("导航与异常恢复");
        int stuck = this.configuration.NavigationStuckSeconds;
        if (DrawSettingSliderInt(
                "导航无进展判定（秒）",
                "advanced-navigation-stuck",
                "角色持续没有有效位置变化时，等待多久才认为导航可能卡住并进入恢复流程。",
                ref stuck,
                4,
                60))
            this.configuration.NavigationStuckSeconds = stuck;
        int recovery = this.configuration.MaxRecoveryAttempts;
        if (DrawSettingSliderInt(
                "最大恢复次数",
                "advanced-max-recovery",
                "导航或自动化异常时允许连续尝试恢复的次数；设为 0 表示不进行额外重试。",
                ref recovery,
                0,
                20))
            this.configuration.MaxRecoveryAttempts = recovery;
        int combatEscapeTimeout = this.configuration.CombatEscapeTimeoutSeconds;
        if (DrawSettingSliderInt(
                "无目标战斗跑离超时（秒）",
                "advanced-combat-escape-timeout",
                "战斗状态中没有可见目标时，最多持续跑离多久；超时后可能触发副本进退来重置异常仇恨。",
                ref combatEscapeTimeout,
                5,
                120))
            this.configuration.CombatEscapeTimeoutSeconds = combatEscapeTimeout;
        float combatEscapeDistance = this.configuration.CombatEscapeDistance;
        if (DrawSettingSliderFloat(
                "无目标战斗跑离距离",
                "advanced-combat-escape-distance",
                "战斗状态中没有目标时，先尝试离开当前位置多远以摆脱残留仇恨；距离越大，跑离范围越远。",
                ref combatEscapeDistance,
                30f,
                200f,
                "%.0f yalms"))
            this.configuration.CombatEscapeDistance = combatEscapeDistance;
    }

    private void DrawFateBlacklist()
    {
        DrawSettingLabel(
            "FATE 黑名单",
            "加入黑名单的 FATE 不会被自动评分、选择、抢占或导航；下方列表固定高度，超出部分可滚动查看。",
            "advanced-fate-blacklist");
        ImGui.TextDisabled("黑名单中的 FATE 不会被自动评分、选择、抢占或导航。");

        ushort? selected = null;
        DrawSettingLabel(
            "添加 FATE",
            "从下拉框选择一个 FATE 加入黑名单；已经在黑名单中的条目会在选项前显示勾号。",
            "advanced-fate-blacklist-add");
        ImGui.SameLine(0f, 12f);
        this.DrawFateSelector(
            string.Empty,
            "fate-blacklist-add",
            null,
            ref selected,
            includeMapName: true,
            requireSearch: true,
            showRecent: false,
            closeOnSelect: false);
        if (selected is { } selectedFateId && selectedFateId != 0)
        {
            if (this.configuration.FateBlacklist.Contains(selectedFateId))
                this.configuration.FateBlacklist.Remove(selectedFateId);
            else
                this.configuration.FateBlacklist.Add(selectedFateId);
            this.configuration.FateBlacklist = this.configuration.FateBlacklist.Distinct().Order().ToList();
        }

        int removeIndex = -1;
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.2f, 0.21f, 0.23f, 1f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.42f, 0.44f, 0.47f, 1f));
        ImGui.BeginChild(
            "fate-blacklist-list",
            new Vector2(0, 180f),
            true,
            ImGuiWindowFlags.AlwaysVerticalScrollbar);
        bool blacklistHovered = ImGui.IsWindowHovered();
        float blacklistWheel = ImGui.GetIO().MouseWheel;
        float blacklistScroll = ImGui.GetScrollY();
        float blacklistScrollMax = ImGui.GetScrollMaxY();
        if (this.configuration.FateBlacklist.Count == 0)
        {
            ImGui.TextDisabled("当前没有屏蔽的 FATE。");
        }
        else
        {
            for (int i = 0; i < this.configuration.FateBlacklist.Count; i++)
            {
                ushort fateId = this.configuration.FateBlacklist[i];
                ImGui.PushID($"blacklist-{fateId}-{i}");
                ImGui.BulletText(this.selectionCatalog.GetFateDisplayName(fateId, includeMapName: true));
                ImGui.SameLine();
                ImGui.TextDisabled($"编号 {fateId}");
                ImGui.SameLine();
                if (ImGui.SmallButton("移除"))
                    removeIndex = i;
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        if (blacklistHovered
            && blacklistScrollMax > 0f
            && blacklistWheel != 0f
            && ((blacklistWheel > 0f && blacklistScroll <= 0.5f)
                || (blacklistWheel < 0f && blacklistScroll >= blacklistScrollMax - 0.5f)))
        {
            float scrollStep = ImGui.GetTextLineHeight() * 5f;
            ImGui.SetScrollY(ImGui.GetScrollY() - blacklistWheel * scrollStep);
        }
        ImGui.PopStyleColor(2);

        if (removeIndex >= 0)
            this.configuration.FateBlacklist.RemoveAt(removeIndex);
    }

    private void DrawDiagnosticsTab()
    {
        DrawSectionTitle("Wiki FATE 数据");
        Vector4 wikiColor = this.controller.IsStaticFateCatalogAvailable
            ? new Vector4(0.35f, 0.9f, 0.45f, 1f)
            : new Vector4(0.95f, 0.45f, 0.3f, 1f);
        ImGui.TextColored(
            wikiColor,
            this.controller.IsStaticFateCatalogAvailable ? "已加载" : "不可用");
        ImGui.SameLine();
        ImGui.Text($"FATE 条目 {this.controller.StaticFateCatalogFateCount}  ·  合集 {this.controller.StaticFateCatalogCollectionCount}");
        if (!this.controller.IsStaticFateCatalogAvailable
            && !string.IsNullOrWhiteSpace(this.controller.StaticFateCatalogLoadError))
            ImGui.TextColored(new Vector4(1f, 0.55f, 0.35f, 1f), $"加载错误：{this.controller.StaticFateCatalogLoadError}");
        ImGui.TextDisabled("Wiki 数据只在诊断页展示；运行页仅显示实时运行状态。");
        if (ImGui.Button("扫描当前 FATE 并写入诊断日志"))
            this.controller.RequestLogScan();

        ImGui.Spacing();
        DrawSectionTitle("测试操作");
        ImGui.TextDisabled("以下操作会直接调用游戏接口，仅用于排查传送、等级同步和 Boss 周围清理问题。");
        if (ImGui.Button("执行副本进退"))
            this.controller.RequestDutyRoundTrip();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "以单人解除限制进入一次泰坦歼灭战并立即退出。\\n"
                + "执行时会中断当前导航和 FATE；处于小队或已有副本队列时会拒绝执行。");
        }
        if (ImGui.Button("取消等级同步（测试）"))
            this.controller.RequestCancelLevelSync();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("调用游戏原生 FateLevelSync(813, FATE ID, 0) 取消同步，不会主动停止自动化；结果会写入诊断页和 XLLog。");
        if (ImGui.Button("取消同步并清理 Boss 周围（测试）"))
            this.controller.RequestCancelSyncAndCleanBossAreaTest();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("仅在讨伐BOSS战斗状态可用：调用原生取消同步，然后立即清理扩大的 Boss 周围非 FATE 怪物范围，完成后重新同步。");

        ImGui.Spacing();
        DrawSectionTitle("运行诊断");
        ImGui.TextWrapped("等级同步通过 FFXIVClientStructs FateManager.LevelSync 执行，并以 IPlayerState.IsLevelSynced 验证；_FateProgress 的人工点击事件仅用于版本诊断。");
        ImGui.TextDisabled("详细日志会在独立窗口中显示，长消息会自动换行；当前保留最近 500 条记录。");
        if (ImGui.Button("打开详细日志窗口"))
            this.diagnosticLogOpen = true;
        ImGui.SameLine();
        ImGui.TextDisabled($"当前 {this.controller.Diagnostics.Count}/500 条");
    }

    private void DrawDiagnosticLogWindow()
    {
        if (!this.diagnosticLogOpen)
            return;

        bool open = this.diagnosticLogOpen;
        ImGui.SetNextWindowSize(new Vector2(900, 620), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSizeConstraints(new Vector2(560, 320), new Vector2(1600, 1200));
        if (ImGui.Begin("AutoFatre - 运行诊断日志", ref open))
        {
            ImGui.SetWindowFontScale(1.08f);
            ImGui.TextDisabled("长消息会自动换行；日志最多保留最近 500 条。滚动到其他位置后，自动滚动不会打断查看。");
            if (ImGui.Button("复制全部日志"))
                ImGui.SetClipboardText(this.BuildDiagnosticLogText());
            ImGui.SameLine();
            if (ImGui.Button("跳到底部"))
            {
                this.diagnosticLogAutoScroll = true;
                this.diagnosticLogJumpToBottom = true;
            }
            ImGui.SameLine();
            ImGui.Checkbox("自动滚动", ref this.diagnosticLogAutoScroll);
            ImGui.Separator();

            bool childVisible = ImGui.BeginChild(
                "DiagnosticLogWindowBody",
                new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 1.5f),
                true,
                ImGuiWindowFlags.AlwaysVerticalScrollbar);
            if (childVisible)
            {
                bool wasAtBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f;
                IReadOnlyList<DiagnosticEntry> entries = this.controller.Diagnostics;
                if (entries.Count == 0)
                {
                    ImGui.TextDisabled("暂无诊断日志。");
                }
                else
                {
                    for (int i = 0; i < entries.Count; i++)
                    {
                        DiagnosticEntry entry = entries[i];
                        Vector4 color = DiagnosticColor(entry.Severity);
                        string line = $"{entry.Timestamp:HH:mm:ss} [{DiagnosticSeverityLabel(entry.Severity)}] {entry.Message}";
                        ImGui.PushStyleColor(ImGuiCol.Text, color);
                        ImGui.TextWrapped(line);
                        ImGui.PopStyleColor();
                        if (i + 1 < entries.Count)
                            ImGui.Separator();
                    }
                }

                if (this.diagnosticLogAutoScroll
                    && (wasAtBottom || ImGui.IsWindowAppearing() || this.diagnosticLogJumpToBottom))
                    ImGui.SetScrollHereY(1f);
                this.diagnosticLogJumpToBottom = false;
            }
            ImGui.EndChild();
        }

        ImGui.End();
        this.diagnosticLogOpen = open;
    }

    private string BuildDiagnosticLogText() => string.Join(
        Environment.NewLine,
        this.controller.Diagnostics.Select(entry =>
            $"{entry.Timestamp:HH:mm:ss} [{DiagnosticSeverityLabel(entry.Severity)}] {entry.Message}"));

    private static Vector4 DiagnosticColor(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => new Vector4(1f, 0.3f, 0.3f, 1f),
        DiagnosticSeverity.Warning => new Vector4(1f, 0.75f, 0.25f, 1f),
        DiagnosticSeverity.Information => new Vector4(0.7f, 0.85f, 1f, 1f),
        _ => new Vector4(0.65f, 0.65f, 0.65f, 1f),
    };

    private static string DiagnosticSeverityLabel(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "错误",
        DiagnosticSeverity.Warning => "警告",
        DiagnosticSeverity.Information => "信息",
        _ => "调试",
    };

    private void DrawDeathRecordsWindow()
    {
        if (!this.deathRecordsOpen)
            return;

        bool open = this.deathRecordsOpen;
        ImGui.SetNextWindowSize(new Vector2(620, 420), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("AutoFatre - 失败记录", ref open))
        {
            ImGui.SetWindowFontScale(1.08f);
            ImGui.TextDisabled("记录 FATE 期间的死亡和自动化失败；时间按本地时区显示。");
            ImGui.SameLine();
            if (ImGui.Button("清空记录"))
            {
                this.configuration.DeathRecords.Clear();
                this.pluginInterface.SavePluginConfig(this.configuration);
            }

            ImGui.Separator();
            if (ImGui.BeginChild("DeathRecordsList", new Vector2(0, 0), true))
            {
                ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 1f), "死亡记录");
                if (this.controller.DeathRecords.Count == 0)
                    ImGui.TextDisabled("暂无死亡记录。");
                else
                {
                    foreach (DeathRecord record in this.controller.DeathRecords)
                    {
                        string name = string.IsNullOrWhiteSpace(record.FateName) ? "未知 FATE" : record.FateName;
                        ImGui.BulletText($"{record.OccurredAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  #{record.FateId}  {name}  地图 {this.selectionCatalog.GetTerritoryName(record.TerritoryId)}");
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), "FATE 失败记录");
                if (this.controller.FateFailureRecords.Count == 0)
                    ImGui.TextDisabled("暂无失败记录。");
                else
                {
                    foreach (FateFailureRecord record in this.controller.FateFailureRecords)
                    {
                        string name = string.IsNullOrWhiteSpace(record.FateName) ? "未知 FATE" : record.FateName;
                        ImGui.BulletText(
                            $"{record.OccurredAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}  #{record.FateId}  {name}  " +
                            $"[{record.FailureKind}] {record.Reason}");
                    }
                }

                ImGui.EndChild();
            }
        }

        ImGui.End();
        this.deathRecordsOpen = open;
    }

    private void DrawTerritorySelector(
        string label,
        string selectorKey,
        ref uint territoryId,
        bool allowCurrent)
    {
        this.SetSelectorWidth();
        string preview = territoryId == 0 && allowCurrent
            ? $"当前地图（{this.selectionCatalog.GetTerritoryName(this.controller.CurrentTerritory)}）"
            : this.selectionCatalog.GetTerritoryName(territoryId);
        if (!ImGui.BeginCombo($"{label}##{selectorKey}", preview, ImGuiComboFlags.HeightLarge))
            return;

        string search = this.GetSelectorSearch(selectorKey);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.InputTextWithHint($"##search-{selectorKey}", "搜索地图名称...", ref search, 128))
            this.SetSelectorSearch(selectorKey, search);

        ImGui.Separator();
        ImGui.BeginChild($"selector-results-{selectorKey}", new Vector2(0, 300), true);

        if (allowCurrent && (string.IsNullOrWhiteSpace(search) || "当前地图".Contains(search, StringComparison.CurrentCultureIgnoreCase)))
        {
            bool selected = territoryId == 0;
            if (ImGui.Selectable($"当前地图（{this.selectionCatalog.GetTerritoryName(this.controller.CurrentTerritory)}）##current-{selectorKey}", selected))
            {
                territoryId = 0;
                this.CompleteSelection(selectorKey);
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        int shown = 0;
        foreach (NamedGameRow territory in this.selectionCatalog.Territories)
        {
            if (!Matches(territory.Name, territory.Id, search))
                continue;

            bool selected = territoryId == territory.Id;
            if (ImGui.Selectable($"{territory.Name}##territory-{territory.Id}-{selectorKey}", selected))
            {
                territoryId = territory.Id;
                this.CompleteSelection(selectorKey);
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"地图编号 {territory.Id}");
            if (selected)
                ImGui.SetItemDefaultFocus();
            shown++;
        }

        if (shown == 0 && !(allowCurrent && string.IsNullOrWhiteSpace(search)))
            ImGui.TextDisabled("没有匹配的地图。");
        ImGui.EndChild();
        ImGui.EndCombo();
    }

    private void DrawFateSelector(
        string label,
        string selectorKey,
        uint? territoryId,
        ref ushort? fateId,
        bool includeMapName = false,
        bool requireSearch = false,
        float maximumWidth = 420f,
        bool showRecent = false,
        bool closeOnSelect = true)
    {
        this.SetSelectorWidth(maximumWidth);
        IReadOnlyList<FateSelectionEntry> available = territoryId is { } mapTerritory
            ? this.selectionCatalog.GetFatesForTerritory(mapTerritory)
            : this.selectionCatalog.Fates;
        string preview = this.selectionCatalog.GetFateDisplayName(fateId, includeMapName);
        if (!ImGui.BeginCombo($"{label}##{selectorKey}", preview, ImGuiComboFlags.HeightLarge))
        {
            this.DrawSelectedFateNotice(fateId, available);
            return;
        }

        string search = this.GetSelectorSearch(selectorKey);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.InputTextWithHint($"##search-{selectorKey}", "搜索 FATE 名称、类型或目标...", ref search, 192))
            this.SetSelectorSearch(selectorKey, search);

        ImGui.Separator();
        ImGui.BeginChild($"selector-results-{selectorKey}", new Vector2(0, 300), true);

        bool noSelection = fateId is null or 0;
        if (ImGui.Selectable($"未选择 FATE##none-{selectorKey}", noSelection))
        {
            fateId = null;
            this.CompleteSelection(selectorKey);
        }
        if (noSelection)
            ImGui.SetItemDefaultFocus();

        int recentShown = showRecent && string.IsNullOrWhiteSpace(search)
            ? this.DrawRecentFateChoices(available, selectorKey, ref fateId, includeMapName)
            : 0;
        if (recentShown > 0)
            ImGui.Separator();

        if (available.Count == 0)
        {
            ImGui.TextDisabled(territoryId is null
                ? "当前客户端没有可用的 FATE 数据。"
                : "该地图没有客户端开放世界 FATE 地图数据；运行时扫描仍可按默认逻辑处理当前 FATE。");
        }
        else if (requireSearch && string.IsNullOrWhiteSpace(search))
        {
            ImGui.TextDisabled("输入地图名、FATE 名称、类型或目标开始搜索。");
        }
        else
        {
            const int maxResults = 100;
            int shown = 0;
            if (recentShown > 0)
                ImGui.TextDisabled("全部 FATE");
            foreach (FateSelectionEntry fate in available
                          .Where(candidate => MatchesFate(candidate, search))
                         .Where(candidate => recentShown == 0
                                             || !string.IsNullOrWhiteSpace(search)
                                             || !this.configuration.RecentTargetFateIds.Contains(candidate.FateId))
                          .OrderBy(candidate => candidate.MapName ?? "~", StringComparer.CurrentCulture)
                         .ThenBy(candidate => candidate.ClientName, StringComparer.CurrentCulture)
                         .ThenBy(candidate => candidate.FateId))
            {
                bool selected = fateId == fate.FateId;
                string series = fate.Enrichment?.CollectionIds.Count > 0 ? " [系列]" : string.Empty;
                string optionName = includeMapName
                    ? $"{fate.MapName ?? "地图未知"} | {fate.ClientName}"
                    : fate.ClientName;
                string blacklistMark = this.configuration.FateBlacklist.Contains(fate.FateId) ? "✓ " : string.Empty;
                string optionText = $"{blacklistMark}{optionName}{series}";
                string optionMetadata = $"{fate.TypeName}{(fate.IsAutomationSupported ? string.Empty : " · 暂未支持")} · 编号 {fate.FateId}";
                if (this.DrawWrappedFateOption(
                        optionText,
                        optionMetadata,
                        $"fate-{fate.FateId}-{selectorKey}",
                        selected,
                        out bool optionHovered))
                {
                    fateId = fate.FateId;
                    if (showRecent)
                        this.RecordRecentFate(fate.FateId);
                    if (closeOnSelect)
                        this.CompleteSelection(selectorKey);
                }
                if (optionHovered)
                {
                    string trigger = fate.Enrichment?.Trigger is { } knownTrigger
                                     && !string.IsNullOrWhiteSpace(knownTrigger)
                        ? knownTrigger
                        : "未知信息";
                    ImGui.SetTooltip($"触发条件：{trigger}");
                }
                if (++shown >= maxResults)
                {
                    ImGui.TextDisabled($"仅显示前 {maxResults} 条结果，请继续缩小关键词范围。");
                    break;
                }
            }

            if (shown == 0)
                ImGui.TextDisabled("没有匹配的 FATE。");
        }

        ImGui.EndChild();
        ImGui.EndCombo();
        this.DrawSelectedFateNotice(fateId, available);
    }

    private int DrawRecentFateChoices(
        IReadOnlyList<FateSelectionEntry> available,
        string selectorKey,
        ref ushort? fateId,
        bool includeMapName)
    {
        int shown = 0;
        foreach (ushort recentId in this.configuration.RecentTargetFateIds.Take(15))
        {
            FateSelectionEntry? fate = available.FirstOrDefault(candidate => candidate.FateId == recentId);
            if (fate is null)
                continue;

            if (shown == 0)
                ImGui.TextDisabled("最近使用");

            bool selected = fateId == fate.FateId;
            string series = fate.Enrichment?.CollectionIds.Count > 0 ? " [系列]" : string.Empty;
            string optionName = includeMapName
                ? $"{fate.MapName ?? "地图未知"} | {fate.ClientName}"
                : fate.ClientName;
            string blacklistMark = this.configuration.FateBlacklist.Contains(fate.FateId) ? "✓ " : string.Empty;
            string optionText = $"{blacklistMark}{optionName}{series}";
            string optionMetadata = $"{fate.TypeName} · 编号 {fate.FateId}";
            if (this.DrawWrappedFateOption(
                    optionText,
                    optionMetadata,
                    $"recent-fate-{fate.FateId}-{selectorKey}",
                    selected,
                    out _))
            {
                fateId = fate.FateId;
                this.RecordRecentFate(fate.FateId);
                this.CompleteSelection(selectorKey);
            }
            shown++;
        }

        return shown;
    }

    private void RecordRecentFate(ushort fateId)
    {
        if (fateId == 0)
            return;

        this.configuration.RecentTargetFateIds.Remove(fateId);
        this.configuration.RecentTargetFateIds.Insert(0, fateId);
        if (this.configuration.RecentTargetFateIds.Count > 15)
            this.configuration.RecentTargetFateIds.RemoveRange(15, this.configuration.RecentTargetFateIds.Count - 15);
        this.pluginInterface.SavePluginConfig(this.configuration);
    }

    private bool DrawWrappedFateOption(
        string text,
        string metadata,
        string key,
        bool selected,
        out bool hovered)
    {
        string wrapped = WrapSelectorText(text);
        Vector2 stylePadding = ImGui.GetStyle().FramePadding;
        float availableWidth = MathF.Max(180f, ImGui.GetContentRegionAvail().X - stylePadding.X * 2f);
        bool inlineMetadata = !wrapped.Contains('\n')
            && ImGui.CalcTextSize(wrapped).X + ImGui.GetStyle().ItemSpacing.X + ImGui.CalcTextSize(metadata).X <= availableWidth;
        float textHeight = ImGui.CalcTextSize(wrapped).Y;
        if (!inlineMetadata)
            textHeight += ImGui.GetStyle().ItemSpacing.Y + ImGui.GetTextLineHeight();
        float height = textHeight + stylePadding.Y * 2f;

        Vector2 rowStart = ImGui.GetCursorScreenPos();
        bool pressed = ImGui.Selectable($"##{key}", selected, ImGuiSelectableFlags.None, new Vector2(0f, height));
        hovered = ImGui.IsItemHovered();
        if (selected)
            ImGui.SetItemDefaultFocus();
        Vector2 rowEnd = ImGui.GetCursorScreenPos();

        ImGui.SetCursorScreenPos(rowStart + new Vector2(stylePadding.X, stylePadding.Y));
        ImGui.TextUnformatted(wrapped);
        if (inlineMetadata)
        {
            ImGui.SameLine(0f, ImGui.GetStyle().ItemSpacing.X);
            ImGui.TextDisabled(metadata);
        }
        else
        {
            ImGui.TextDisabled(metadata);
        }
        ImGui.SetCursorScreenPos(rowEnd);
        return pressed;
    }

    private static string WrapSelectorText(string text)
    {
        float maximumWidth = MathF.Max(180f, ImGui.GetContentRegionAvail().X - 12f);
        if (!text.Contains('\n') && ImGui.CalcTextSize(text).X <= maximumWidth)
            return text;

        var wrapped = new StringBuilder(text.Length + 16);
        var line = new StringBuilder();
        foreach (char character in text)
        {
            if (character == '\n')
            {
                AppendWrappedLine(wrapped, line);
                line.Clear();
                continue;
            }

            string candidate = line.ToString() + character;
            if (line.Length > 0 && ImGui.CalcTextSize(candidate).X > maximumWidth)
            {
                AppendWrappedLine(wrapped, line);
                line.Clear();
            }

            line.Append(character);
        }

        AppendWrappedLine(wrapped, line);
        return wrapped.ToString();
    }

    private static void AppendWrappedLine(StringBuilder destination, StringBuilder line)
    {
        if (destination.Length > 0)
            destination.Append('\n');
        destination.Append(line);
    }

    private void DrawItemSelector(string label, string selectorKey, ref uint itemId, float maximumWidth = 420f)
    {
        this.SetSelectorWidth(maximumWidth);
        if (!ImGui.BeginCombo($"{label}##{selectorKey}", this.selectionCatalog.GetItemName(itemId), ImGuiComboFlags.HeightLarge))
            return;

        string search = this.GetSelectorSearch(selectorKey);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.InputTextWithHint($"##search-{selectorKey}", "搜索物品名称...", ref search, 128))
            this.SetSelectorSearch(selectorKey, search);

        ImGui.Separator();
        ImGui.BeginChild($"selector-results-{selectorKey}", new Vector2(0, 300), true);

        bool noSelection = itemId == 0;
        if (ImGui.Selectable($"未选择物品##none-{selectorKey}", noSelection))
        {
            itemId = 0;
            this.CompleteSelection(selectorKey);
        }
        if (noSelection)
            ImGui.SetItemDefaultFocus();

        if (string.IsNullOrWhiteSpace(search))
        {
            ImGui.TextDisabled($"全部物品（{this.selectionCatalog.Items.Count}）");
        }

        {
            IEnumerable<NamedGameRow> matchingItems = string.IsNullOrWhiteSpace(search)
                ? this.selectionCatalog.Items
                : this.selectionCatalog.Items.Where(item => Matches(item.Name, item.Id, search));
            int shown = 0;
            foreach (NamedGameRow item in matchingItems)
            {
                bool selected = itemId == item.Id;
                if (ImGui.Selectable($"{item.Name}##item-{item.Id}-{selectorKey}", selected))
                {
                    itemId = item.Id;
                    this.CompleteSelection(selectorKey);
                }
                ImGui.SameLine();
                ImGui.TextDisabled($"物品编号 {item.Id}");
                if (selected)
                    ImGui.SetItemDefaultFocus();
                shown++;
            }

            if (shown == 0 && !string.IsNullOrWhiteSpace(search))
                ImGui.TextDisabled("没有匹配的物品。");
        }

        ImGui.EndChild();
        ImGui.EndCombo();
    }

    private void DrawCompanionItemSelector(string label, string selectorKey, ref uint itemId)
    {
        this.SetSelectorWidth();
        string preview = itemId == 0 ? "不召唤宠物" : this.selectionCatalog.GetItemName(itemId);
        if (!ImGui.BeginCombo($"{label}##{selectorKey}", preview, ImGuiComboFlags.HeightLarge))
            return;

        string search = this.GetSelectorSearch(selectorKey);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.InputTextWithHint($"##search-{selectorKey}", "搜索宠物物品名称...", ref search, 128))
            this.SetSelectorSearch(selectorKey, search);
        ImGui.Separator();
        ImGui.BeginChild($"selector-results-{selectorKey}", new Vector2(0, 300), true);
        bool none = itemId == 0;
        if (ImGui.Selectable($"不召唤宠物##none-{selectorKey}", none))
        {
            itemId = 0;
            this.CompleteSelection(selectorKey);
        }
        if (none)
            ImGui.SetItemDefaultFocus();

        int shown = 0;
        foreach (NamedGameRow item in this.selectionCatalog.CompanionItems)
        {
            if (!Matches(item.Name, item.Id, search))
                continue;
            bool selected = itemId == item.Id;
            if (ImGui.Selectable($"{item.Name}##companion-{item.Id}-{selectorKey}", selected))
            {
                itemId = item.Id;
                this.CompleteSelection(selectorKey);
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"物品编号 {item.Id}");
            if (selected)
                ImGui.SetItemDefaultFocus();
            if (++shown >= 100)
                break;
        }
        if (shown == 0 && !string.IsNullOrWhiteSpace(search))
            ImGui.TextDisabled("没有匹配的宠物召唤物品。");
        ImGui.EndChild();
        ImGui.EndCombo();
    }

    private void DrawAetheryteSelector(
        string label,
        string selectorKey,
        uint territoryId,
        ref uint aetheryteId)
    {
        this.SetSelectorWidth();
        if (!ImGui.BeginCombo($"{label}##{selectorKey}", this.selectionCatalog.GetAetheryteName(aetheryteId), ImGuiComboFlags.HeightLarge))
            return;

        string search = this.GetSelectorSearch(selectorKey);
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.InputTextWithHint($"##search-{selectorKey}", "搜索以太之光名称...", ref search, 128))
            this.SetSelectorSearch(selectorKey, search);

        ImGui.Separator();
        ImGui.BeginChild($"selector-results-{selectorKey}", new Vector2(0, 300), true);

        if (string.IsNullOrWhiteSpace(search)
            || "自动选择最近的以太之光".Contains(search, StringComparison.CurrentCultureIgnoreCase))
        {
            bool selected = aetheryteId == 0;
            if (ImGui.Selectable($"自动选择最近的以太之光##auto-{selectorKey}", selected))
            {
                aetheryteId = 0;
                this.CompleteSelection(selectorKey);
            }
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        uint effectiveTerritory = territoryId == 0 ? this.controller.CurrentTerritory : territoryId;
        int shown = 0;
        foreach (NamedGameRow aetheryte in this.selectionCatalog.GetUnlockedAetherytes(effectiveTerritory))
        {
            if (!Matches(aetheryte.Name, aetheryte.Id, search))
                continue;

            bool selected = aetheryteId == aetheryte.Id;
            if (ImGui.Selectable($"{aetheryte.Name}##aetheryte-{aetheryte.Id}-{selectorKey}", selected))
            {
                aetheryteId = aetheryte.Id;
                this.CompleteSelection(selectorKey);
            }
            ImGui.SameLine();
            ImGui.TextDisabled($"水晶编号 {aetheryte.Id}");
            if (selected)
                ImGui.SetItemDefaultFocus();
            shown++;
        }

        if (shown == 0 && !string.IsNullOrWhiteSpace(search))
            ImGui.TextDisabled("没有匹配的已解锁以太之光。");
        else if (shown == 0)
            ImGui.TextDisabled("该地图没有可供手动选择的已解锁以太之光。");
        ImGui.EndChild();
        ImGui.EndCombo();
    }

    private void DrawSelectedFateNotice(
        ushort? fateId,
        IReadOnlyList<FateSelectionEntry> available)
    {
        if (fateId is not { } selectedId || !this.selectionCatalog.TryGetFate(selectedId, out FateSelectionEntry selected))
            return;

        if (!available.Any(candidate => candidate.FateId == selectedId))
        {
            ImGui.TextColored(
                new Vector4(1f, 0.45f, 0.3f, 1f),
                "当前选择不属于所选地图，请重新选择 FATE。");
        }
        if (!selected.IsAutomationSupported)
        {
            ImGui.TextColored(
                new Vector4(1f, 0.45f, 0.3f, 1f),
                $"{selected.TypeName}目前暂未支持，自动化不会前往或触发这个 FATE。");
        }
        if (selected.Enrichment?.CollectionIds.Count > 0)
        {
            ImGui.TextWrapped(
                "这是系列 FATE，自动化程序会考虑其前置 FATE，但有时可能无法正确触发，"
                + "以具体触发条件为准。");
        }
    }

    private string GetSelectorSearch(string selectorKey) =>
        this.selectorSearch.GetValueOrDefault(selectorKey, string.Empty);

    private void SetSelectorSearch(string selectorKey, string search) =>
        this.selectorSearch[selectorKey] = search;

    private void CompleteSelection(string selectorKey)
    {
        this.selectorSearch[selectorKey] = string.Empty;
        ImGui.CloseCurrentPopup();
    }

    private static bool Matches(string name, uint id, string search) =>
        string.IsNullOrWhiteSpace(search)
        || name.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase)
        || id.ToString().Contains(search.Trim(), StringComparison.Ordinal);

    private static bool MatchesFate(FateSelectionEntry fate, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        string query = search.Trim();
        StaticFateCatalogEntry? enrichment = fate.Enrichment;
        return fate.ClientName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || fate.FateId.ToString().Contains(query, StringComparison.Ordinal)
               || fate.TypeName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
               || fate.MapName?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true
               || enrichment?.WikiTaskName.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true
               || enrichment?.Trigger.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true
               || enrichment is not null && EnumerateTargets(enrichment.Targets).Any(target =>
                   target.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private static IEnumerable<string> EnumerateTargets(StaticFateTargets targets) =>
        targets.Destroy
            .Concat(targets.Protect)
            .Concat(targets.Collect)
            .Concat(targets.Escort)
            .Concat(targets.Defeat);

    private static void DrawSettingLabel(string label, string explanation, string key)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(0f, 6f);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.32f, 0.42f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.48f, 0.62f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.2f, 0.4f, 0.55f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 8f);
        ImGui.SmallButton($"!##setting-help-{key}");
        bool hovered = ImGui.IsItemHovered();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);
        if (hovered)
            ImGui.SetTooltip(explanation);
    }

    private static bool DrawSettingCheckbox(
        string label,
        string key,
        string explanation,
        ref bool value)
    {
        DrawSettingLabel(label, explanation, key);
        ImGui.SameLine(0f, 12f);
        return ImGui.Checkbox($"##setting-checkbox-{key}", ref value);
    }

    private static bool DrawSettingSliderInt(
        string label,
        string key,
        string explanation,
        ref int value,
        int minimum,
        int maximum)
    {
        DrawSettingLabel(label, explanation, key);
        ImGui.SameLine(0f, 12f);
        SetSettingControlWidth();
        return ImGui.SliderInt($"##setting-slider-{key}", ref value, minimum, maximum);
    }

    private static bool DrawSettingSliderFloat(
        string label,
        string key,
        string explanation,
        ref float value,
        float minimum,
        float maximum,
        string format)
    {
        DrawSettingLabel(label, explanation, key);
        ImGui.SameLine(0f, 12f);
        SetSettingControlWidth();
        return ImGui.SliderFloat($"##setting-slider-{key}", ref value, minimum, maximum, format);
    }

    private void DrawEnumCombo<T>(
        string label,
        string key,
        string explanation,
        ref T value,
        Func<T, string> display)
        where T : struct, Enum
    {
        DrawSettingLabel(label, explanation, key);
        ImGui.SameLine(0f, 12f);
        SetSettingControlWidth();
        if (!ImGui.BeginCombo($"##setting-combo-{key}", display(value)))
            return;
        foreach (T candidate in Enum.GetValues<T>())
        {
            bool selected = EqualityComparer<T>.Default.Equals(value, candidate);
            if (ImGui.Selectable(display(candidate), selected)) value = candidate;
            if (selected) ImGui.SetItemDefaultFocus();
        }
        ImGui.EndCombo();
    }

    private void DrawChoiceButtons<T>(
        string label,
        ref T value,
        params (T Value, string Label)[] choices)
        where T : struct, Enum
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine(0f, 12f);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f);
        for (int i = 0; i < choices.Length; i++)
        {
            (T candidate, string text) = choices[i];
            bool selected = EqualityComparer<T>.Default.Equals(value, candidate);
            Vector4 buttonColor = selected
                ? new Vector4(0.18f, 0.48f, 0.72f, 1f)
                : new Vector4(0.16f, 0.18f, 0.22f, 1f);
            Vector4 hoveredColor = selected
                ? new Vector4(0.24f, 0.58f, 0.84f, 1f)
                : new Vector4(0.23f, 0.26f, 0.31f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, buttonColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, hoveredColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, hoveredColor);
            float width = MathF.Max(84f, ImGui.CalcTextSize(text).X + 28f);
            if (ImGui.Button($"{text}##choice-{label}-{i}", new Vector2(width, 0)))
                value = candidate;
            ImGui.PopStyleColor(3);
            if (i + 1 < choices.Length)
                ImGui.SameLine(0f, 6f);
        }

        ImGui.PopStyleVar();
    }

    private void DrawSoundSelector(string label, string key, string explanation, ref uint value)
    {
        value = Math.Min(value, 16u);
        DrawSettingLabel(label, explanation, key);
        ImGui.SameLine(0f, 12f);
        SetSettingControlWidth();
        string preview = value == 0 ? "无音效" : $"音效 {value}（<se.{value}>）";
        if (ImGui.BeginCombo($"##sound-selector-{key}", preview))
        {
            for (uint candidate = 0; candidate <= 16; candidate++)
            {
                bool selected = value == candidate;
                string text = candidate == 0 ? "无音效" : $"音效 {candidate}（<se.{candidate}>）";
                if (ImGui.Selectable(text, selected))
                    value = candidate;
                if (selected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"试听##{label}"))
            this.controller.PreviewSoundAlert(value);
    }

    private void SetSelectorWidth(float maximum = 520f)
    {
        float available = ImGui.GetContentRegionAvail().X;
        float width = Math.Min(maximum, Math.Max(220f, available * 0.8f));
        ImGui.SetNextItemWidth(width);
    }

    private static void SetSettingControlWidth(float maximum = 340f)
    {
        float available = ImGui.GetContentRegionAvail().X;
        ImGui.SetNextItemWidth(Math.Min(maximum, Math.Max(180f, available)));
    }

    private static string ModeLabel(AutomationMode mode) => mode switch
    {
        AutomationMode.SingleMapLoop => "单地图循环",
        AutomationMode.TargetFate => "指定 FATE",
        AutomationMode.PresetSequence => "自定义预设序列",
        _ => mode.ToString(),
    };

    private static string FallbackLabel(TargetFateFallbackPolicy policy) => policy switch
    {
        TargetFateFallbackPolicy.WaitOnly => "只等待指定 FATE",
        TargetFateFallbackPolicy.FarmOtherFates => "未出现时刷其他 FATE",
        _ => policy.ToString(),
    };

    private static string SequenceLabel(SequenceCompletionPolicy policy) => policy == SequenceCompletionPolicy.Stop ? "完成后停止" : "循环整个列表";

    private static string PullRefillPolicyLabel(PullRefillPolicy policy) => policy switch
    {
        PullRefillPolicy.RefillAtHalf => "剩余不超过一半时补充",
        PullRefillPolicy.ClearBatchFirst => "整批清空后再拉",
        _ => policy.ToString(),
    };

    private static string StopKindLabel(StopConditionKind kind) => kind switch
    {
        StopConditionKind.FateCount => "成功完成次数",
        StopConditionKind.ItemCount => "背包物品数量",
        StopConditionKind.TargetFate => "完成指定 FATE",
        _ => kind.ToString(),
    };

    private static int ToInt(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;
    private static uint ToUInt(int value) => value <= 0 ? 0u : (uint)value;
    private static ushort? ToUShortOrNull(int value) => value > 0 ? (ushort)Math.Clamp(value, 1, ushort.MaxValue) : null;
}
