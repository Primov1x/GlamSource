namespace GlamSource.Core;

public interface IGlamourService
{
    IReadOnlyList<EquipmentSlot> GetTargetEquipment();
}
