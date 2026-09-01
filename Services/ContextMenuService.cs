using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamSource;
using GlamSource.Core;

namespace GlamSource.Services;

public sealed class ContextMenuService : IDisposable
{
    private readonly IContextMenu _contextMenu;
    private readonly IGameGui _gameGui;
    private readonly Action<uint> _onItemClicked;
    private readonly GameDataService? _gameDataService;
    private readonly IItemDetailService? _itemDetail;

    private static readonly HashSet<string> GameAddonWhitelist = new()
    {
        "CharacterInspect",
        "ChatLog",
        "ColorantColoring",
        "ContentsInfoDetail",
        "DailyQuestSupply",
        "FreeCompanyCreditShop",
        "GrandCompanyExchange",
        "HousingCatalogPreview",
        "HousingGoods",
        "InclusionShop",
        "ItemSearch",
        "Journal",
        "MateriaAttach",
        "MiragePrismPrismBoxCrystallize",
        "RecipeMaterialList",
        "RecipeNote",
        "RecipeTree",
        "Shop",
        "ShopExchangeCurrency",
        "ShopExchangeItem",
        "ShopExchangeItemDialog",
        "SubmarinePartsMenu",
        "Tryon",
    };

    public ContextMenuService(IContextMenu contextMenu, IGameGui gameGui, Action<uint> onItemClicked,
        GameDataService? gameDataService = null, IItemDetailService? itemDetail = null)
    {
        _contextMenu = contextMenu;
        _gameGui = gameGui;
        _contextMenu.OnMenuOpened += OnMenuOpened;
        _onItemClicked = onItemClicked;
        _gameDataService = gameDataService;
        _itemDetail = itemDetail;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        Plugin.Log.Information("[CTX] OnMenuOpened type={Type} target={Target}",
            args.MenuType, args.Target?.GetType().Name ?? "null");

        var itemId = ExtractItemId(args);
        if (itemId is > 0)
        {
            args.AddMenuItem(new MenuItem
            {
                Name = "Item Source",
                PrefixChar = 'G',
                PrefixColor = 52,
                OnClicked = _ => _onItemClicked(itemId.Value),
            });
        }

        AddMountMenuItemIfApplicable(args);
    }

    // ponytail: right-click a player who's mounted -> "Check Mount" resolves Character.Mount.MountId
    // (GameDataService.GetMountId) to its unlock item (ItemDetailService.ResolveMountItemId, from the
    // FFXIV Collect mounts dataset) and reuses the exact same item-detail callback as "Item Source" —
    // no separate UI needed, ItemDetailWindow already shows source/set/image for any item id.
    private void AddMountMenuItemIfApplicable(IMenuOpenedArgs args)
    {
        if (_gameDataService == null || _itemDetail == null) return;
        if (args.MenuType != ContextMenuType.Default) return;
        if (args.Target is not MenuTargetDefault { TargetObject: { } target }) return;

        var mountId = _gameDataService.GetMountId(target);
        if (mountId is not > 0) return;

        var mountItemId = _itemDetail.ResolveMountItemId(mountId.Value);
        if (mountItemId is not > 0) return;

        args.AddMenuItem(new MenuItem
        {
            Name = "Check Mount",
            PrefixChar = 'G',
            PrefixColor = 52,
            OnClicked = _ => _onItemClicked(mountItemId.Value),
        });
    }

    private uint? ExtractItemId(IMenuOpenedArgs args)
    {
        if (args.MenuType == ContextMenuType.Inventory)
        {
            var inv = (MenuTargetInventory)args.Target;
            if (inv.TargetItem.HasValue)
                return CorrectItemId(inv.TargetItem.Value.ItemId);
            return null;
        }

        if (args.MenuType != ContextMenuType.Default)
            return null;

        var addonName = args.AddonName;
        Plugin.Log.Information("[CTX-Default] AddonName={Addon}", addonName ?? "null");
        if (string.IsNullOrEmpty(addonName) || !GameAddonWhitelist.Contains(addonName))
            return null;

        return ExtractItemIdFromAddon(addonName);
    }

