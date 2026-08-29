using System;
using System.Collections.Generic;
using System.Linq;
using GlamSource.Core;
using Lumina;
using Lumina.Data.Files;

namespace GlamSource.Services.ModelExport;

// ponytail: static bind-pose export, LOD0, no weapons, no live pose — the vertices in .mdl are
// already in bind pose, so a skeleton is not needed for a still viewer. Equipment race code fixed
// to c0101: most gear ships only that model and the game fits it to races at runtime via skeleton
// deforms we don't replicate. Body/face/hair/tail DO use the live Customize race code.
/// <summary>Character base-model parameters resolved from the live Customize array.</summary>
public sealed record CharacterModelInfo(string RaceCode, int Face, int Hair, int TailOrEars)
{
    /// <summary>Model race code (cXXXX) from Race/Tribe/Gender customize bytes.</summary>
    public static string ResolveRaceCode(byte race, byte tribe, byte gender)
    {
        // female codes = male + 0100; Highlander (Hyur tribe 2) has its own pair
        var maleCode = race switch
        {
            1 => tribe == 2 ? 301 : 101, // Hyur: Highlander / Midlander
            2 => 501,  // Elezen
            3 => 1101, // Lalafell
            4 => 701,  // Miqo'te
            5 => 901,  // Roegadyn
            6 => 1301, // Au Ra
            7 => 1501, // Hrothgar
            8 => 1701, // Viera
            _ => 101,
        };
        return $"c{maleCode + (gender == 1 ? 100 : 0):D4}";
    }
}

public sealed class ModelExportService
{
    private const string RaceCode = "c0101";

    private readonly GameData _gameData;

    // single-entry cache: same item set requested repeatedly while the viewer is open
    private (string Key, byte[] Glb)? _cache;

    public ModelExportService(GameData gameData)
        => _gameData = gameData;

    /// <summary>Per-request resolution trace for /api/model3d/debug — why did items drop out?</summary>
    public List<string> LastTrace { get; } = new();

