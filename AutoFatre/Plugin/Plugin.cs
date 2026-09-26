using FieldNavigation;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AutoFatre;

public sealed class Plugin : IAsyncDalamudPlugin
{
    private readonly AutoFatreConfiguration configuration;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IPluginLog log;
    private readonly StaticFateCatalog staticFateCatalog;
    private readonly StaticFateTerritoryCatalog staticFateTerritoryCatalog;
    private readonly GameDataSelectionCatalog selectionCatalog;
    private readonly FateAutomationController controller;
    private readonly AutoFatreWindow window;
    private readonly WindowSystem windowSystem = new("AutoFatre");
    private readonly AutoFatreOverlayWindow overlayWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IChatGui chat,
        ICommandManager commandManager,
        IFramework framework,
        IClientState clientState,
        IPlayerState playerState,
        IFateTable fateTable,
        IObjectTable objectTable,
        ITargetManager targetManager,
        ICondition condition,
        IPartyList partyList,
        IAetheryteList aetheryteList,
        IGameInventory gameInventory,
        IBuddyList buddyList,
        IDataManager dataManager,
        IAddonLifecycle addonLifecycle,
        IGameGui gameGui,
        IGameInteropProvider gameInteropProvider)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.commandManager = commandManager;
        this.configuration = pluginInterface.GetPluginConfig() as AutoFatreConfiguration ?? new AutoFatreConfiguration();
        this.configuration.Migrate();
        this.configuration.Normalize();
        // Enabled is runtime state. Never resume automation solely because the previous
        // plugin instance happened to be running when its configuration was written.
        this.configuration.Enabled = false;
        this.staticFateCatalog = new StaticFateCatalog();
        this.staticFateTerritoryCatalog = new StaticFateTerritoryCatalog();
        this.selectionCatalog = new GameDataSelectionCatalog(
            dataManager,
            aetheryteList,
            this.staticFateCatalog,
            this.staticFateTerritoryCatalog);
        var vnavmesh = new VnavmeshIpc(pluginInterface);
        var lifestream = new LifestreamIpc(pluginInterface);
        var fateRepository = new FateRepository(fateTable, clientState);
        var aetheryteTravelPlanner = new AetheryteTravelPlanner(dataManager, aetheryteList);
        var targetSelector = new FateTargetSelector(objectTable, dataManager);
        var inventoryCounter = new InventoryCounter(gameInventory);
        var companion = new CompanionAdapter(buddyList, inventoryCounter, dataManager, objectTable);
        var mount = new MountAdapter(condition, objectTable);
        var landing = new LandingAdapter(gameInteropProvider);
        var dutyAggroReset = new DutyAggroResetAdapter(dataManager, gameGui, gameInteropProvider);
        var soundAlerts = new SoundAlertAdapter();
        var textAdvance = new TextAdvanceIpc(pluginInterface);
        this.controller = new FateAutomationController(
            this.configuration,
            log,
            chat,
            framework,
            clientState,
            playerState,
            objectTable,
            targetManager,
            condition,
            partyList,
            aetheryteList,
            aetheryteTravelPlanner,
            dataManager,
            vnavmesh,
            lifestream,
            fateRepository,
            this.staticFateCatalog,
            targetSelector,
            inventoryCounter,
            companion,
            () => pluginInterface.SavePluginConfig(this.configuration),
            mount,
            landing,
            dutyAggroReset,
            soundAlerts,
            textAdvance,
            addonLifecycle,
            gameGui);
        this.window = new AutoFatreWindow(
            pluginInterface,
            this.configuration,
            this.controller,
            this.selectionCatalog,
            inventoryCounter,
            this.SetOverlayWindowVisibility);
        this.overlayWindow = new AutoFatreOverlayWindow(pluginInterface, this.configuration, this.window);
        this.windowSystem.AddWindow(this.overlayWindow);
        this.pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;

        this.commandManager.AddHandler("/autofatre", this.CreateCommandInfo());
        this.commandManager.AddHandler("/af", this.CreateCommandInfo());
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(
                this.staticFateCatalog.LoadAsync(cancellationToken),
                this.staticFateTerritoryCatalog.LoadAsync(cancellationToken))
            .ConfigureAwait(false);
        this.selectionCatalog.Load();
        this.log.Information(
            "伙伴物品索引已载入：CompanionItems={CompanionItemCount}（ItemAction.Action=853），" +
            "基萨尔野菜 ItemId={GysahlGreensItemId}",
            this.selectionCatalog.CompanionItems.Count,
            CompanionAdapter.GysahlGreensItemId);
        this.NormalizeConfiguredTerritories();
        if (this.staticFateCatalog.IsAvailable)
        {
            this.log.Information(
                "已载入内置 FATE 补充目录：{FateCount} 条 FATE，{CollectionCount} 个合集；客户端 Fate 表仍为主数据源",
                this.staticFateCatalog.FateCount,
                this.staticFateCatalog.CollectionCount);
        }
        else
        {
            this.log.Warning(
                "内置 FATE 补充目录加载失败，将继续使用客户端数据和默认逻辑：{Error}",
                this.staticFateCatalog.LoadError ?? "未知加载错误");
        }
        if (this.staticFateTerritoryCatalog.IsAvailable)
        {
            this.log.Information(
                "已载入客户端 LGB FATE 地图目录：{FateCount} 条，游戏版本 {GameVersion}，快照 {SnapshotId}",
                this.staticFateTerritoryCatalog.FateCount,
                this.staticFateTerritoryCatalog.GameVersion ?? "未知",
                this.staticFateTerritoryCatalog.SnapshotId ?? "未知");
        }
        else
        {
            this.log.Warning(
                "客户端 LGB FATE 地图目录加载失败；仅可使用内置补充地图数据：{Error}",
                this.staticFateTerritoryCatalog.LoadError ?? "未知加载错误");
        }
        if (this.selectionCatalog.LoadError is { } selectionError)
            this.log.Error("游戏数据名称索引加载失败：{Error}", selectionError);
    }

    private void NormalizeConfiguredTerritories()
    {
        int migrated = 0;
        ushort? firstTargetFate = this.configuration.TargetFateIds.FirstOrDefault() is { } configuredTarget
            && configuredTarget != 0
            ? configuredTarget
            : null;
        if (this.configuration.Mode == AutomationMode.TargetFate
            && firstTargetFate is { } targetFate
            && this.selectionCatalog.TryGetTerritoryIdForFate(targetFate, out uint targetTerritory)
            && targetTerritory != this.configuration.SingleMapTerritoryId)
        {
            this.configuration.SingleMapTerritoryId = targetTerritory;
            this.configuration.SingleMapAetheryteId = 0;
            migrated++;
        }

        if (this.configuration.Mode == AutomationMode.TargetFate
            && firstTargetFate is { } firstTarget
            && this.selectionCatalog.TryGetTerritoryIdForFate(firstTarget, out uint firstTerritory))
        {
            List<ushort> sameMapTargets = this.configuration.TargetFateIds
                .Where(id => id == firstTarget
                    || this.selectionCatalog.TryGetTerritoryIdForFate(id, out uint territory)
                       && territory == firstTerritory)
                .Distinct()
                .ToList();
            if (!sameMapTargets.SequenceEqual(this.configuration.TargetFateIds))
            {
                this.configuration.TargetFateIds = sameMapTargets;
                this.configuration.TargetFateId = firstTarget;
                migrated++;
            }
        }

        uint singleMapTerritory = this.selectionCatalog.CanonicalizeTerritoryId(this.configuration.SingleMapTerritoryId);
        if (singleMapTerritory != this.configuration.SingleMapTerritoryId)
        {
            this.configuration.SingleMapTerritoryId = singleMapTerritory;
            this.configuration.SingleMapAetheryteId = 0;
            migrated++;
        }

        foreach (MapPreset map in this.configuration.PresetSequences.SelectMany(sequence => sequence.Maps))
        {
            uint canonicalTerritory = this.selectionCatalog.CanonicalizeTerritoryId(map.TerritoryId);
            if (canonicalTerritory == map.TerritoryId)
                continue;

            map.TerritoryId = canonicalTerritory;
            map.AetheryteId = 0;
            migrated++;
        }

        if (migrated == 0)
            return;

        this.pluginInterface.SavePluginConfig(this.configuration);
        this.log.Information("已将 {Count} 个任务实例地图配置迁移到同名的开放世界地图", migrated);
    }

    public ValueTask DisposeAsync()
    {
        this.commandManager.RemoveHandler("/autofatre");
        this.commandManager.RemoveHandler("/af");
        this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        this.windowSystem.RemoveAllWindows();
        this.window.Dispose();
        this.controller.Dispose();
        this.pluginInterface.SavePluginConfig(this.configuration);
        return ValueTask.CompletedTask;
    }

    private void SetOverlayWindowVisibility(bool visible)
    {
        this.overlayWindow.IsOpen = visible;
    }

    private void OnCommand(string _, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case string command when string.IsNullOrWhiteSpace(command):
                this.window.Open();
                break;
            case "start":
                this.controller.Start();
                break;
            case "stop":
                this.controller.Stop();
                break;
            case "pause":
                this.controller.Pause();
                break;
            case "retry":
                this.controller.Retry();
                break;
            case "status":
                this.log.Information("AutoFatre state: {State}; {Reason}", this.controller.State, this.controller.StatusReason);
                break;
            case "scan":
                this.controller.RequestLogScan();
                break;
            case "ui":
            case "config":
                this.window.Open();
                break;
            default:
                this.log.Warning("未知 AutoFatre 命令：{Command}；可用命令：start、stop、pause、retry、status、scan、ui", args.Trim());
                break;
        }
    }

    private CommandInfo CreateCommandInfo() => new(this.OnCommand)
    {
        HelpMessage = "打开 AutoFatre UI；start|stop|pause|retry|status|scan|ui",
        ShowInHelp = true,
    };
}
