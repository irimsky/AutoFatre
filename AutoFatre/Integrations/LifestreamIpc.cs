using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace AutoFatre;

/// <summary>
/// Typed adapter for the IPC contract exported by NightmareXIV/Lifestream.
/// The signatures are sourced from Lifestream/IPC/IPCProvider.cs.
/// </summary>
public sealed class LifestreamIpc
{
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<uint, byte, bool> teleport;
    private readonly ICallGateSubscriber<uint, bool> aethernetTeleportByPlaceNameId;
    private readonly ICallGateSubscriber<object> abort;
    private readonly ICallGateSubscriber<bool> canChangeInstance;
    private readonly ICallGateSubscriber<int> numberOfInstances;
    private readonly ICallGateSubscriber<int, object> changeInstance;

    public LifestreamIpc(IDalamudPluginInterface pluginInterface)
    {
        this.isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        this.teleport = pluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        this.aethernetTeleportByPlaceNameId = pluginInterface.GetIpcSubscriber<uint, bool>(
            "Lifestream.AethernetTeleportByPlaceNameId");
        this.abort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
        // Verified against installed Lifestream 2.5.4.17 IPCProvider.
        this.canChangeInstance = pluginInterface.GetIpcSubscriber<bool>("Lifestream.CanChangeInstance");
        this.numberOfInstances = pluginInterface.GetIpcSubscriber<int>("Lifestream.GetNumberOfInstances");
        this.changeInstance = pluginInterface.GetIpcSubscriber<int, object>("Lifestream.ChangeInstance");
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return this.isBusy.HasFunction && this.teleport.HasFunction;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsAethernetAvailable
    {
        get
        {
            try
            {
                return this.aethernetTeleportByPlaceNameId.HasFunction;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            try
            {
                return this.isBusy.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    public bool Teleport(uint aetheryteId, byte subIndex = 0)
    {
        try
        {
            return this.teleport.InvokeFunc(aetheryteId, subIndex);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Requests an intra-city aethernet hop using the destination PlaceName row id.
    /// This is the official Lifestream IPC used by Questionable and BOCCHI.
    /// </summary>
    public bool AethernetTeleportByPlaceNameId(uint placeNameId)
    {
        try
        {
            return this.aethernetTeleportByPlaceNameId.InvokeFunc(placeNameId);
        }
        catch
        {
            return false;
        }
    }

    public void Abort()
    {
        try
        {
            this.abort.InvokeAction();
        }
        catch
        {
            // Lifestream may have been unloaded while AutoFatre is stopping.
        }
    }

    public bool TryRestoreInstance(uint instance)
    {
        try
        {
            if (instance is < 1 or > 9 || !this.canChangeInstance.HasFunction || !this.numberOfInstances.HasFunction
                || !this.changeInstance.HasAction || !this.canChangeInstance.InvokeFunc()
                || this.numberOfInstances.InvokeFunc() < instance) return false;
            this.changeInstance.InvokeAction((int)instance);
            return true;
        }
        catch { return false; }
    }
}
