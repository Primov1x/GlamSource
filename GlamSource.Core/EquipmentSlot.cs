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
    string? GlamourItemName)
{
    public bool IsGlamoured => GlamourItemId.HasValue;
}
