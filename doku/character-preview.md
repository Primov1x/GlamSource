# Character-Tab Web-Preview — Stand & Doku

Stand: 0.0.0.159. Betrifft die Web-UI (`Services/WebUiPage.cs`/`WebUiService.cs`) und die
CharaView-Anbindung (`Services/PreviewRenderer.cs`, `Windows/GlamourPreviewWindow.cs`,
`Windows/GlamSourceShellWindow.cs`). Für den Release-Prozess selbst siehe [`../RELEASING.md`](../RELEASING.md).

## Was es ist

Der "Character"-Tab im Web-UI zeigt eine **live gerenderte** Vorschau über das Spiel-eigene
CharaView (dieselbe Technik wie Fitting Room/Adventurer Plate) — kein Nachbau wie der
three.js-basierte "3D Viewer"-Tab, sondern die echte Spiel-Engine. Dadurch: korrekte Shader,
Materialien, Deform — keine der Nachbau-Bugs, mit denen der 3D-Viewer kämpft.

Das Bild wird als MJPEG-Stream (`multipart/x-mixed-replace`) über den bestehenden Raw-TCP-Server
ausgeliefert (`GET /api/preview3d/stream`), im Browser per `fetch()` + eigenem Multipart-Parser auf
ein `<canvas>` gezeichnet (kein `<img src=multipart>` — siehe "Warum kein `<img>`" unten).

## Capture-Pipeline (`PreviewRenderer.cs`)

1. **Doppel-gepufferter, non-blocking GPU-Readback**: zwei STAGING-Texturen im Wechsel,
   `D3D11_MAP_FLAG_DO_NOT_WAIT` — ein unfertiger Copy wird übersprungen statt den Draw()-Thread zu
   blockieren. Eine Sequenznummer pro Buffer verhindert, dass ein später gestarteter, aber zuerst
   fertiger Copy einen bereits gezeigten neueren Frame rückwärts überschreibt (Flackern).
2. **Encode läuft im Hintergrund-Thread** (`Task.Run`), NICHT auf dem Draw()-Thread — nur der
   schnelle Rohpixel-Memcpy + Map/Unmap bleiben inline mit `Draw()` (D3D11-Zwang). Wichtig: ein
   früherer Versuch, den Capture-Call selbst über `Framework.RunOnFrameworkThread` von einem
   HTTP-Worker-Thread zu triggern, hat das Spiel gecrasht (D3D11-State-Korruption mit fremdem
   Plugin-Hook) — Capture-Trigger bleibt fest an `UiBuilder.Draw` gebunden.
3. **Idle-Drosselung**: volle Rate (`EncodeThrottleMs`, ~60fps-Ziel) nur 600ms nach einer echten
   Kamera-Aktion (Drehen/Zoomen/Pan/Auto-Drehen/Item-Wechsel/Stream-Connect), sonst ~1fps
   (`IdleEncodeThrottleMs`). Grund: niemand braucht ein Live-Video eines stillstehenden Charakters.
4. **JPEG normal, PNG nur im Transparenz-Modus** (JPEG kann keine Transparenz).

## Features im Character-Tab

- **Drehen**: Linksklick-Ziehen → Yaw/Pitch der Kamera (nicht des Modells).
- **Zoom**: Mausrad, multiplikativ (prozentualer Schritt, nicht additiv — sonst fühlt sich Zoom
  Richtung Nahaufnahme immer langsamer an). Bereich 0.5–80× (war ursprünglich 6×).
- **Zoom-zu-Cursor**: zoomt Richtung Mausposition, nicht nur zur Bildmitte (`PanCamera`, existierte
  vorher ungenutzt im Code).
- **Rechtsklick-Ziehen**: Pan (Kamerahöhe/-position verschieben) — nützlich sobald reingezoomt.
- **🎠 Auto-Drehen**: Turntable-Rotation wie bei Online-Shop-Produktansichten.
- **🔄 Preview zurücksetzen**: volles `Release()`+`Initialize()` — Notausstieg falls die Vorschau
  hängt oder was Falsches zeigt (siehe Bugs unten). Stoppt auch Auto-Drehen.
