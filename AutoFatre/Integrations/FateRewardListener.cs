using System.Collections.Concurrent;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using NativeFateManager = FFXIVClientStructs.FFXIV.Client.Game.Fate.FateManager;

namespace AutoFatre;

public sealed record FateRewardCaptureContext(long Session, int Scope, bool AutomationActive, uint Territory, uint Instance);

public readonly record struct FateRewardObservation(
    long Session, int Scope, bool AutomationActive, uint Territory, uint Instance,
    DateTime ReceivedAt, ushort FateId, byte Flags, byte Medal)
{
    public bool Success => (this.Flags & 1) != 0;
    public string MedalDescription => this.Medal switch
    {
        0 => "Gold(金牌)",
        1 => "Silver(银牌)",
        2 => "Bronze(铜牌)",
        3 => "CriticalEngagement",
        _ => $"Unknown({this.Medal})",
    };
}

/// <summary>Only copies packet values in the detour. Controller consumes the queue on Framework.Update.</summary>
public sealed unsafe class FateRewardListener : IDisposable
{
    private delegate void HandleRewardDelegate(NativeFateManager* manager, FateRewardPacket* packet);
    private readonly ConcurrentQueue<FateRewardObservation> observations = new();
    private Hook<HandleRewardDelegate>? hook;
    private FateRewardCaptureContext context = new(0, 0, false, 0, 0);
    private volatile bool disposed;
    private int copyErrors;

    public FateRewardListener(IGameInteropProvider interop)
    {
        try
        {
            nint address = (nint)NativeFateManager.MemberFunctionPointers.HandleFateRewardPacket;
            if (address == 0)
                throw new InvalidOperationException("HandleFateRewardPacket 签名未解析");
            this.hook = interop.HookFromAddress<HandleRewardDelegate>(address, this.Detour);
            this.hook.Enable();
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
            this.hook?.Dispose();
            this.hook = null;
        }
    }

    public bool IsAvailable => this.hook is not null && !this.disposed;
    public string? Error { get; }
    public void SetContext(FateRewardCaptureContext value) => Volatile.Write(ref this.context, value);
    public bool TryDequeue(out FateRewardObservation reward) => this.observations.TryDequeue(out reward);
    public int TakeCopyErrors() => Interlocked.Exchange(ref this.copyErrors, 0);

    public void Dispose()
    {
        if (this.disposed)
            return;
        this.disposed = true;
        this.hook?.Disable();
        this.hook?.Dispose();
        this.observations.Clear();
    }

    private void Detour(NativeFateManager* manager, FateRewardPacket* packet)
    {
        // Keep the original callable locally even if unload starts while this callback is running.
        Hook<HandleRewardDelegate> original = this.hook!;
        try
        {
            FateRewardCaptureContext capture = Volatile.Read(ref this.context);
            if (!this.disposed && capture.Session != 0 && packet is not null)
                this.observations.Enqueue(new(capture.Session, capture.Scope, capture.AutomationActive,
                    capture.Territory, capture.Instance, DateTime.UtcNow, packet->FateId,
                    (byte)packet->Flags, (byte)packet->Medal));
        }
        catch (Exception)
        {
            Interlocked.Increment(ref this.copyErrors);
        }
        finally
        {
            original.OriginalDisposeSafe(manager, packet);
        }
    }
}