    private uint? ExtractItemIdFromAddon(string addonName)
    {
        return addonName switch
        {
            "RecipeNote" => ExtractRecipeNoteItemId(),
            "RecipeTree" or "RecipeMaterialList" => ExtractRecipeItemContextItemId(),
            "ColorantColoring" => ExtractColorantColoringItemId(),
            "GrandCompanyExchange" or "ShopExchangeItem" => ExtractShopExchangeItemId(),
            "ChatLog" => ExtractChatLogItemId(),
            "ContentsInfoDetail" => ExtractContentsInfoDetailItemId(),
            "ItemSearch" => ExtractItemSearchItemId(),
            // ponytail: ExtractCharacterInspectItemId reads the Examine window's own inventory
            // container + selected-slot index directly — was written but never actually wired in
            // (dead code, fell through to the generic HoveredItem fallback instead, which doesn't
            // reliably fire for the Examine paperdoll's equipped-gear icons).
            "CharacterInspect" => ExtractCharacterInspectItemId() ?? ExtractHoveredItemId(),
            "MiragePrismPrismBoxCrystallize" => ExtractMiragePrismItemId(),
            _ => ExtractHoveredItemId(),
        };
    }

    private unsafe uint? ExtractRecipeNoteItemId()
    {
        var agent = _gameGui.FindAgentInterface("RecipeNote");
        if (agent == default) return null;
        return *(uint*)((nint)agent + 0x398);
    }

    private unsafe uint? ExtractRecipeItemContextItemId()
    {
        var uiModule = UIModule.Instance();
        var agents = uiModule->GetAgentModule();
        var agentPtr = agents->GetAgentByInternalId(AgentId.RecipeItemContext);
        return *(uint*)((nint)agentPtr + 0x28);
    }

    private unsafe uint? ExtractColorantColoringItemId()
    {
        var agent = _gameGui.FindAgentInterface("ColorantColoring");
        if (agent == default) return null;
        return *(uint*)((nint)agent + 0x3C);
    }

    private unsafe uint? ExtractShopExchangeItemId()
    {
        var agent = _gameGui.FindAgentInterface("GrandCompanyExchange");
        if (agent == default) agent = _gameGui.FindAgentInterface("ShopExchangeItem");
        if (agent == default) return null;
        return *(uint*)((nint)agent + 0x54);
    }

    private unsafe uint? ExtractChatLogItemId()
    {
        var ptr = _gameGui.FindAgentInterface("ChatLog");
        if (ptr == default) return null;
        var agent = (AgentChatLog*)(nint)ptr;
        return agent->ContextItemId;
    }

    private unsafe uint? ExtractContentsInfoDetailItemId()
    {
        var agent = _gameGui.FindAgentInterface("ContentsInfo");
        if (agent == default) return null;
        return *(uint*)((nint)agent + 0x17CC);
    }

    private unsafe uint? ExtractItemSearchItemId()
    {
        return (uint)AgentContext.Instance()->UpdateCheckerParam;
    }

    private unsafe uint? ExtractCharacterInspectItemId()
    {
        try
        {
            var im = InventoryManager.Instance();
            if (im == null) { Plugin.Log.Information("[CTX-CI] InventoryManager null"); return null; }
            
            var container = im->GetInventoryContainer(InventoryType.Examine);
            if (container == null) { Plugin.Log.Information("[CTX-CI] Examine container null"); return null; }
            
            var agent = _gameGui.FindAgentInterface("CharacterInspect");
            if (agent == default) { Plugin.Log.Information("[CTX-CI] Agent null"); return null; }
            
            var selectedSlot = *(int*)((nint)agent + 0x44C);
            Plugin.Log.Information("[CTX-CI] selectedSlot={Slot}", selectedSlot);
            
            if (selectedSlot < 0 || selectedSlot >= container->Size)
            {
                Plugin.Log.Information("[CTX-CI] slot out of range (size={Size})", container->Size);
                return null;
            }
            
            var item = container->GetInventorySlot(selectedSlot);
            if (item == null) { Plugin.Log.Information("[CTX-CI] item null"); return null; }
            
            var itemId = CorrectItemId(item->ItemId);
            Plugin.Log.Information("[CTX-CI] itemId={Id}", itemId);
            return itemId;
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "[CTX-CI] Failed");
            return null;
        }
    }

    private unsafe uint? ExtractMiragePrismItemId()
    {
        var uiModule = UIModule.Instance();
        var agents = uiModule->GetAgentModule();
        var agent = (AgentMiragePrismPrismBox*)agents->GetAgentByInternalId(AgentId.MiragePrismPrismBox);
        return CorrectItemId((uint)agent->Data->TempContextItem.ItemId);
    }

    private uint? ExtractHoveredItemId()
    {
        var hovered = (uint)_gameGui.HoveredItem;
        return hovered > 0 ? CorrectItemId(hovered) : null;
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