- **🧊 Pose einfrieren**: stoppt die Live-Animations-Kopie (`SuspendCharacterCopy`, gab's vorher
  schon intern, war nur nie an die Web-UI angebunden) — Kamera bleibt bedienbar, nur die
  Idle-Animation (Atmen/Wippen) hört auf. Nötig weil die Idle-Drosselung (Punkt 3 oben) aus einer
  sanften Idle-Animation ein sichtbares "springt jede Sekunde" macht.
- **🪄 Transparenter Hintergrund** (experimentell, siehe Limitierung unten).
- **🩺 Preview-Stream-Debug**: `GET /api/preview3d/debug` inline im Tab — Frames encoded/skipped,
  Fehler, aktuelle/letzte Stream-fps, Zoom-Wert, Kamera-Distanz, Draw()-Aufrufrate.

## Bekannte Limitierung: Transparenter Hintergrund

Naiver Chroma-Key: Flood-Fill vom Bildrand, alles zusammenhängend Ähnliche zum Rand wird
transparent. Funktioniert nur, wenn sich der Charakter farblich vom (dunkelgrauen) Studio-Hintergrund
abhebt. Bei überwiegend schwarzer Kleidung vor dem dunklen Hintergrund gibt's **keinen** Farbsprung
an der Silhouette — der Flood-Fill läuft dann einfach durch die Kleidung durch (live beobachtet,
Char komplett bis auf helle Details verschwunden). CharaView liefert keine echten Alpha-/Tiefendaten
zum Trennen von Hintergrund und Charakter (im FFXIVClientStructs-Feld-Dump von `TryonCharaView`
nachgeschaut — nichts Vergleichbares vorhanden). Ohne so eine Datenquelle ist das nicht robust
lösbar, nur für bestimmte (helle) Outfits brauchbar.

## Bekannte Limitierung: fremde Agents können den Render-Slot übernehmen

CharaView läuft über einen geteilten Slot (`AgentTryon`, Slot 2). Andere Spiel-UIs (Glamour-Plate-
Editor, Fitting Room, Adventurer-Plate-Karte `AgentCharaCard`, Party-"Gruppenfoto"-Banner
`AgentBannerParty`/`AgentBannerMIP`) nutzen denselben Mechanismus und können den Slot kurzzeitig
kapern — dann zeigt die Vorschau fremden Inhalt (live beobachtet: fremdes Adventure-Plate,
fremdes Spieler-Portrait). `PreviewRenderer.Tick()` prüft `IsAgentActive()` auf allen vier bekannten
Agents und pausiert währenddessen (kein Schreiben/Rendern, Stream friert auf letztem echten Frame
ein). Deckt vermutlich nicht JEDEN denkbaren Übernahme-Weg ab — beim erneuten Auftreten: Debug-Feld
`nativeUiOwnsSlot` checken, sonst hilft nur "🔄 Preview zurücksetzen" von Hand.

## Warum kein `<img src="...">` für den Stream

`multipart/x-mixed-replace` in einem `<img>`-Tag hat in modernen Chromium-Browsern kein
garantiertes Repaint-Tempo — der Netzwerk-Layer kann mit 70+ fps liefern, während der Browser das
Element intern viel seltener neu malt (kein JS-Hook, um das überhaupt zu messen). Live bestätigt:
Server maß 76fps, sichtbar war trotzdem starkes Ruckeln. Client parst den Multipart-Stream jetzt
selbst (`fetch` + `ReadableStream`-Reader, eigener Boundary-/Content-Length-Parser,
`createImageBitmap` pro Teil) und zeichnet direkt auf ein `<canvas>` — damit bestimmt der Code den
Repaint-Zeitpunkt, nicht der Browser.

## Web-Live-Target-Sync

Der Web-Preview folgte früher nur dann einem frisch im Spiel angeklickten Ziel, wenn zusätzlich das
native ImGui-Fenster offen war (die Dispatch-Logik lief nur innerhalb `DrawCharacterTab()`s eigenem
`Draw()`-Aufruf). Jetzt läuft dieselbe Logik (`GlamSourceShellWindow.SyncPreviewForWeb()`) jeden
`Framework.Update`-Tick, unabhängig von jedem Fenster-Sichtbarkeitsstatus.

