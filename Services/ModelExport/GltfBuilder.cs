using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GlamSource.Services.ModelExport;

/// <summary>One mesh plus the index of its PNG texture (into the textures list), or -1 for none.</summary>
public sealed record GltfMeshInput(DecodedMesh Geometry, int TextureIndex);

// ponytail: hand-rolled GLB (glTF 2.0 binary) writer — the format is just JSON + one binary
// buffer; a gltf library dependency would be bigger than this file.
public static class GltfBuilder
{
    public static byte[] BuildGlb(IReadOnlyList<GltfMeshInput> meshes, IReadOnlyList<byte[]> pngTextures)
    {
        using var bin = new MemoryStream();
        var bufferViews = new List<object>();
        var accessors = new List<object>();
        var gltfMeshes = new List<object>();
        var nodes = new List<object>();
        var images = new List<object>();
        var textures = new List<object>();
        var materials = new List<object>();

        // one material per PNG + one shared untextured fallback
        for (var t = 0; t < pngTextures.Count; t++)
        {
            var view = AddView(bin, bufferViews, pngTextures[t], 0);
            images.Add(new { bufferView = view, mimeType = "image/png" });
            textures.Add(new { source = t });
            materials.Add(new
            {
                pbrMetallicRoughness = new
                {
                    baseColorTexture = new { index = t },
                    metallicFactor = 0.0,
                    roughnessFactor = 0.9,
                },
                alphaMode = "MASK",
                alphaCutoff = 0.5,
                doubleSided = true,
            });
        }
        var fallbackMaterial = materials.Count;
        materials.Add(new
        {
            pbrMetallicRoughness = new { baseColorFactor = new[] { 0.7, 0.7, 0.75, 1.0 }, metallicFactor = 0.0, roughnessFactor = 0.9 },
            doubleSided = true,
        });

        foreach (var input in meshes)
        {
            var g = input.Geometry;
            var attrs = new Dictionary<string, int>
            {
                ["POSITION"] = AddAccessor(bin, bufferViews, accessors, g.Positions, 3, "VEC3", 34962),
            };
            if (g.Normals.Length > 0)
                attrs["NORMAL"] = AddAccessor(bin, bufferViews, accessors, g.Normals, 3, "VEC3", 34962);
            if (g.Uvs.Length > 0)
                attrs["TEXCOORD_0"] = AddAccessor(bin, bufferViews, accessors, g.Uvs, 2, "VEC2", 34962);

            var idxBytes = new byte[g.Indices.Length * 2];
            Buffer.BlockCopy(g.Indices, 0, idxBytes, 0, idxBytes.Length);
            var idxView = AddView(bin, bufferViews, idxBytes, 34963);
            var idxAccessor = accessors.Count;
            accessors.Add(new { bufferView = idxView, componentType = 5123, count = g.Indices.Length, type = "SCALAR" });

            var meshIdx = gltfMeshes.Count;
            gltfMeshes.Add(new
            {
                primitives = new[]
                {
                    new
                    {
                        attributes = attrs,
                        indices = idxAccessor,
                        material = input.TextureIndex >= 0 && input.TextureIndex < pngTextures.Count
                            ? input.TextureIndex
                            : fallbackMaterial,
                    },
                },
            });
            nodes.Add(new { mesh = meshIdx });
        }

        var gltf = new
        {
            asset = new { version = "2.0", generator = "GlamSource" },
            scene = 0,
            scenes = new[] { new { nodes = Enumerable.Range(0, nodes.Count).ToArray() } },
            nodes,
            meshes = gltfMeshes,
            accessors,
            bufferViews,
            buffers = new[] { new { byteLength = bin.Length } },
            images,
            textures,
            materials,
            samplers = Array.Empty<object>(),
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(gltf);

        return WrapGlb(json, bin.ToArray());
    }

    private static int AddAccessor(MemoryStream bin, List<object> views, List<object> accessors, float[] data, int comps, string type, int target)
    {
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        var view = AddView(bin, views, bytes, target);
        var count = data.Length / comps;

        // POSITION accessors require min/max per spec
        var min = new float[comps];
        var max = new float[comps];
        Array.Fill(min, float.MaxValue);
        Array.Fill(max, float.MinValue);
        for (var i = 0; i < data.Length; i++)
        {
            var c = i % comps;
            if (data[i] < min[c]) min[c] = data[i];
            if (data[i] > max[c]) max[c] = data[i];
        }

        var idx = accessors.Count;
        accessors.Add(new { bufferView = view, componentType = 5126, count, type, min, max });
        return idx;
    }

    private static int AddView(MemoryStream bin, List<object> views, byte[] data, int target)
    {
        // 4-byte alignment between views
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var offset = bin.Length;
        bin.Write(data);
        var idx = views.Count;
        if (target != 0)
            views.Add(new { buffer = 0, byteOffset = offset, byteLength = data.Length, target });
        else
            views.Add(new { buffer = 0, byteOffset = offset, byteLength = data.Length });
        return idx;
    }

    private static byte[] WrapGlb(byte[] json, byte[] bin)
    {
        // pad JSON with spaces, BIN with zeros, both to 4 bytes
        var jsonPad = (4 - json.Length % 4) % 4;
        var binPad = (4 - bin.Length % 4) % 4;
        var total = 12 + 8 + json.Length + jsonPad + 8 + bin.Length + binPad;

        using var ms = new MemoryStream(total);
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u); // "glTF"
        w.Write(2u);
        w.Write((uint)total);
        w.Write((uint)(json.Length + jsonPad));
        w.Write(0x4E4F534Au); // "JSON"
        w.Write(json);
        for (var i = 0; i < jsonPad; i++) w.Write((byte)0x20);
        w.Write((uint)(bin.Length + binPad));
        w.Write(0x004E4942u); // "BIN\0"
        w.Write(bin);
        for (var i = 0; i < binPad; i++) w.Write((byte)0);
        return ms.ToArray();
    }
}
