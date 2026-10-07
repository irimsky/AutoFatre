using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Newtonsoft.Json;

namespace AutoFatre;

public enum AutoFatreSettingsPage
{
    Interface,
    Companion,
    Movement,
    Combat,
    PriorityTargets,
    DeathRecovery,
    Blacklist,
    Sound,
    Diagnostics,
}

/// <summary>Category navigation and window chrome; page controls reuse the existing UI helpers.</summary>
public sealed class AutoFatreSettingsWindow : Window, IDisposable
{
    private static readonly Vector4 HeaderColor = new(0.85f, 0.72f, 0.35f, 1f);
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly AutoFatreConfiguration configuration;
    private readonly Action<AutoFatreSettingsPage> drawPage;
    private AutoFatreSettingsPage selectedPage = AutoFatreSettingsPage.Interface;

    public AutoFatreSettingsWindow(IDalamudPluginInterface pluginInterface,
        AutoFatreConfiguration configuration, Action<AutoFatreSettingsPage> drawPage)
        : base("AutoFatre 设置###AutoFatreSettingsWindow")
    {
        this.pluginInterface = pluginInterface;
        this.configuration = configuration;
        this.drawPage = drawPage;
        this.Size = new Vector2(980, 600);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Open()
    {
        this.IsOpen = true;
        this.BringToFront();
    }

    public override void Draw()
    {
        string before = JsonConvert.SerializeObject(this.configuration);
        ImGui.SetWindowFontScale(AutoFatreWindow.UiFontScale);
        float scale = ImGuiHelpers.GlobalScale;
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 4f * scale);
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(8f, 4f) * scale);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 6f) * scale);

        float sidebarWidth = Math.Clamp(ImGui.GetFontSize() * 15f, 220f * scale, 380f * scale);
        using (var sidebar = ImRaii.Child("##SettingsNavigation", new Vector2(sidebarWidth, 0), true))
        {
            if (sidebar)
            {
                ImGui.TextColored(HeaderColor, "设置");
                ImGui.Spacing();
                foreach (AutoFatreSettingsPage page in Enum.GetValues<AutoFatreSettingsPage>())
                {
                    if (ImGui.Selectable($"{GetPageLabel(page)}###SettingsPage-{page}", this.selectedPage == page))
                        this.selectedPage = page;
                }
            }
        }

        ImGui.SameLine();
        // Each category retains its own scroll position.
        using (var content = ImRaii.Child($"##SettingsContent-{this.selectedPage}", Vector2.Zero, true))
        {
            if (content)
            {
                ImGui.TextColored(HeaderColor, GetPageLabel(this.selectedPage));
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                this.drawPage(this.selectedPage);
            }
        }

        if (before != JsonConvert.SerializeObject(this.configuration))
        {
            this.configuration.Normalize();
            this.pluginInterface.SavePluginConfig(this.configuration);
        }
    }

    private static string GetPageLabel(AutoFatreSettingsPage page) => page switch
    {
        AutoFatreSettingsPage.Interface => "界面",
        AutoFatreSettingsPage.Companion => "伙伴",
        AutoFatreSettingsPage.Movement => "移动与导航",
        AutoFatreSettingsPage.Combat => "战斗",
        AutoFatreSettingsPage.PriorityTargets => "迷失目标",
        AutoFatreSettingsPage.DeathRecovery => "死亡恢复",
        AutoFatreSettingsPage.Blacklist => "FATE 黑名单",
        AutoFatreSettingsPage.Sound => "音效提醒",
        AutoFatreSettingsPage.Diagnostics => "诊断",
        _ => page.ToString(),
    };

    public void Dispose() => this.IsOpen = false;
}