## API-Endpunkte (alle nur bei `WebUiLive3DPreview` aktiv)

| Endpoint | Zweck |
|---|---|
| `GET /api/preview3d/stream` | MJPEG-Stream |
| `GET /api/preview3d/debug` | Diagnose-JSON (fps, Fehler, Zoom, etc.) |
| `POST /api/action/preview3d/rotate?dx=&dy=` | Kamera drehen |
| `POST /api/action/preview3d/zoom?delta=` | Zoom (multiplikativ) |
| `POST /api/action/preview3d/zoomat?delta=&px=&py=` | Zoom Richtung Cursor-Position |
| `POST /api/action/preview3d/pan?dx=&dy=` | Kamera verschieben (Höhe etc.) |
| `POST /api/action/preview3d/setitem?slot=&itemId=&stain0=&stain1=` | Hypothetisches Item zeigen (nicht zwingend angezogen) |
| `POST /api/action/preview3d/cleargear` | zurück zur live getragenen Ausrüstung |
| `POST /api/action/preview3d/reset` | volles Release()+Initialize() |
| `POST /api/action/preview3d/freeze?on=` | Pose einfrieren/lösen |
| `POST /api/action/preview3d/transparent?on=` | Transparenz-Chroma-Key an/aus |

## Diese Session gefixte Bugs (Kurzfassung, chronologisch)

- MJPEG-Fix: synchroner GPU-Stall (150ms-Deckel) → async Double-Buffer, ~6-7fps → später 20-70+fps
  je nach Draw()-Rate.
- Metal/Rough-Bake lief nur im No-Diffuse-Pfad, nie bei Diffuse+Id-Map-Kombo (3D-Viewer, nicht
  Character-Tab).
- `atr_bak` (Zopf) wurde nicht mitversteckt, nur `atr_kam` — beide von derselben EQP-Flag gesteuert.
- Racial Deform (Knie/Ellbogen-Lücke) — echter Algorithmus aus TexTools/xivModdingFramework
  dekompiliert und portiert (Ahnen-Vererbung im Skelettbaum), nutzt live gelesene
  Havok-`ParentIndices` statt eigenem `.sklb`-Parser.
- `repo.json`-Release-Asset war 10 Tage / 6 Versionen veraltet (CI hat's nie hochgeladen, nur die
  Zips) — Dalamud-Client sah nie neue Versionen. CI-Fix + Doku in `RELEASING.md`.
- `<img>`-MJPEG-Ruckeln → eigener Canvas-Parser (siehe oben).
- Fremdes Portrait/Adventure-Plate im Preview → `IsAgentActive()`-Check auf allen 4 relevanten
  Agents (`AgentTryon`, `AgentCharaCard`, `AgentBannerParty`, `AgentBannerMIP`).
- Nach Reset: richtiger Körper, aber falsche/leere Kleidung → `CopyFromCharacter` füllt nur
  `ModelData`, nicht `_items` (was der Renderer für Kleidung liest) — einmaliges Nachschreiben via
  `SetItemSlotData` direkt nach jedem Init.
- Zoom fühlte sich Richtung Nahaufnahme immer langsamer an → multiplikativ statt additiv.
- Transparenz-Modus hat echtes Ingame-FPS mitgerissen → Encode lief synchron auf dem Draw()-Thread,
  jetzt Hintergrund-Thread.
- Naiver 1-Pixel-Chroma-Key hat Kleidung/Haare mitgefressen → Flood-Fill vom Rand (nur
  zusammenhängender Hintergrund).

## Offene Punkte

- Transparenz bei dunklen Outfits (siehe Limitierung oben) — kein sauberer Fix in Sicht ohne echte
  Alpha-/Tiefendaten vom Spiel.
- Native-Slot-Übernahme (siehe Limitierung oben) — deckt bekannte 4 Agents ab, evtl. nicht alle.
- Pan-Skalierung (`PanCamera`-Aufrufe) ist geschätzt/ungetunt, kein exaktes Einheiten-Wissen.
