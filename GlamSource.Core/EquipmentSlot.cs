namespace GlamSource.Core;

public enum EquipmentSlotType
{
    MainHand,
    OffHand,
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Earrings,
    Necklace,
    Bracelets,
    RingRight,
    RingLeft
}

public sealed record EquipmentSlot(
    EquipmentSlotType Slot,
    uint ActualItemId,
    string ActualItemName,
    uint? GlamourItemId,
    string? GlamourItemName,
    IReadOnlyList<ItemSource>? ActualItemSources = null,
    IReadOnlyList<ItemSource>? GlamourItemSources = null)
{
    public bool IsGlamoured => GlamourItemId.HasValue;
}
