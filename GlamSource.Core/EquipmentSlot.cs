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
    IReadOnlyList<ItemSource>? GlamourItemSources = null,
    // ponytail: 2 stains per slot (dye channels) since API12; 0 = unbemalt
    byte Stain0 = 0,
    byte Stain1 = 0)
{
    public bool IsGlamoured => GlamourItemId.HasValue;
    public bool HasStain => Stain0 != 0 || Stain1 != 0;
}
