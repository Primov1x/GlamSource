# GlamSource — Dalamud Plugin Development Knowledge Base

Stand: 30.07.2026 | Dalamud API Level 15 | Lumina 7.6.0 | .NET 10

---

## 1. Projekt-Architektur

### Verzeichnisstruktur
```
GlamSource/
├── GlamSource.csproj          # Haupt-Plugin (Dalamud-abhängig, net10.0-windows7.0)
├── Plugin.cs                  # Entry Point, DI, Service-Wiring
├── GameDataService.cs         # Equipment-Reading (InventoryManager, DrawData)
├── GlamourService.cs          # IGlamourService Stub
├── Windows/
│   ├── MainWindow.cs          # Equipment-Tabelle (Dalamud Window)
│   ├── ItemDetailWindow.cs    # Item-Detail-Fenster (Sources, NPC, Kosten)
│   └── ConfigWindow.cs
├── Services/
│   └── ContextMenuService.cs  # Rechtsklick-Kontextmenü auf Items
├── GlamSource.Core/           # Dalamud-unabhängig, testbar
│   ├── IGlamourService.cs
│   ├── EquipmentSlot.cs
│   ├── ItemSourceService.cs   # IItemSourceService Interface + Enums
│   ├── LuminaItemSourceService.cs  # Lumina-basierter Source-Lookup
│   ├── ItemDetailService.cs   # Detaillierte Item-Infos (NPC, Zone, Kosten)
│   ├── UniversalisService.cs  # Market-Preise via REST API
│   └── FixtureGlamourService.cs    # JSON-basierter Mock-Service
├── GlamSource.Core.Tests/     # Unit Tests
│   └── fixtures/target-example.json
├── GlamSource.Mock/           # DalaMock-Projekt (NICHT in Git)
│   ├── Program.cs             # ImGuiScene-basierter Einstieg
│   ├── MockGlamourService.cs
│   ├── MockMainWindow.cs
│   ├── EditableGlamourService.cs   # Fake-Player mit Item-Auswahl
│   ├── EditorWindow.cs        # Item-Editor mit Slot-Listen
│   └── MapPreviewWindow.cs    # Mock Map-Vorschau
└── reference/
    ├── FINDINGS.md             # Referenz-Plugin-Analyse
    └── Dalamud/                # Dalamud SDK Referenz
```

### Wichtige Projekte im Workspace
```
C:\Users\t.fritzen\Projects\Private\
├── GlamSource/                # Dieses Plugin
├── DalaMock-15/               # DalaMock Framework (API Level 15)
├── Glamourer/                 # Glamourer Plugin (IPC-Referenz)
├── Glamourer.Api/             # Glamourer API (lokal gebaut, 2.8.2)
├── GearsetHelperPlugin/       # Equipment-Reading-Referenz
├── ItemVendorLocation/        # Vendor/NPC/Map-Referenz
├── DynamicBridge/             # Glamourer-IPC-Referenz
├── LuminaSupplemental/        # Item→Duty-Drop-Mappings (CSV)
├── Artisan/                   # Crafting-Automation (IPC)
└── Questionable/              # Quest-Automation (IPC)
```

---

## 2. Lumina — Game-Daten-Zugriff

### Grundregel: NIEMALS Sheet-Property-Namen raten

Lumina 7.6.0 generiert Sheet-Structs aus YAML-Schemas. Die Property-Namen
sind NICHT intuitiv und ändern sich zwischen Versionen. VOR dem ersten
Zugriff auf ein neues Sheet IMMER dumpen:

```csharp
foreach (var p in typeof(Lumina.Excel.Sheets.SHEET_NAME)
    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
    .Where(p => p.Name != "RowId" && p.Name != "SubRowId")
    .OrderBy(p => p.Name))
    Console.WriteLine($"  {p.Name} : {p.PropertyType.Name}");
```

