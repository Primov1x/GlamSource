// Vendored from Penumbra.GameData (https://github.com/xivdev/Penumbra), AGPL-3.0 — same license as GlamSource.
// Lumina lacks post-patch-7.2 model header fields (NeckMorphCount etc.); this parser is current.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
namespace Penumbra.GameData.Files;

public interface IWritable
{
    public bool   Valid { get; }
    public byte[] Write();
}