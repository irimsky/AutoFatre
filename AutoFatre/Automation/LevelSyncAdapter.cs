using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;

namespace AutoFatre;

/// <summary>
/// Drives only the FATE progress addon. The receive-event listener records the real manual-click
/// parameters in diagnostics so callback compatibility can be checked after game updates.
/// </summary>
public sealed unsafe class LevelSyncAdapter : IDisposable
{
    private const string AddonName = "_FateProgress";
    private readonly IAddonLifecycle addonLifecycle;
    private readonly Action<string> diagnostic;
    private bool disposed;

    public LevelSyncAdapter(IAddonLifecycle addonLifecycle, Action<string> diagnostic)
    {
        this.addonLifecycle = addonLifecycle;
        this.diagnostic = diagnostic;
        this.addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, AddonName, this.OnReceiveEvent);
    }

    public bool TrySync(ushort expectedFateId)
    {
        FateManager* manager = FateManager.Instance();
        if (manager == null || manager->CurrentFate == null)
        {
            this.diagnostic("FateManager 或 CurrentFate 尚未就绪");
            return false;
        }

        ushort currentFateId = manager->GetCurrentFateId();
        if (currentFateId != expectedFateId)
        {
            this.diagnostic($"FateManager 当前 FATE={currentFateId}，等待目标 FATE={expectedFateId}");
            return false;
        }

        FateContext* result = manager->LevelSync();
        this.diagnostic($"已调用 FateManager.LevelSync，result=0x{(nint)result:X}");
        return result != null;
    }

    public bool IsSyncedTo(ushort expectedFateId)
    {
        FateManager* manager = FateManager.Instance();
        return manager != null
            && manager->CurrentFate != null
            && manager->GetCurrentFateId() == expectedFateId
            && manager->IsSyncedToFate(manager->CurrentFate);
    }

    /// <summary>
    /// Sends the game's native FATE level-sync command with its documented "do not sync"
    /// parameter. Questionable's GameCommand table identifies command 813 as FateLevelSync,
    /// with param1=FATE id and param2=0 for cancellation. Writing FateManager fields directly
    /// does not run the client's state transition and is intentionally not used here.
    /// </summary>
    public bool TryCancelSync(ushort expectedFateId)
    {
        FateManager* manager = FateManager.Instance();
        if (manager == null)
        {
            this.diagnostic("FateManager 尚未就绪，无法取消等级同步");
            return false;
        }

        if (manager->CurrentFate == null)
        {
            this.diagnostic("FateManager 当前没有活动 FATE，无法取消等级同步");
            return false;
        }

        ushort currentFateId = manager->GetCurrentFateId();
        if (currentFateId != expectedFateId)
        {
            this.diagnostic($"拒绝取消其他 FATE 的等级同步：当前 FATE={currentFateId}，目标={expectedFateId}");
            return false;
        }

        const int fateLevelSyncCommand = 813;
        bool sent = GameMain.ExecuteCommand(fateLevelSyncCommand, expectedFateId, 0);
        this.diagnostic(
            $"已调用原生 FateLevelSync 取消命令：command={fateLevelSyncCommand}, fateId={expectedFateId}, param2=0, result={sent}");
        return sent;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.addonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, AddonName, this.OnReceiveEvent);
    }

    private void OnReceiveEvent(AddonEvent _, AddonArgs args)
    {
        if (args is AddonReceiveEventArgs receive)
        {
            this.diagnostic(
                $"_FateProgress ReceiveEvent type={receive.AtkEventType} param={receive.EventParam}");
        }
    }
}