Falls der Build wegen anderer Fehler nicht läuft: die kaputte Datei
temporär über .csproj ausschließen:
```xml
<Compile Remove="BrokenFile.cs" />
```

### Verifizierte Sheet-Properties (Lumina 7.6.0)

#### Item
```
Name : ReadOnlySeString
LevelEquip : Byte              # benötigtes Level zum Tragen
LevelItem : RowRef<ItemLevel>  # Item Level
PriceMid : UInt32              # Gil-Verkaufspreis (NPC→Spieler)
PriceLow : UInt32              # Gil-Einkaufspreis (Spieler→NPC)
Icon : UInt16                  # Icon-ID
ItemSearchCategory : RowRef    # > 0 = marktfähig
ItemUICategory : RowRef        # Kategorie (Currency, etc.)
EquipSlotCategory : RowRef<EquipSlotCategory>
ModelMain : UInt64             # Model-ID (gepackt)
ModelSub : UInt64              # Sub-Model-ID (gepackt)
Description : ReadOnlySeString
```

#### Recipe
```
ItemResult : RowRef<Item>      # Ergebnis-Item
AmountResult : Byte            # Menge
CraftType : RowRef<CraftType>  # Crafter-Typ
RecipeLevelTable : RowRef      # .RowId = das Level
Ingredient : Collection        # 8 Zutaten (parallel mit AmountIngredient)
AmountIngredient : Collection  # 8 Mengen
RequiredCraftsmanship : UInt16
```

#### CraftType → ClassJob Mapping
```
CraftType.RowId + 8 = ClassJob.RowId
0=CRP(8), 1=BSM(9), 2=ARM(10), 3=GSM(11),
4=LTW(12), 5=WVR(13), 6=ALC(14), 7=CUL(15)
ClassJob.Abbreviation = "BSM", "ARM", etc.
ClassJob.Name = "Blacksmith", "Armorer", etc.
```

#### SpecialShop
```
Name : ReadOnlySeString
UseCurrencyType : Byte         # 0/8=Gil, 16=Tomestone-Map, 4=TomestonesItem
Item : Collection<ItemStruct>
```

#### SpecialShop.ItemStruct
```
Quest : RowRef<Quest>          # Freischalt-Quest (auf ItemStruct, NICHT auf Shop!)
AchievementUnlock : RowRef<Achievement>
ItemCosts : Collection<ItemCostsStruct>
ReceiveItems : Collection<ReceiveItemsStruct>
```

#### ReceiveItemsStruct
```
Item : RowRef<Item>            # was man bekommt
ReceiveCount : UInt32          # Menge
ReceiveHq : Boolean
```

#### ItemCostsStruct
```
ItemCost : RowRef<Item>        # was man bezahlt
CurrencyCost : UInt32          # Menge
CollectabilityCost : UInt16
HqCost : Byte
```

**ACHTUNG: UseCurrencyType**
Manche SpecialShops nutzen `UseCurrencyType` statt `ItemCost`:
- `UseCurrencyType=16` → Tomestones via GilCurrencyMap:
  `{1:28, 2:33913, 3:33912, 4:33914, 5:33915, 6:41784, 7:41785}`
- `UseCurrencyType=8` → immer Gil (Item 1)
- `UseCurrencyType=4` → TomestonesItem Sheet Lookup
- `UseCurrencyType=0` oder `RowId >= 8` → direkt aus ItemCost

#### GilShopItem (SUBROW Sheet)
```
Item : RowRef<Item>
```
Laden mit: `GetSubrowExcelSheet<GilShopItem>()`

#### EquipSlotCategory
Alle `SByte`, > 0 = Item passt in den Slot:
```
MainHand, OffHand, Head, Body, Gloves, Waist, Legs, Feet,
Ears, Neck, Wrists, FingerL, FingerR, SoulCrystal
```
Mapping zu EquipmentSlotType:
```
Gloves→Hands, Ears→Earrings, Neck→Necklace,
Wrists→Bracelets, FingerL→RingLeft, FingerR→RingRight
```

