using FFXIVClientStructs.FFXIV.Client.UI;

namespace AutoFatre;

/// <summary>Plays one of the game's built-in fixed chat sound effects.</summary>
public sealed unsafe class SoundAlertAdapter
{
    public void Play(uint soundEffectId)
    {
        if (soundEffectId is < 1 or > 16)
            return;

        UIGlobals.PlayChatSoundEffect(soundEffectId);
    }
}
