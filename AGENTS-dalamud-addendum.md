# GlamSource — Dalamud Addendum

## Glamourer IPC — Erkenntnisse (29.07.2026)

- `GetState` (JObject): crasht mit `IpcTypeMismatchError` — Newtonsoft.Json
  kann das Glamourer-Response-Format nicht in JObject deserialisieren.
  Nicht verwendbar.
- `GetStateBase64`: liefert GZip-komprimiertes Binärformat, KEIN JSON.
  Nur für Speichern/Wiederherstellen gedacht, nicht zum Parsen.
  DynamicBridge nutzt es auch nur als opaken String.
- `InvalidKey` (ec=6): tritt nur bei Write-Methoden auf laut Glamourer-
  Quellcode (FINDINGS.md §4). Bei uns trotzdem bei Read — vermutlich
  Timing-Problem beim Actor-Load. Löst sich nach wenigen Frames.
- **Glamourer IPC ist NICHT der richtige Weg für Equipment-Reading.**

## Equipment-Daten — korrekter Ansatz

- Eigener Character: `InventoryManager` (Dalamud) → exakte Item-IDs
- Andere Spieler: Examine-System (`CharacterInspect`-Addon, wie
  GearsetHelperPlugin) → Item-IDs direkt vom Server
- DrawData-Parsing: funktioniert prinzipiell, aber Model→Item-Reverse-
  Lookup ist unzuverlässig (falsche Matches bei Crafter-Tools, fehlendes
  EquipSlotCategory-Filtering)

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

## DalaMock

- Plugin's WindowSystem.Draw wird von DalaMock's UiBuilder nicht
  aufgerufen (DI-Wiring-Problem)
- Holzhammer: ImGuiScene direkt resolven, OnBuildUi += window.Draw()
- MockGlamourService/EditableGlamourService für Fake-Player-Daten
- Lumina-Zugriff: `new GameData(@"D:\FF\game\sqpack", null)`
