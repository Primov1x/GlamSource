// Vendored from Penumbra.GameData (https://github.com/xivdev/Penumbra), AGPL-3.0 — same license as GlamSource.
// Lumina lacks post-patch-7.2 model header fields (NeckMorphCount etc.); this parser is current.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lumina.Data.Parsing;

namespace Penumbra.GameData.Files.ModelStructs;

public unsafe struct ModelHeader
{
    // MeshHeader
    public        float                  Radius;
    public        ushort                 MeshCount;
    public        ushort                 AttributeCount;
    public        ushort                 SubmeshCount;
    public        ushort                 MaterialCount;
    public        ushort                 BoneCount;
    public        ushort                 BoneTableCount;
    public        ushort                 ShapeCount;
    public        ushort                 ShapeMeshCount;
    public        ushort                 ShapeValueCount;
    public        byte                   LodCount;
    public        MdlStructs.ModelFlags1 Flags1;
    public        ushort                 ElementIdCount;
    public        byte                   TerrainShadowMeshCount;
    public        MdlStructs.ModelFlags2 Flags2;
    public        float                  ModelClipOutDistance;
    public        float                  ShadowClipOutDistance;
    public        ushort                 CullingGridCount;
    public        ushort                 TerrainShadowSubmeshCount;
    public        byte                   Flags3;
    public        byte                   BGChangeMaterialIndex;
    public        byte                   BGCrestChangeMaterialIndex;
    public        byte                   NeckMorphCount;
    public        ushort                 BoneTableArrayCountTotal;
    public        ushort                 Unknown8;
    public        ushort                 Unknown9;
    private fixed byte                   _padding[6];
}