#### ENpcBase
```
ENpcData : Collection<RowRef>  # enthält Shop-IDs, Talk-IDs, etc.
```

#### ENpcResident
```
Singular : ReadOnlySeString    # NPC-Name
```

#### Level
```
X : Single, Y : Single, Z : Single  # Raw-Koordinaten
Territory : RowRef<TerritoryType>    # LEER im Mock!
Map : RowRef<Map>
Object : RowRef                      # z.B. ENpcBase-RowId
Type : Byte                          # 8 = ENpc
```

#### Map
```
PlaceName : RowRef<PlaceName>
TerritoryType : RowRef<TerritoryType>
SizeFactor : UInt16            # z.B. 100, 200
OffsetX : Int16
OffsetY : Int16
Id : ReadOnlySeString          # Map-Pfad für Texturen
```

#### ContentFinderCondition
```
Name : ReadOnlySeString
Content : RowRef               # → InstanceContent
ContentType : RowRef           # 4 = Trial, 2 = Dungeon, 5 = Raid
TrialRoulette : Boolean
```

#### InstanceContent
```
ContentFinderCondition : RowRef<ContentFinderCondition>
BossCurrencyA/B/C : Collection<UInt16>  # Item-IDs (aber LEER im Mock!)
FinalBossCurrencyA/B/C : UInt16
```

#### Quest
```
Name : ReadOnlySeString
InstanceContentUnlock : RowRef  # Trial/Dungeon den die Quest freischaltet
InstanceContent : Collection    # zugehörige Duties
```

### RowRef-Zugriffsmuster

```csharp
// SICHER: RowId prüfen, ValueNullable verwenden
var name = ref.RowId > 0 ? ref.ValueNullable?.Name.ToString() : null;

// CRASH: .Value auf ungültige Referenz
var name = ref.Value.Name.ToString(); // → InvalidOperationException!

// STRUCT Sheets: kein null-Check, sondern RowId == 0
foreach (var item in itemSheet)
{
    if (item.RowId == targetId) { /* gefunden */ break; }
}
// NICHT: itemSheet.FirstOrDefault(...) → gibt default(T) zurück, nicht null
```

### Sheets die im Mock LEER sind
```
TerritoryType : 0 Rows
InstanceContent : 0 Rows (Count gibt nichts zurück)
```
Workaround: `Level.Map.PlaceName` statt `Level.Territory.PlaceName`

---

## 3. InventoryManager — Equipment-Daten

### Eigener Character
```csharp
using FFXIVClientStructs.FFXIV.Client.Game;

var im = InventoryManager.Instance();
var container = im->GetInventoryContainer(InventoryType.EquippedItems);
// 14 Slots: 0-11 = Equipment, 12=SoulCrystal, 13=Waist(unused)
for (var i = 0; i < container->Size; i++)
{
    var item = container->Items[i];
    uint itemId = item.ItemId;
    uint glamourId = item.GlamourId;
}
```

### Andere Spieler (Examine)
```csharp
var container = im->GetInventoryContainer(InventoryType.Examine);
// Nur verfügbar wenn Examine-Fenster offen (CharacterInspect Addon)
// Prüfe: Plugin.GameGui.GetAddonByName("CharacterInspect") != nint.Zero
```

### InventoryItem-Felder (verifiziert)
```
ItemId : UInt32
GlamourId : UInt32
Condition : UInt16
Container : InventoryType
Flags : ItemFlags
Quantity : Int32
Slot : Int16
Materia : Span<ushort>
MateriaGrades : Span<byte>
Stains : Span<byte>
```

### Slot-Mapping
```csharp
0=MainHand, 1=OffHand, 2=Head, 3=Body, 4=Hands,
5=Legs, 6=Feet, 7=Earrings, 8=Necklace, 9=Bracelets,
10=RingRight, 11=RingLeft, 12+=skip (SoulCrystal, Waist)
```