    /// <summary>Build a GLB containing all equipment models for the given slots. Returns null when
    /// nothing could be resolved.</summary>
    public byte[]? BuildGlb(IReadOnlyList<EquipmentSlot> slots, CharacterModelInfo? chara = null, bool bypassCache = false, SkeletonPose? pose = null)
    {
        bypassCache = bypassCache || pose != null; // live pose changes every capture — never cache it
        LastTrace.Clear();
        LastTrace.Add($"slots in: {slots.Count}, chara: {chara?.RaceCode ?? "none"}");
        var items = slots
            .Select(s => (Slot: s.Slot, ItemId: s.GlamourItemId ?? s.ActualItemId, Stain: s.Stain0))
            .Where(x => x.ItemId > 0 && SlotInfo(x.Slot) != null)
            .ToList();
        LastTrace.Add($"usable items: {items.Count} [{string.Join(", ", items.Select(x => $"{x.Slot}:{x.ItemId}"))}]");
        if (items.Count == 0) return null;

        var key = string.Join(",", items.Select(x => $"{x.Slot}:{x.ItemId}:{x.Stain}")) + $"|{chara}";
        if (!bypassCache && _cache is { } c && c.Key == key) { LastTrace.Add("cache hit"); return c.Glb; }

        var meshInputs = new List<GltfMeshInput>();
        var pngs = new List<byte[]>();
        var materialCache = new Dictionary<string, (int, float[]?)>();

        var itemSheet = _gameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var stainSheet = _gameData.GetExcelSheet<Lumina.Excel.Sheets.Stain>();
        foreach (var (slot, itemId, stainId) in items)
        {
            // ponytail: whole-mesh tint from the stain color — real dye only recolors specific
            // colorset rows; good-enough approximation until colorset support lands.
            float[]? tint = null;
            if (stainId != 0 && stainSheet?.GetRowOrDefault(stainId) is { } stainRow)
            {
                var packed = stainRow.Color;
                tint = new[] { (packed & 0xFF) / 255f, ((packed >> 8) & 0xFF) / 255f, ((packed >> 16) & 0xFF) / 255f };
            }
            var row = itemSheet?.GetRowOrDefault(itemId % 1_000_000);
            if (row == null) { LastTrace.Add($"{slot}:{itemId} no item row"); continue; }
            var modelMain = row.Value.ModelMain;
            var setId = (ushort)modelMain;
            var itemVariant = (ushort)(modelMain >> 16);
            if (setId == 0) { LastTrace.Add($"{slot}:{itemId} setId 0"); continue; }

            var info = SlotInfo(slot)!.Value;
            // ponytail: most gear ships one race-agnostic model (RaceCode, c0101) and the game fits
            // it via skeleton deform at runtime — but some items only ever got a model for ONE
            // gender of a race (verified: a hat that only exists as c1801, not c0101 or c1701 male).
            // Try the generic model first, then the viewer's actual race/gender, then its opposite
            // gender (same 4-digit race prefix, +100/-100).
            var raceCandidates = new List<string> { RaceCode };
            if (chara != null)
            {
                raceCandidates.Add(chara.RaceCode);
                if (int.TryParse(chara.RaceCode.AsSpan(1), out var rc))
                    raceCandidates.Add($"c{(rc % 200 < 100 ? rc + 100 : rc - 100):D4}");
            }
            string? mdlPath = null;
            foreach (var rc in raceCandidates)
            {
                var candidate = $"chara/{info.Category}/{info.Prefix}{setId:D4}/model/{rc}{info.Prefix}{setId:D4}_{info.Suffix}.mdl";
                if (_gameData.FileExists(candidate)) { mdlPath = candidate; break; }
            }
            if (mdlPath == null) { LastTrace.Add($"{slot}:{itemId} missing (tried {string.Join(", ", raceCandidates)})"); continue; }

            var raw = _gameData.GetFile(mdlPath);
            if (raw == null) { LastTrace.Add($"{slot}:{itemId} GetFile null"); continue; }
            Penumbra.GameData.Files.MdlFile mdl;
            try { mdl = new Penumbra.GameData.Files.MdlFile(raw.Data); }
            catch (Exception ex) { LastTrace.Add($"{slot}:{itemId} mdl parse: {ex.Message}"); continue; }
            if (!mdl.Valid) { LastTrace.Add($"{slot}:{itemId} mdl invalid"); continue; }

            // imc: item variant -> material variant folder
            byte materialId = 1;
            try
            {
                var imcPath = $"chara/{info.Category}/{info.Prefix}{setId:D4}/{info.Prefix}{setId:D4}.imc";
                var imc = _gameData.GetFile<ImcFile>(imcPath);
                if (imc != null && itemVariant > 0)
                {
                    var part = imc.GetVariant(info.ImcPart, itemVariant - 1);
                    if (part.MaterialId > 0) materialId = part.MaterialId;
                }
            }
            catch { /* keep default v0001 */ }

            var meshes = MdlGeometry.DecodeLod0(mdl);
            LastTrace.Add($"{slot}:{itemId} meshes decoded: {meshes.Count}");
            foreach (var m in meshes)
            {
                if (pose != null) SkinApply.Apply(m, mdl.Bones, mdl.BoneTables, pose);
                var texIndex = -1;
                var effectiveTint = tint;
                if (m.MaterialIndex >= 0 && m.MaterialIndex < mdl.Materials.Length)
                {
                    var mtrlName = mdl.Materials[m.MaterialIndex];
                    // some equipment submeshes (skin peeking through a low-cut top, bare hands on
                    // fingerless gloves, ...) reference the character's OWN skin material by name
                    // (mt_c{race}b{bodyId}_*.mtrl) instead of the item's own — route those to the
                    // body material folder, not the item's equipment folder.
                    var bodyRefMatch = System.Text.RegularExpressions.Regex.Match(mtrlName, @"^/mt_c(\d{4})b(\d{4})_");
                    var mtrlPath = !mtrlName.StartsWith('/')
                        ? mtrlName
                        : bodyRefMatch.Success
                            ? $"chara/human/c{bodyRefMatch.Groups[1].Value}/obj/body/b{bodyRefMatch.Groups[2].Value}/material/v0001{mtrlName}"
                            : $"chara/{info.Category}/{info.Prefix}{setId:D4}/material/v{materialId:D4}{mtrlName}";
                    var (t, colorSetTint) = ResolveMaterialByPath(mtrlPath, pngs, materialCache);
                    texIndex = t;
                    // stain always wins over colorset average when the item is actually dyed
                    if (texIndex < 0 && effectiveTint == null) effectiveTint = colorSetTint;
                    LastTrace.Add($"{slot}:{itemId} mesh mtrl={mtrlPath} tex={texIndex} colorSetTint={(colorSetTint == null ? "null" : $"{colorSetTint[0]:F2},{colorSetTint[1]:F2},{colorSetTint[2]:F2}")} stain={(tint == null ? "null" : $"{tint[0]:F2},{tint[1]:F2},{tint[2]:F2}")}");
                }
                meshInputs.Add(new GltfMeshInput(m, texIndex, effectiveTint));
            }
        }

        if (chara != null)
        {
            var rc = chara.RaceCode;
            // body: skin shows where gear doesn't cover; textures use runtime-resolved "--" paths
            // we can't look up, so body/face fall back to a skin-tone tint when untextured.
            var skinTint = new[] { 0.85f, 0.66f, 0.56f };
            var hairTint = new[] { 0.35f, 0.30f, 0.28f };
            // ponytail: base body model id varies per race (Hyur etc = b0001, Viera male = b0002,
            // ...) — no full per-race table yet, just try the two ids confirmed to exist.
            var bodyId = _gameData.FileExists($"chara/human/{rc}/obj/body/b0001/model/{rc}b0001_top.mdl") ? "b0001" : "b0002";
            AddCharaPart($"chara/human/{rc}/obj/body/{bodyId}/model/{rc}{bodyId}_top.mdl", rc, $"body/{bodyId}", skinTint, meshInputs, pngs, materialCache, pose);
            AddCharaPart($"chara/human/{rc}/obj/face/f{chara.Face:D4}/model/{rc}f{chara.Face:D4}_fac.mdl", rc, $"face/f{chara.Face:D4}", skinTint, meshInputs, pngs, materialCache, pose);
            AddCharaPart($"chara/human/{rc}/obj/hair/h{chara.Hair:D4}/model/{rc}h{chara.Hair:D4}_hir.mdl", rc, $"hair/h{chara.Hair:D4}", hairTint, meshInputs, pngs, materialCache, pose);
            if (chara.TailOrEars > 0)
            {
                // tail (Miqo'te/Au Ra/Hrothgar) or ears (Viera) — whichever path exists
                AddCharaPart($"chara/human/{rc}/obj/tail/t{chara.TailOrEars:D4}/model/{rc}t{chara.TailOrEars:D4}_til.mdl", rc, $"tail/t{chara.TailOrEars:D4}", hairTint, meshInputs, pngs, materialCache, pose);
                AddCharaPart($"chara/human/{rc}/obj/zear/z{chara.TailOrEars:D4}/model/{rc}z{chara.TailOrEars:D4}_zer.mdl", rc, $"zear/z{chara.TailOrEars:D4}", skinTint, meshInputs, pngs, materialCache, pose);
            }
        }

        if (meshInputs.Count == 0) return null;
        var glb = GltfBuilder.BuildGlb(meshInputs, pngs);
        _cache = (key, glb);
        return glb;
    }

