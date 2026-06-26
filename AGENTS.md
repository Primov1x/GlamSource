@'
# AGENTS.md — GlamSource

> Onboarding-Karte für AI Coding Agents die in diesem Repo arbeiten.

---

## 1. Was ist GlamSource

**GlamSource** ist ein Dalamud-Plugin für FFXIV das anzeigt woher Glamour-Items und Mounts stammen — direkt im Spiel, ohne Wiki.

| Feature | Beschreibung |
|---|---|
| Item-Source | Woher kommt dieses Rüstungsstück / dieser Gegenstand |
| Mount-Source | Woher bekommt man dieses Mount |
| Tooltip-Integration | Anzeige beim Hover über Items/Mounts |
| Suche | `/glamsource` öffnet Suchfenster |
| Offline-Cache | XIVAPI-Daten lokal gecacht, kein dauerhafter Online-Zwang |

---

## 2. Tech Stack

| Layer | Tech |
|---|---|
| Sprache | C# 14, .NET 10 |
| Plugin-Framework | Dalamud API 14 |
| UI | ImGui via Dalamud WindowSystem |
| Spieldaten | Lumina Excel Sheets (IDataManager) |
| Externe Daten | XIVAPI + Garland Tools (lokaler JSON-Cache) |
| Build | Dalamud.NET.Sdk 15.0.0 |

---

## 3. Projektstruktur

Kein Problem. Hier ist deine AGENTS.md — einfach als neue Datei anlegen:
powershell@'
# AGENTS.md — GlamSource

> Onboarding-Karte für AI Coding Agents die in diesem Repo arbeiten.

---

## 1. Was ist GlamSource

**GlamSource** ist ein Dalamud-Plugin für FFXIV das anzeigt woher Glamour-Items und Mounts stammen — direkt im Spiel, ohne Wiki.

| Feature | Beschreibung |
|---|---|
| Item-Source | Woher kommt dieses Rüstungsstück / dieser Gegenstand |
| Mount-Source | Woher bekommt man dieses Mount |
| Tooltip-Integration | Anzeige beim Hover über Items/Mounts |
| Suche | `/glamsource` öffnet Suchfenster |
| Offline-Cache | XIVAPI-Daten lokal gecacht, kein dauerhafter Online-Zwang |

---

## 2. Tech Stack

| Layer | Tech |
|---|---|
| Sprache | C# 14, .NET 10 |
| Plugin-Framework | Dalamud API 14 |
| UI | ImGui via Dalamud WindowSystem |
| Spieldaten | Lumina Excel Sheets (IDataManager) |
| Externe Daten | XIVAPI + Garland Tools (lokaler JSON-Cache) |
| Build | Dalamud.NET.Sdk 15.0.0 |

---

## 3. Projektstruktur
GlamSource/

├── GlamSource/

│   ├── Plugin.cs              # Entry Point, DI, Command-Registration

│   ├── Configuration.cs       # Plugin-Einstellungen (persistent)

│   ├── Windows/

│   │   ├── MainWindow.cs      # /glamsource Hauptfenster + Suche

│   │   └── ConfigWindow.cs    # Einstellungen

│   ├── Services/

│   │   ├── ItemSourceService.cs   # Lumina-Abfragen für Items

│   │   ├── MountSourceService.cs  # Lumina + Cache für Mounts

│   │   └── WikiCacheService.cs    # XIVAPI JSON-Cache (lokal)

│   └── Data/

│       └── SourceCache.cs     # Datenmodelle für gecachte Sources

├── Data/

│   └── source-cache.json      # Gecachte API-Daten (gitignored)

├── CLAUDE.md

├── AGENTS.md

└── GlamSource.sln

---

## 4. Dalamud Services — verfügbar via [PluginService]

| Service | Zweck |
|---|---|
| IDalamudPluginInterface | Plugin-Interface, Config, AssemblyLocation |
| IDataManager | Lumina Excel Sheets (Items, Mounts, Recipes…) |
| ITextureProvider | Icons und Texturen laden |
| ICommandManager | Slash-Commands registrieren |
| IClientState | Spieler-Status, aktueller Character |
| IPluginLog | Logging |
| IGameGui | Tooltip-Hooks, UI-Interaktion |
| IContextMenu | Rechtsklick-Kontextmenü Einträge |

---

## 5. Datenquellen

### Lumina (offline, direkt aus Spieldaten)
- `Item` Sheet → Name, Icon, EquipSlot, IsUntradable
- `RecipeLookup` Sheet → ob craftbar + welches Recipe
- `GilShop` / `SpecialShop` → ob kaufbar + Preis
- `Mount` Sheet → Mount-Name, Icon, Patch-Info
- `InstanceContent` → Dungeon/Trial-Referenzen

