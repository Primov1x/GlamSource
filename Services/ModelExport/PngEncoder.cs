using System;
using System.IO;
using System.IO.Compression;

namespace GlamSource.Services.ModelExport;

// ponytail: minimal RGBA8 PNG writer (filter 0, one IDAT) — avoids an image-lib dependency just
// to embed textures in a GLB. Zlib framing hand-rolled around DeflateStream.
public static class PngEncoder
{
    public static byte[] EncodeRgba(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR
        Span<byte> ihdr = stackalloc byte[13];
        WriteBe(ihdr, 0, width);
        WriteBe(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type RGBA
        WriteChunk(ms, "IHDR", ihdr.ToArray());

        // raw scanlines with filter byte 0
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            var dst = y * (1 + width * 4);
            raw[dst] = 0;
            Buffer.BlockCopy(rgba, y * width * 4, raw, dst + 1, width * 4);
        }

        // zlib: 2-byte header + deflate + adler32
        using var compressed = new MemoryStream();
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x9C);
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(raw);
        Span<byte> adler = stackalloc byte[4];
        WriteBe(adler, 0, (int)Adler32(raw));
        compressed.Write(adler);
        WriteChunk(ms, "IDAT", compressed.ToArray());

        WriteChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] payload)
    {
        Span<byte> len = stackalloc byte[4];
        WriteBe(len, 0, payload.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(payload);
        Span<byte> crc = stackalloc byte[4];
        var crcVal = Crc32(typeBytes, payload);
        WriteBe(crc, 0, (int)crcVal);
        s.Write(crc);
    }

    private static void WriteBe(Span<byte> b, int o, int v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var x in data)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] type, byte[] payload)
    {
        if (_crcTable == null)
        {
            _crcTable = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                var c = i;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                _crcTable[i] = c;
            }
        }
        var crc = 0xFFFFFFFFu;
        foreach (var x in type) crc = _crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        foreach (var x in payload) crc = _crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
