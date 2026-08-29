// Vendored from Penumbra.GameData (https://github.com/xivdev/Penumbra), AGPL-3.0 — same license as GlamSource.
// Lumina lacks post-patch-7.2 model header fields (NeckMorphCount etc.); this parser is current.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace Penumbra.GameData.Files.ModelStructs;

public unsafe struct NeckMorphStruct
{
    public float PositionX;
    public float PositionY;
    public float PositionZ;
    public uint  ConstValue;
    public float NormalX;
    public float NormalY;
    public float NormalZ;
    public byte  BoneIndex1;
    public byte  BoneIndex2;
    public byte  BoneIndex3;
    public byte  BoneIndex4;
}