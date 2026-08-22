namespace GlamSource.Core;

/// <summary>Provides access to equipment data of the current target and item lookup functionality for the plugin UI.</summary>
public interface IGlamourService
{
    IReadOnlyList<EquipmentSlot> GetTargetEquipment();
    // ponytail: self view reads InventoryType.EquippedItems directly, always available even without target.
    IReadOnlyList<EquipmentSlot> GetSelfEquipment();
    IReadOnlyList<(uint id, string name)> SearchItems(string query);
    // ponytail: passive scan via ObjectTable[index] + DrawData. Must be called on the framework thread.
    // Returns null if the object is missing, not rendered, or not a character.
    // Default: not supported (fixture / mock have no game object table).
    IReadOnlyList<EquipmentSlot>? TryGetVisibleGlamour(int objectIndex) => null;
}