---

## 4. DrawData — Fallback für andere Spieler

DrawData liest Model-IDs aus dem Character-Rendering. Unzuverlässig,
aber der einzige Weg ohne Examine.

### Model-ID Unpacking (nach Penumbra.GameData)
```csharp
var primaryId    = (ushort)(item.ModelMain & 0xFFFF);
var secondaryId  = (ushort)((item.ModelMain >> 16) & 0xFFFF);
var variant      = (byte)(item.ModelMain >> 32);
```

### EquipSlotCategory-Filtering
IMMER nach Slot filtern, sonst matchen Crafter-Tools auf Weapon-Slots.
Cache beim Start: `Dictionary<EquipmentSlotType, List<Item>>`

### Bekannte Limitierungen
- Nur Waffen-Slots liefern zuverlässig Daten für andere Spieler
- Armor-Slots sind oft alle 0 wenn der Character nicht vollständig geladen ist
- `FirstOrDefault` auf Struct-Sheets gibt `default(T)` zurück (RowId=0), nicht null
- `matchedItem.Name.ToString()` crasht auf default(Item) → immer RowId > 0 prüfen

---

## 5. Glamourer IPC — NICHT verwenden für Equipment-Reading

### GetState (JObject) — CRASHT
```
IpcTypeMismatchError: Glamourer.GetState → Newtonsoft.Json kann das
Response-Format nicht in JObject deserialisieren.
Path 'Item2.Equipment.MainHand'
```
Ursache: Glamourer's JSON enthält nested Objects die Newtonsoft nicht handlen kann.

### GetStateBase64 — BINÄRDATEN
Liefert GZip-komprimiertes proprietäres Binärformat, KEIN JSON.
Nur für Speichern/Wiederherstellen, nicht zum Parsen.
DynamicBridge nutzt es auch nur als opaken String.

### Fazit
Glamourer IPC ist NICHT geeignet für Equipment-Reading.
Verwende InventoryManager (eigen) oder Examine (andere Spieler).

---

## 6. NPC-Location — Shop→NPC→Zone→Koordinaten

### Sheet-Kette
```
Item → GilShopItem (match auf Item.RowId)
     → GilShop (Parent-Row)
     → ENpcBase (ENpcData enthält Shop-IDs)
     → ENpcResident (Singular = NPC-Name)
     → Level (Object.RowId = ENpcBase.RowId, Type=8)
     → Map (PlaceName, SizeFactor, OffsetX/Y)
     → PlaceName (Name = Zone-Name)
```

### Level-Filter
```csharp
if (level.Type != 8) continue;  // Type 8 = ENpc
// NICHT: level.Object.RowId >= 1000000 (falsch)
```

### Koordinaten-Umrechnung (Raw → Map)
```csharp
float ToMapCoordinate(float raw, ushort sizeFactor, short offset)
{
    var scale = sizeFactor / 100.0f;
    return (raw / 1000.0f * scale) + (41.0f / scale) / 2.0f + 1.0f;
}
// X = Level.X, Y = Level.Z (Z ist Y auf der 2D-Map)
```

### Cache-Struktur
```csharp
Dictionary<uint, List<NpcLocationInfo>> _shopNpcLookup
// ShopRowId → List von NPCs (mehrere NPCs können denselben Shop anbieten)
```

### Map öffnen (In-Game)
```csharp
using Dalamud.Game.Text.SeStringHandling.Payloads;
var mapLink = new MapLinkPayload(territoryTypeId, mapId, mapX, mapY);
Plugin.GameGui.OpenMapWithMapLink(mapLink);
// mapX/mapY sind bereits umgerechnete Map-Koordinaten (float)
```

---

## 7. Item→Duty-Drop-Zuordnung

