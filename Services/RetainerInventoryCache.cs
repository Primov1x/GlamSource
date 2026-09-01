using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace GlamSource.Services;

// ponytail: "man sieht welches mat wo liegt und wie viel" — the game only ever loads ONE
// retainer's inventory pages at a time (whichever you currently have open at the bell); there's
// no way to see all retainers' contents simultaneously. So we snapshot each retainer's inventory
// into this cache the moment it's open, keyed by retainer NAME, and keep every snapshot for the
// rest of the session — visit a retainer once, its contents stay browsable here until you close
// the game. Not persisted to disk; a fresh session starts empty until each retainer is opened once.
public static class RetainerInventoryCache
{
    // retainer name -> (itemId -> total quantity across all its pages)
    private static readonly ConcurrentDictionary<string, Dictionary<uint, int>> Snapshots = new();

    /// <summary>Call on the Framework thread, throttled — cheap no-op unless a retainer's
    /// inventory is actually loaded right now (i.e. the player has one open at the bell).</summary>
    public static unsafe void UpdateFromCurrentRetainer()
    {
        var mgr = RetainerManager.Instance();
        if (mgr == null) return;
        var active = mgr->GetActiveRetainer();
        if (active == null) return;
        var name = active->NameString;
        if (string.IsNullOrEmpty(name)) return;

        var im = InventoryManager.Instance();
        if (im == null) return;

        var snapshot = new Dictionary<uint, int>();
        void Scan(InventoryType type)
        {
            var container = im->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) return;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->Items[i];
                if (item.ItemId == 0) continue;
                snapshot[item.ItemId] = snapshot.GetValueOrDefault(item.ItemId) + (int)item.Quantity;
            }
        }
        Scan(InventoryType.RetainerPage1);
        Scan(InventoryType.RetainerPage2);
        Scan(InventoryType.RetainerPage3);
        Scan(InventoryType.RetainerPage4);
        Scan(InventoryType.RetainerPage5);
        Scan(InventoryType.RetainerPage6);
        Scan(InventoryType.RetainerPage7);
        Scan(InventoryType.RetainerCrystals);
        Scan(InventoryType.RetainerMarket);

        if (snapshot.Count == 0) return; // pages exist but nothing loaded yet this instant — don't overwrite a good snapshot with an empty one
        Snapshots[name] = snapshot;
    }

    /// <summary>Every known retainer (visited at least once this session) that holds this item, with quantity.</summary>
    public static IReadOnlyList<(string RetainerName, int Count)> GetHolders(uint itemId)
    {
        var result = new List<(string, int)>();
        foreach (var (name, items) in Snapshots)
            if (items.TryGetValue(itemId, out var count) && count > 0)
                result.Add((name, count));
        return result;
    }

    public static int GetTotal(uint itemId)
    {
        var total = 0;
        foreach (var items in Snapshots.Values)
            total += items.GetValueOrDefault(itemId);
        return total;
    }

    /// <summary>Full owned breakdown for the Web UI (bags + saddlebag scanned live, retainers from
    /// this cache) — same shape as ItemDetailWindow's GetItemCount/GetInventoryBreakdown, just
    /// callable from WebUiService without pulling in the ImGui window class.</summary>
    public static unsafe (int Total, int Bags, int Saddlebag, IReadOnlyList<(string Name, int Count)> Retainers) GetOwnedBreakdown(uint itemId)
    {
        if (itemId == 0 || itemId > 500000) return (0, 0, 0, Array.Empty<(string, int)>());

        var im = InventoryManager.Instance();
        if (im == null) return (0, 0, 0, Array.Empty<(string, int)>());

        int bags = 0, saddlebag = 0;
        void Scan(InventoryType type, ref int acc)
        {
            var container = im->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) return;
            for (var i = 0; i < container->Size; i++)
                if (container->Items[i].ItemId == itemId)
                    acc += (int)container->Items[i].Quantity;
        }
        Scan(InventoryType.Inventory1, ref bags);
        Scan(InventoryType.Inventory2, ref bags);
        Scan(InventoryType.Inventory3, ref bags);
        Scan(InventoryType.Inventory4, ref bags);
        Scan(InventoryType.Crystals, ref bags);
        Scan(InventoryType.Currency, ref bags);
        Scan(InventoryType.SaddleBag1, ref saddlebag);
        Scan(InventoryType.SaddleBag2, ref saddlebag);

        var retainers = GetHolders(itemId);
        var total = bags + saddlebag + retainers.Sum(r => r.Count);
        return (total, bags, saddlebag, retainers);
    }
}
