# AGENTS.md — GlamSource

## Was ist GlamSource
Dalamud-Plugin für FFXIV: zeigt woher Glamour-Items und Mounts stammen — direkt im Spiel, ohne Wiki. Features: Item-/Mount-Source, Tooltip-Integration, `/glamsource` Suchfenster, lokaler Offline-Cache.

## Projektstruktur
```
GlamSource/
├── GlamSource/
│   ├── Plugin.cs                  # Entry Point, DI, Commands
│   ├── Configuration.cs           # Persistente Einstellungen
│   ├── Windows/
│   │   ├── MainWindow.cs          # /glamsource Fenster + Suche
│   │   └── ConfigWindow.cs
│   ├── Services/
│   │   ├── ItemSourceService.cs   # Lumina-Abfragen Items
│   │   ├── MountSourceService.cs  # Lumina + Cache Mounts
│   │   └── WikiCacheService.cs    # XIVAPI JSON-Cache
│   └── Data/
│       └── SourceCache.cs         # Datenmodelle
├── Data/
│   └── source-cache.json          # Gecachte API-Daten (gitignored)
├── CLAUDE.md
├── AGENTS.md
└── GlamSource.sln
```

## Dalamud Services ([PluginService])
| Service | Zweck |
|---|---|
| IDalamudPluginInterface | Plugin-Interface, Config, AssemblyLocation |
| IDataManager | Lumina Excel Sheets |
| ITextureProvider | Icons laden |
| ICommandManager | Slash-Commands |
| IClientState | Spieler-Status |
| IPluginLog | Logging |
| IGameGui | Tooltip-Hooks |
| IContextMenu | Rechtsklick-Menü |

## Datenquellen
**Lumina (offline):** `Item`, `RecipeLookup`, `GilShop`, `SpecialShop`, `Mount`, `InstanceContent` Sheets

**XIVAPI (gecacht):** `https://xivapi.com/item/{id}` → Drop-Sources; Cache: `%AppData%\XIVLauncher\pluginConfigs\GlamSource\cache.json`

## Wo füge ich X hinzu?
| Ziel | Datei |
|---|---|
| Neue Item-Quelle | `Services/ItemSourceService.cs` |
| Neue Mount-Info | `Services/MountSourceService.cs` |
| Neues UI-Fenster | `Windows/<Name>Window.cs` + in `Plugin.cs` registrieren |
| Neuer Slash-Command | `CommandManager.AddHandler` in `Plugin.cs` |
| Neue gecachte Daten | `WikiCacheService.cs` + `Data/SourceCache.cs` |

## Konventionen
- Services via `[PluginService]` — nie `new Service()`
- `Dispose()` meldet alle Handler ab
- Lumina via `DataManager.GetExcelSheet<T>()`
- Logging via `IPluginLog`
- HTTP-Calls immer async