### Problem
Lumina hat keine direkte Item→Duty-Verknüpfung. InstanceContent-Sheet
ist im Mock leer. Boss-Name-Matching über CFC funktioniert nur für ~50%.

### Lösung: LuminaSupplemental (Critical-Impact)
```
C:\Users\t.fritzen\Projects\Private\LuminaSupplemental\
```

Liefert CSV-basierte Mappings:
- `DungeonDrop.csv`: ItemId → ContentFinderConditionId
- `ItemSupplement.csv`: ItemId → SourceItemId + ItemSupplementSource
- `DungeonBossDrop.csv`: ItemId → Boss-Info

Laden via `CsvLoader`:
```csharp
var dungeonDrops = CsvLoader.LoadResource<DungeonDrop>(
    CsvLoader.DungeonDropResourceName, gameData);
```

### ItemSupplementSource Enum
```csharp
public enum ItemSupplementSource
{
    No, Monster, Loot, Instance, Gathering, Shop, Event, Quest, ...
}
```

### Fallback-Kette für Item-Sources
1. Recipe-Sheet → Crafted
2. GilShopItem → Vendor (Gil)
3. SpecialShop → Shop (Tomestones, Tokens)
4. LuminaSupplemental → Duty Drops
5. Quest-Sheet → Quest Rewards
6. Reverse SpecialShop (Item als Kosten) → Duty-Verknüpfung
7. Generischer Fallback-Text

---

## 8. Context-Menu-Integration

### Dalamud API
```csharp
[PluginService] IContextMenu ContextMenu;
ContextMenu.OnMenuOpened += OnMenuOpened;

private void OnMenuOpened(IMenuOpenedArgs args)
{
    var itemId = ExtractItemId(args);
    if (itemId > 0)
        args.AddMenuItem(new MenuItem { Name = "Item Source", OnClicked = ... });
}
```

### Menu-Typen
```
ContextMenuType.Inventory → MenuTargetInventory
  - inv.TargetItem.Value.ItemId (direkt verfügbar)

ContextMenuType.Default → MenuTargetDefault
  - args.AddonName bestimmt die Extraktion
  - CharacterInspect: ExtractHoveredItemId() (Fallback)
  - RecipeNote: Agent-Pointer + Offset 0x398
  - ShopExchangeItem/GrandCompanyExchange: Agent-Pointer + Offset 0x54
  - ChatLog: eigene Logik
```

### HQ-Korrektur
```csharp
if (itemId > 1000000) itemId -= 1000000;   // HQ Flag
if (itemId > 500000) itemId -= 500000;     // Collectible Flag
```

### Wichtige Fallen
- `TargetContentId != 0` filtert NICHT pauschal → Examine-Items haben ContentId
- Agent-Pointer-Offsets ändern sich mit Game-Patches
- `ExtractHoveredItemId()` als Fallback für unbekannte Addons
- AddonName ist `null` bei Spieler-Rechtsklick (kein Item)

---

## 9. DalaMock — Mock-Entwicklung

### Grundsetup
```csharp
var mockContainer = new MockContainer(
    dalamudConfiguration: config, askPath: false);
var imGuiScene = mockContainer.GetContainer()
    .Resolve<DalaMock.Core.Imgui.ImGuiScene>();  // Voll qualifizieren!
imGuiScene.OnBuildUi += () => { window.Draw(); };
imGuiScene.Run();
```

### Bekannte Probleme
- Plugin's `WindowSystem.Draw` wird NICHT aufgerufen (DI-Wiring)
  → Holzhammer: direkt an `ImGuiScene.OnBuildUi` hängen
- `ImGuiScene` ist Namespace UND Klasse → voll qualifizieren
- MockMainWindow/EditorWindow haben KEIN `IsOpen` (keine Dalamud-Windows)
- `localStorage`/`sessionStorage` nicht verfügbar

