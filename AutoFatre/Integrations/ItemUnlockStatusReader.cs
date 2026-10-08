using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AutoFatre;

public enum ItemUseStatus { Unsupported, WaitingForCharacter, NotUsed, Used }

public static unsafe class ItemUnlockStatusReader
{
    public static ItemUseStatus Read(GemstoneProduct product, bool characterLoaded)
    {
        if (!product.SupportsUsedCheck || product.UnlockId == 0) return ItemUseStatus.Unsupported;
        if (!characterLoaded) return ItemUseStatus.WaitingForCharacter;
        PlayerState* player = PlayerState.Instance();
        UIState* ui = UIState.Instance();
        if (player is null || ui is null) return ItemUseStatus.WaitingForCharacter;
        bool used = product.ActionId switch
        {
            25183 => player->IsOrchestrionRollUnlocked(product.UnlockId),
            29459 => player->IsFramersKitUnlocked(product.UnlockId),
            853 => ui->IsCompanionUnlocked(product.UnlockId),
            1322 => player->IsMountUnlocked(product.UnlockId),
            2633 => ui->IsUnlockLinkUnlocked(product.UnlockId),
            3357 => ui->IsTripleTriadCardUnlocked((ushort)product.UnlockId),
            20086 => player->IsOrnamentUnlocked(product.UnlockId),
            _ => false,
        };
        return used ? ItemUseStatus.Used : ItemUseStatus.NotUsed;
    }
}
