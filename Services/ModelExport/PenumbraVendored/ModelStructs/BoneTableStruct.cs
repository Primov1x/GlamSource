// Vendored from Penumbra.GameData (https://github.com/xivdev/Penumbra), AGPL-3.0 — same license as GlamSource.
// Lumina lacks post-patch-7.2 model header fields (NeckMorphCount etc.); this parser is current.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Lumina.Data;

namespace Penumbra.GameData.Files.ModelStructs;

public struct BoneTableStruct
{
    public ushort[] BoneIndex;
    public uint     BoneCount;

    public static BoneTableStruct ReadV5(LuminaBinaryReader br)
    {
        var ret = new BoneTableStruct
        {
            BoneIndex = br.ReadUInt16Array(64),
            BoneCount = br.ReadUInt32(),
        };
        return ret;
    }

    public static BoneTableStruct ReadV6(LuminaBinaryReader br)
    {
        var ret      = new BoneTableStruct();
        var startPos = br.BaseStream.Position;
        var offset   = br.ReadUInt16();
        var size     = br.ReadUInt16();
        var retPos   = br.BaseStream.Position;
        br.BaseStream.Position = startPos + offset * 4; // offset relative to header size for some reason.
        ret.BoneIndex          = br.ReadUInt16Array(size);
        ret.BoneCount          = (uint)ret.BoneIndex.Length;
        br.BaseStream.Position = retPos;
        return ret;
    }

    public static void WriteV5(BinaryWriter w, IReadOnlyCollection<BoneTableStruct> tables)
    {
        foreach (var table in tables)
        {
            foreach (var index in table.BoneIndex)
                w.Write(index);

            w.Write(table.BoneCount);
        }
    }

    public static void WriteV6(BinaryWriter w, IReadOnlyCollection<BoneTableStruct> tables)
    {
        var currentOffset = tables.Count;
        foreach (var table in tables)
        {
            w.Write((ushort)currentOffset);
            w.Write((ushort)table.BoneCount);
            var pos = w.BaseStream.Position;
            w.BaseStream.Position += (currentOffset - 1) * 4;
            foreach (var bone in table.BoneIndex)
                w.Write(bone);
            if ((table.BoneCount & 1) == 1)
                w.Write((ushort)0);
            currentOffset         += (ushort)((table.BoneCount + 1) / 2) - 1;
            w.BaseStream.Position =  pos;
        }

        w.BaseStream.Position += tables.Sum(t => (t.BoneCount & 1) == 1 ? t.BoneCount + 1 : t.BoneCount) * 2;
    }
}