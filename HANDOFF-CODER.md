# Handoff für lokalen Coder — GlamSource Fixes

**Repo**: `C:\Users\t.fritzen\Projects\Private\GlamSource`
**Build**: `dotnet build` aus Repo-Root. Zero warnings policy.
**Deploy** (nach build): `bin/x64/Debug/*.dll` + `GlamSource.json` nach `C:/Users/t.fritzen/AppData/Roaming/XIVLauncher/devPlugins/GlamSource/`

## Hard Rules
- Alles sequenziell, Hauptthread. Keine parallelen Tasks.
- Nie `ImGui.SetWindowFontScale` oder `IO.FontGlobalScale`.
- Sizes via `ImGui.GetFontSize()`, keine hardcoded Pixel.
- Chirurgische Edits. Nichts adjacent "verbessern".
- `// ponytail:` Comment für deliberate simplifications.
- Nicht editieren: `DalaMock-15/`, `reference/`, `node_modules/`, `dist/`.
- Wrap Lumina Sheet-Lookups in try/catch für `Lumina.Excel.Exceptions.MismatchedColumnHashException`.
- Alle `IDisposable` in `Plugin.Dispose()`.
- Framework-thread für Game state (`Framework.RunOnFrameworkThread`).

---

## Fix 1: "Unknown Vendor" Bug (z.B. Curtana Zenith, ItemID 7828)

**Root Cause**: `BuildShopNpcCache` in `GlamSource.Core/ItemDetailService.cs` (Zeilen 747-926) bindet NPC an raw `ENpcData[]`-RowIds. Real Shop-IDs oft eingepackt in PreHandler/TopicSelect Wrapper. Type-Code steckt in `(rowId >> 16)`:

| Type | Code |
|---|---|
| GilShop | 0x0004 |
| CustomTalk | 0x000B |
| GcShop | 0x0016 |
| SpecialShop | 0x001B |
| FcShop | 0x002A |
| TopicSelect | 0x0032 |
| PreHandler | 0x0036 |
| InclusionShop | 0x003a |
| CollectablesShop | 0x003B |

### Aktion

Neuer privater Helper in `ItemDetailService.cs`:

```csharp
private void RegisterShopBinding(uint rowId, NpcLocationInfo info, HashSet<uint> visited)
{
    if (!visited.Add(rowId)) return;
    var typeCode = rowId >> 16;
    var localId = rowId & 0xFFFF;

    try
    {
        if (typeCode == 0x0036) // PreHandler
        {
            var sheet = _dataManager.GetExcelSheet<PreHandler>();
            var row = sheet?.GetRowOrDefault(localId);
            if (row.HasValue)
                RegisterShopBinding(row.Value.Target.RowId, info, visited);
            return;
        }
        if (typeCode == 0x0032) // TopicSelect
        {
            var sheet = _dataManager.GetExcelSheet<TopicSelect>();
            var row = sheet?.GetRowOrDefault(localId);
            if (row.HasValue)
            {
                foreach (var shopRef in row.Value.Shop)
                    RegisterShopBinding(shopRef.RowId, info, visited);
            }
            return;
        }
    }
    catch (Lumina.Excel.Exceptions.MismatchedColumnHashException)
    {
        // ponytail: schema drift, skip binding for this row
        return;
    }

    // Terminal: SpecialShop / GilShop / GcShop / InclusionShop / FcShop / CollectablesShop
    if (!_shopNpcLookup.TryGetValue(rowId, out var list))
    {
        list = new List<NpcLocationInfo>();
        _shopNpcLookup[rowId] = list;
    }
    list.Add(info);
    _shopNpcNameOnly[rowId] = info.NpcName;
}
```

### Replace-Sites in `BuildShopNpcCache`

Alle 4 Stellen, die raw `_shopNpcLookup[shopId].Add(...)` machen, ersetzen durch:
```csharp
RegisterShopBinding(shopId, info, new HashSet<uint>());
```

Ungefähre Zeilen (in Datei prüfen, evtl. verschoben):
- ~812-822 (supplemental path)
- ~834-844 (main Level path)
- ~889-897 (LGB fallback)
- ~920-923 (name-only Stage 3 fallback)

### Referenz-Pattern

`C:\Users\t.fritzen\Projects\Private\ItemVendorLocation\ItemVendorLocation\ItemLookup.AddItem.cs` Zeilen 146-208 (`AddItemsInPrehandler`, `AddItemsInTopicSelect`, `MatchEventHandlerType`).

---

## Fix 2: 4 Fehlende Provider (MogStation/Retainer/Airship/Submarine)

Template existiert schon in `GlamSource.Core/LuminaItemSourceService.cs`. Muster übernehmen nach `ItemDetailService.cs`.

### Fields (nach Zeile ~103)

```csharp
private readonly HashSet<uint> _storeItemIds = new();
private readonly Dictionary<uint, List<uint>> _itemToRetainerTaskMap = new();
private readonly Dictionary<uint, List<uint>> _itemToAirshipPointMap = new();
private readonly Dictionary<uint, List<uint>> _itemToSubmarineExplorationMap = new();
```

