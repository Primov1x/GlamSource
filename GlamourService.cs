using System;
using System.Collections.Generic;
using GlamSource.Core;

namespace GlamSource;

public class GlamourService : IGlamourService
{
    public IReadOnlyList<EquipmentSlot> GetTargetEquipment() => Array.Empty<EquipmentSlot>();
}
