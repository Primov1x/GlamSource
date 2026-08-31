# Character-Tab Web-Preview — Stand & Doku

Stand: 0.0.0.165. Betrifft die Web-UI (`Services/WebUiPage.cs`/`WebUiService.cs`) und die
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
- **🧊 Pose einfrieren** — dritte Implementierung (Brio-Technik: Bone-Transforms jeden Frame mit
  Snapshot überschreiben, siehe Abschnitt "Einfrieren: dritter Versuch" unten). Live-Verifikation
  ausstehend.
- **🪄 Transparenter Hintergrund** (experimentell, siehe Limitierung unten).
- **🩺 Preview-Stream-Debug**: `GET /api/preview3d/debug` inline im Tab — Frames encoded/skipped,
  Fehler, aktuelle/letzte Stream-fps, Zoom-Wert, Kamera-Distanz, Draw()-Aufrufrate.

## Einfrieren: vierter Versuch (0.0.0.165, nativer Hook) — im Test

Versuch 3 (0.0.0.162, Bone-Arrays aus `Tick()` überschreiben) ist live gescheitert — Ursache jetzt
verstanden: das Skeleton-Update (Animation-Sampling, `SyncModelSpace`, Physik) läuft im Render-Task
des Spiels (`Framework.TaskRenderGraphicsRender`), also NACH `UiBuilder.Draw` — was wir in Tick()
schreiben, wird danach komplett neu berechnet, bevor es je gerendert wird.

Versuch 4 macht es exakt wie Brio (Quellcode gelesen, `Brio/Game/Posing/SkeletonService.cs`):