### Program.cs — KRITISCHE Regeln
1. Konstruktor-Signaturen prüfen VOR jeder Änderung:
   ```powershell
   Select-String -Path .\XYZ.cs -Pattern "public XYZ" -Context 0,3
   ```
2. Diagnose-Code SOFORT nach Verwendung entfernen
3. Funktionierenden Stand kennen und wiederherstellen können

### Lumina im Mock
```csharp
var gameData = new Lumina.GameData(@"D:\FF\game\sqpack", null);
```
Pfad hardcoded auf lokalen Game-Install.

---

## 10. Release-Pipeline

### Version-Bump — IMMER in BEIDEN Dateien
```
GlamSource.csproj: <Version> UND <AssemblyVersion>
repo.json: AssemblyVersion UND TestingAssemblyVersion
```

### Release-ZIP
```
bin\x64\Release\GlamSource\latest.zip  (DalamudPackager-Output)
```
NICHT aus `GlamSource.Release\` oder `GlamSource.ZipContents\` (veraltet, 0.0.0.1).

### Release-Ablauf
```powershell
dotnet build -c Release
# Verifizieren:
Expand-Archive .\bin\x64\Release\GlamSource\latest.zip -Dest C:\temp\verify -Force
Get-Content C:\temp\verify\GlamSource.json | Select-String "AssemblyVersion"
# Upload:
gh release upload LATEST .\bin\x64\Release\GlamSource\latest.zip --clobber
# Push:
git push origin main
```

### Versions-Mismatch-Fehler
```
Distributed plugin version does not match repo version
```
Ursache: `repo.json` sagt Version X, aber ZIP enthält Version Y.
Fix: beide synchron halten, aus dem RICHTIGEN Build-Output hochladen.

### In-Game Installation
1. `/xlplugins` → GlamSource DEINSTALLIEREN (nicht Update-Button)
2. Neu installieren aus Custom Repo
3. Falls immer noch alt: `/xlsettings` → Repo entfernen + neu hinzufügen

### Raw-Cache
GitHub Raw-Dateien werden 5 Minuten gecacht (`max-age=300`).
```powershell
curl.exe -I https://raw.githubusercontent.com/.../repo.json
# X-Cache: MISS + Source-Age: 0 = frisch
```

---

## 11. Externe APIs

### Universalis (Market-Preise)
```
GET https://universalis.app/api/v2/{world}/{itemId}?listings=1&entries=0
GET https://universalis.app/api/v2/{dc}/{itemId}?listings=1&entries=0

Response: { minPriceNQ, minPriceHQ, listings[0].worldName, ... }
```
Rate-Limit: max 1 req/sec. Cachen pro ItemId.

### Garland Tools
```
https://garlandtools.org/db/#item/{itemId}  (Web-UI)
```
API-Endpunkte sind nicht gut dokumentiert.

### Teamcraft
```
https://ffxivteamcraft.com/db/en/item/{itemId}  (Web-UI)
```

### Consolegameswiki
```
https://ffxiv.consolegameswiki.com/wiki/{itemName.Replace(" ", "_")}
```

---

## 12. Referenz-Plugins — Wo was nachschauen

| Thema | Plugin | Pfad |
|-------|--------|------|
| Vendor/NPC/Map-Location | ItemVendorLocation | `ItemVendorLocation\` |
| Equipment-Reading (Examine) | GearsetHelperPlugin | `GearsetHelperPlugin\` |
| Glamourer-IPC | DynamicBridge | `DynamicBridge\` |
| Item→Duty-Drops | LuminaSupplemental | `LuminaSupplemental\` |
| Inventory-Tracking | InventoryTools | (nicht geklont) |
| Crafting-Automation | Artisan | `Artisan\` |
| Quest-Automation | Questionable | `Questionable\` |
| DalaMock Framework | DalaMock-15 | `DalaMock-15\` |

### Referenz-Code finden
```powershell
Get-ChildItem C:\Users\t.fritzen\Projects\Private\PLUGIN\ -Recurse -Include *.cs |
    Select-String -Pattern "SUCHBEGRIFF" -Context 2,5 |
    Select-Object -First 30
