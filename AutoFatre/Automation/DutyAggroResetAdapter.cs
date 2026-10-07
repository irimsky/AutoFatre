using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace AutoFatre;

/// <summary>
/// Owns the emergency solo-duty operation used to clear a stuck combat state. All entry and
/// confirmation calls are restricted to The Bowl of Embers and queues issued by this adapter.
/// </summary>
public sealed unsafe class DutyAggroResetAdapter : IDisposable
{
    public const uint IfritContentFinderConditionId = 56;
    private const uint FinishTerritoryTransportCommand = 816;
    private const uint LeaveDutyCommand = 819;

    private readonly IGameGui gameGui;
    private readonly ContentFinderCondition? ifritDuty;
    private bool queueIssuedByUs;
    private bool finishTerritoryTransportIssued;
    private bool disposed;

    public DutyAggroResetAdapter(
        IDataManager dataManager,
        IGameGui gameGui,
        IGameInteropProvider _)
    {
        this.gameGui = gameGui;
        this.ifritDuty = dataManager.GetExcelSheet<ContentFinderCondition>()
            .GetRowOrDefault(IfritContentFinderConditionId);
    }

    public bool QueueIssuedByUs => this.queueIssuedByUs;
    public bool LastFinishTerritoryTransportResult { get; private set; }
    public bool LastLeaveDutyResult { get; private set; }

    public ContentsFinderQueueState QueueState
    {
        get
        {
            ContentsFinder* finder = ContentsFinder.Instance();
            ContentsFinderQueueInfo* queue = finder == null ? null : finder->GetQueueInfo();
            return queue == null ? ContentsFinderQueueState.None : queue->QueueState;
        }
    }

    public bool IsInIfritDuty
    {
        get
        {
            GameMain* gameMain = GameMain.Instance();
            return gameMain != null
                && gameMain->CurrentContentFinderConditionId == IfritContentFinderConditionId;
        }
    }

    public bool Validate(out string reason)
    {
        if (this.ifritDuty is not { } duty || duty.RowId != IfritContentFinderConditionId)
        {
            reason = "无法读取伊弗利特歼灭战的 ContentFinderCondition 56";
            return false;
        }

        if (!duty.AllowUndersized)
        {
            reason = "当前客户端数据不允许伊弗利特歼灭战使用解除限制";
            return false;
        }

        if (UIState.Instance() == null
            || duty.Content.RowId == 0
            || !UIState.IsInstanceContentUnlocked(duty.Content.RowId))
        {
            reason = "伊弗利特歼灭战尚未解锁，无法用于重置仇恨";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool TryQueueUnrestricted(out string reason)
    {
        if (!this.Validate(out reason))
            return false;

        ContentsFinder* finder = ContentsFinder.Instance();
        ContentsFinderQueueInfo* queue = finder == null ? null : finder->GetQueueInfo();
        if (finder == null || queue == null)
        {
            reason = "ContentsFinder 尚未就绪";
            return false;
        }

        if (!this.queueIssuedByUs && queue->QueueState != ContentsFinderQueueState.None)
        {
            reason = $"检测到非 AutoFatre 的副本队列（{queue->QueueState}），拒绝接管";
            return false;
        }

        finder->IsUnrestrictedParty = true;
        finder->IsMinimalIL = false;
        finder->IsSilenceEcho = false;
        finder->IsExplorerMode = false;
        finder->IsLevelSync = false;

        uint dutyId = IfritContentFinderConditionId;
        queue->QueueDuties(&dutyId, 1);
        this.queueIssuedByUs = true;
        reason = "已以解除限制请求进入伊弗利特歼灭战";
        return true;
    }

    public bool TryCommence()
    {
        if (!this.queueIssuedByUs)
            return false;

        AddonContentsFinderConfirm* confirm = this.gameGui
            .GetAddonByName<AddonContentsFinderConfirm>("ContentsFinderConfirm");
        if (confirm == null || !confirm->AtkUnitBase.IsVisible || !confirm->AtkUnitBase.IsReady)
            return false;

        AtkComponentButton* button = confirm->CommenceButton;
        if (button == null || !button->IsEnabled || button->AtkResNode == null || !button->AtkResNode->IsVisible())
            return false;

        AtkComponentNode* owner = button->AtkComponentBase.OwnerNode;
        if (owner == null)
            return false;

        AtkEvent* clickEvent = owner->AtkResNode.AtkEventManager.Event;
        if (clickEvent == null)
            return false;

        confirm->AtkUnitBase.ReceiveEvent(
            clickEvent->State.EventType,
            (int)clickEvent->Param,
            clickEvent);
        return true;
    }

    public bool TryLeaveIfrit()
    {
        if (GameMain.Instance() == null)
            return false;

        // Questionable's GameCommand table documents 816 as TerritoryTransportFinish and 819
        // as LeaveDuty. DailyRoutines uses this exact ordered pair. Invoke the generated native
        // entry point directly; routing through a local ExecuteCommand hook can return success
        // while the original client command is not actually dispatched after duty entry.
        if (!this.finishTerritoryTransportIssued)
        {
            this.LastFinishTerritoryTransportResult = GameMain.ExecuteCommand((int)FinishTerritoryTransportCommand);
            this.finishTerritoryTransportIssued = true;
            this.LastLeaveDutyResult = false;
            return false;
        }

        this.LastLeaveDutyResult = GameMain.ExecuteCommand((int)LeaveDutyCommand);
        return this.LastLeaveDutyResult;
    }

    public void MarkEnteredDuty()
    {
        this.queueIssuedByUs = false;
        this.finishTerritoryTransportIssued = false;
        this.LastFinishTerritoryTransportResult = false;
        this.LastLeaveDutyResult = false;
    }

    public void CancelOwnedQueue()
    {
        if (!this.queueIssuedByUs)
            return;

        ContentsFinder* finder = ContentsFinder.Instance();
        ContentsFinderQueueInfo* queue = finder == null ? null : finder->GetQueueInfo();
        if (queue != null && queue->QueueState != ContentsFinderQueueState.None)
            queue->CancelQueue();
        this.queueIssuedByUs = false;
    }

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;
    }
}