### XIVAPI (online, einmalig gecacht)
- Detaillierte Drop-Sources (welcher Boss, welches Duty)
- Endpoint: `https://xivapi.com/item/{id}`
- Cache: `%AppData%\XIVLauncher\pluginConfigs\GlamSource\cache.json`

---

## 6. Konventionen

- Services via `[PluginService]` injizieren — nie `new Service()`.
- `Dispose()` MUSS alle Handler abmelden.
- Lumina-Sheets über `DataManager.GetExcelSheet<T>()`.
- Logging via `IPluginLog`, nie `Console.WriteLine`.
- ImGui-Draws nur in registrierten Window-Klassen.
- Externe HTTP-Calls immer async, nie im UI-Thread blockieren.

---

## 7. Wo füge ich X hinzu?

| Ziel | So geht's |
|---|---|
| Neue Item-Quelle erkennen | `Services/ItemSourceService.cs` erweitern |
| Neue Mount-Info | `Services/MountSourceService.cs` erweitern |
| Neues UI-Fenster | `Windows/<Name>Window.cs` + in `Plugin.cs` registrieren |
| Neuer Slash-Command | `CommandManager.AddHandler` in `Plugin.cs` + Dispose entfernen |
| Neue gecachte Daten | `WikiCacheService.cs` + `Data/SourceCache.cs` Modell ergänzen |




# DOX framework

- DOX is highly performant AGENTS.md hierarchy installed here
- Agent must follow DOX instructions across any edits

## Core Contract

- AGENTS.md files are binding work contracts for their subtrees
- Work products, source materials, instructions, records, assets, and durable docs must stay understandable from the nearest applicable AGENTS.md plus every parent AGENTS.md above it

## Read Before Editing

1. Read the root AGENTS.md
2. Identify every file or folder you expect to touch
3. Walk from the repository root to each target path
4. Read every AGENTS.md found along each route
5. If a parent AGENTS.md lists a child AGENTS.md whose scope contains the path, read that child and continue from there
6. Use the nearest AGENTS.md as the local contract and parent docs for repo-wide rules
7. If docs conflict, the closer doc controls local work details, but no child doc may weaken DOX

Do not rely on memory. Re-read the applicable DOX chain in the current session before editing.

## Update After Editing

Every meaningful change requires a DOX pass before the task is done.

Update the closest owning AGENTS.md when a change affects:

- purpose, scope, ownership, or responsibilities
- durable structure, contracts, workflows, or operating rules
- required inputs, outputs, permissions, constraints, side effects, or artifacts
- user preferences about behavior, communication, process, organization, or quality
- AGENTS.md creation, deletion, move, rename, or index contents

Update parent docs when parent-level structure, ownership, workflow, or child index changes. Update child docs when parent changes alter local rules. Remove stale or contradictory text immediately. Small edits that do not change behavior or contracts may leave docs unchanged, but the DOX pass still must happen.

## Hierarchy

- Root AGENTS.md is the DOX rail: project-wide instructions, global preferences, durable workflow rules, and the top-level Child DOX Index
- Child AGENTS.md files own domain-specific instructions and their own Child DOX Index
- Each parent explains what its direct children cover and what stays owned by the parent
- The closer a doc is to the work, the more specific and practical it must be

## Child Doc Shape

- Create a child AGENTS.md when a folder becomes a durable boundary with its own purpose, rules, responsibilities, workflow, materials, or quality standards
- Work Guidance must reflect the current standards of the project or user instructions; if there are no specific standards or instructions yet, leave it empty
- Verification must reflect an existing check; if no verification framework exists yet, leave it empty and update it when one exists

Default section order:
- Purpose
- Ownership
- Local Contracts
- Work Guidance
- Verification
- Child DOX Index

## Style

- Keep docs concise, current, and operational
- Document stable contracts, not diary entries
- Put broad rules in parent docs and concrete details in child docs
- Prefer direct bullets with explicit names
- Do not duplicate rules across many files unless each scope needs a local version
- Delete stale notes instead of explaining history
- Trim obvious statements, repeated rules, misplaced detail, and warnings for risks that no longer exist

## Closeout

1. Re-check changed paths against the DOX chain
2. Update nearest owning docs and any affected parents or children
3. Refresh every affected Child DOX Index
4. Remove stale or contradictory text
5. Run existing verification when relevant
6. Report any docs intentionally left unchanged and why

## User Preferences

When the user requests a durable behavior change, record it here or in the relevant child AGENTS.md

## Child DOX Index

This project is not yet indexed. Before continuing you must scan the project, build the DOX tree and replace this message with the actual index. Go deep and scan files recursively to properly evaluate complexity and create nested DOX files where needed.