    /// <summary>Load one character base-model part (body/face/hair/tail/ears); silently skipped
    /// when the path doesn't exist for this race. Untextured meshes get the fallback tint.</summary>
    private void AddCharaPart(string mdlPath, string raceCode, string partFolder, float[] fallbackTint,
        List<GltfMeshInput> meshInputs, List<byte[]> pngs, Dictionary<string, (int, float[]?)> materialCache, SkeletonPose? pose)
    {
        if (!_gameData.FileExists(mdlPath)) { LastTrace.Add($"chara part missing: {mdlPath}"); return; }
        var raw = _gameData.GetFile(mdlPath);
        if (raw == null) return;
        Penumbra.GameData.Files.MdlFile mdl;
        try { mdl = new Penumbra.GameData.Files.MdlFile(raw.Data); }
        catch (Exception ex) { LastTrace.Add($"chara part parse {mdlPath}: {ex.Message}"); return; }
        if (!mdl.Valid) { LastTrace.Add($"chara part invalid: {mdlPath}"); return; }

        var meshes = MdlGeometry.DecodeLod0(mdl);
        LastTrace.Add($"chara part {partFolder}: {meshes.Count} meshes");
        foreach (var m in meshes)
        {
            if (pose != null) SkinApply.Apply(m, mdl.Bones, mdl.BoneTables, pose);
            var texIndex = -1;
            float[]? tint = fallbackTint;
            if (m.MaterialIndex >= 0 && m.MaterialIndex < mdl.Materials.Length)
            {
                var mtrlName = mdl.Materials[m.MaterialIndex];
                // ponytail: body/hair materials sit under a v0001 variant folder, face materials
                // don't (verified against real files) — try with it first, fall back without.
                var mtrlPathVariant = $"chara/human/{raceCode}/obj/{partFolder}/material/v0001{mtrlName}";
                var mtrlPathFlat = $"chara/human/{raceCode}/obj/{partFolder}/material{mtrlName}";
                var mtrlPath = mtrlName.StartsWith('/')
                    ? (_gameData.FileExists(mtrlPathVariant) ? mtrlPathVariant : mtrlPathFlat)
                    : mtrlName;
                var (t, colorSetTint) = ResolveMaterialByPath(mtrlPath, pngs, materialCache);
                texIndex = t;
                // untextured -> colorset average if we found one, else flat fallback
                if (texIndex < 0) tint = colorSetTint ?? fallbackTint;
            }
            // ponytail: skin/hair "_base"/"_hir" textures are a neutral grounding layer, not the
            // final color — the game multiplies them by the character's actual skin/hair color
            // (set via CustomizeParameter, which we don't have file access to). Always multiply by
            // our flat approximation, even when a texture was found — a bare base.tex renders as
            // dull blue-gray, not skin tone.
            meshInputs.Add(new GltfMeshInput(m, texIndex, tint));
        }
    }

