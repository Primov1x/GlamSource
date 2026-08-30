using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlamSource.Services.ModelExport;

/// <summary>One mesh plus the index of its PNG texture (into the textures list), or -1 for none.
/// Tint (RGB 0-1) multiplies the base color — rough whole-mesh dye approximation. NormalTextureIndex
/// is a decoded tangent-space normal map (see NormalMapDecoder), or -1 for none.</summary>
/// <summary>Role tags a material as "skin"/"hair"/"eye" for the viewer's JS to apply proper
/// non-metallic-roughness shading (sheen for skin, anisotropic specular for hair, clearcoat for
/// wet eyes) instead of flat MeshStandardMaterial — a first step toward closer-to-real-game
/// shading. null for equipment/accessories (no special treatment).</summary>
public sealed record GltfMeshInput(DecodedMesh Geometry, int TextureIndex, float[]? Tint = null, int NormalTextureIndex = -1, int MetallicRoughnessTextureIndex = -1, string? Role = null);

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

        for (var t = 0; t < pngTextures.Count; t++)
        {
            var view = AddView(bin, bufferViews, pngTextures[t], 0);
            images.Add(new { bufferView = view, mimeType = "image/png" });
            textures.Add(new { source = t });
        }

        // one material per (texture, tint, normal texture) combo — dye multiplies base color
        var materialByKey = new Dictionary<string, int>();
        int MaterialFor(int texIndex, float[]? tint, int normalTexIndex, int metalRoughTexIndex, string? role)
        {
            var f = tint is { Length: 3 } ? new[] { (double)tint[0], tint[1], tint[2], 1.0 } : new[] { 1.0, 1.0, 1.0, 1.0 };
            var hasNormal = normalTexIndex >= 0 && normalTexIndex < pngTextures.Count;
            var hasMetalRough = metalRoughTexIndex >= 0 && metalRoughTexIndex < pngTextures.Count;
            var key = $"{texIndex}:{f[0]:F3},{f[1]:F3},{f[2]:F3}:{(hasNormal ? normalTexIndex : -1)}:{(hasMetalRough ? metalRoughTexIndex : -1)}:{role}";
            if (materialByKey.TryGetValue(key, out var m)) return m;
            m = materials.Count;
            // extras is a free-form glTF spec field for exactly this — passing app-specific data
            // through to the loader without abusing a real PBR property. three.js's GLTFLoader
            // exposes it as material.userData.gltfExtensions is NOT this; plain material.userData
            // does NOT auto-populate from extras either — the viewer JS reads it off the raw
            // material def in the parsed json, see WebUiPage.cs's post-load material pass.
            var extras = role != null ? new { role } : null;
            // ponytail: without a baked metallicRoughness texture, metallicFactor stays 0 — guessing
            // a flat metalness for every material (no per-pixel data) looked worse than plain diffuse
            // in earlier testing. With the texture, factors are 1.0 (pure multiplier) per glTF spec.
            if (texIndex >= 0 && texIndex < pngTextures.Count)
                materials.Add(new
                {
                    pbrMetallicRoughness = new
                    {
                        baseColorTexture = new { index = texIndex },
                        baseColorFactor = f,
                        metallicRoughnessTexture = hasMetalRough ? new { index = metalRoughTexIndex } : null,
                        metallicFactor = hasMetalRough ? 1.0 : 0.0,
                        roughnessFactor = hasMetalRough ? 1.0 : 0.85,
                    },
                    normalTexture = hasNormal ? new { index = normalTexIndex } : null,
                    alphaMode = "MASK",
                    alphaCutoff = 0.5,
                    doubleSided = true,
                    extras,
                });
            else
                materials.Add(new
                {
                    pbrMetallicRoughness = new
                    {
                        baseColorFactor = tint is { Length: 3 } ? f : new[] { 0.7, 0.7, 0.75, 1.0 },
                        metallicFactor = 0.0,
                        roughnessFactor = 0.85,
                    },
                    doubleSided = true,
                    extras,
                });
            materialByKey[key] = m;
            return m;
        }

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
            // ponytail: without an explicit TANGENT attribute, glTF has the renderer derive one from
            // screen-space UV/position derivatives per the spec's fallback. three.js does this, but
            // it visibly broke on several meshes here (streaky/blotchy normal-map lighting on the
            // hat, oddly dark shading on the face) — computing real per-vertex tangents ourselves
            // (standard Lengyel method) sidesteps whatever about these meshes' UV layout the
            // fallback didn't like.
            if (input.NormalTextureIndex >= 0 && g.Normals.Length > 0 && g.Uvs.Length > 0)
            {
                var tangents = ComputeTangents(g);
                if (tangents != null)
                    attrs["TANGENT"] = AddAccessor(bin, bufferViews, accessors, tangents, 4, "VEC4", 34962);
            }

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
                        material = MaterialFor(input.TextureIndex, input.Tint, input.NormalTextureIndex, input.MetallicRoughnessTextureIndex, input.Role),
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
        // ponytail: "cannot read properties of null (reading 'index')" — normalTexture/
        // metallicRoughnessTexture are set to C# null when absent (e.g. "hasMetalRough ? ... : null"),
        // which serializes to JSON `null`, not an omitted key. three.js's GLTFLoader checks
        // `!== undefined` (true for null) then reads `.index` off it — crashes. glTF spec wants the
        // key left out entirely for "no texture", so drop nulls at serialize time instead of hand-
        // building two near-identical anonymous objects per optional field.
        var jsonOpts = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var json = JsonSerializer.SerializeToUtf8Bytes(gltf, jsonOpts);

        return WrapGlb(json, bin.ToArray());
    }

    /// <summary>Per-vertex tangents (xyz + handedness w), standard Lengyel method: accumulate a
    /// tangent/bitangent per triangle from its UV gradient, average per shared vertex, then
    /// Gram-Schmidt orthogonalize against the vertex normal. Returns null if degenerate (e.g. a
    /// zero-UV-area triangle everywhere) rather than emit garbage tangents.</summary>
    private static float[]? ComputeTangents(DecodedMesh g)
    {
        var n = g.Positions.Length / 3;
        var tan = new float[n * 3];
        var bitan = new float[n * 3];

        for (var t = 0; t < g.Indices.Length; t += 3)
        {
            int i0 = g.Indices[t], i1 = g.Indices[t + 1], i2 = g.Indices[t + 2];
            var p0x = g.Positions[i0 * 3]; var p0y = g.Positions[i0 * 3 + 1]; var p0z = g.Positions[i0 * 3 + 2];
            var p1x = g.Positions[i1 * 3]; var p1y = g.Positions[i1 * 3 + 1]; var p1z = g.Positions[i1 * 3 + 2];
            var p2x = g.Positions[i2 * 3]; var p2y = g.Positions[i2 * 3 + 1]; var p2z = g.Positions[i2 * 3 + 2];
            var e1x = p1x - p0x; var e1y = p1y - p0y; var e1z = p1z - p0z;
            var e2x = p2x - p0x; var e2y = p2y - p0y; var e2z = p2z - p0z;

            var u0 = g.Uvs[i0 * 2]; var v0 = g.Uvs[i0 * 2 + 1];
            var u1 = g.Uvs[i1 * 2]; var v1 = g.Uvs[i1 * 2 + 1];
            var u2 = g.Uvs[i2 * 2]; var v2 = g.Uvs[i2 * 2 + 1];
            var du1 = u1 - u0; var dv1 = v1 - v0;
            var du2 = u2 - u0; var dv2 = v2 - v0;

            var denom = du1 * dv2 - du2 * dv1;
            if (MathF.Abs(denom) < 1e-12f) continue; // degenerate UV triangle — skip, don't poison
            var f = 1f / denom;

            var tx = f * (dv2 * e1x - dv1 * e2x);
            var ty = f * (dv2 * e1y - dv1 * e2y);
            var tz = f * (dv2 * e1z - dv1 * e2z);
            var bx = f * (du1 * e2x - du2 * e1x);
            var by = f * (du1 * e2y - du2 * e1y);
            var bz = f * (du1 * e2z - du2 * e1z);

            foreach (var i in new[] { i0, i1, i2 })
            {
                tan[i * 3] += tx; tan[i * 3 + 1] += ty; tan[i * 3 + 2] += tz;
                bitan[i * 3] += bx; bitan[i * 3 + 1] += by; bitan[i * 3 + 2] += bz;
            }
        }

        var result = new float[n * 4];
        var anyValid = false;
        for (var i = 0; i < n; i++)
        {
            var nx = g.Normals[i * 3]; var ny = g.Normals[i * 3 + 1]; var nz = g.Normals[i * 3 + 2];
            var tx = tan[i * 3]; var ty = tan[i * 3 + 1]; var tz = tan[i * 3 + 2];

            // Gram-Schmidt: T - N * dot(N, T)
            var d = nx * tx + ny * ty + nz * tz;
            var ox = tx - nx * d; var oy = ty - ny * d; var oz = tz - nz * d;
            var len = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
            if (len < 1e-8f)
            {
                // no usable tangent at this vertex (e.g. isolated/degenerate) — pick an arbitrary
                // axis perpendicular to the normal so the accessor still has a unit vector, not NaN.
                ox = 1f; oy = 0f; oz = 0f;
                d = nx; // reuse d as dot(N,(1,0,0))
                ox -= nx * d;
                len = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
                if (len < 1e-8f) { ox = 0f; oy = 1f; oz = 0f; len = 1f; }
            }
            else anyValid = true;
            ox /= len; oy /= len; oz /= len;

            // handedness: sign of dot(cross(N,T), B)
            var cx = ny * oz - nz * oy; var cy = nz * ox - nx * oz; var cz = nx * oy - ny * ox;
            var bx = bitan[i * 3]; var by = bitan[i * 3 + 1]; var bz = bitan[i * 3 + 2];
            var w = (cx * bx + cy * by + cz * bz) < 0f ? -1f : 1f;

            result[i * 4] = ox; result[i * 4 + 1] = oy; result[i * 4 + 2] = oz; result[i * 4 + 3] = w;
        }
        return anyValid ? result : null;
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
