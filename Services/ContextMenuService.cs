using System;
using System.Collections.Generic;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamSource;

namespace GlamSource.Services;

public sealed class ContextMenuService : IDisposable
{
    private readonly IContextMenu _contextMenu;
    private readonly IGameGui _gameGui;
    private readonly Action<uint> _onItemClicked;

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

    public ContextMenuService(IContextMenu contextMenu, IGameGui gameGui, Action<uint> onItemClicked)
    {
        _contextMenu = contextMenu;
        _gameGui = gameGui;
        _contextMenu.OnMenuOpened += OnMenuOpened;
        _onItemClicked = onItemClicked;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        Plugin.Log.Information("[CTX] OnMenuOpened type={Type} target={Target}",
            args.MenuType, args.Target?.GetType().Name ?? "null");

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
            return null;
        }

        if (args.MenuType != ContextMenuType.Default)
            return null;

        var addonName = args.AddonName;
        Plugin.Log.Information("[CTX-Default] AddonName={Addon}", addonName ?? "null");
        if (string.IsNullOrEmpty(addonName) || !GameAddonWhitelist.Contains(addonName))
            return null;

        var defaultTarget = (MenuTargetDefault)args.Target;
        if (defaultTarget.TargetContentId != 0)
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
            "CharacterInspect" => ExtractCharacterInspectItemId(),
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
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.Examine);
        var agent = _gameGui.FindAgentInterface("CharacterInspect");
        if (agent == default) return null;
        var selectedSlot = *(int*)((nint)agent + 0x44C);
        var item = container->GetInventorySlot(selectedSlot);
        return CorrectItemId(item->GetItemId());
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

