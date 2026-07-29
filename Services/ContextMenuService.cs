using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

namespace GlamSource.Services;

public sealed class ContextMenuService : IDisposable
{
    private readonly IContextMenu _contextMenu;
    private readonly Action<uint> _onItemClicked;

    public ContextMenuService(IContextMenu contextMenu, Action<uint> onItemClicked)
    {
        _contextMenu = contextMenu;
        _contextMenu.OnMenuOpened += OnMenuOpened;
        _onItemClicked = onItemClicked;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        var itemId = ExtractItemId(args);
        if (itemId is not > 0)
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = "Item Source",
            OnClicked = _ => _onItemClicked(itemId.Value),
        });
    }

    private uint? ExtractItemId(IMenuOpenedArgs args)
    {
        if (args.MenuType == ContextMenuType.Inventory)
        {
            var inv = (MenuTargetInventory)args.Target;
            if (inv.TargetItem.HasValue)
                return CorrectItemId(inv.TargetItem.Value.ItemId);
        }

        return null;
    }

    private static uint CorrectItemId(uint itemId)
    {
        return itemId switch
        {
            > 1000000 => itemId - 1000000,
            > 500000 and < 1000000 => itemId - 500000,
            _ => itemId,
        };
    }

    public void Dispose()
    {
        _contextMenu.OnMenuOpened -= OnMenuOpened;
    }
}
