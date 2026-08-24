namespace GlamSource.Core;

public enum ItemSourceType
{
    Unknown,
    Crafted,
    Vendor,
    Quest,
    Dungeon,
    Trial,
    Raid,
    Achievement,
    MogStation,
    PvP,
    TreasureHunt,
    Shop,
    Fate,
    Mob,
    Coffer,
    Gathering,
    Retainer,
    Airship,
    Submarine,
    Relic,
    Other
}

public record ItemSource(ItemSourceType Type, string Description);

public interface IItemSourceService
{
    IReadOnlyList<ItemSource> GetSources(uint itemId);
}
