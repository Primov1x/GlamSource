namespace GlamSource.Core;

public interface IGlamourService
{
    IReadOnlyList<EquipmentSlot> GetTargetEquipment();
    IReadOnlyList<(uint id, string name)> SearchItems(string query);
}
