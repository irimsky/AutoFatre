using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace AutoFatre;

public sealed class AutoFatreOverlayWindow : Window
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly AutoFatreConfiguration configuration;
    private readonly AutoFatreWindow mainWindow;

    public AutoFatreOverlayWindow(
        IDalamudPluginInterface pluginInterface,
        AutoFatreConfiguration configuration,
        AutoFatreWindow mainWindow,
        Action openSettings)
        : base("AutoFatre - 悬浮窗")
    {
        this.pluginInterface = pluginInterface;
        this.configuration = configuration;
        this.mainWindow = mainWindow;

        this.Size = new Vector2(380, 520);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(300, 220),
            MaximumSize = new Vector2(700, 1000),
        };
        this.ShowCloseButton = true;
        this.RespectCloseHotkey = false;
        this.IsOpen = configuration.ShowOverlayWindow;

        this.TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            IconOffset = new Vector2(1, 1),
            Priority = -100,
            Click = _ => openSettings(),
            ShowTooltip = () => ImGui.SetTooltip("打开 AutoFatre 设置"),
        });
        this.TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Home,
            IconOffset = new Vector2(1, 1),
            Priority = -100,
            Click = _ => this.mainWindow.Open(),
            ShowTooltip = () => ImGui.SetTooltip("打开主窗口"),
        });
        this.TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.ClipboardList,
            IconOffset = new Vector2(1, 1),
            Priority = -100,
            Click = _ => this.mainWindow.OpenDiagnosticLog(),
            ShowTooltip = () => ImGui.SetTooltip("打开诊断日志"),
        });
    }

    public override void Draw()
    {
        if (!this.configuration.ShowOverlayWindow)
        {
            this.IsOpen = false;
            return;
        }

        this.mainWindow.DrawOverlayContents();
    }

    public override void OnClose()
    {
        this.configuration.ShowOverlayWindow = false;
        this.pluginInterface.SavePluginConfig(this.configuration);
    }
}
