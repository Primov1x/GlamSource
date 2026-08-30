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

    /// <summary>The exact PNGs baked/embedded into the last BuildGlb call, in tex=N order matching
    /// LastTrace — inspect these directly (unlit, unshaded) instead of guessing from a lit 3D
    /// render or a lossy screenshot when a color looks wrong.</summary>
    public List<byte[]> LastTextures { get; } = new();

    /// <summary>Parallel to <see cref="LastTextures"/> — a short human-readable label (material path
    /// + what was baked) for each texture, so the raw-texture debug view doesn't leave the user
    /// cross-referencing "tex=N" against the trace by hand.</summary>
    public List<string> LastTextureLabels { get; } = new();

    /// <summary>Build a GLB containing all equipment models for the given slots. Returns null when
    /// nothing could be resolved.</summary>
    public byte[]? BuildGlb(IReadOnlyList<EquipmentSlot> slots, CharacterModelInfo? chara = null, bool bypassCache = false, SkeletonPose? pose = null, CustomizeColors? colors = null)
    {
        bypassCache = bypassCache || pose != null; // live pose changes every capture — never cache it
        LastTrace.Clear();
        LastTextures.Clear();
        LastTextureLabels.Clear();
        LastTrace.Add($"slots in: {slots.Count}, chara: {chara?.RaceCode ?? "none"}");
        if (pose != null)
        {
            LastTrace.Add("--- skeleton capture ---");
            LastTrace.AddRange(pose.DebugLog);
            LastTrace.Add("--- end skeleton capture ---");
        }
        LastTrace.Add(colors == null
            ? "customize colors: null (buffer not ready / degenerate read rejected — using flat 0.85/0.66/0.56 skin, 0.35/0.30/0.28 hair fallback)"
            : $"customize colors: skin={colors.Value.Skin[0]:F2},{colors.Value.Skin[1]:F2},{colors.Value.Skin[2]:F2} hair={colors.Value.Hair[0]:F2},{colors.Value.Hair[1]:F2},{colors.Value.Hair[2]:F2} src=[{colors.Value.DebugSource}]");
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
        var materialCache = new Dictionary<string, (int, float[]?, int, int)>();

        var itemSheet = _gameData.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var stainSheet = _gameData.GetExcelSheet<Lumina.Excel.Sheets.Stain>();
        ushort headSetId = 0;
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
            if (slot == GlamSource.Core.EquipmentSlotType.Head) headSetId = setId;

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
            string? usedRace = null;
            foreach (var rc in raceCandidates)
            {
                var candidate = $"chara/{info.Category}/{info.Prefix}{setId:D4}/model/{rc}{info.Prefix}{setId:D4}_{info.Suffix}.mdl";
                if (_gameData.FileExists(candidate)) { mdlPath = candidate; usedRace = rc; break; }
            }
            if (mdlPath == null) { LastTrace.Add($"{slot}:{itemId} missing (tried {string.Join(", ", raceCandidates)})"); continue; }

            // ponytail: REVERTED (0.0.0.126) — shipped in 0.0.0.125, made things actively worse
            // ("jetzt ist alles deformt", visible distortion across the whole outfit, not just the
            // arm gap) and didn't even close the original gap. The direct per-bone lookup (no
            // inheritance-walk, see PdbReader's doc comment) was too big a simplification of the
            // real algorithm to trust blind — needs real verification against known-good vertex
            // positions before trying again, not another guess under time pressure. Keeping
            // PdbReader/RacialDeform as infrastructure, just not wired live.
            IReadOnlyDictionary<string, System.Numerics.Matrix4x4>? racialDeforms = null;

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
            if (mdl.LodCount > 0)
            {
                var lod0 = mdl.Lods[0];
                var decl0 = mdl.VertexDeclarations[lod0.MeshIndex];
                LastTrace.Add($"  vtxdecl[0]: [{string.Join(",", decl0.VertexElements.Select(e => $"u{e.Usage}t{e.Type}s{e.Stream}"))}]");
            }
            foreach (var m in meshes)
            {
                if (m.SubmeshesKept < m.SubmeshesTotal)
                    LastTrace.Add($"  submeshes: {m.SubmeshesKept}/{m.SubmeshesTotal} kept, dropped attrs=[{string.Join(",", m.DroppedAttributes)}]");
                if (racialDeforms != null)
                {
                    RacialDeform.Apply(m, mdl.Bones, mdl.BoneTables, racialDeforms);
                    LastTrace.Add($"  racial deform applied ({usedRace}->{chara!.RaceCode}, {racialDeforms.Count} bone entries)");
                }
                if (pose != null)
                {
                    var stats = SkinApply.Apply(m, mdl.Bones, mdl.BoneTables, pose);
                    if (stats.Vertices > 0)
                        LastTrace.Add($"  skin: {stats.SkinnedVertices}/{stats.Vertices} verts, bones matched {stats.BoneRefsMatched}/{stats.BoneRefsTotal}, rejected {stats.RejectedVertices}, unmatched=[{string.Join(",", stats.UnmatchedBoneNames ?? [])}]");
                }
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
                    var (t, colorSetTint, normalTex, metalRoughTex) = ResolveMaterialByPath(mtrlPath, effectiveTint, pngs, materialCache);
                    texIndex = t;
                    // ponytail: when a real texture exists, ResolveMaterialByPath already baked
                    // effectiveTint into the PNG pixels directly (see its own comment for why —
                    // glTF baseColorFactor multiply was confirmed NOT visually applying in the
                    // viewer despite correct material JSON). Don't apply it a second time via
                    // baseColorFactor. Only the flat-no-texture fallback still needs it.
                    if (texIndex >= 0) effectiveTint = null;
                    else if (effectiveTint == null) effectiveTint = colorSetTint;
                    LastTrace.Add($"{slot}:{itemId} mesh mtrl={mtrlPath} tex={texIndex} normal={normalTex} colorSetTint={(colorSetTint == null ? "null" : $"{colorSetTint[0]:F2},{colorSetTint[1]:F2},{colorSetTint[2]:F2}")} stain={(tint == null ? "null" : $"{tint[0]:F2},{tint[1]:F2},{tint[2]:F2}")}");
                    meshInputs.Add(new GltfMeshInput(m, texIndex, effectiveTint, normalTex, metalRoughTex));
                    continue;
                }
                meshInputs.Add(new GltfMeshInput(m, texIndex, effectiveTint));
            }
        }

        if (chara != null)
        {
            var rc = chara.RaceCode;
            // body: skin shows where gear doesn't cover; textures use runtime-resolved "--" paths
            // we can't look up, so body/face fall back to a skin-tone tint when untextured. Prefer
            // the character's REAL live skin/hair color (read from the shader constant buffer —
            // see CustomizeColorsService) over the flat guessed approximation whenever we have it.
            var skinTint = colors?.Skin ?? new[] { 0.85f, 0.66f, 0.56f };
            var hairTint = colors?.Hair ?? new[] { 0.35f, 0.30f, 0.28f };
            LastTrace.Add($"fallback tints in use: skin={skinTint[0]:F2},{skinTint[1]:F2},{skinTint[2]:F2} hair={hairTint[0]:F2},{hairTint[1]:F2},{hairTint[2]:F2}");
            // ponytail: base body model id varies per race (Hyur etc = b0001, Viera male = b0002,
            // ...) — no full per-race table yet, just try the two ids confirmed to exist.
            var bodyId = _gameData.FileExists($"chara/human/{rc}/obj/body/b0001/model/{rc}b0001_top.mdl") ? "b0001" : "b0002";
            AddCharaPart($"chara/human/{rc}/obj/body/{bodyId}/model/{rc}{bodyId}_top.mdl", rc, $"body/{bodyId}", skinTint, meshInputs, pngs, materialCache, pose);
            // ponytail: face decal submeshes are named atr_fv_a..atr_fv_g — verified directly against
            // real game files (D:\FF\game via a standalone MDL-attribute probe, not guessed): 7
            // letters matching the 7 CustomizeIndex.FaceFeatures bits in alphabetical/bit order.
            // Without this, EVERY feature decal (moles/scars/tattoo lines) rendered overlaid at once
            // regardless of which the player actually toggled on ("das ist nicht mein Gesicht").
            // Hide whichever bit ISN'T set — a character with zero features toggled hides all 7.
            var faceHideAttrs = new List<string>();
            var featureBits = colors?.FaceFeatures ?? 0;
            for (var bit = 0; bit < 7; bit++)
                if ((featureBits & (1 << bit)) == 0)
                    faceHideAttrs.Add($"atr_fv_{(char)('a' + bit)}");
            AddCharaPart($"chara/human/{rc}/obj/face/f{chara.Face:D4}/model/{rc}f{chara.Face:D4}_fac.mdl", rc, $"face/f{chara.Face:D4}", skinTint, meshInputs, pngs, materialCache, pose, eyeTint: colors?.Eye, decalTint: colors?.Feature, hideAttributes: faceHideAttrs);
            // ponytail: the equipped hat's EQP flags decide what part of the hair renders — exactly
            // what the game does. HeadHideHair (full helmets) drops the hair part entirely;
            // HeadHideScalp hides only the "atr_kam" scalp/top-hair submeshes, which is what was
            // clipping through the cap's crown.
            var hairHidden = headSetId != 0 && EqpReader.HidesHair(_gameData, headSetId);
            var hairHideAttrs = headSetId != 0 ? EqpReader.HiddenHairAttributes(_gameData, headSetId) : Array.Empty<string>();
            if (hairHidden)
                LastTrace.Add($"hair hidden entirely by head gear e{headSetId:D4} (EQP HeadHideHair)");
            else
            {
                if (hairHideAttrs.Count > 0)
                    LastTrace.Add($"head gear e{headSetId:D4} EQP hides hair attrs: [{string.Join(",", hairHideAttrs)}]");
                AddCharaPart($"chara/human/{rc}/obj/hair/h{chara.Hair:D4}/model/{rc}h{chara.Hair:D4}_hir.mdl", rc, $"hair/h{chara.Hair:D4}", hairTint, meshInputs, pngs, materialCache, pose, colors?.Highlight, hairHideAttrs);
            }
            if (chara.TailOrEars > 0)
            {
                // tail (Miqo'te/Au Ra/Hrothgar) or ears (Viera) — whichever path exists
                AddCharaPart($"chara/human/{rc}/obj/tail/t{chara.TailOrEars:D4}/model/{rc}t{chara.TailOrEars:D4}_til.mdl", rc, $"tail/t{chara.TailOrEars:D4}", hairTint, meshInputs, pngs, materialCache, pose);
                AddCharaPart($"chara/human/{rc}/obj/zear/z{chara.TailOrEars:D4}/model/{rc}z{chara.TailOrEars:D4}_zer.mdl", rc, $"zear/z{chara.TailOrEars:D4}", skinTint, meshInputs, pngs, materialCache, pose, outerTint: hairTint);
            }
        }

        if (meshInputs.Count == 0) return null;
        LastTextures.AddRange(pngs);
        var glb = GltfBuilder.BuildGlb(meshInputs, pngs);
        _cache = (key, glb);
        return glb;
    }

    /// <summary>Load one character base-model part (body/face/hair/tail/ears); silently skipped
    /// when the path doesn't exist for this race. Untextured meshes get the fallback tint.</summary>
    private void AddCharaPart(string mdlPath, string raceCode, string partFolder, float[] fallbackTint,
        List<GltfMeshInput> meshInputs, List<byte[]> pngs, Dictionary<string, (int, float[]?, int, int)> materialCache, SkeletonPose? pose,
        float[]? highlightTint = null, IReadOnlyCollection<string>? hideAttributes = null, float[]? eyeTint = null, float[]? decalTint = null, float[]? outerTint = null)
    {
        if (!_gameData.FileExists(mdlPath)) { LastTrace.Add($"chara part missing: {mdlPath}"); return; }
        var raw = _gameData.GetFile(mdlPath);
        if (raw == null) return;
        Penumbra.GameData.Files.MdlFile mdl;
        try { mdl = new Penumbra.GameData.Files.MdlFile(raw.Data); }
        catch (Exception ex) { LastTrace.Add($"chara part parse {mdlPath}: {ex.Message}"); return; }
        if (!mdl.Valid) { LastTrace.Add($"chara part invalid: {mdlPath}"); return; }

        var meshes = MdlGeometry.DecodeLod0(mdl, hideAttributes);
        LastTrace.Add($"chara part {partFolder}: {meshes.Count} meshes");

        // ponytail: two passes — some chara-part submeshes (e.g. face "etc_a/b/c": eyebrows/lash/
        // decal detail layers) have no diffuse texture AND no colorset, only norm+mask (the game
        // derives their look from the mask via a shader we don't replicate). Solid-filling those
        // with the flat skin tint renders them as opaque blobs over the eyes/mouth — visibly wrong,
        // confirmed by a screenshot showing exactly that. Once ANY submesh in this part has a real
        // texture/colorset (e.g. the face's "fac" material), skip the sourceless ones instead of
        // opaque-filling them. If NOTHING in the part has a real source (e.g. hair, which has no
        // diffuse/colorset at all), keep the flat fallback — no hair is worse than flat-color hair.
        var pending = new List<(GltfMeshInput Mesh, bool HadRealSource)>();
        foreach (var m in meshes)
        {
            if (m.SubmeshesKept < m.SubmeshesTotal)
                LastTrace.Add($"  submeshes: {m.SubmeshesKept}/{m.SubmeshesTotal} kept, dropped attrs=[{string.Join(",", m.DroppedAttributes)}]");
            if (pose != null)
            {
                var stats = SkinApply.Apply(m, mdl.Bones, mdl.BoneTables, pose);
                if (stats.Vertices > 0)
                    LastTrace.Add($"  skin: {stats.SkinnedVertices}/{stats.Vertices} verts, bones matched {stats.BoneRefsMatched}/{stats.BoneRefsTotal}, rejected {stats.RejectedVertices}, unmatched=[{string.Join(",", stats.UnmatchedBoneNames ?? [])}]");
            }
            var texIndex = -1;
            float[]? tint = fallbackTint;
            var hadRealSource = false;
            if (m.MaterialIndex >= 0 && m.MaterialIndex < mdl.Materials.Length)
            {
                var mtrlName = mdl.Materials[m.MaterialIndex];
                // ponytail: the iris material's base texture is neutral grayscale too — needs the
                // character's actual eye color, not the skin tint every other face material uses.
                if (eyeTint != null && mtrlName.Contains("_iri_")) tint = eyeTint;
                // ponytail: Viera ear outer shell is hair-colored (black fur in the real screenshot
                // comparison), not skin-colored — only the inner "_fac_" strip is skin tone. Was
                // using skinTint for the whole ear part, tinting the outer shell wrong.
                if (outerTint != null && !mtrlName.Contains("_fac_")) tint = outerTint;
                // ponytail: some non-Hyur body models (e.g. Viera b0002) reference the shared
                // Hyur skin material by name (skin_mask.tex etc. is generic across races) instead
                // of their own race's folder — same misroute the equipment loop already handles,
                // route those to c0101's body folder instead of 404ing under this race/part.
                var bodyRefMatch = System.Text.RegularExpressions.Regex.Match(mtrlName, @"^/mt_c(\d{4})b(\d{4})_");
                // ponytail: body/hair materials sit under a v0001 variant folder, face materials
                // don't (verified against real files) — try with it first, fall back without.
                var mtrlPathVariant = $"chara/human/{raceCode}/obj/{partFolder}/material/v0001{mtrlName}";
                var mtrlPathFlat = $"chara/human/{raceCode}/obj/{partFolder}/material{mtrlName}";
                var mtrlPath = bodyRefMatch.Success
                    ? $"chara/human/c{bodyRefMatch.Groups[1].Value}/obj/body/b{bodyRefMatch.Groups[2].Value}/material/v0001{mtrlName}"
                    : mtrlName.StartsWith('/')
                        ? (_gameData.FileExists(mtrlPathVariant) ? mtrlPathVariant : mtrlPathFlat)
                        : mtrlName;
                // ponytail: skin/hair "_base"/"_hir" textures are a neutral grounding layer, not the
                // final color — the game multiplies them by the character's actual skin/hair color
                // (set via CustomizeParameter). Always tint, even when a texture was found — a bare
                // base.tex renders as dull blue-gray, not skin tone. Baked directly into the PNG
                // pixels (see ResolveMaterialByPath) — glTF baseColorFactor multiply was confirmed
                // NOT visually applying in the viewer, so don't also rely on it here.
                var (t, colorSetTint, normalTex, metalRoughTex) = ResolveMaterialByPath(mtrlPath, tint, pngs, materialCache, highlightTint,
                    decalTint: mtrlName.Contains("_etc_") ? decalTint : null);
                texIndex = t;
                hadRealSource = texIndex >= 0 || colorSetTint != null;
                if (texIndex >= 0) tint = null;
                else tint = colorSetTint ?? fallbackTint;
                LastTrace.Add($"  {partFolder} mtrl={mtrlPath} tex={texIndex} normal={normalTex} finalTint={(tint == null ? "null" : $"{tint[0]:F2},{tint[1]:F2},{tint[2]:F2}")}");
                // ponytail: ear outer shell is hair-tinted (see outerTint above) but was still
                // tagged role="skin" — the skin material's warm peachy sheen dominated on the thin,
                // mostly-edge-on ear geometry, reading as brownish even though the base color was
                // correctly near-black. Tag it "hair" like its actual tint source.
                var isEarShell = partFolder.StartsWith("zear/", StringComparison.Ordinal) && !mtrlName.Contains("_fac_");
                var role = mtrlName.Contains("_iri_") ? "eye"
                    : partFolder.StartsWith("hair/", StringComparison.Ordinal) || isEarShell ? "hair"
                    : partFolder.StartsWith("body/", StringComparison.Ordinal) || partFolder.StartsWith("face/", StringComparison.Ordinal) || partFolder.StartsWith("zear/", StringComparison.Ordinal) ? "skin"
                    : null;
                pending.Add((new GltfMeshInput(m, texIndex, tint, normalTex, metalRoughTex, role), hadRealSource));
                continue;
            }
            pending.Add((new GltfMeshInput(m, texIndex, tint), hadRealSource));
        }

        // ponytail: scoped to "face/" only — the zear (Viera ear) part has the SAME sourceless-
        // material shape (fac_a has a real texture, the outer ear shell "z0004_a" doesn't) but there
        // the sourceless submesh is real structural ear geometry, not a decal overlay. Dropping it
        // there removed the outer ear entirely ("nur das innere ist da" — confirmed by screenshot).
        // Only face's etc_a/b/c are genuinely optional decal layers.
        var anyRealSource = pending.Exists(p => p.HadRealSource);
        var dropSourceless = anyRealSource && partFolder.StartsWith("face/", StringComparison.Ordinal);
        foreach (var p in pending)
        {
            if (!p.HadRealSource && dropSourceless)
            {
                LastTrace.Add($"  {partFolder}: dropped sourceless decal submesh (other submeshes have real texture/colorset)");
                continue;
            }
            meshInputs.Add(p.Mesh);
        }
    }

    /// <summary>Resolves one material to (texture index, flat tint for the no-texture case, normal
    /// map index). When a real texture is produced (picked diffuse or baked colorset) and
    /// <paramref name="tint"/> is given, the tint is multiplied directly into the PNG's pixels
    /// before embedding — NOT left to glTF's baseColorFactor. Confirmed by direct test (a black-
    /// dyed jacket, a tinted skin texture) that baseColorFactor's multiply was not visually taking
    /// effect in the viewer despite correct material JSON; baking it into the texture sidesteps
    /// whatever in the load/render path was dropping it. Cache key includes the tint since the same
    /// material path can be requested with a different tint (different stain, different chara-part
    /// skin/hair tint).</summary>
    private (int Tex, float[]? Tint, int NormalTex, int MetalRoughTex) ResolveMaterialByPath(string mtrlPath, float[]? tint, List<byte[]> pngs, Dictionary<string, (int, float[]?, int, int)> cache, float[]? highlightTint = null, float[]? decalTint = null)
    {
        var cacheKey = tint == null ? mtrlPath : $"{mtrlPath}|{tint[0]:F3},{tint[1]:F3},{tint[2]:F3}";
        if (highlightTint != null) cacheKey += $"|hl{highlightTint[0]:F3},{highlightTint[1]:F3},{highlightTint[2]:F3}";
        if (decalTint != null) cacheKey += $"|dc{decalTint[0]:F3},{decalTint[1]:F3},{decalTint[2]:F3}";
        if (cache.TryGetValue(cacheKey, out var cached)) return cached;
        (int, float[]?, int, int) result = (-1, null, -1, -1);
        try
        {
            if (!_gameData.FileExists(mtrlPath)) { LastTrace.Add($"  mtrl missing: {mtrlPath}"); cache[cacheKey] = result; return result; }


            var mtrlRaw = _gameData.GetFile(mtrlPath);
            var mtrl = _gameData.GetFile<MtrlFile>(mtrlPath);
            var texPath = PickDiffuse(mtrl);
            LastTrace.Add($"  mtrl={mtrlPath} allTex=[{string.Join(", ", MaterialTexturePaths(mtrl))}] picked={texPath ?? "none"}");
            var texIndex = -1;
            float[]? colorSetTint = null;
            if (texPath != null && _gameData.FileExists(texPath))
            {
                var tex = _gameData.GetFile<TexFile>(texPath);
                if (tex != null)
                {
                    // Lumina decodes to BGRA (byte0=B, byte2=R — the exact channel-order quirk the
                    // colorset bake already compensates for, see MaterialColorTable's header), but
                    // this picked-texture path was feeding ImageData straight into the RGBA PNG
                    // encoder with R and B swapped. THAT was the "blue-gray skin" from day one:
                    // base.tex is verified to be a warm tan (156,126,110) in real data — swapped it
                    // renders as (110,126,156), the blue-gray every screenshot showed. Colorset-baked
                    // gear was unaffected (compensated); every picked texture (skin, face, eyes,
                    // ears, hair accessory) was swapped.
                    var pixels = BgraToRgba(tex.ImageData);
                    if (tint != null) pixels = ApplyTint(pixels, tint);
                    var png = PngEncoder.EncodeRgba(pixels, tex.Header.Width, tex.Header.Height);
                    texIndex = pngs.Count;
                    pngs.Add(png);
                    LastTextureLabels.Add($"{mtrlPath} (picked diffuse: {texPath})");
                }
                else LastTrace.Add($"  tex GetFile null: {texPath}");
            }
            // Dawntrail-era gear commonly has no diffuse texture at all — color lives in the
            // material's color table, and WHICH row applies per-pixel comes from the id texture
            // (Red = ramp position, Green = A/B blend — see MaterialColorTable.BakeDiffuse). Bake
            // a real diffuse texture when we have an id map; a flat colorset average otherwise.
            var metalRoughIndex = -1;
            if (texIndex < 0 && mtrlRaw != null)
            {
                var idPath = MaterialTexturePaths(mtrl).FirstOrDefault(p => p.EndsWith("_id.tex"));
                byte[]? baked = null;
                if (idPath != null && _gameData.FileExists(idPath))
                {
                    var idTex = _gameData.GetFile<TexFile>(idPath);
                    if (idTex != null)
                        // ponytail: stain passed straight in — BakeDiffuse blends it in only on
                        // rows the material's own dye table actually flags as dyeable (see its doc
                        // comment), not the whole texture. A blanket multiply here previously wiped
                        // out accent colors the game itself never recolors.
                        baked = Penumbra.GameData.Files.MaterialColorTable.BakeDiffuse(mtrlRaw.Data, idTex.ImageData, idTex.Header.Width, idTex.Header.Height, tint);
                    if (baked != null)
                    {
                        // ponytail: fine surface detail (emblems, panel-line shadowing — "das
                        // silberne X fehlt", confirmed absent from the raw id-map bake itself) comes
                        // from an AO pass baked into the mask texture, not the color table — verified
                        // against Penumbra's own exporter (MaterialExporter.cs, BuildCharacter:
                        // "MultiplyOperation.Execute(baseColor, maskOperation.Occlusion)", Occlusion
                        // = mask.B). Multiply it in the same way, nearest-sampled since mask and id
                        // textures aren't always the same resolution.
                        var maskPath = MaterialTexturePaths(mtrl).FirstOrDefault(p => p.EndsWith("_mask.tex"));
                        if (maskPath != null && _gameData.FileExists(maskPath))
                        {
                            var maskTex = _gameData.GetFile<TexFile>(maskPath);
                            if (maskTex != null)
                                ApplyMaskOcclusion(baked, idTex!.Header.Width, idTex.Header.Height, maskTex.ImageData, maskTex.Header.Width, maskTex.Header.Height);
                        }
                        var png = PngEncoder.EncodeRgba(baked, idTex!.Header.Width, idTex.Header.Height);
                        texIndex = pngs.Count;
                        pngs.Add(png);
                        LastTextureLabels.Add($"{mtrlPath} (colorset baked via {idPath})");
                        LastTrace.Add($"  colorset BAKED via {idPath} ({idTex.Header.Width}x{idTex.Header.Height})");
                        // metal trim/buckles have real per-row Metalness/Roughness in the color table
                        // that was never read before (see MaterialColorTable.BakeMetallicRoughness).
                        var metalRough = Penumbra.GameData.Files.MaterialColorTable.BakeMetallicRoughness(mtrlRaw.Data, idTex.ImageData, idTex.Header.Width, idTex.Header.Height);
                        if (metalRough != null)
                        {
                            var mrPng = PngEncoder.EncodeRgba(metalRough, idTex.Header.Width, idTex.Header.Height);
                            metalRoughIndex = pngs.Count;
                            pngs.Add(mrPng);
                            LastTextureLabels.Add($"{mtrlPath} (metal/roughness)");
                        }
                    }
                }
                // hair.shpk has no colorset — ported directly from Penumbra's own exporter
                // (Import/Models/Export/MaterialExporter.cs, BuildHair): strand color is
                // Lerp(mainTint, highlightTint, normal.B/255), cutout alpha is normal.A, and the
                // result is multiplied by mask.A/255. Confirmed via a format trace that hair's
                // norm.tex/mask.tex are BC7 (real 4-channel data, Lumina fully decodes it — unlike
                // BC5 body/face normal maps, no X/Y-remap-plus-reconstructed-Z here), so these are
                // genuine stored channels, not guessed ones.
                if (baked == null && highlightTint != null)
                {
                    var maskPath = MaterialTexturePaths(mtrl).FirstOrDefault(p => p.EndsWith("_mask.tex"));
                    var hairNormPath = MaterialTexturePaths(mtrl).FirstOrDefault(p => p.EndsWith("_norm.tex"));
                    if (maskPath != null && _gameData.FileExists(maskPath) && hairNormPath != null && _gameData.FileExists(hairNormPath) && tint != null)
                    {
                        var maskTex = _gameData.GetFile<TexFile>(maskPath);
                        var hairNormTex = _gameData.GetFile<TexFile>(hairNormPath);
                        if (maskTex != null && hairNormTex != null)
                        {
                            baked = BakeHairMask(hairNormTex.ImageData, hairNormTex.Header.Width, hairNormTex.Header.Height,
                                maskTex.ImageData, maskTex.Header.Width, maskTex.Header.Height, tint, highlightTint);
                            if (baked != null)
                            {
                                var png = PngEncoder.EncodeRgba(baked, hairNormTex.Header.Width, hairNormTex.Header.Height);
                                texIndex = pngs.Count;
                                pngs.Add(png);
                                LastTextureLabels.Add($"{mtrlPath} (hair strand bake)");
                                LastTrace.Add($"  hair strand BAKED via {hairNormPath}+{maskPath} ({hairNormTex.Header.Width}x{hairNormTex.Header.Height})");
                            }
                        }
                    }
                }
                // face decal layers (eyebrows/lashes — "etc_a"/"etc_b", no diffuse/colorset, only
                // norm+mask) were previously always dropped as "nothing to show" ("etc.mtrl... only
                // norm+mask, the game derives their look from the mask via a shader we don't
                // replicate" — turned out true for hair too, and the hair fix (normal.A = cutout
                // alpha) generalizes: tint everything the normal map's alpha marks as covered with
                // decalTint (hair color — eyebrows/lashes are hair-colored in FFXIV), instead of
                // dropping. Diagnostic format trace included since this is an optimistic port, not
                // independently verified for THIS material family yet.
                if (baked == null && decalTint != null)
                {
                    var decalNormPath = MaterialTexturePaths(mtrl).FirstOrDefault(p => p.EndsWith("_norm.tex"));
                    if (decalNormPath != null && _gameData.FileExists(decalNormPath))
                    {
                        var decalNormTex = _gameData.GetFile<TexFile>(decalNormPath);
                        if (decalNormTex != null)
                        {
                            LastTrace.Add($"  decal norm tex format: {decalNormTex.Header.Format}");
                            baked = BakeDecalAlphaCutout(decalNormTex.ImageData, decalNormTex.Header.Width, decalNormTex.Header.Height, decalTint);
                            if (baked != null)
                            {
                                var png = PngEncoder.EncodeRgba(baked, decalNormTex.Header.Width, decalNormTex.Header.Height);
                                texIndex = pngs.Count;
                                pngs.Add(png);
                                LastTextureLabels.Add($"{mtrlPath} (decal bake)");
                                LastTrace.Add($"  decal BAKED via {decalNormPath} ({decalNormTex.Header.Width}x{decalNormTex.Header.Height})");
                            }
                        }
                    }
                }
                // structural geometry with no diffuse/colorset/decal source (e.g. the Viera outer
                // ear shell — real always-visible shape, not a toggleable decal) still has a mask
                // texture. A flat single-color fill ("Ohren fixen" — read as flat plastic, no
                // surface detail) loses all the shading the mask's occlusion channel would give it.
                // Bake tint * mask.B (same AO convention as ApplyMaskOcclusion) as a real texture,
                // fully opaque — no cutout, this is solid geometry.
                if (baked == null && tint is { Length: 3 })
                {
                    var occMaskPath = MaterialTexturePaths(mtrl).FirstOrDefault(p => p.EndsWith("_mask.tex"));
                    if (occMaskPath != null && _gameData.FileExists(occMaskPath))
                    {
                        var occMaskTex = _gameData.GetFile<TexFile>(occMaskPath);
                        if (occMaskTex != null)
                        {
                            baked = BakeTintWithOcclusion(occMaskTex.ImageData, occMaskTex.Header.Width, occMaskTex.Header.Height, tint);
                            var png = PngEncoder.EncodeRgba(baked, occMaskTex.Header.Width, occMaskTex.Header.Height);
                            texIndex = pngs.Count;
                            pngs.Add(png);
                            LastTextureLabels.Add($"{mtrlPath} (tint+occlusion bake)");
                            LastTrace.Add($"  tint+occlusion BAKED via {occMaskPath} ({occMaskTex.Header.Width}x{occMaskTex.Header.Height})");
                        }
                    }
                }
                if (baked == null)
                {
                    colorSetTint = Penumbra.GameData.Files.MaterialColorTable.AverageDiffuse(mtrlRaw.Data);
                    LastTrace.Add($"  colorset tint (no id map): {(colorSetTint == null ? "null" : $"{colorSetTint[0]:F2},{colorSetTint[1]:F2},{colorSetTint[2]:F2}")}");
                }
            }

            // normal map: BC5 (X/Y only, Z reconstructed) — see NormalMapDecoder. Suffix varies by
            // asset family ("_n.tex" on some equipment, "_norm.tex" on chara parts/other gear).
            var normalIndex = -1;
            var normalPath = MaterialTexturePaths(mtrl).FirstOrDefault(p => p.EndsWith("_n.tex"))
                ?? MaterialTexturePaths(mtrl).FirstOrDefault(p => p.EndsWith("_norm.tex"));
            if (normalPath != null && _gameData.FileExists(normalPath))
            {
                var nTex = _gameData.GetFile<TexFile>(normalPath);
                if (nTex != null)
                {
                    var decoded = NormalMapDecoder.DecodeFromBc5(nTex.ImageData, nTex.Header.Width, nTex.Header.Height);
                    var png = PngEncoder.EncodeRgba(decoded, nTex.Header.Width, nTex.Header.Height);
                    normalIndex = pngs.Count;
                    pngs.Add(png);
                    LastTextureLabels.Add($"{mtrlPath} (normal map: {normalPath})");
                }
            }

            result = (texIndex, colorSetTint, normalIndex, metalRoughIndex);
        }
        catch (Exception ex) { LastTrace.Add($"  mtrl exception: {ex.Message}"); }
        cache[cacheKey] = result;
        return result;
    }

    /// <summary>Swap Lumina's decoded BGRA byte order into the RGBA the PNG encoder expects.
    /// Returns a new array — never mutates Lumina's cached TexFile.ImageData.</summary>
    private static byte[] BgraToRgba(byte[] bgra)
    {
        var result = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            result[i] = bgra[i + 2];
            result[i + 1] = bgra[i + 1];
            result[i + 2] = bgra[i];
            result[i + 3] = bgra[i + 3];
        }
        return result;
    }

    /// <summary>Multiply <paramref name="rgba"/>'s RGB in-place by a mask texture's Blue channel
    /// (Penumbra's "Occlusion" — see caller). maskBgra is Lumina's raw undecoded bytes: byte 0 is
    /// Blue in that layout (same convention BgraToRgba compensates for on diffuse textures), so no
    /// swap is needed to read it directly. Nearest-neighbor sampled since mask and id/diffuse
    /// textures aren't always the same resolution.</summary>
    /// <summary>Bake a flat tint into a real texture, multiplied by the mask's occlusion channel
    /// (byte 0 — see <see cref="ApplyMaskOcclusion"/>) — for structural geometry with no diffuse or
    /// colorset (e.g. the Viera outer ear shell) that would otherwise render as one flat, detail-
    /// less color. Fully opaque; this is solid geometry, not a cutout.</summary>
    private static byte[] BakeTintWithOcclusion(byte[] maskBgra, int width, int height, float[] tint)
    {
        var result = new byte[width * height * 4];
        for (var p = 0; p < width * height; p++)
        {
            var o = p * 4;
            var occlusion = maskBgra[o] / 255f;
            result[o + 0] = (byte)Math.Clamp(tint[0] * occlusion * 255f, 0f, 255f);
            result[o + 1] = (byte)Math.Clamp(tint[1] * occlusion * 255f, 0f, 255f);
            result[o + 2] = (byte)Math.Clamp(tint[2] * occlusion * 255f, 0f, 255f);
            result[o + 3] = 255;
        }
        return result;
    }

    private static void ApplyMaskOcclusion(byte[] rgba, int width, int height, byte[] maskBgra, int maskWidth, int maskHeight)
    {
        if (maskWidth <= 0 || maskHeight <= 0) return;
        for (var y = 0; y < height; y++)
        {
            var my = Math.Min(maskHeight - 1, y * maskHeight / height);
            for (var x = 0; x < width; x++)
            {
                var mx = Math.Min(maskWidth - 1, x * maskWidth / width);
                var maskOffset = (my * maskWidth + mx) * 4;
                if (maskOffset + 0 >= maskBgra.Length) continue;
                var occlusion = maskBgra[maskOffset] / 255f;
                var o = (y * width + x) * 4;
                rgba[o] = (byte)(rgba[o] * occlusion);
                rgba[o + 1] = (byte)(rgba[o + 1] * occlusion);
                rgba[o + 2] = (byte)(rgba[o + 2] * occlusion);
            }
        }
    }

    /// <summary>Multiply an RGBA8 pixel buffer's RGB by a tint (0-1 each); alpha untouched. Returns
    /// a new array — never mutates the source (Lumina's decoded TexFile.ImageData may be reused).</summary>
    private static byte[] ApplyTint(byte[] rgba, float[] tint)
    {
        var result = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            result[i] = (byte)(rgba[i] * tint[0]);
            result[i + 1] = (byte)(rgba[i + 1] * tint[1]);
            result[i + 2] = (byte)(rgba[i + 2] * tint[2]);
            result[i + 3] = rgba[i + 3];
        }
        return result;
    }

    /// <summary>Flat-tint an alpha-cutout decal (face etc_a/etc_b — eyebrows/lashes) using the same
    /// normal.A-as-cutout-shape convention verified for hair (<see cref="BakeHairMask"/>). No weight
    /// blend — just solid tint everywhere the shape covers.</summary>
    private static byte[] BakeDecalAlphaCutout(byte[] normalRgba, int width, int height, float[] tint)
    {
        var result = new byte[width * height * 4];
        var r = (byte)Math.Clamp(tint[0] * 255f, 0f, 255f);
        var g = (byte)Math.Clamp(tint[1] * 255f, 0f, 255f);
        var b = (byte)Math.Clamp(tint[2] * 255f, 0f, 255f);
        for (var p = 0; p < width * height; p++)
        {
            var o = p * 4;
            result[o + 0] = r;
            result[o + 1] = g;
            result[o + 2] = b;
            result[o + 3] = normalRgba[o + 3];
        }
        return result;
    }

    /// <summary>Hair strand coloring, ported directly from Penumbra's own exporter
    /// (Import/Models/Export/MaterialExporter.cs, BuildHair — real production code, not a guess):
    /// <c>color = Lerp(mainTint, highlightTint, normal.B/255)</c>, multiplied by
    /// <c>mask.A/255</c>, with <c>alpha = normal.A</c> for the strand cutout shape. Output is at the
    /// normal texture's resolution; mask is nearest-sampled since the two can differ in size.</summary>
    private static byte[] BakeHairMask(byte[] normalRgba, int width, int height, byte[] maskRgba, int maskWidth, int maskHeight, float[] mainTint, float[] highlightTint)
    {
        if (maskWidth <= 0 || maskHeight <= 0) return normalRgba.Length == 0 ? [] : new byte[width * height * 4];
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var my = Math.Min(maskHeight - 1, y * maskHeight / height);
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                var weight = normalRgba[o + 2] / 255f; // normal.B
                var mx = Math.Min(maskWidth - 1, x * maskWidth / width);
                var maskAlpha = maskRgba[(my * maskWidth + mx) * 4 + 3] / 255f; // mask.A
                result[o + 0] = (byte)Math.Clamp((mainTint[0] + (highlightTint[0] - mainTint[0]) * weight) * maskAlpha * 255f, 0f, 255f);
                result[o + 1] = (byte)Math.Clamp((mainTint[1] + (highlightTint[1] - mainTint[1]) * weight) * maskAlpha * 255f, 0f, 255f);
                result[o + 2] = (byte)Math.Clamp((mainTint[2] + (highlightTint[2] - mainTint[2]) * weight) * maskAlpha * 255f, 0f, 255f);
                result[o + 3] = normalRgba[o + 3]; // normal.A — real strand cutout shape
            }
        }
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
