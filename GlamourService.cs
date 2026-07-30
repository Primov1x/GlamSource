using System;
using System.Collections.Generic;
using GlamSource.Core;

namespace GlamSource;

public class GlamourService : IGlamourService
{
    public IReadOnlyList<EquipmentSlot> GetTargetEquipment() => Array.Empty<EquipmentSlot>();
    public IReadOnlyList<(uint id, string name)> SearchItems(string query) => Array.Empty<(uint, string)>();
}
