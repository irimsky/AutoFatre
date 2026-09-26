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

    public LifestreamIpc(IDalamudPluginInterface pluginInterface)
    {
        this.isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        this.teleport = pluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        this.aethernetTeleportByPlaceNameId = pluginInterface.GetIpcSubscriber<uint, bool>(
            "Lifestream.AethernetTeleportByPlaceNameId");
        this.abort = pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort");
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
}
