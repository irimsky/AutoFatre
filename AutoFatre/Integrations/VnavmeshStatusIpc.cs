using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace AutoFatre;

/// <summary>Separates a registered navigation provider from a territory mesh still loading.</summary>
public sealed class VnavmeshStatusIpc(IDalamudPluginInterface pluginInterface)
{
    private readonly ICallGateSubscriber<bool> ready = pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
    private readonly ICallGateSubscriber<float> progress = pluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress");

    public bool TryRead(out bool meshReady, out float? buildProgress)
    {
        meshReady = false;
        buildProgress = null;
        try
        {
            // Readiness is deliberately independent from movement IPCs. During a vnavmesh
            // reload the provider can expose Nav.IsReady while its movement gates are still
            // coming back; that is a transient provider state, not a mesh failure.
            if (!this.ready.HasFunction)
                return false;
            meshReady = this.ready.InvokeFunc();
        }
        catch { return false; }
        // Progress is optional; its absence must not block a working navigation provider.
        try
        {
            if (this.progress.HasFunction)
            {
                float value = this.progress.InvokeFunc();
                if (float.IsFinite(value) && value >= 0)
                    buildProgress = Math.Clamp(value, 0, 1);
            }
        }
        catch { }
        return true;
    }
}
