namespace GlamSource.Core;

public interface IGlamourService
{
    string? GetLocationName(uint territoryId);
    string? GetItemSource(long itemId);
    string? GetMountSource(uint mountId);
}
