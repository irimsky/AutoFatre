using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AutoFatre;

public sealed unsafe class DeathRecoveryAdapter(IGameGui gameGui)
{
    public bool TryAcceptRaise()
    {
        AddonSelectYesno* yesno = GetVisibleYesno();
        if (yesno == null)
            return false;

        string prompt = GetPrompt(yesno);
        if (!LooksLikeRaiseOffer(prompt))
            return false;

        yesno->AtkUnitBase.FireCallbackInt(0);
        return true;
    }

    public bool TryConfirmReturn()
    {
        AddonSelectYesno* yesno = GetVisibleYesno();
        if (yesno == null)
            return false;

        string prompt = GetPrompt(yesno);
        if (!LooksLikeDeathReturn(prompt))
            return false;

        yesno->AtkUnitBase.FireCallbackInt(0);
        return true;
    }

    private AddonSelectYesno* GetVisibleYesno()
    {
        AddonSelectYesno* yesno = gameGui.GetAddonByName<AddonSelectYesno>("SelectYesno");
        return yesno != null && yesno->AtkUnitBase.IsVisible && yesno->PromptText != null
            ? yesno
            : null;
    }

    private static string GetPrompt(AddonSelectYesno* yesno) =>
        yesno->PromptText->NodeText.ToString().Trim();

    private static bool LooksLikeRaiseOffer(string prompt)
    {
        string normalized = Normalize(prompt);
        return (normalized.Contains("接受", StringComparison.Ordinal)
                && (normalized.Contains("复活", StringComparison.Ordinal)
                    || normalized.Contains("苏生", StringComparison.Ordinal)
                    || normalized.Contains("蘇生", StringComparison.Ordinal)))
            || (normalized.Contains("accept", StringComparison.OrdinalIgnoreCase)
                && (normalized.Contains("raise", StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains("resurrect", StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains("reviv", StringComparison.OrdinalIgnoreCase)))
            || (normalized.Contains("蘇生", StringComparison.Ordinal)
                && (normalized.Contains("受け", StringComparison.Ordinal)
                    || normalized.Contains("承諾", StringComparison.Ordinal)))
            || (normalized.Contains("accepter", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("résurrection", StringComparison.OrdinalIgnoreCase))
            || (normalized.Contains("wiederbeleb", StringComparison.OrdinalIgnoreCase)
                && normalized.Contains("annehmen", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeDeathReturn(string prompt)
    {
        string normalized = Normalize(prompt);
        return (normalized.Contains("return", StringComparison.OrdinalIgnoreCase)
                && (normalized.Contains("starting point", StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains("home point", StringComparison.OrdinalIgnoreCase)))
            || (normalized.Contains("返回", StringComparison.Ordinal)
                && (normalized.Contains("开始地点", StringComparison.Ordinal)
                    || normalized.Contains("起始地点", StringComparison.Ordinal)
                    || normalized.Contains("出发地点", StringComparison.Ordinal)
                    || normalized.Contains("出發地點", StringComparison.Ordinal)
                    || normalized.Contains("复活点", StringComparison.Ordinal)
                    || normalized.Contains("返回点", StringComparison.Ordinal)))
            || normalized.Contains("開始地点に戻", StringComparison.Ordinal)
            || normalized.Contains("point de départ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Ausgangspunkt", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string prompt) =>
        string.Join(' ', prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
