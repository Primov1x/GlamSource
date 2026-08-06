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
    Ear,
    Necklace,
    Bracelets,
    RingRight,
    RingLeft,
    Waist
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
