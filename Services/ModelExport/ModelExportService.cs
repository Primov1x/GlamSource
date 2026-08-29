using System;
using System.Collections.Generic;
using System.Linq;
using GlamSource.Core;
using Lumina;
using Lumina.Data.Files;

namespace GlamSource.Services.ModelExport;

// ponytail: static bind-pose export, LOD0, equipment+accessories only (no body/face/hair, no
// weapons, no dye tint yet) — the vertices in .mdl are already in bind pose, so a skeleton is
// not needed for a still viewer. Race code fixed to c0101: most gear ships only that model and
// the game fits it to races at runtime via skeleton deforms we don't replicate anyway.
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
    public byte[]? BuildGlb(IReadOnlyList<EquipmentSlot> slots)
    {
        LastTrace.Clear();
        LastTrace.Add($"slots in: {slots.Count}");
        var items = slots
            .Select(s => (Slot: s.Slot, ItemId: s.GlamourItemId ?? s.ActualItemId, Stain: s.Stain0))
            .Where(x => x.ItemId > 0 && SlotInfo(x.Slot) != null)
            .ToList();
        LastTrace.Add($"usable items: {items.Count} [{string.Join(", ", items.Select(x => $"{x.Slot}:{x.ItemId}"))}]");
        if (items.Count == 0) return null;

        var key = string.Join(",", items.Select(x => $"{x.Slot}:{x.ItemId}:{x.Stain}"));
        if (_cache is { } c && c.Key == key) { LastTrace.Add("cache hit"); return c.Glb; }

        var meshInputs = new List<GltfMeshInput>();
        var pngs = new List<byte[]>();
        var texIndexByPath = new Dictionary<string, int>();

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
                if (m.MaterialIndex >= 0 && m.MaterialIndex < mdl.Materials.Length)
                    texIndex = ResolveDiffuseTexture(mdl.Materials[m.MaterialIndex], info, setId, materialId, pngs, texIndexByPath);
                meshInputs.Add(new GltfMeshInput(m, texIndex, tint));
            }
        }

        if (meshInputs.Count == 0) return null;
        var glb = GltfBuilder.BuildGlb(meshInputs, pngs);
        _cache = (key, glb);
        return glb;
    }

    private int ResolveDiffuseTexture(string mtrlName, (string Category, string Prefix, string Suffix, int ImcPart) info, ushort setId, byte materialId, List<byte[]> pngs, Dictionary<string, int> cache)
    {
        // mdl stores "/mt_....mtrl" — relative to the set's material variant folder
        var mtrlPath = mtrlName.StartsWith('/')
            ? $"chara/{info.Category}/{info.Prefix}{setId:D4}/material/v{materialId:D4}{mtrlName}"
            : mtrlName;
        if (cache.TryGetValue(mtrlPath, out var cached)) return cached;

        var result = -1;
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
                        result = pngs.Count;
                        pngs.Add(png);
                    }
                }
            }
        }
        catch { /* untextured fallback material */ }

        cache[mtrlPath] = result;
        return result;
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
        return paths.FirstOrDefault(p => p.EndsWith("_d.tex"))
            ?? paths.FirstOrDefault(p => !p.EndsWith("_n.tex") && !p.EndsWith("_s.tex") && !p.EndsWith("_m.tex") && p.EndsWith(".tex"));
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
