namespace GlamSource.Core;

// ponytail: chrome-only localization (labels/tooltips/buttons) — NOT item/game data, which is
// already localized for free via Dalamud's IDataManager loading Lumina sheets in the client's own
// game language. Keyed by the English string itself (no id-name bikeshedding, always a readable
// fallback if a key is missing). Dynamic/interpolated strings (item IDs, counts, slot names) are
// intentionally out of scope for now — only literal chrome strings get translated. See
// doku/item-source-detection.md's Localization TODO for what's covered vs. deferred.
//
// NOT used by the web UI — Services/WebUiPage.cs has its own JS `I18N` object (different process,
// a browser, so a shared runtime table isn't practical). Same "chrome only" scope, separate table.
public static class Loc
{
    /// <summary>"en" or "de". ImGui side reads/writes this from Configuration.Language.</summary>
    public static string Language = "en";

    public static string T(string en)
        => Language == "de" && De.TryGetValue(en, out var de) ? de : en;

    private static readonly Dictionary<string, string> De = new()
    {
        ["Open Web UI"] = "Web-UI öffnen",
        ["Re-show the in-game web overlay — use this if it was hidden/closed."] =
            "Web-Overlay im Spiel wieder einblenden — falls es versteckt/geschlossen wurde.",
        ["Lookup"] = "Suche",
        ["Character"] = "Charakter",
        ["Settings"] = "Einstellungen",
        ["Item Lookup"] = "Item-Suche",
        ["Search any item..."] = "Beliebiges Item suchen...",
        ["Clear search"] = "Suche leeren",
        ["Type 3+ characters to search."] = "Mindestens 3 Zeichen eingeben.",
        ["No items found."] = "Keine Items gefunden.",
        ["Target Equipment"] = "Ausrüstung des Ziels",
        ["Slot"] = "Slot",
        ["Worn Item"] = "Getragen",
        ["Glamour"] = "Glamour",
        ["Source"] = "Quelle",
        ["Stain"] = "Farbe",
        ["No equipment data available — pick a target or examine a player."] =
            "Keine Ausrüstungsdaten — Ziel wählen oder einen Spieler betrachten.",
        ["  [!]  DrawData fallback in use — Examine the target for full equipment data."] =
            "  [!]  DrawData-Fallback aktiv — Ziel betrachten für vollständige Ausrüstungsdaten.",
        ["Empty"] = "Leer",
        ["(none)"] = "(keins)",
        ["Unknown"] = "Unbekannt",
        ["Undyed"] = "Ungefärbt",
        ["Pin"] = "Fixieren",
        ["Unpin"] = "Lösen",
        ["Release the pinned snapshot"] = "Fixierten Schnappschuss lösen",
        ["Freeze the current snapshot"] = "Aktuellen Schnappschuss fixieren",
        ["Viewing recent snapshot"] = "Zeigt gespeicherten Schnappschuss",
        ["Pinned"] = "Fixiert",
        ["Live from target"] = "Live vom Ziel",
        ["Click somebody or pick from Recent"] = "Jemanden anklicken oder aus dem Verlauf wählen",
        ["Clear Recent"] = "Verlauf löschen",
        ["Apply to Self"] = "Auf mich anwenden",
        ["Requires Glamourer plugin"] = "Benötigt das Glamourer-Plugin",
        ["Copy this snapshot (glamour where set, else actual) to your own character.\nWeapons are skipped."] =
            "Diesen Schnappschuss (Glamour wo gesetzt, sonst Original) auf den eigenen Charakter übertragen.\nWaffen werden ausgelassen.",
        ["Fitting Room"] = "Anprobe",
        ["Queue each slot into the vanilla Fitting Room. Weapons skipped."] =
            "Jeden Slot in die normale Anprobe einreihen. Waffen werden ausgelassen.",
        ["Preview initializing..."] = "Vorschau wird initialisiert...",
        ["Waiting for texture..."] = "Warte auf Textur...",
        ["Show Weapon/Tool"] = "Waffe/Werkzeug anzeigen",
        ["Drag: rotate · Right-drag: orbit · Wheel: zoom to cursor"] =
            "Ziehen: drehen · Rechts-Ziehen: umkreisen · Rad: zoomen",
        ["Recent"] = "Verlauf",
        ["(none yet)"] = "(noch nichts)",
        ["View stored snapshot"] = "Gespeicherten Schnappschuss ansehen",
        ["Remove from Recent"] = "Aus Verlauf entfernen",
        ["General"] = "Allgemein",
        ["Movable Window"] = "Verschiebbares Fenster",
        ["Allow dragging this window by its body."] = "Fenster per Ziehen am Inhalt verschiebbar machen.",
        ["Show Crafting Savings"] = "Handwerks-Ersparnis anzeigen",
        ["Compare market price vs. crafting cost in the item detail window."] =
            "Marktpreis vs. Herstellungskosten im Item-Detailfenster vergleichen.",
        ["Debug API"] = "Debug-API",
        ["Read-only HTTP API on localhost:23423 for external tools."] =
            "Nur-Lese-HTTP-API auf localhost:23423 für externe Tools.",
        ["Web UI"] = "Web-UI",
        ["HTML alternative UI on http://localhost:23424 — open in a browser or via Browsingway for an in-game overlay."] =
            "HTML-Alternativ-UI auf http://localhost:23424 — im Browser oder via Browsingway als Ingame-Overlay.",
        ["Browsingway found"] = "Browsingway gefunden",
        ["Browsingway not installed — in-game overlay unavailable"] =
            "Browsingway nicht installiert — Ingame-Overlay nicht verfügbar",
        ["Auto-Overlay"] = "Auto-Overlay",
        ["Overlay is created automatically in Browsingway's config;\nGlamSource then sets its URL, shows it when this window opens,\nhides it on close. Drag/resize it like any Browsingway overlay."] =
            "Overlay wird automatisch in Browsingways Konfiguration erstellt;\nGlamSource setzt dann die URL, zeigt es beim Öffnen dieses Fensters,\nversteckt es beim Schließen. Ziehen/Größe ändern wie jedes Browsingway-Overlay.",
        ["3D Preview (experimental)"] = "3D-Vorschau (experimentell)",
        ["Streams the live 3D character view into the web UI, like the inline preview above.\nUses raw D3D11 GPU texture readback — riskier than the rest of GlamSource.\nDisable if you notice crashes; report so it can be fixed."] =
            "Streamt die Live-3D-Charakteransicht in die Web-UI, wie die Vorschau oben.\nNutzt rohes D3D11-GPU-Texture-Readback — riskanter als der Rest von GlamSource.\nBei Abstürzen deaktivieren und melden.",
        ["Auto-Gathering"] = "Auto-Sammeln",
        ["Mount-up distance"] = "Aufsitz-Distanz",
        ["Mount up when the gathering node is farther away than this."] =
            "Aufsitzen, wenn der Sammelpunkt weiter entfernt ist als das.",
        ["Gearsets"] = "Ausrüstungssets",
        ["Miner set"] = "Minen-Set",
        ["Botanist set"] = "Kräuter-Set",
        ["Fisher set"] = "Angel-Set",

        // ---- ItemDetailWindow.cs ----
        ["CRAFTED"] = "HANDWERK",
        ["VENDOR"] = "HÄNDLER",
        ["TRIAL"] = "PRÜFUNG",
        ["RAID"] = "RAID",
        ["DUNGEON"] = "DUNGEON",
        ["QUEST"] = "QUEST",
        ["UNKNOWN"] = "UNBEKANNT",
        ["ACHIEVEMENT"] = "ERFOLG",
        ["MOG STATION"] = "MOG STATION",
        ["PvP"] = "PvP",
        ["TREASURE HUNT"] = "SCHATZKARTE",
        ["SHOP"] = "SHOP",
        ["GATHERING"] = "SAMMELN",
        ["OTHER"] = "SONSTIGES",
        ["TRIPLE TRIAD"] = "TRIPLE TRIAD",
        ["Item not found."] = "Item nicht gefunden.",
        ["Loading prices..."] = "Preise werden geladen...",
        ["SOURCES"] = "QUELLEN",
        ["CRAFTING SAVINGS"] = "HANDWERKS-ERSPARNIS",
        ["Item ID"] = "Item-ID",
        ["iLvl"] = "iLvl",
        ["Set"] = "Set",
        ["Rest of the set:"] = "Rest des Sets:",
        ["← Back"] = "← Zurück",
        ["Wiki"] = "Wiki",
        ["Market prices"] = "Marktpreise",
        ["Gear"] = "Ausrüstung",
        ["Glam"] = "Glamour",
        ["Stain:"] = "Farbe:",
        ["World:"] = "Welt:",
        ["Gather"] = "Sammeln",
        ["Gather (Cooling down...)"] = "Sammeln (Abklingzeit...)",
        ["Materials:"] = "Materialien:",
        ["Pieces:"] = "Teile:",
        ["Duty Drops"] = "Duty-Drops",
        ["Current duty:"] = "Aktuelle Duty:",
        ["Not inside a duty"] = "Nicht in einer Duty",
        ["Search dungeon, trial, raid..."] = "Dungeon, Prüfung, Raid suchen...",
        ["No drops known for this duty."] = "Keine Drops für diese Duty bekannt.",
        ["Boss"] = "Boss",
        ["Chest"] = "Truhe",
        ["Elsewhere in the duty (chests & mobs)"] = "Unterwegs in der Duty (Truhen & Gegner)",
        ["Treasure chests along the way (Garland Tools)"] = "Truhen unterwegs (Garland Tools)",
        ["Loading treasure chests..."] = "Truhen werden geladen...",
        ["All slots"] = "Alle Slots",
        ["All jobs"] = "Alle Jobs",
        ["iLvl from"] = "iLvl ab",
        ["iLvl to"] = "iLvl bis",
        ["Cost:"] = "Kosten:",
        ["Show item details"] = "Item-Details anzeigen",
        ["Gathering..."] = "Sammelt...",
        ["Unknown vendor"] = "Unbekannter Händler",
        ["Right-click to copy"] = "Rechtsklick zum Kopieren",
        ["Open map"] = "Karte öffnen",
        ["Unlocked"] = "Freigeschaltet",
        ["Duty Finder"] = "Duty Finder",
        ["Locked (prerequisites incomplete)"] = "Gesperrt (Voraussetzungen fehlen)",
        ["Start quest chain"] = "Questreihe starten",
        ["Quest unlocked"] = "Quest freigeschaltet",
        ["Open Mog Station"] = "Mog Station öffnen",
        ["Open Crafting Log"] = "Herstellungsliste öffnen",
        ["Bags"] = "Taschen",
        ["Saddlebag"] = "Satteltasche",
        ["Crafted"] = "Handwerk",
        ["Comparison:"] = "Vergleich:",
        ["Market (NQ):"] = "Markt (NQ):",
        ["Crafted cost:"] = "Herstellungskosten:",
        ["Savings:"] = "Ersparnis:",
        ["No market price available for comparison."] = "Kein Marktpreis zum Vergleichen verfügbar.",
    };
}