- Nativer Hook auf die Engine-Funktion **`UpdateBonePhysics`** (Brios Signatur wortwörtlich
  übernommen; Brios Kommentar dazu: "all the main skeleton stuff like positions, IK and physics is
  done at this point"). Original zuerst aufrufen, DANACH unsere Bones überschreiben — die Engine
  kommt nicht mehr dazu, sie vor dem Rendern erneut zu stampfen.
- Schreiben via **`hkaPose.AccessBoneModelSpace`** statt roher Array-Writes — pflegt Havoks
  Sync-Flags korrekt (auch das macht Brio so, `ApplySnapshot`).
- Anders als Brio (die ein ganzes Entity-System verwalten) frieren wir genau EIN Skeleton ein: den
  CharaView-Klon. `Tick()` refresht jeden Frame den `Render.Skeleton*`-Zeiger des Klons; der Detour
  vergleicht/nutzt nur den. `Release()`/`Dispose()` nullen den Zeiger, bevor der Klon stirbt;
  Dispose disposed den Hook unconditional (lebender Detour nach Plugin-Unload = sicherer Crash).
- Hook wird lazy beim ersten Einfrieren installiert, bei Unfreeze nur disabled. Bricht die Signatur
  bei einem Spiel-Patch, schlägt der Sig-Scan fehl → Freeze still deaktiviert, Fehler im
  Debug-Endpoint (`lastError`) sichtbar, kein Crash.

**Noch nicht live verifiziert.** Ktisis-Alternative (mehrere Engine-Funktionen komplett neutralisieren:
`SyncModelSpace`, `CalcBoneModelSpace`, LookAtIK, KineDriver …) wäre der Plan B, ist aber global
(friert ALLES ein, deshalb ist Ktisis GPose-only) — für unseren Ein-Charakter-Fall ist Brios
Overwrite-nach-Physik der sauberere Single-Point-Ansatz.

## Bekannte Limitierung: Einfrieren funktioniert nicht (Versuche 1+2, historisch)

Ursprünglicher Wunsch: "1x Glam+Char einfangen, dann nur noch angucken" ohne Live-Animation
(Atmen/Wippen), weil die Idle-Drosselung (s.o.) aus der sanften Idle-Animation ein sichtbares
"springt jede Sekunde" macht.

Zwei Versuche, beide live getestet, **keiner hat funktioniert**:

1. `SuspendCharacterCopy(true)` — stoppt nur UNSERE `ModelData.CopyFromCharacter`-Aufrufe.
   Char bewegte sich trotzdem weiter.
2. Zusätzlich `TryonCharaView.DoUpdate = false` gesetzt (echtes Feld, per Reflection gegen die
   echte FFXIVClientStructs.dll gefunden, Name legte "steuert ob CharaView überhaupt fortschreibt"
   nahe) — **auch das hat nichts geändert**, per Nutzer-Rückmeldung "nicht geklappt".

Dritte Recherche (0.0.0.161, gegen die echte FFXIVClientStructs-Struct-Definition, nicht geraten):
`TryonCharaView` hat schlicht **kein** Pause/Freeze-Feld — die 16 dokumentierten Felder sind
State/ClientObjectId/CameraType/Camera/Agent/ModelData/Race/Sex/ZoomRatio/FreeCompanyCrestBitfield/
CharacterDataCopied/CharacterLoaded plus Callbacks, dazu die Sicht-Flags (`DoUpdate`,
`HideOtherEquipment`, `HideVisor`, `HideWeapon`, `CloseVisor`, `HideVieraEars`, `DrawWeapon`) — keins
davon steuert die interne Animation. `CharaView.Update(counter, charRef)` schreibt offenbar bei
jedem Tick in ein internes Character-Objekt weiter, unabhängig von `DoUpdate` (das Feld steuert nur,
ob vom Agent überhaupt neu geholt wird — bestätigt der gescheiterte zweite Versuch).

Interessant, aber keine Lösung: der **verwandte** Struct `CharaViewPortrait` (Portrait-Editor,
anderer Agent) hat tatsächlich `IsAnimationPaused()`/`ToggleAnimationPlayback(bool)` — echtes
Freeze existiert also im Spiel, nur nicht auf dem Struct, das wir benutzen (`TryonCharaView`). Das
umzubauen hieße: kompletter Wechsel der Renderslot-Anbindung von `AgentTryon` auf einen
Portrait-Agent — deutlich größerer Umbau als der Wunsch wert ist, nicht angefasst.

9 Byte am Ende von `TryonCharaView` (0x31F–0x327) sind in FFXIVClientStructs undokumentiert — könnten
theoretisch sowas wie ein Pause-Flag enthalten, aber ohne echte Reverse-Engineering-Session reines
Blindraten an rohen Speicher-Offsets. Nicht versucht (Crash-Risiko, keine Grundlage).

Verbleibende, nicht versuchte Optionen:
- ein komplett eigenes, "gefälschtes" Character-Objekt im Speicher (Nutzer-Idee) — riskant, echte
  Spiel-Speicherstruktur nachbauen, hohe Crash-Gefahr, nicht ohne tiefe native Recherche versucht.
- Umstieg auf `CharaViewPortrait`/dessen Agent statt `TryonCharaView` — hat das gesuchte Feature,
  aber größerer Architektur-Umbau.

**Stand: nicht weiter verfolgt.** Für den ursprünglichen Anwendungsfall (statischer Snapshot)
existiert im **3D-Viewer**-Tab bereits ein funktionierendes, komplett unabhängiges System
(`🧍 Idle`-Button, eigenes `SkeletonPose`-Snapshot statt Live-Objekt-Referenz) — hat andere Nachteile
(Shader-Näherung statt echter Spiel-Renderer), aber friert wirklich ein.

## Bekannte Limitierung: Transparenter Hintergrund

Naiver Chroma-Key: Flood-Fill vom Bildrand, alles zusammenhängend Ähnliche zum Rand wird
transparent. Funktioniert nur, wenn sich der Charakter farblich vom (dunkelgrauen) Studio-Hintergrund
abhebt. Bei überwiegend schwarzer Kleidung vor dem dunklen Hintergrund gibt's **keinen** Farbsprung
an der Silhouette — der Flood-Fill läuft dann einfach durch die Kleidung durch (live beobachtet,
Char komplett bis auf helle Details verschwunden). CharaView liefert keine echten Alpha-/Tiefendaten
zum Trennen von Hintergrund und Charakter (im FFXIVClientStructs-Feld-Dump von `TryonCharaView`
nachgeschaut — nichts Vergleichbares vorhanden). Auch eine gezielt gesetzte Greenscreen-Backdrop-Farbe
(würde das Problem umgehen — Flood-Fill gegen eine Farbe, die im Charakter garantiert nicht
vorkommt) ist keine Option: kein `BackgroundColor`/`ClearColor`/`StudioColor`-Feld in `TryonCharaView`
oder der Basisklasse `CharaView` vorhanden (0.0.0.161 nachgeprüft, echte Struct-Definition), die
Studio-Backdrop-Farbe ist fest im Spiel verdrahtet. Ohne so eine Datenquelle ist das nicht robust
lösbar, nur für bestimmte (helle) Outfits brauchbar.

**Neue Spur (0.0.0.163/164, Recherche)**: Struktur-Indizien sprechen dafür, dass der Alpha-Kanal
des Rendertargets die Charakter-Maske BEREITS enthält und wir sie bisher schlicht weggeworfen haben
(JPEG kennt kein Alpha, der PNG-Pfad überschreibt Alpha mit dem Flood-Fill-Ergebnis):

- Der graue Studio-Hintergrund ist laut FFXIVClientStructs ein **separates UI-Asset**
  (`RaptureAtkModule.CharaViewDefaultBackgroundTexture` = `ui/common/CharacterBg.tex`), das die UI
  HINTER die CharaView-Textur komponiert — dafür muss die Textur selbst Alpha tragen.
- Der Portrait-Editor (`CharaViewPortrait.BackgroundState` 0="nichts") behandelt Hintergrund
  ebenfalls als eigene, abschaltbare Ebene.

Empirischer Test eingebaut: `alphaMin`/`alphaMax` im Debug-Endpoint (Sonde über das rohe
BGRA-Rendertarget). `min==max==255` → Alpha nutzlos, Spur tot. Spread → Maske gratis vorhanden,
Fix wird trivial (Flood-Fill löschen, vorhandenes Alpha in PNG durchreichen). Fallback falls Alpha
flach: Depth-/G-Buffer des CharaView (`RenderTargetManager`, Offset 0x360) als Geometrie-Maske
kopieren — Hintergrund hat keine Geometrie, Depth bleibt auf Clear-Wert (dieselbe Technik wie die
bekannten ReShade-"transparente Screenshots" der GPose-Community).

## Bekannte Limitierung: fremde Agents können den Render-Slot übernehmen

CharaView läuft über einen geteilten Slot (`AgentTryon`, Slot 2). Andere Spiel-UIs, die eine eigene
`...CharaView`-Struct besitzen (per Grep gegen FFXIVClientStructs gefunden, nicht nur die live
beobachteten), nutzen denselben Mechanismus und können den Slot kurzzeitig kapern — dann zeigt die
Vorschau fremden Inhalt (live beobachtet: fremdes Adventure-Plate, fremdes Spieler-Portrait).
`PreviewRenderer.Tick()` prüft `IsAgentActive()` auf inzwischen **neun** Agents und pausiert
währenddessen (kein Schreiben/Rendern, Stream friert auf letztem echten Frame ein):

- `AgentTryon` (Fitting Room, Glamour Plate) — live beobachtet
- `AgentCharaCard` (Adventurer Plate) — live beobachtet
- `AgentBannerParty` / `AgentBannerMIP` (Party-"Gruppenfoto") — live beobachtet
- `AgentColorant` (Dye-Vorschau), `AgentGearSet` (Gearset-Vorschau), `AgentInspect`
  (Charakter-Inspizieren-Fenster), `AgentMiragePrismMiragePlate` (Glamour-Plate-Editor selbst),
  `AgentStatus` — nicht live beobachtet, aber besitzen laut Struct-Dump je ein eigenes
  `...CharaView`-Feld, also selbes Risiko; präventiv mit demselben Guard versehen (0.0.0.161).

Deckt vermutlich immer noch nicht JEDEN denkbaren Übernahme-Weg ab — beim erneuten Auftreten:
Debug-Feld `nativeUiOwnsSlot` checken, sonst hilft nur "🔄 Preview zurücksetzen" von Hand.

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
- CI-Race: zwei Versionsbumps kurz hintereinander gepusht (0.0.0.159, 0.0.0.160) haben zwei
  parallele CI-Läufe gestartet, die sich beim Hochladen ins selbe `LATEST`-Release überholt haben
  → Dalamud-Installer-Fehler "Distributed plugin version does not match repo version". Fix:
  `concurrency`+`cancel-in-progress` im Workflow, bricht den älteren Lauf bei neuem Push ab.
- Slot-Kaper-Guard auf 5 weitere Agents erweitert (`AgentColorant`, `AgentGearSet`, `AgentInspect`,
  `AgentMiragePrismMiragePlate`, `AgentStatus`) — per Grep gegen FFXIVClientStructs gefunden, nicht
  live beobachtet, präventiv abgedeckt (0.0.0.161).

## Offene Punkte

- **Pose einfrieren: dritter Versuch (Brio-Technik, Bone-Overwrite) eingebaut, live-Verifikation
  ausstehend** (siehe Abschnitt oben).
- Transparenz bei dunklen Outfits (siehe Limitierung oben) — kein sauberer Fix in Sicht ohne echte
  Alpha-/Tiefendaten vom Spiel; auch kein Greenscreen-Backdrop-Feld verfügbar.
- Native-Slot-Übernahme (siehe Limitierung oben) — deckt jetzt bekannte 9 Agents ab, evtl. nicht
  alle.
- Pan-Skalierung (`PanCamera`-Aufrufe) ist geschätzt/ungetunt, kein exaktes Einheiten-Wissen.
