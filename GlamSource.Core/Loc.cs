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
        => T(en, Language);

    // "Web-UI eigener Toggle" — item-source description text (ItemDetailService.cs's "Duty Drop:",
    // "Obtained from:" etc.) follows the web page's OWN localStorage-persisted gs_lang toggle, sent
    // explicitly per-request (?lang=), not the ambient `Language` field above (that's the ImGui
    // side's Configuration.Language, a different, independently-toggleable surface — see
    // doku/item-source-detection.md's Localization section on why they're separate). Same
    // dictionary either way: reuses `De`, just takes the language explicitly instead of reading the
    // static field, so a request thread never races the ImGui Draw() thread's ambient `Language`.
    public static string T(string en, string lang)
        => lang == "de" && De.TryGetValue(en, out var de) ? de : en;

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
        // short button labels, centered under the preview image (moved out of the toolbar) — user
        // proposed these two directly ("Vorschau" und "Glamen") over the old, wider toolbar labels.
        ["Apply"] = "Glamen",
        ["Preview"] = "Vorschau",
        ["Apply to Self"] = "Auf mich anwenden",
        ["Requires Glamourer plugin"] = "Benötigt das Glamourer-Plugin",
        ["Copy this snapshot (glamour where set, else actual) to your own character.\nWeapons are skipped."] =
            "Diesen Schnappschuss (Glamour wo gesetzt, sonst Original) auf den eigenen Charakter übertragen.\nWaffen werden ausgelassen.",
        ["Fitting Room"] = "Anprobe",
        ["Queue each slot into the vanilla Fitting Room. Weapons skipped."] =
            "Jeden Slot in die normale Anprobe einreihen. Waffen werden ausgelassen.",
        ["Preview initializing..."] = "Vorschau wird initialisiert...",
        ["Waiting for texture..."] = "Warte auf Textur...",
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
        ["Can contain:"] = "Kann enthalten:",
        ["Unlocked"] = "Freigeschaltet",
        ["Not unlocked"] = "Nicht freigeschaltet",
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
        ["Search duty or boss (e.g. Susano)..."] = "Duty oder Boss suchen (z.B. Susano)...",
        ["No drops known for this duty."] = "Keine Drops für diese Duty bekannt.",
        ["Boss"] = "Boss",
        ["Chest"] = "Truhe",
        ["Elsewhere in the duty (chests & mobs)"] = "Unterwegs in der Duty (Truhen & Gegner)",
        ["Treasure chests along the way (Garland Tools)"] = "Truhen unterwegs (Garland Tools)",
        ["Loading treasure chests..."] = "Truhen werden geladen...",
        ["Shopping list"] = "Einkaufsliste",
        ["Put this piece on your own character via Glamourer (weapons skipped)"] = "Dieses Teil per Glamourer auf den eigenen Charakter legen (Waffen ausgenommen)",
        ["No snapshot yet — target or examine a character first."] = "Noch kein Snapshot — erst einen Charakter anvisieren oder betrachten.",
        ["Not equippable."] = "Nicht anlegbar.",
        ["Recurring event"] = "Wiederkehrendes Event",
        ["One-time event"] = "Einmaliges Event",
        ["active now"] = "läuft gerade",
        ["not running right now"] = "läuft gerade nicht",
        ["no longer obtainable"] = "nicht mehr erhältlich",
        ["live status unknown — check in-game"] = "Live-Status unbekannt — im Spiel prüfen",
        ["Applied."] = "Angewendet.",
        ["Dungeons"] = "Dungeons",
        ["All"] = "Alle",
        ["Mounts & minions"] = "Reittiere & Begleiter",
        ["Drops (Garland Tools)"] = "Drops (Garland Tools)",
        ["Exchange"] = "Tausch",
        ["Hand in:"] = "Einlösen:",
        ["Normal"] = "Normal",
        ["Extreme"] = "Extrem",
        ["Savage"] = "Episch",
        // verified against our own German game data (ContentFinderCondition, e.g. "Shinryu's
        // Domain (Unreal)" -> "Traumprüfung - Heldenlied von Shinryu") — "Fatal" was wrong, that's
        // actually the German word for ULTIMATE duties ("Omega (fatal)", "Dancing Mad (fatal)"),
        // not Unreal.
        ["Unreal"] = "Traumprüfung",
        ["Alliance"] = "Allianz",
        ["Trials"] = "Prüfungen",
        ["Raids"] = "Raids",
        ["Ultimates"] = "Ultimates", // kept English per user request, not "Fatale"
        ["Other duties"] = "Sonstige",
        ["Back to preview"] = "Zurück zur Vorschau",
        ["stops"] = "Stationen",
        ["Total cost:"] = "Gesamtkosten:",
        ["owned"] = "vorhanden",
        ["missing"] = "fehlt",
        ["Everything needed for the shown outfit: best source per piece, grouped by NPC / craft / duty, with what you already own"] =
            "Alles für das gezeigte Outfit: beste Quelle je Teil, gruppiert nach NPC / Craft / Duty, mit dem, was du schon hast",
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

        // "übersetzung fehlt" — item-source description text (ItemDetailService.cs), reached via
        // Loc.T(en, lang) with an EXPLICIT language (not the ambient `Language` field above), see
        // that overload's doc comment. Keyed by just the fixed English words each description is
        // built from, not the full interpolated template — e.g. Tr("Duty Drop") + ": " + name, not
        // one dictionary entry per possible name. ("Extreme"/"Exchange" already existed above.)
        ["Drop"] = "Drop",
        ["Raid"] = "Raid",
        ["Trial"] = "Prüfung",
        ["Dungeon"] = "Dungeon",
        ["Deep Dungeon"] = "Tiefes Gewölbe",
        ["Ultimate"] = "Fataler Raid",
        ["Duty"] = "Duty",
        ["Quest Reward"] = "Quest-Belohnung",
        ["Fate Drop"] = "FATE-Drop",
        ["Mob Drop"] = "Gegner-Drop",
        ["House Vendor"] = "Haushändler",
        ["Obtained from"] = "Erhalten von",
        ["Coffer"] = "Truhe",
        ["Achievement(s)"] = "Erfolg(e)",
        ["Field Op Coffer"] = "Feldeinsatz-Truhe",
        ["PvP Vendor Reward"] = "PvP-Händler-Belohnung",
        ["PvP Season"] = "PvP-Season",
        ["currently available"] = "aktuell verfügbar",
        ["series ended"] = "Season beendet",
        ["Fisher"] = "Angler",
        ["Won from an NPC in a Triple Triad match"] = "In einem Triple-Triad-Match von einem NPC gewonnen.",
        ["Grand Company Quartermaster"] = "Kompanie-Quartiermeister",
        ["rank"] = "Rang",
        ["Quartermaster"] = "Quartiermeister",
        ["Outfit — a glamour set made up of the pieces below. The outfit itself isn't sold or dropped; each piece has its own source."] =
            "Outfit — ein Glamour-Set aus den unten aufgeführten Teilen. Das Outfit selbst wird nicht verkauft oder droppt nicht; jedes Teil hat seine eigene Quelle.",
        ["Company Workshop — crafted as a Free Company workshop project (Company Crafting Log), not by a single crafter."] =
            "Kompanie-Werkstatt — als Freie-Gesellschaft-Werkstattprojekt hergestellt (Kompanie-Fertigungsbuch), nicht von einem einzelnen Handwerker.",
        ["Company Workshop — primed from its base wheel in the Free Company workshop."] =
            "Kompanie-Werkstatt — aus seinem Basis-Rad in der Freie-Gesellschaft-Werkstatt grundiert.",
        ["Cosmic Exploration (Sinus Ardorum) — obtained on the Cosmic Exploration site through its missions, gathering or fishing."] =
            "Kosmische Erkundung (Sinus Ardorum) — auf der Kosmische-Erkundung-Stätte über Missionen, Sammeln oder Angeln erhältlich.",
        ["Spearfishing"] = "Speerfischen",
        ["Island Sanctuary — gathered, grown or crafted on your island (Isleworks); island items can't be taken off the island."] =
            "Eiland-Zuflucht — auf der eigenen Insel gesammelt, angebaut oder hergestellt (Eilandwerk); Inselgegenstände können die Insel nicht verlassen.",
        ["Gathering"] = "Sammeln",
        ["Miner"] = "Minenarbeiter",
        ["Botanist"] = "Kräuterkundiger",
        ["random/hidden yield at Miner or Botanist nodes of that level (Timeworn maps and the like), not tied to one specific node."] =
            "zufälliger/versteckter Ertrag an Minen- oder Kräuterpunkten dieser Stufe (z. B. verwitterte Karten), nicht an einen bestimmten Punkt gebunden.",
        ["Fish — listed in the fishing log, but its spot isn't in the game's FishingSpot table (ocean fishing, the Diadem, or an event/special spot)."] =
            "Fisch — im Angeltagebuch gelistet, sein Angelplatz steht aber nicht in der FishingSpot-Tabelle des Spiels (Hochseeangeln, das Diadem oder ein Event-/Sonderplatz).",
        ["Artifact gear — awarded by that job's level-cap job quests, not sold anywhere."] =
            "Artefakt-Ausrüstung — Belohnung aus den Job-Quests am Levelcap dieses Jobs, wird nirgends verkauft.",
        ["Moogle Treasure Trove event currency — earned from the event's selected duties while it ran; retired afterward."] =
            "Moogle-Treasure-Trove-Eventwährung — während des laufenden Events aus den ausgewählten Duties verdient; danach ausgelaufen.",
        ["Manderville relic weapon step — obtained by progressing the Endwalker relic quest line (Hildibrand / House Manderville), never sold."] =
            "Manderville-Relikt-Waffenstufe — durch Fortschritt in der Endwalker-Relikt-Questreihe (Hildibrand / Haus Manderville) erhalten, wird nie verkauft.",
        ["Anima relic weapon step — obtained by progressing the Heavensward relic quest line (Ardashir, Azys Lla), never sold."] =
            "Anima-Relikt-Waffenstufe — durch Fortschritt in der Heavensward-Relikt-Questreihe (Ardashir, Azys Lla) erhalten, wird nie verkauft.",
        ["Eureka (The Forbidden Land) — Eureka-only gear, exchanged with Gerolt / the Expedition Artisan inside Eureka; not obtainable outside it."] =
            "Eureka (Das verbotene Land) — Ausrüstung nur in Eureka, eingetauscht bei Gerolt / dem Expeditionshandwerker in Eureka; außerhalb nicht erhältlich.",
        ["Deep Dungeon currency — earned as a reward for clearing that Deep Dungeon's floors (progression reward, not a drop or purchase)."] =
            "Deep-Dungeon-Währung — als Belohnung für das Durchqueren der Stockwerke dieses Deep Dungeons verdient (Fortschrittsbelohnung, kein Drop oder Kauf).",
        ["Bozja/Zadnor Resistance relic currency — earned from Critical Engagements and Duels in Bozja/Zadnor, and from Save the Queen relic quest steps."] =
            "Bozja/Zadnor-Widerstands-Reliktwährung — aus Kritischen Gefechten und Duellen in Bozja/Zadnor sowie aus den Save-the-Queen-Reliktquest-Schritten verdient.",
        ["Bozja/Zadnor field currency — earned from Critical Engagements, Duels, and general activity in the Bozjan Southern Front/Zadnor."] =
            "Bozja/Zadnor-Feldwährung — aus Kritischen Gefechten, Duellen und allgemeiner Aktivität an der Bozja-Südfront/in Zadnor verdient.",
        ["Occult Crescent currency — earned from combat participation (Critical Engagements/duels) in the Occult Crescent (South Horn)."] =
            "Occult-Crescent-Währung — durch Kampfteilnahme (Kritische Gefechte/Duelle) im Occult Crescent (Südhorn) verdient.",
        ["Trade-in only"] = "Nur Eintausch",
        ["handed over at"] = "wird abgegeben bei",
        ["the item itself isn't sold there."] = "das Item selbst wird dort nicht verkauft.",
        ["Available for purchase on the Mog Station."] = "Im Mog-Station-Shop erhältlich.",
        ["Unobtainable — the game itself classifies this item as no longer acquirable (e.g. gear for a since-removed equipment slot, such as belts after Stormblood)."] =
            "Nicht erhältlich — das Spiel selbst stuft dieses Item als nicht mehr erwerbbar ein (z. B. Ausrüstung für einen entfernten Ausrüstungsslot, wie Gürtel nach Stormblood).",
        ["Retired dye — patch 7.5 consolidated most named dyes into the Spectrum Dye system. Exchange it for a current dye at a Calamity Salvager."] =
            "Ausgelaufene Farbe — Patch 7.5 hat die meisten benannten Farben ins Spektralfarben-System überführt. Bei einem Katastrophen-Recycler gegen eine aktuelle Farbe eintauschen.",
        ["Materia — converted from fully spiritbonded (100%) equipment, not purchased or dropped directly."] =
            "Materia — aus vollständig geistgebundener (100 %) Ausrüstung gewonnen, nicht direkt gekauft oder gedroppt.",
        ["Garden seed — obtained by cross-breeding compatible seeds in a garden plot, not purchased directly."] =
            "Gartensamen — durch Kreuzen kompatibler Samen in einem Beet erhalten, nicht direkt kaufbar.",
        ["Legacy 1.0 item — predates patch 1.19. Only players who transferred a character from the original FFXIV 1.0 have this; permanently unobtainable otherwise."] =
            "1.0-Legacy-Item — stammt aus der Zeit vor Patch 1.19. Nur Spieler, die einen Charakter aus dem originalen FFXIV 1.0 übertragen haben, besitzen es; sonst dauerhaft nicht erhältlich.",
        ["Aetherial gear — dropped from ARR-era dungeon treasure chests or awarded from Battlecraft Leves. Random loot pool, not tied to one specific dungeon/leve."] =
            "Ätherische Ausrüstung — droppt aus Dungeon-Truhen der ARR-Ära oder als Belohnung von Kampf-Leven. Zufälliger Loot-Pool, nicht an einen bestimmten Dungeon/ein bestimmtes Leve gebunden.",
        ["its augmented upgrade"] = "sein verbessertes Upgrade",
        ["Retired"] = "Ausgelaufen",
        ["replaced by"] = "ersetzt durch",
        ["No longer obtainable itself; the augmented upgrade is still purchasable."] =
            "Selbst nicht mehr erhältlich; das verbesserte Upgrade ist weiterhin kaufbar.",
        ["Diadem (Heavensward exploratory missions, patches 3.1–3.55) random-stat loot — the original Diadem was retired with the 5.1 rework, so this is no longer obtainable."] =
            "Diadem (Heavensward-Erkundungsmissionen, Patches 3.1–3.55) Zufallsstat-Loot — das ursprüngliche Diadem wurde mit dem 5.1-Rework abgeschafft, daher nicht mehr erhältlich.",
        ["PvP season reward — handed out at the end of that Feast / Crystalline Conflict season for the rank reached; not obtainable after the season ended."] =
            "PvP-Season-Belohnung — am Ende jener Fest-/Kristallkonflikt-Season für den erreichten Rang vergeben; nach Season-Ende nicht mehr erhältlich.",
        ["PvP tournament reward — given to placers of that year's Feast / Crystalline Conflict regional championship."] =
            "PvP-Turnier-Belohnung — an Platzierte der regionalen Fest-/Kristallkonflikt-Meisterschaft jenes Jahres vergeben.",
        ["Mog Station — Tales of Adventure (job/retainer level boost), purchased from the online store."] =
            "Mog Station — Abenteuergeschichten (Job-/Gehilfen-Levelboost), im Online-Shop gekauft.",
        ["Chocobo Racing (Gold Saucer) — a racing chocobo's registration, created by breeding or retiring a chocobo at the Chocobo Square; never sold."] =
            "Chocobo-Rennen (Goldener Saal) — die Anmeldung eines Rennchocobos, entsteht durch Züchten oder Pensionieren eines Chocobos am Chocobo-Platz; wird nie verkauft.",
        ["Retired Allagan tomestone — no longer earned from any duty; each expansion rotates the older tomestone types out."] =
            "Ausgelaufener Allagischer-Steintafel-Typ — wird aus keiner Duty mehr verdient; jede Erweiterung löst die älteren Steintafel-Typen ab.",
        ["Skysteel relic tool step — obtained by progressing the Shadowbringers crafter/gatherer relic tool quests (Denys, Foundation); never sold."] =
            "Himmelsstahl-Relikt-Werkzeugstufe — durch Fortschritt in den Shadowbringers-Relikt-Werkzeugquests für Handwerker/Sammler (Denys, Fundament) erhalten; wird nie verkauft.",
        ["Splendorous relic tool step — obtained by progressing the Endwalker crafter/gatherer relic tool quests (Studium, Old Sharlayan); never sold."] =
            "Glanzvolle Relikt-Werkzeugstufe — durch Fortschritt in den Endwalker-Relikt-Werkzeugquests für Handwerker/Sammler (Studium, Alt-Sharlayan) erhalten; wird nie verkauft.",
        ["Cosmotool relic step — earned and upgraded through Cosmic Exploration (Sinus Ardorum) missions; never sold."] =
            "Kosmowerkzeug-Reliktstufe — durch Missionen der Kosmischen Erkundung (Sinus Ardorum) verdient und aufgewertet; wird nie verkauft.",
        ["Starter tool — handed out when unlocking the class at its guild; never sold."] =
            "Starter-Werkzeug — wird beim Freischalten der Klasse an der Gilde ausgehändigt; wird nie verkauft.",
        ["Resplendent tool quest item — made and handed in during the final Skysteel relic tool quests; \"Obsolete\" ones are leftovers from an earlier version of that quest."] =
            "Glanzvolles Werkzeug-Questitem — im Rahmen der letzten Himmelsstahl-Relikt-Werkzeugquests hergestellt und abgegeben; „Obsolete\"-Varianten sind Überbleibsel einer früheren Version dieser Quest.",
        ["Resistance relic weapon step — obtained by progressing the Shadowbringers Save the Queen relic quest line (Bozja); never sold."] =
            "Widerstands-Relikt-Waffenstufe — durch Fortschritt in der Shadowbringers-Save-the-Queen-Reliktquestreihe (Bozja) erhalten; wird nie verkauft.",
        ["Triple Triad card — not in the bundled NPC/drop table (newer card). Typical sources: Triple Triad NPC wins, Gold Saucer card packs, tournaments, or duty drops."] =
            "Triple-Triad-Karte — nicht in der mitgelieferten NPC-/Drop-Tabelle (neuere Karte). Typische Quellen: Triple-Triad-Siege gegen NPCs, Kartenpakete im Goldenen Saal, Turniere oder Duty-Drops.",
        ["Tribal (beast tribe) society quest item — handed out and used within those quests, never sold or dropped."] =
            "Stammes-Questitem (Bestienstamm) — wird innerhalb dieser Quests ausgegeben und verwendet, nie verkauft oder gedroppt.",
        ["No known current source. Often old gear that's been rotated out of its vendor over patches — may still be a rare drop, achievement, or account-bound reward we don't track."] =
            "Keine bekannte aktuelle Quelle. Oft alte Ausrüstung, die im Laufe der Patches aus ihrem Händlerangebot verschwunden ist — kann noch ein seltener Drop, Erfolg oder eine accountgebundene Belohnung sein, die wir nicht erfassen.",
        ["No known current source. Likely an instance-bound currency/point earned by participating in specific content (e.g. combat engagements, event objectives) rather than bought, crafted, or dropped — we don't track those individually."] =
            "Keine bekannte aktuelle Quelle. Vermutlich eine instanzgebundene Währung/Punkte-Art, die durch Teilnahme an bestimmten Inhalten (z. B. Gefechte, Event-Ziele) verdient wird statt gekauft, hergestellt oder gedroppt zu werden — wir erfassen diese nicht einzeln.",
        ["Vendor"] = "Händler",
        ["Merchant"] = "Händler",
        ["Unknown Currency"] = "Unbekannte Währung",
        ["Item Exchange"] = "Item-Tausch",
        ["Shop"] = "Shop",
    };
}
