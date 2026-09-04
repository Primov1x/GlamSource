using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GlamSource.Core;

namespace GlamSource.Services;

// "hat man das mount oder minion schon unlocked, überall hinzufügen" — native unlock check via
// PlayerState/UIState (verified live against real Item/ItemAction sheet data + FFXIVClientStructs
// source, same APIs Dalamud's own IUnlockState and several community plugins use). Lives in
// Services/ (not GlamSource.Core) because it needs clib/unsafe access Core doesn't have.
public static class UnlockCheckService
{
    /// null = itemId isn't a mount/minion unlock item (nothing to show); true/false = unlock status.
    public static unsafe bool? CheckUnlocked(IItemDetailService detail, uint itemId)
    {
        var mountRowId = detail.MountRowIdForItem(itemId);
        if (mountRowId.HasValue)
        {
            var ps = PlayerState.Instance();
            return ps != null && ps->IsMountUnlocked(mountRowId.Value);
        }
        var companionRowId = detail.CompanionRowIdForItem(itemId);
        if (companionRowId.HasValue)
        {
            var ui = UIState.Instance();
            return ui != null && ui->IsCompanionUnlocked(companionRowId.Value);
        }
        var orchestrionRowId = detail.OrchestrionRowIdForItem(itemId);
        if (orchestrionRowId.HasValue)
        {
            var ps = PlayerState.Instance();
            return ps != null && ps->IsOrchestrionRollUnlocked(orchestrionRowId.Value);
        }
        // covers emotes and hairstyles (and a few other UnlockLink-gated unlocks) alike — same
        // Item.ItemAction field, same native check, see UnlockLinkIdForItem's doc comment.
        var unlockLinkId = detail.UnlockLinkIdForItem(itemId);
        if (unlockLinkId.HasValue)
        {
            var ui = UIState.Instance();
            return ui != null && ui->IsUnlockLinkUnlocked(unlockLinkId.Value);
        }
        var triadCardRowId = detail.TripleTriadCardRowIdForItem(itemId);
        if (triadCardRowId.HasValue)
        {
            var ui = UIState.Instance();
            return ui != null && ui->IsTripleTriadCardUnlocked((ushort)triadCardRowId.Value);
        }
        return null;
    }
}