### SafeBuild-Calls im Ctor

```csharp
SafeBuild(BuildStoreItemCache);
SafeBuild(BuildRetainerVentureCache);
SafeBuild(BuildAirshipDropCache);
SafeBuild(BuildSubmarineDropCache);
```

### Emission-Blocks in `BuildSources`

Nach Zeile ~487, vor generic fallback ~489 einfügen. Minimal `ItemSourceDetail`, alles außer Type + Description null:

```csharp
if (_storeItemIds.Contains(itemId))
    results.Add(new ItemSourceDetail { Type = ItemSourceType.MogStation, Description = "Mog Station" });

if (_itemToRetainerTaskMap.ContainsKey(itemId))
    results.Add(new ItemSourceDetail { Type = ItemSourceType.Retainer, Description = "Retainer Venture" });

if (_itemToAirshipPointMap.ContainsKey(itemId))
    results.Add(new ItemSourceDetail { Type = ItemSourceType.Airship, Description = "Airship Voyage" });

if (_itemToSubmarineExplorationMap.ContainsKey(itemId))
    results.Add(new ItemSourceDetail { Type = ItemSourceType.Submarine, Description = "Submarine Voyage" });
```

### Build*Cache Methoden

1:1 aus `LuminaItemSourceService.cs` Zeilen 400-454 nach `ItemDetailService.cs` kopieren:
- `BuildStoreItemCache` → `CsvLoader.LoadResource<StoreItem>(CsvLoader.StoreItemResourceName)`
- `BuildRetainerVentureCache` → `LoadResource<RetainerVentureItem>(CsvLoader.RetainerVentureItemResourceName)`
- `BuildAirshipDropCache` → `LoadResource<AirshipDrop>(CsvLoader.AirshipDropResourceName)`
- `BuildSubmarineDropCache` → `LoadResource<SubmarineDrop>(CsvLoader.SubmarineDropResourceName)`

### ItemSourceType enum

Prüfen ob `MogStation`, `Retainer`, `Airship`, `Submarine` schon existieren. Wenn nicht, im selben File wie andere Werte ergänzen. Grep: `enum ItemSourceType`.

---

## Fix 3: MockMainWindow Redesign (ingame-Look)

File: `GlamSource.Mock/MockMainWindow.cs`. Aktuell 5-Spalten-Tabelle. User will ingame-Layout:

- Item header row: `Slot | Item Name` (klein)
- Section `"SOURCES"` per `ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "SOURCES")` + `ImGui.Separator()`
- Pro Source: farbiges Pill (`GetSourceColor` behalten) + Shop/Vendor Name + NPC + Cost items Liste
- Buttons für Gathering (wenn Coords vorhanden) → sonst weglassen

Click-through zu `_itemDetailWindow.ShowItem()` behalten. Sizes über `ImGui.GetFontSize()`.

**Minimal-Version**: Bestehende Tabelle behalten, aber Source-Spalte umbauen zu vertikalem Block pro Source (Pill + Name + Cost-Items untereinander statt inline).

---

## Verify Checklist

1. `dotnet build` aus Repo-Root — exit 0, **zero warnings**.
2. DLLs kopieren: `cp -v bin/x64/Debug/*.dll bin/x64/Debug/GlamSource.json "$APPDATA/XIVLauncher/devPlugins/GlamSource/"`.
3. Report: welche Files geändert + Build-Status + Warning-Count.

## Test-IDs (nach Deploy ingame)

| ID | Kategorie | Erwartung |
|---|---|---|
| 7828 | Curtana Zenith (Relic Vendor) | NPC statt "Unknown vendor" |
| 2638 | MogStation | "Mog Station" erscheint |
| 4850 | Retainer Venture | "Retainer Venture" erscheint |
| 12224 | Airship Drop | "Airship Voyage" erscheint |
| 5665 | Submarine Drop | "Submarine Voyage" erscheint |
| 1675 | Achievement | Regression-Check, weiter funktionieren |

## Deferred (nicht in diesem Auftrag)

- Recent-glamour SetItemSlotData Fix (ingame verify)
- `MapToCharaViewItemSlot` Slot-Mapping
- PvP / TreasureHunt Provider

---

## Bereits verifiziert (nicht nochmal machen)

- EXDSchema yml files vorhanden: `reference/Dalamud/lib/Lumina.Excel/deps/EXDSchema/` → `PreHandler.yml`, `TopicSelect.yml`, `InclusionShop.yml`, `SpecialShop.yml`. Source-Generator produziert Types beim Build.
- `LuminaItemSourceService.cs` hat volles 4-Provider Muster (Fields 37-40, SafeBuild 63-66, Emission 222-244, Build* 400-454). Wiederverwendbar.
- MogStation/Retainer/Airship/Submarine in `ItemDetailService.cs` nachweislich abwesend (grep bestätigt).
