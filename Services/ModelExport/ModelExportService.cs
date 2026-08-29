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
    public byte[]? BuildGlb(IReadOnlyList<EquipmentSlot> slots, CharacterModelInfo? chara = null)
    {
        LastTrace.Clear();
        LastTrace.Add($"slots in: {slots.Count}, chara: {chara?.RaceCode ?? "none"}");
        var items = slots
            .Select(s => (Slot: s.Slot, ItemId: s.GlamourItemId ?? s.ActualItemId, Stain: s.Stain0))
            .Where(x => x.ItemId > 0 && SlotInfo(x.Slot) != null)
            .ToList();
        LastTrace.Add($"usable items: {items.Count} [{string.Join(", ", items.Select(x => $"{x.Slot}:{x.ItemId}"))}]");
        if (items.Count == 0) return null;

        var key = string.Join(",", items.Select(x => $"{x.Slot}:{x.ItemId}:{x.Stain}")) + $"|{chara}";
        if (_cache is { } c && c.Key == key) { LastTrace.Add("cache hit"); return c.Glb; }

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
            var mdlPath = $"chara/{info.Category}/{info.Prefix}{setId:D4}/model/{RaceCode}{info.Prefix}{setId:D4}_{info.Suffix}.mdl";
            if (!_gameData.FileExists(mdlPath)) { LastTrace.Add($"{slot}:{itemId} missing {mdlPath}"); continue; }

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
                var texIndex = -1;
                var effectiveTint = tint;
                if (m.MaterialIndex >= 0 && m.MaterialIndex < mdl.Materials.Length)
                {
                    var mtrlName = mdl.Materials[m.MaterialIndex];
                    var mtrlPath = mtrlName.StartsWith('/')
                        ? $"chara/{info.Category}/{info.Prefix}{setId:D4}/material/v{materialId:D4}{mtrlName}"
                        : mtrlName;
                    var (t, colorSetTint) = ResolveMaterialByPath(mtrlPath, pngs, materialCache);
                    texIndex = t;
                    // stain always wins over colorset average when the item is actually dyed
                    if (texIndex < 0 && effectiveTint == null) effectiveTint = colorSetTint;
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
            AddCharaPart($"chara/human/{rc}/obj/body/b0001/model/{rc}b0001_top.mdl", rc, "body/b0001", skinTint, meshInputs, pngs, materialCache);
            AddCharaPart($"chara/human/{rc}/obj/face/f{chara.Face:D4}/model/{rc}f{chara.Face:D4}_fac.mdl", rc, $"face/f{chara.Face:D4}", skinTint, meshInputs, pngs, materialCache);
            AddCharaPart($"chara/human/{rc}/obj/hair/h{chara.Hair:D4}/model/{rc}h{chara.Hair:D4}_hir.mdl", rc, $"hair/h{chara.Hair:D4}", hairTint, meshInputs, pngs, materialCache);
            if (chara.TailOrEars > 0)
            {
                // tail (Miqo'te/Au Ra/Hrothgar) or ears (Viera) — whichever path exists
                AddCharaPart($"chara/human/{rc}/obj/tail/t{chara.TailOrEars:D4}/model/{rc}t{chara.TailOrEars:D4}_til.mdl", rc, $"tail/t{chara.TailOrEars:D4}", hairTint, meshInputs, pngs, materialCache);
                AddCharaPart($"chara/human/{rc}/obj/zear/z{chara.TailOrEars:D4}/model/{rc}z{chara.TailOrEars:D4}_zer.mdl", rc, $"zear/z{chara.TailOrEars:D4}", skinTint, meshInputs, pngs, materialCache);
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
        List<GltfMeshInput> meshInputs, List<byte[]> pngs, Dictionary<string, (int, float[]?)> materialCache)
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
            var texIndex = -1;
            float[]? tint = fallbackTint;
            if (m.MaterialIndex >= 0 && m.MaterialIndex < mdl.Materials.Length)
            {
                var mtrlName = mdl.Materials[m.MaterialIndex];
                var mtrlPath = mtrlName.StartsWith('/')
                    ? $"chara/human/{raceCode}/obj/{partFolder}/material/v0001{mtrlName}"
                    : mtrlName;
                var (t, colorSetTint) = ResolveMaterialByPath(mtrlPath, pngs, materialCache);
                texIndex = t;
                // textured -> real colors, no tint; else colorset average if we found one, else flat fallback
                if (texIndex < 0) tint = colorSetTint ?? fallbackTint;
            }
            meshInputs.Add(new GltfMeshInput(m, texIndex, texIndex >= 0 ? null : tint));
        }
    }

    private (int Tex, float[]? Tint) ResolveMaterialByPath(string mtrlPath, List<byte[]> pngs, Dictionary<string, (int, float[]?)> cache)
    {
        if (cache.TryGetValue(mtrlPath, out var cached)) return cached;
        (int, float[]?) result = (-1, null);
        try
        {
            if (_gameData.FileExists(mtrlPath))
            {
                var mtrl = _gameData.GetFile<MtrlFile>(mtrlPath);
                var texPath = PickDiffuse(mtrl);
                if (texPath != null && _gameData.FileExists(texPath))
                {
                    var tex = _gameData.GetFile<TexFile>(texPath);
                    if (tex != null)
                    {
                        var png = PngEncoder.EncodeRgba(tex.ImageData, tex.Header.Width, tex.Header.Height);
                        result = (pngs.Count, null);
                        pngs.Add(png);
                    }
                }
                if (result.Item1 < 0 && mtrl != null)
                    result = (-1, TintFromColorSet(mtrl));
            }
        }
        catch { /* untextured fallback */ }
        cache[mtrlPath] = result;
        return result;
    }

    /// <summary>Average diffuse color over the material's colorset rows — Dawntrail-era gear often
    /// has no diffuse texture at all; its color lives in this table. Rough (per-pixel row mapping
    /// via the id texture is ignored) but beats flat gray.</summary>
    private static unsafe float[]? TintFromColorSet(MtrlFile mtrl)
    {
        var info = mtrl.ColorSetInfo;
        float r = 0, g = 0, b = 0;
        var n = 0;
        for (var row = 0; row < 16; row++)
        {
            var cr = (float)BitConverter.UInt16BitsToHalf(info.Data[row * 16 + 0]);
            var cg = (float)BitConverter.UInt16BitsToHalf(info.Data[row * 16 + 1]);
            var cb = (float)BitConverter.UInt16BitsToHalf(info.Data[row * 16 + 2]);
            if (float.IsNaN(cr) || float.IsNaN(cg) || float.IsNaN(cb)) continue;
            if (cr + cg + cb < 0.02f) continue; // empty/black row
            r += Math.Clamp(cr, 0f, 1f); g += Math.Clamp(cg, 0f, 1f); b += Math.Clamp(cb, 0f, 1f);
            n++;
        }
        return n > 0 ? new[] { r / n, g / n, b / n } : null;
    }

    /// <summary>Pick the diffuse/base texture from a material's texture list — "_d.tex" preferred,
    /// else the first non-normal/non-mask entry.</summary>
    private static string? PickDiffuse(MtrlFile? mtrl)
    {
        if (mtrl == null) return null;
        var paths = new List<string>();
        foreach (var t in mtrl.TextureOffsets)
        {
            var end = Array.IndexOf(mtrl.Strings, (byte)0, t.Offset);
            if (end < 0) end = mtrl.Strings.Length;
            paths.Add(System.Text.Encoding.UTF8.GetString(mtrl.Strings, t.Offset, end - t.Offset));
        }
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
