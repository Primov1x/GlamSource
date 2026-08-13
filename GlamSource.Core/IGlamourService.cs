namespace GlamSource.Core;

/// <summary>Provides access to equipment data of the current target and item lookup functionality for the plugin UI.</summary>
public interface IGlamourService
{
    IReadOnlyList<EquipmentSlot> GetTargetEquipment();
    IReadOnlyList<(uint id, string name)> SearchItems(string query);
}