    private (int Tex, float[]? Tint) ResolveMaterialByPath(string mtrlPath, List<byte[]> pngs, Dictionary<string, (int, float[]?)> cache)
    {
        if (cache.TryGetValue(mtrlPath, out var cached)) return cached;
        (int, float[]?) result = (-1, null);
        try
        {
            if (!_gameData.FileExists(mtrlPath)) { LastTrace.Add($"  mtrl missing: {mtrlPath}"); cache[mtrlPath] = result; return result; }

            var mtrlRaw = _gameData.GetFile(mtrlPath);
            var mtrl = _gameData.GetFile<MtrlFile>(mtrlPath);
            var texPath = PickDiffuse(mtrl);
            LastTrace.Add($"  mtrl={mtrlPath} allTex=[{string.Join(", ", MaterialTexturePaths(mtrl))}] picked={texPath ?? "none"}");
            if (texPath != null && _gameData.FileExists(texPath))
            {
                var tex = _gameData.GetFile<TexFile>(texPath);
                if (tex != null)
                {
                    var png = PngEncoder.EncodeRgba(tex.ImageData, tex.Header.Width, tex.Header.Height);
                    result = (pngs.Count, null);
                    pngs.Add(png);
                }
                else LastTrace.Add($"  tex GetFile null: {texPath}");
            }
            // Dawntrail-era gear commonly has no diffuse texture at all — color lives in the
            // material's color table, and WHICH row applies per-pixel comes from the id texture
            // (Red = ramp position, Green = A/B blend — see MaterialColorTable.BakeDiffuse). Bake
            // a real diffuse texture when we have an id map; a flat colorset average otherwise.
            if (result.Item1 < 0 && mtrlRaw != null)
            {
                var idPath = MaterialTexturePaths(mtrl).FirstOrDefault(p => p.EndsWith("_id.tex"));
                byte[]? baked = null;
                if (idPath != null && _gameData.FileExists(idPath))
                {
                    var idTex = _gameData.GetFile<TexFile>(idPath);
                    if (idTex != null)
                        baked = Penumbra.GameData.Files.MaterialColorTable.BakeDiffuse(mtrlRaw.Data, idTex.ImageData, idTex.Header.Width, idTex.Header.Height);
                    if (baked != null)
                    {
                        var png = PngEncoder.EncodeRgba(baked, idTex!.Header.Width, idTex.Header.Height);
                        result = (pngs.Count, null);
                        pngs.Add(png);
                        LastTrace.Add($"  colorset BAKED via {idPath} ({idTex.Header.Width}x{idTex.Header.Height})");
                    }
                }
                if (baked == null)
                {
                    var tint = Penumbra.GameData.Files.MaterialColorTable.AverageDiffuse(mtrlRaw.Data);
                    LastTrace.Add($"  colorset tint (no id map): {(tint == null ? "null" : $"{tint[0]:F2},{tint[1]:F2},{tint[2]:F2}")}");
                    result = (-1, tint);
                }
            }
        }
        catch (Exception ex) { LastTrace.Add($"  mtrl exception: {ex.Message}"); }
        cache[mtrlPath] = result;
        return result;
    }