```

---

## 13. Diagnose-Disziplin

### Regeln
1. **Nach 2-3 gescheiterten Versuchen: STOPP und rückfragen**
2. **Lumina-Properties: DUMPEN, nie raten**
3. **Diagnose-Code nach Verwendung SOFORT entfernen**
4. **Program.cs nach Dumps SOFORT wiederherstellen**
5. **Statusberichte nur mit echtem Nachweis** (Build-Output, Screenshots, Logs)
6. **Keine Behauptungen über entfernten/verschobenen Code ohne git-diff-Beleg**
7. **Agent-Pointer-Offsets aus Referenz-Plugins abschauen, nicht raten**
8. **RowRef.Value crasht bei ungültiger Referenz → immer RowId > 0 prüfen**

### Diagnose-Logging Pattern
```csharp
// Einmalig (Konstruktor/Start):
Console.WriteLine($"[CACHE] Built {count} entries");

// Pro-Frame (in Draw()): IMMER throtteln
if (changed) Plugin.Log.Information("[DIAG] ...");

// NIEMALS Console.WriteLine in Draw() ohne Guard
```

### FFXIV SeIcon-Zeichen
Rendern nicht in ImGui. Ersetze durch lesbaren Text:
- Gil-Icon → "Gil"
- Andere Icons → entfernen

---

## 14. Häufige Fallen

### RowRef.Value vs ValueNullable
```csharp
// CRASH:
ref.Value.Name.ToString()  // InvalidOperationException wenn ungültig

// SICHER:
ref.RowId > 0 ? ref.ValueNullable?.Name.ToString() : null
```

### Struct-Sheets und FirstOrDefault
```csharp
// FALSCH: gibt default(Item) zurück (RowId=0), nicht null
var item = sheet.FirstOrDefault(i => i.Name...);
item.Name.ToString();  // NullRef auf default Struct!

// RICHTIG:
foreach (var item in sheet)
{
    if (item.RowId == targetId) { /* gefunden */ break; }
}
```

### Newtonsoft.Json in Dalamud
Plugin packt Newtonsoft 13.0.3 ein, Dalamud nutzt intern eine andere Version.
IPC-Deserialisierung kann durch Versions-Konflikte crashen.

### ContentId-Check bei Context-Menu
```csharp
// FALSCH: filtert Examine-Items raus
if (defaultTarget.TargetContentId != 0) return null;

// RICHTIG: Whitelist vertrauen
if (!GameAddonWhitelist.Contains(addonName)) return null;
```

### nuget.config
Root-Level `nuget.config` mit lokalem Pfad NICHT committen.
Steht in `.gitignore`. CI nutzt `.github\NuGet.Config`.

### GlamSource.Mock nicht in Git
Mock-Dateien werden nicht getrackt. Program.cs-Änderungen gehen
bei Dumps/Tests verloren. Immer den funktionierenden Stand kennen.

---

## 15. Build-Befehle Cheatsheet

```powershell
# Plugin bauen
dotnet build -c Release

# Mock starten
dotnet run --project .\GlamSource.Mock\ -c Debug 2>&1

# Tests
dotnet test .\GlamSource.Core.Tests\ -c Release

# Release verifizieren
Expand-Archive .\bin\x64\Release\GlamSource\latest.zip -Dest C:\temp\v -Force
Get-Content C:\temp\v\GlamSource.json | Select-String "AssemblyVersion"

# Release hochladen
gh release upload LATEST .\bin\x64\Release\GlamSource\latest.zip --clobber

# Git
git status
git add FILE1 FILE2
git commit -m "MESSAGE"
git push origin main

# Lumina-Sheet dumpen (temporär in Program.cs)
# SOFORT danach Program.cs wiederherstellen!
```
