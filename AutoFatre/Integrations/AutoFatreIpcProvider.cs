using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace AutoFatre;

/// <summary>
/// Public IPC surface for one-shot FATE objectives. The contract uses only primitive values so
/// callers do not need a reference to AutoFatre's assembly.
/// </summary>
public sealed class AutoFatreIpcProvider : IDisposable
{
    private readonly ICallGateProvider<ushort, int, bool> startSingleFate;
    private readonly ICallGateProvider<ushort, int, bool, bool> startSingleFateWithExchange;
    private readonly ICallGateProvider<object> stop;
    private readonly ICallGateProvider<string> getState;
    private readonly ICallGateProvider<string> getStatus;
    private readonly ICallGateProvider<bool> isRunning;
    private readonly ICallGateProvider<int> getCompletedCount;
    private readonly ICallGateProvider<int> getRequiredCount;
    private readonly ICallGateProvider<string> getResult;
    private readonly ICallGateProvider<uint, uint, int, string> startItemFarm;
    private readonly ICallGateProvider<uint, uint, int, bool, string> startItemFarmWithExchange;
    private readonly ICallGateProvider<ushort, int, string> startFateFarm;
    private readonly ICallGateProvider<ushort, int, bool, string> startFateFarmWithExchange;
    private readonly ICallGateProvider<string, string> getItemFarmResult;
    private readonly ICallGateProvider<string, bool> stopItemFarm;
    private readonly ICallGateProvider<string, bool> enableItemFarmCleanup;
    private readonly ICallGateProvider<string, bool> resumeItemFarmAfterCleanup;
    private readonly FateAutomationController controller;
    private readonly IFramework framework;
    private bool disposed;

    public AutoFatreIpcProvider(
        IDalamudPluginInterface pluginInterface,
        IFramework framework,
        FateAutomationController controller)
    {
        this.framework = framework;
        this.controller = controller;
        this.startSingleFate = pluginInterface.GetIpcProvider<ushort, int, bool>("AutoFatre.StartSingleFate");
        this.startSingleFateWithExchange = pluginInterface.GetIpcProvider<ushort, int, bool, bool>("AutoFatre.StartSingleFateWithExchange");
        this.stop = pluginInterface.GetIpcProvider<object>("AutoFatre.Stop");
        this.getState = pluginInterface.GetIpcProvider<string>("AutoFatre.GetState");
        this.getStatus = pluginInterface.GetIpcProvider<string>("AutoFatre.GetStatus");
        this.isRunning = pluginInterface.GetIpcProvider<bool>("AutoFatre.IsRunning");
        this.getCompletedCount = pluginInterface.GetIpcProvider<int>("AutoFatre.GetCompletedCount");
        this.getRequiredCount = pluginInterface.GetIpcProvider<int>("AutoFatre.GetRequiredCount");
        this.getResult = pluginInterface.GetIpcProvider<string>("AutoFatre.GetResult");
        this.startItemFarm = pluginInterface.GetIpcProvider<uint, uint, int, string>("AutoFatre.StartItemFarm");
        this.startItemFarmWithExchange = pluginInterface.GetIpcProvider<uint, uint, int, bool, string>("AutoFatre.StartItemFarmWithExchange");
        this.startFateFarm = pluginInterface.GetIpcProvider<ushort, int, string>("AutoFatre.StartFateFarm");
        this.startFateFarmWithExchange = pluginInterface.GetIpcProvider<ushort, int, bool, string>("AutoFatre.StartFateFarmWithExchange");
        this.startSingleFateWithExchange.RegisterFunc(controller.StartSingleFateWithExchange);
        this.startFateFarm.RegisterFunc(controller.StartFateFarm);
        this.startItemFarmWithExchange.RegisterFunc(controller.StartItemFarmWithExchange);
        this.startFateFarmWithExchange.RegisterFunc(controller.StartFateFarmWithExchange);
        this.getItemFarmResult = pluginInterface.GetIpcProvider<string, string>("AutoFatre.GetItemFarmResult");
        this.stopItemFarm = pluginInterface.GetIpcProvider<string, bool>("AutoFatre.StopItemFarm");
        this.enableItemFarmCleanup = pluginInterface.GetIpcProvider<string, bool>("AutoFatre.EnableItemFarmCleanup");
        this.resumeItemFarmAfterCleanup = pluginInterface.GetIpcProvider<string, bool>("AutoFatre.ResumeItemFarmAfterCleanup");
        this.enableItemFarmCleanup.RegisterFunc(controller.EnableItemFarmCleanup);
        this.resumeItemFarmAfterCleanup.RegisterFunc(controller.ResumeItemFarmAfterCleanup);
        this.startItemFarm.RegisterFunc(controller.StartItemFarm);
        this.getItemFarmResult.RegisterFunc(controller.GetItemFarmResult);
        this.stopItemFarm.RegisterFunc(controller.StopItemFarm);

        this.startSingleFate.RegisterFunc(controller.StartSingleFate);
        this.stop.RegisterAction(() =>
            _ = this.framework.RunOnFrameworkThread(() => controller.Stop("IPC 请求停止")));
        this.getState.RegisterFunc(() => controller.State.ToString());
        this.getStatus.RegisterFunc(() => controller.StatusReason);
        this.isRunning.RegisterFunc(() => controller.IsRunning);
        this.getCompletedCount.RegisterFunc(() => controller.SingleFateCompletedCount);
        this.getRequiredCount.RegisterFunc(() => controller.SingleFateRequiredCount);
        this.getResult.RegisterFunc(() => controller.SingleFateResult);
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
        this.startSingleFate.UnregisterFunc();
        this.startSingleFateWithExchange.UnregisterFunc();
        this.stop.UnregisterAction();
        this.getState.UnregisterFunc();
        this.getStatus.UnregisterFunc();
        this.isRunning.UnregisterFunc();
        this.getCompletedCount.UnregisterFunc();
        this.getRequiredCount.UnregisterFunc();
        this.getResult.UnregisterFunc();
        this.startItemFarm.UnregisterFunc();
        this.startItemFarmWithExchange.UnregisterFunc();
        this.startFateFarm.UnregisterFunc();
        this.startFateFarmWithExchange.UnregisterFunc();
        this.getItemFarmResult.UnregisterFunc();
        this.stopItemFarm.UnregisterFunc();
        this.enableItemFarmCleanup.UnregisterFunc();
        this.resumeItemFarmAfterCleanup.UnregisterFunc();
    }
}