    private static List<string> MaterialTexturePaths(MtrlFile? mtrl)
    {
        var paths = new List<string>();
        if (mtrl == null) return paths;
        foreach (var t in mtrl.TextureOffsets)
        {
            var end = Array.IndexOf(mtrl.Strings, (byte)0, t.Offset);
            if (end < 0) end = mtrl.Strings.Length;
            paths.Add(System.Text.Encoding.UTF8.GetString(mtrl.Strings, t.Offset, end - t.Offset));
        }
        return paths;
    }

    /// <summary>Pick the diffuse/base texture from a material's texture list — "_d.tex" preferred,
    /// else the first non-normal/non-mask entry.</summary>
    private static string? PickDiffuse(MtrlFile? mtrl)
    {
        var paths = MaterialTexturePaths(mtrl);
        // strictly diffuse/base candidates only — the old "first not-normal/spec/mask" fallback
        // happily grabbed id/mask maps and rendered gear in neon blue/green
        return paths.FirstOrDefault(p => p.EndsWith("_d.tex"))
            ?? paths.FirstOrDefault(p => p.EndsWith("_base.tex"))
            ?? paths.FirstOrDefault(p => p.EndsWith("_b.tex"));
    }

    private static (string Category, string Prefix, string Suffix, int ImcPart)? SlotInfo(EquipmentSlotType slot) => slot switch
    {
        EquipmentSlotType.Head => ("equipment", "e", "met", 0),
        EquipmentSlotType.Body => ("equipment", "e", "top", 1),
        EquipmentSlotType.Hands => ("equipment", "e", "glv", 2),
        EquipmentSlotType.Legs => ("equipment", "e", "dwn", 3),
        EquipmentSlotType.Feet => ("equipment", "e", "sho", 4),
        EquipmentSlotType.Earrings or EquipmentSlotType.Ear => ("accessory", "a", "ear", 0),
        EquipmentSlotType.Necklace => ("accessory", "a", "nek", 1),
        EquipmentSlotType.Bracelets => ("accessory", "a", "wrs", 2),
        EquipmentSlotType.RingRight => ("accessory", "a", "rir", 3),
        EquipmentSlotType.RingLeft => ("accessory", "a", "ril", 4),
        _ => null,
    };
}
