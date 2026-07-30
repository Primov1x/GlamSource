# GlamSource — Dalamud Addendum

## Glamourer IPC — Erkenntnisse (29.07.2026)

- `GetState` (JObject): crasht mit `IpcTypeMismatchError` — Glamourer's
  Response-JSON enthält nested Objects die Newtonsoft als JToken
  deserialisieren will aber ein JArray erwartet. Nicht verwendbar.
- `GetStateBase64`: liefert GZip-komprimiertes BINÄRFORMAT, kein JSON.
  Nur für Speichern/Wiederherstellen, nicht zum Parsen.
  DynamicBridge nutzt es auch nur als opaken String.
- **Glamourer IPC ist NICHT geeignet für Equipment-Reading.**

## Equipment-Daten — korrekter Ansatz (verifiziert)

- Eigener Character: `InventoryManager.Instance()
  ->GetInventoryContainer(InventoryType.EquippedItems)`
  → `item.ItemId`, `item.GlamourId`
- Andere Spieler (Examine offen): `InventoryType.Examine`
- Andere Spieler (ohne Examine): DrawData-Fallback mit
  EquipSlotCategory-Filtering (unzuverlässig, nur Waffen)
- Examine-Addon-Check: `Plugin.GameGui.GetAddonByName("CharacterInspect")`

## Context-Menu

- `ContextMenuType.Inventory` + `MenuTargetInventory` funktioniert ✓
- `ContextMenuType.Default` braucht Addon-spezifische unsafe Pointer-
  Reads (Agent-Offsets, wie ItemVendorLocation) — komplex, ändert sich
  mit Game-Patches
- Rechtsklick auf Spieler = Default mit AddonName=null → kein Item

## Release-Pipeline

- Release-ZIP: `bin\x64\Release\GlamSource\latest.zip` (DalamudPackager)
- NICHT aus `GlamSource.Release\` oder `GlamSource.ZipContents\`
- Verifizierung: immer aus heruntergeladenem Release-Zip, nie lokal
- `nuget.config` (Root) ist lokaler Dev-Feed, nicht committen

## DalaMock — Setup-Regeln (aus Erfahrung)

### Program.cs Konstruktor-Kompatibilität

Program.cs bricht JEDES MAL wenn ein Konstruktor im Hauptprojekt geändert
wird (neue Parameter, umbenannte Klassen). Vor jedem Mock-Build:
```powershell
Select-String -Path .\GlamSource.Core\ItemDetailService.cs -Pattern "public ItemDetailService" -Context 0,3
Select-String -Path .\Windows\ItemDetailWindow.cs -Pattern "public ItemDetailWindow" -Context 0,3
Select-String -Path .\GlamSource.Mock\MockMainWindow.cs -Pattern "class MockMainWindow" -Context 0,10
Select-String -Path .\GlamSource.Mock\EditorWindow.cs -Pattern "class EditorWindow" -Context 0,10
```
Program.cs an die tatsächlichen Signaturen anpassen, NICHT raten.

### MockMainWindow + EditorWindow haben KEIN IsOpen

Diese Klassen erben NICHT von Dalamud `Window`. Sie sind plain C#-Klassen
mit einer `Draw()`-Methode. Kein `IsOpen`, kein `Toggle()`, kein `Dispose()`.

### ImGuiScene direkt resolven (Holzhammer)

Plugin's `WindowSystem.Draw` wird von DalaMock's UiBuilder NICHT aufgerufen
(DI-Wiring-Problem). Workaround:
```csharp
var imGuiScene = mockContainer.GetContainer()
    .Resolve<DalaMock.Core.Imgui.ImGuiScene>();
imGuiScene.OnBuildUi += () =>
{
    mainWindow.Draw();
    editorWindow.Draw();
    itemDetailWindow.Draw();
};
imGuiScene.Run();
```
Namespace-Konflikt: `ImGuiScene` ist sowohl Namespace als auch Klasse.
Immer voll qualifizieren: `DalaMock.Core.Imgui.ImGuiScene`.

### Lumina im Mock

Lumina direkt instanziieren, NICHT über Dalamud-DI:
```csharp
var gameData = new Lumina.GameData(@"D:\FF\game\sqpack", null);
```
Pfad ist hardcoded auf den lokalen Game-Install.

### Lumina-Sheet-Properties: IMMER dumpen, NIE raten

Vor dem Zugriff auf unbekannte Sheet-Properties:
```csharp
foreach (var p in typeof(Lumina.Excel.Sheets.SheetName)
    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
    .OrderBy(p => p.Name))
    Console.WriteLine($"  {p.Name} : {p.PropertyType.Name}");
```
Falls der Build wegen anderer Fehler nicht läuft: die kaputte Datei
temporär über .csproj ausschließen:
```xml
<Compile Remove="BrokenFile.cs" />
```

### InventoryItem-Felder (verifiziert, API Level 15)

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

### EquipSlotCategory-Properties (verifiziert, Lumina 7.6.0)

Alle `sbyte`, > 0 = Item passt in den Slot:
```
MainHand, OffHand, Head, Body, Gloves, Waist, Legs, Feet,
Ears, Neck, Wrists, FingerL, FingerR, SoulCrystal
```
Mapping: Gloves→Hands, Ears→Earrings, Neck→Necklace,
Wrists→Bracelets, FingerL→RingLeft, FingerR→RingRight

### Mock-Dateien NICHT in Git

`GlamSource.Mock/` ist nicht getrackt. Program.cs-Änderungen gehen
bei jedem Dump/Test verloren. Backups machen oder den funktionierenden
Stand irgendwo festhalten.

### Diagnose-Code nach Verwendung ENTFERNEN

Console.WriteLine-Dumps, Property-Reflection-Scripts, temporäre
Sheet-Dumps: nach dem Ergebnis SOFORT aus Program.cs entfernen und
den funktionierenden Stand wiederherstellen.
