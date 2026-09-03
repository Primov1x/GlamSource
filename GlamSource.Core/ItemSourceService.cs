namespace GlamSource.Core;

/// GetEventStatusAsync result. Recurring = the CSV text carried a year suffix (seasonal event,
/// comes back annually); Active = a live Lodestone check (null = couldn't check, no guess).
public sealed record EventStatus(string EventName, bool Recurring, bool? Active);

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
    TripleTriad,
    Other
}

public record ItemSource(ItemSourceType Type, string Description);

public interface IItemSourceService
{
    IReadOnlyList<ItemSource> GetSources(uint itemId);
}
