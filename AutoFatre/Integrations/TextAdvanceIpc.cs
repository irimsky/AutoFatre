using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace AutoFatre;

/// <summary>
/// Optional integration with TextAdvance. TextAdvance owns the Talk/SelectYesno/request
/// hand-in UI; AutoFatre still owns finding and interacting with the FATE event NPC.
/// </summary>
public sealed class TextAdvanceIpc(IDalamudPluginInterface pluginInterface) : IDisposable
{
    private readonly ICallGateSubscriber<bool> isInExternalControl =
        pluginInterface.GetIpcSubscriber<bool>("TextAdvance.IsInExternalControl");
    private readonly ICallGateSubscriber<string, TextAdvanceTerritoryConfig, bool> enableExternalControl =
        pluginInterface.GetIpcSubscriber<string, TextAdvanceTerritoryConfig, bool>(
            "TextAdvance.EnableExternalControl");
    private readonly ICallGateSubscriber<string, bool> disableExternalControl =
        pluginInterface.GetIpcSubscriber<string, bool>("TextAdvance.DisableExternalControl");
    private readonly string pluginName = pluginInterface.InternalName;
    private bool activated;

    public bool IsAvailable
    {
        get
        {
            try
            {
                return this.isInExternalControl.HasFunction
                    && this.enableExternalControl.HasFunction
                    && this.disableExternalControl.HasFunction;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsActive
    {
        get
        {
            try { return this.isInExternalControl.InvokeFunc(); }
            catch { return false; }
        }
    }

    /// <summary>Whether this plugin successfully owns the external-control lease.</summary>
    public bool OwnsControl => this.activated && this.IsActive;

    public bool EnableForCollection()
    {
        if (!this.IsAvailable)
            return false;

        try
        {
            bool accepted = this.enableExternalControl.InvokeFunc(
                this.pluginName,
                new TextAdvanceTerritoryConfig
                {
                    EnableQuestAccept = true,
                    EnableQuestComplete = true,
                    EnableRewardPick = true,
                    EnableRequestHandin = true,
                    EnableRequestFill = true,
                    EnableAutoInteract = false,
                });
            this.activated |= accepted;
            // IsInExternalControl is global.  Another plugin may own the lease, in
            // which case reporting true would make AutoFatre stop advancing its own UI.
            return accepted || this.OwnsControl;
        }
        catch
        {
            return false;
        }
    }

    public bool EnableForPreparingFate()
    {
        if (!this.IsAvailable)
            return false;

        try
        {
            bool accepted = this.enableExternalControl.InvokeFunc(
                this.pluginName,
                new TextAdvanceTerritoryConfig
                {
                    EnableQuestAccept = true,
                    EnableAutoInteract = false,
                });
            this.activated |= accepted;
            // Do not mistake another plugin's external-control lease for ours.  FATE
            // opener dialogs are special Talk pages and must fall back to native
            // callbacks when TextAdvance did not accept this request.
            return accepted || this.OwnsControl;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Temporarily suppress TextAdvance's global dialogue handling while our shop adapter owns it.</summary>
    public void EnableForExchange()
    {
        if (!this.IsAvailable) return;
        try
        {
            bool accepted = this.enableExternalControl.InvokeFunc(this.pluginName, new TextAdvanceTerritoryConfig
            {
                EnableQuestAccept = false, EnableQuestComplete = false, EnableRewardPick = false,
                EnableRequestHandin = false, EnableRequestFill = false, EnableAutoInteract = false,
                EnableTalkSkip = false, EnableCutsceneEsc = false, EnableCutsceneSkipConfirm = false,
            });
            this.activated |= accepted;
        }
        catch { /* Optional plugin may be absent or controlled by another caller. */ }
    }

    public void Disable()
    {
        if (!this.activated)
            return;

        try
        {
            if (this.disableExternalControl.InvokeFunc(this.pluginName)
                || !this.IsActive)
                this.activated = false;
        }
        catch
        {
            this.activated = false;
        }
    }

    public void Dispose() => this.Disable();

    // TextAdvance serializes public fields in the same shape as Questionable's IPC payload.
    private sealed class TextAdvanceTerritoryConfig
    {
        public bool? EnableQuestAccept;
        public bool? EnableQuestComplete;
        public bool? EnableRewardPick;
        public bool? EnableRequestHandin;
        public bool? EnableRequestFill;
        public bool? EnableAutoInteract;
        public bool? EnableTalkSkip;
        public bool? EnableCutsceneEsc;
        public bool? EnableCutsceneSkipConfirm;
    }
}
