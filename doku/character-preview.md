# Character-Tab Web-Preview — Stand & Doku

Stand: 0.0.0.268. Betrifft die Web-UI (`Services/WebUiPage.cs`/`WebUiService.cs`) und die
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
- **Zoom + Pan sind seit 0.0.0.171 DIGITAL** ("Box"-Fix): die Spiel-Kamera bleibt auf ihrem weiten
  Standard-Framing (Char immer komplett im Rendertarget, kein Abschneiden an den unsichtbaren
  Kanten mehr), Mausrad-Zoom (cursor-zentriert, 0.5–16×) und Rechtsklick-Pan sind reine
  Canvas-Transformationen auf dem letzten empfangenen Frame — null Server-Roundtrips, butterweich
  auch während die Idle-Drossel den Stream auf 1fps hält. Trade-off: starker Zoom vergrößert
  576x960-Quellpixel (weich) — schärfer ginge nur über ein vergrößertes natives Rendertarget
  (recherchiert: `Device::CreateTexture2D`-Hook + CharaView-Recreation, separater Schritt falls
  gewünscht). Die alten Server-Endpoints `zoom`/`zoomat`/`pan` existieren weiter, UI nutzt sie
  nicht mehr.
- **🎠 Auto-Drehen**: Turntable-Rotation wie bei Online-Shop-Produktansichten.
- **🔄 Preview zurücksetzen**: volles `Release()`+`Initialize()` — Notausstieg falls die Vorschau
  hängt oder was Falsches zeigt (siehe Bugs unten). Stoppt auch Auto-Drehen.
- **🧊 Pose einfrieren** — **funktioniert (live bestätigt, 0.0.0.165)** und ist seit 0.0.0.166
  **Standard**: ~1s nach Init friert die Pose automatisch ein (verzögert, damit der Snapshot eine
  gesetzte Idle-Pose erwischt statt einen Lade-Frame). Button schaltet ab/wieder an; Reset re-armt
  den Standard. Technik: nativer `UpdateBonePhysics`-Hook, siehe Abschnitt "Einfrieren" unten.
  Kosten: praktisch null (ein Hook-Aufruf + ~200 Bone-Writes pro Frame); zusammen mit der
  Idle-Drossel ist ein eingefrorener Char fast gratis.
- **🪄 Transparenter Hintergrund** (experimentell, siehe Limitierung unten).
- **🩺 Preview-Stream-Debug**: `GET /api/preview3d/debug` inline im Tab — Frames encoded/skipped,
  Fehler, aktuelle/letzte Stream-fps, Zoom-Wert, Kamera-Distanz, Draw()-Aufrufrate.

## Einfrieren: vierter Versuch (0.0.0.165, nativer Hook) — **FUNKTIONIERT, live bestätigt**

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

**Live bestätigt (Nutzer: "freeze klappt")** — und seit 0.0.0.166 Standardverhalten (Auto-Freeze
~1s nach Init, `_autoFreezeCountdown`). Ktisis' Alternativansatz (mehrere Engine-Funktionen global
neutralisieren) wurde damit nie gebraucht.

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

Empirischer Test eingebaut und gelaufen: **`alphaMin==alphaMax==255` (live gemessen, 0.0.0.165)**
— der Alpha-Kanal der Farb-Textur ist flach opak, Spur tot.

**Umgesetzt stattdessen (0.0.0.167): Depth-Buffer-Maske.** Der CharaView-Renderpfad hat ein eigenes
Depth/Stencil-Target (`RenderTargetManager`+0x360, internes Feld, per Raw-Offset gelesen) — der
Hintergrund hat keine Geometrie, seine Depth bleibt auf dem Clear-Wert, jeder Charakter-Pixel weicht
ab: perfekte Silhouetten-Maske, farbunabhängig, dunkle Outfits egal (dieselbe Technik wie die
ReShade-"transparente Screenshots" der GPose-Community). Umsetzung: zweite Staging-Textur
(Single-Buffer — Freeze-Standard macht Frame-Pairing egal), Copy zusammen mit dem Farb-Copy,
Clear-Referenz = Median der vier Bildecken, Alpha=0 wo Depth==Referenz. Format-Dekodierung für
R24G8/D24S8- und R32-Float-Familien; unbekanntes Format fällt auf den alten Flood-Fill zurück
(Fehler in `lastError` sichtbar). Debug-Felder: `depthMaskReady`, `depthFormat`.

**Live bestätigt (0.0.0.167): dunkles Outfit bleibt komplett.** Nachbesserung 0.0.0.168: leichtes
weißes Flackern beim Drehen — Ursache: Depth war anfangs ein einzelner ungepaarter Staging-Buffer
("Freeze macht Pairing egal" — stimmt beim Kamera-Drehen eben nicht: Farbe und Maske aus
verschiedenen Frames lassen am alten Silhouettenrand Backdrop-Pixel durch). Fix: ein Depth-Staging
PRO Farb-Buffer, Depth-Copy direkt VOR dem Farb-Copy desselben Slots auf demselben Context —
D3D11 arbeitet die der Reihe nach ab, Farbe-fertig garantiert Depth-fertig, beide aus demselben
Frame.

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

## Bekanntes Fremdproblem: Browsingway-CEF-Prozess "krank"

Live diagnostiziert (0.0.0.228-Bisect): massives Spiel-Stottern, sobald das Web-Overlay offen war —
ABER: alle Plugin-Subsysteme einzeln UND gleichzeitig abgeschaltet (Kill-Switches: freezehook,
capture, tick, depth, texhook, bwpin) änderte NICHTS, und dieselbe Seite im externen Browser lief
ohne jedes Stottern. Ursache: der CEF-Kindprozess von Browsingway war in dieser Spiel-Session
defekt gestartet (vermutlich GPU-Prozess-Absturz → Software-Rendering). **Fix: Browsingway-Plugin
einmal deaktivieren + aktivieren** (frische CEF-Prozesse) — danach sofort wieder flüssig.
Kein GlamSource-Bug; die Kill-Switches (`POST /api/debug/kill?sys=...&on=...`) bleiben für künftige
Bisects drin.

## Waffen-Anzeige in der Preview — Versuchsprotokoll (Stand 0.0.0.299, 20 Anläufe)

Ziel: eine gezogene/geglamte Waffe im Web-Preview-Klon sichtbar machen. Bisher **nicht
zuverlässig gelöst**. CharaView (`AgentTryon`, slot 2 = TryOn/GearSetPreview) ist der native
Portrait-/Fitting-Room-Renderer — kein normaler Welt-Actor wie bei Brio/Ktisis/Anamnesis, die alle
einen echten gespawnten `Character*` in der Spielwelt bewegen und bekannteste Referenz-Codebasen
für Waffen-Handling sind. Ktisis' eigener CharaView-Einsatz (`Interface/KTK/PreviewNode.cs`, slot 1
= AgentInspect) macht **nur** `ModelData.CopyFromCharacter` — keinerlei Waffen-Sonderbehandlung —
zeigt also vermutlich selbst keine Waffen in seinem Mini-Preview. Kein bekanntes Community-Plugin
baut eine eigene Waffen-Anzeige auf CharaView auf; alle Referenzen (Brio, Ktisis, Anamnesis)
arbeiten mit echten Welt-Actors.

**Bekannt funktionierend, ohne Sonderaufwand:** `ModelData._weaponModelIds` (0x70) wird schon durch
den normalen Pro-Tick-`CopyFromCharacter`-Call korrekt mit der Waffen-ID der Quelle befüllt (per
`/api/debug/weaponstate` live verifiziert: `md.weapon0: id=2027 ...` stimmt exakt mit der
getragenen Waffe). Das Datenmodell hat die Information — nur das Rendering fehlt.

### Chronologie der Versuche (diese Session, ~13 Anläufe)

1. **Native `CharacterSetup.CopyFromCharacter` auf dem Klon** (Brios `ActorSpawnService`-Mechanismus,
   Pfad #13) — erstmals überhaupt ein sichtbares Waffenmodell (245). Vorher: 12 gescheiterte Versuche
   aus früheren Sessions (Items-Pfad, `LoadWeapon`, `SetModelData`, Flag-Clearing auf allen 3 Ebenen,
   echtes `TryOn()`, byte-exakter TryOnItems-Fill — alle dokumentiert als tot, s. History).
2. Waffe sichtbar, aber **eingesteckt** (holstered) — `Timeline.Flags3` Bit 6 (`IsWeaponDrawn`) fehlte.
   Pro-Tick gesetzt (246) → **Duplikat**: Engine wertet den Waffen-Attach jeden Frame neu aus.
3. Bit nur noch **einmalig** nach dem Copy gesetzt (247) → Waffe bewegte sich nicht mit der
   Animation mit (eigenes Skelett, unser Freeze-Hook packt nur den Körper).
4. **Drei Waffen sichtbar.** `WeaponSlot` hat 3 Einträge (MainHand/OffHand/System) — der eine
   Copy-Call spawnt alle drei ungefragt. Erst versucht: `System` hart ausblenden (249) — falsch,
   `System` ist beim Handwerker das **zweite echte Werkzeug**, nicht Müll. Korrigiert (251): pro
   Slot nach `ModelId.Id != 0` filtern statt nach Namen.
5. **Reset räumte nichts weg** — `_weaponDrawn`/`_weaponOnly` überlebten `Release()` (250 Fix).
6. Trotzdem weiter 3 Waffen sichtbar über mehrere Fix-Zyklen — Ursache: **Orphans aus früheren,
   ungeschützten Spawns** (245-248 hatten `CopyFromCharacter` teils ungeschützt gefeuert), die
   Plugin-Reloads überlebten und erst ein **vollständiger Spielneustart** entfernte (bestätigt live,
   analog zum dokumentierten Amboss-Orphan-Bug bei Handwerks-Facilities).
7. Nach sauberem Neustart: Waffe **nicht sichtbar trotz korrekter Daten** (`ModelId` gesetzt,
   `DrawObject` non-null, `IsHidden=false`) — `DrawObject->IsVisible`-Flag manuell gesetzt, aber
   live über mehrere Sekunden stabil `vis=False`, ohne dass sich am Rendering etwas änderte.
8. Gefunden: `DrawDataContainer.HideWeapons(bool)` — die **native** Funktion hinter `/displayarms`.
   Ersetzt die manuelle Flag-Pokerei (254) → Waffe kurzzeitig wieder sichtbar (auch die
   Nicht-Anzeigen-Fälle brauchten denselben nativen Call, nicht nur den Anzeigen-Pfad, 257).
9. **`HideWeapons(false)` zeigt alle 3 Slots pauschal**, überschreibt unseren Modell-ID-Filter direkt
   wieder — Reihenfolge gedreht: nativer Call zuerst, Filter danach (255).
10. Live per `/api/debug/weaponstate` bestätigt: 4. (nie angefasstes) `_unkWeaponData`-Feld leer,
    nicht die Ursache. `DrawObjectData.State`-Byte gedumpt (0x08 für aktive Slots) — kein
    zusätzlicher Aufschluss.
11. **Brios `ActorRedrawService.Redraw()`-Pattern übernommen**: `DisableDraw()` → Copy →
    `IsReadyToDraw()` abwarten → `EnableDraw()`. Ein Guard (`if MainHand.DrawObject == null`)
    verhinderte allerdings, dass der Pfad überhaupt lief — die Engine recycelt den
    Client-Object-Slot des Klons über Plugin-Reloads hinweg, ein altes `DrawObject` blieb non-null
    (bestätigt: 0 Log-Zeilen "weapon path #13" über mehrere Testzyklen trotz Klicks). Guard entfernt
    (262).
12. Danach lief der Redraw-Zyklus tatsächlich (Log bestätigt `EnableDraw()` ~14ms nach `DisableDraw()`),
    aber: **der komplette Klon verschwand aus der Preview**, nicht nur die Waffe. CharaViews
    Render-to-Texture-Pfad verträgt sich offensichtlich nicht mit dem für echte Welt-Actors gedachten
    Draw-Enable/Disable-Zyklus. **Zurückgerollt** (264) — aktueller Stand: reiner Doppel-Copy ohne
    Draw-Zyklus, Waffe wieder unsichtbar, aber Klon stabil sichtbar.

### Fortsetzung (266–298): vom Datenmodell zum Szenegraph

Nach 265 ging die Suche weg von "welche Flags/Reihenfolge" hin zu "was fehlt der Waffe strukturell,
das ein echter Spieler-Charakter automatisch bekommt". Chronologie:

13. **Ktisis' eigene CharaView-Nutzung geprüft** (`Interface/KTK/PreviewNode.cs`) — macht nur
    `ModelData.CopyFromCharacter`, keine Waffen-Sonderbehandlung. Zeigt vermutlich selbst keine
    Waffen. Kein Community-Plugin baut eine Waffen-Anzeige auf CharaView auf.
14. **Glamourers `Interop/WeaponService.cs` recherchiert** (der Kommentar zu Brios Doppel-Copy
    "needed for some tools like Penumbra/Glamourer" führte dorthin). Fund: Glamourer hookt
    `DrawDataContainer.LoadWeapon` und ruft das Original mit `redrawOnEquality=1, skipGameObject=1`
    auf ("controls whether the new weapons are written to the game object or just influence the
    draw object"). Unser eigener toter "Pfad #5" (Monate alt) rief dieselbe Funktion mit lauter
    Nullen auf — nie mit Glamourers Werten getestet. **Pfad #14**: reines `LoadWeapon` mit
    Glamourer-exakten Parametern, KEIN `CharacterSetup`-Copy mehr (267) → Daten perfekt konsistent
    (echte ID, `DrawObject` non-null, `hidden=false`) — **trotzdem nichts sichtbar** ("nicht zu
    sehen"). Wichtige Erkenntnis dabei: `vis=False` stand bei 254 (Pfad #13, wo die Waffe sichtbar
    WAR) genauso — das Flag korreliert nie mit echtem Rendering, seither ignoriert.
15. **Pfad #15**: `CharacterSetup`-Copy (löst vermutlich das Ressourcen-Streaming aus, das der
    Klon sonst nie bekommt) UND danach Glamourer-genaues `LoadWeapon` (268) — kombiniert beide
    Theorien. Baute die Grundlage für alles Folgende.
16. **Pfad #16** (von einer parallelen Session, "genuinely untested" committed): derselbe
    Doppel-Copy + `LoadWeapon`, aber über **15 aufeinanderfolgende Ticks wiederholt** statt einmalig
    — Theorie: der Klon hat keinen normalen Welt-Actor-Tick, das Streaming braucht mehrere Frames.
    Beim Mergen entdeckt: Build-Fix für eine kaputte `Glamourer.Api`-Referenz war im selben Commit.
17. **Echter Spielabsturz** (nativer AV, `C0000005`, im Framework-Tick) — zeitlich nah an einem
    Waffen-Test, aber nicht eindeutig zuzuordnen (ein anderes Plugin war in den letzten Sekunden vor
    dem Crash ebenfalls aktiv). Trotzdem: Pfad #16 feuert bis zu 15× hintereinander 2×
    `CharacterSetup.CopyFromCharacter` + 3× `LoadWeapon` auf demselben nativen Objekt — genau das
    Muster, das dieser Codebase schon einmal einen echten Crash eingebracht hat (siehe
    `_freezeSkeleton = 0`-Kommentar in `Tick()`). **Sicherheits-Guard ergänzt** (283): prüft
    `CharaView.CharacterLoaded` vor jedem Zugriff, bricht bei "nicht geladen" ab — dieselbe Flag,
    die der Auto-Freeze-Mechanismus schon zuverlässig nutzt. Wichtig: ein natives Access Violation
    lässt sich NICHT per C#-`try/catch` abfangen (umgeht .NET-Exception-Handling komplett) — die
    einzige echte Verteidigung ist, die Struktur gar nicht erst anzufassen solange sie nicht bereit
    ist.
18. **Der Guard hat das Feature komplett kaputt gemacht** (Regression, sofort gefunden): Log zeigte
    "aborted, CharaView not CharacterLoaded" bei praktisch jedem Versuch — das 15-Tick-Fenster
    (~0,25s) war kürzer als die Zeit, die `CharacterLoaded` zum Wahrwerden braucht, der Guard hat
    das GESAMTE Retry-Budget beim ersten nicht-geladenen Tick auf null gesetzt. **Fix** (293):
    nicht-geladener Tick wird nur übersprungen (kein Budget verbrannt), separates ~5s-Gesamtlimit
    als echter Notausstieg. Sicherheits-Eigenschaft bleibt, nur die Aufgabe-Schwelle war falsch.
19. **Tiefe native Struktur-Diagnose** (294–297), Schritt für Schritt per `/api/debug/weaponstate`
    live verifiziert, nicht geraten:
    - `DrawObjectData.Weapon*` (Offset 0x08) ist bei uns **non-null** und identisch mit
      `DrawObject*` (Offset 0x18 über `DrawData`) — `Weapon` erbt von `DrawObject`, dieselbe
      Adresse. Das Weapon-Szeneobjekt **existiert wirklich**, `Weapon::Create()`/`Initialize()`
      liefen (widerlegt die erste Theorie "Objekt wird nie erzeugt").
    - `Weapon.AttachTarget` (Offset 0xA58 relativ zum Weapon-Objekt) zeigt **exakt** auf
      `ch->GameObject.DrawObject` (den eigenen Klon-Körper) — korrekt verankert (widerlegt die
      zweite Theorie "falsch angehängt").
    - Waffen-Weltposition (`Object.Position`, Offset 0x50) ist plausibel körpernah
      (z. B. `<-0.22, 0.91, -0.33>`) — widerlegt die Nutzer-Theorie "spawnt out of vision"
      (guter Gedanke, aber die Zahlen sprechen dagegen).
    - **`Object.ChildObject`** (Offset 0x30, clib-Kommentar wörtlich: *"for humans this is a
      weapon"*) zeigt beim Körper auf eine **komplett andere** Adresse — weder Haupt- noch
      Nebenhand-Waffe. Die Sibling-Kette (`NextSiblingObject`, Offset 0x28) wurde 6 Hops weit
      verfolgt — keine der beiden Waffen taucht irgendwo darin auf. `AttachTarget` ist eine
      **Einbahnstraßen-Verknüpfung** (Waffe kennt Elternteil), die Gegenrichtung (Elternteil kennt
      dieses Kind) fehlt komplett.
20. **Pfad #17**: `Object.AddChild(Object* child)` ist eine echte native Member-Funktion (in
    `Object.cs` gefunden, nicht geraten) — der Engine-eigene Weg, genau diese Verknüpfung
    herzustellen. Aufgerufen für jeden befüllten Waffen-Slot direkt nach `LoadWeapon` (298).
    **Ergebnis**: lief 10× ohne Absturz, Log bestätigt die Aufrufe — aber die Sibling-Kette enthält
    die Waffe danach immer noch nicht, und sichtbar ist weiterhin nichts. Entweder hat `AddChild`
    eine Vorbedingung, die nicht erfüllt ist, oder es pflegt intern eine andere Liste als die, die
    wir lesen.

### Aktueller Stand (298)

- Datenmodell durchgängig korrekt: Modell-ID, `DrawObject`, `AttachTarget`, Weltposition — alles
  stimmt und ist live verifiziert, nicht angenommen.
- Waffen-Szeneobjekt existiert nachweislich, ist korrekt am Klon-Körper verankert (Waffe→Körper).
- Die Rückrichtung (Körper→Waffe, `ChildObject`/Sibling-Kette) fehlt — mit `AddChild()` (der
  vermutlich richtigen nativen Funktion dafür) korrigiert, ohne sichtbaren Effekt.
- Kein Absturz seit dem `CharacterLoaded`-Guard (283) + Budget-Fix (293).

### Pfad #18 (eingebaut, nicht live testbar — kein laufendes Spiel verfügbar hier)

Zwei Sachen, die Pfad #17 nie tatsächlich geprüft hat: ob `AddChild()` laut der Basis-`Object`-
Struct wirklich **beide** Enden der Verknüpfung setzt (`weaponObj->ParentObject` auf `bodyObj`,
UND `bodyObj->ChildObject` zurück auf die Waffe) — bisher wurde nur angenommen, dass es
funktioniert, nie nachgeprüft. Falls `AddChild` eine unerfüllte Vorbedingung hat, bleiben beide
falsch. Zusätzlich `OnAddedToWorld()` (der andere in der Doku vermerkte, nie ausprobierte native
Call) direkt nach `AddChild` aufgerufen — falls die Verknüpfung zwar sitzt, das Objekt aber noch
"scharfgeschaltet" werden muss.

Loggt pro Slot: `weaponObj->ParentObject` (erwartet: `bodyObj`) und `bodyObj->ChildObject`
(erwartet: `weaponObj`) — Log-Zeile `weapon path #18` in `dalamud.log`. Kann hier nicht getestet
werden (ClientStructs' `Service<T>`-Auflösung hängt außerhalb eines echten `ffxiv_dx11.exe`, siehe
`GlamSource.Mock`-Notizen) — braucht einen echten Live-Test samt Log-Auszug.

### Nächste Ansatzpunkte (nicht ausprobiert)

- `AddChild()`s tatsächliche Wirkung verifizieren: liest evtl. eine dritte, noch nicht gefundene
  Liste, oder braucht einen zusätzlichen Aufruf danach (z. B. `OnAddedToWorld()`, ebenfalls in
  `Object.cs` gefunden, nie ausprobiert) um die neue Verknüpfung "scharfzuschalten".
- **Inspect-Fenster-Hypothese** (aus 265, weiterhin offen): das normale in-game Inspect-Fenster
  zeigt Waffen anderer Spieler zuverlässig über dieselbe CharaView-Struktur. Der native Weg dahin
  wurde nie isoliert nachgebaut.
- Camera/Culling-Seite nie isoliert getestet (Ortho-Frustum könnte den Waffen-Bounding-Volume
  abschneiden, unabhängig vom Szenegraph-Problem).
- Kein Decompiler/Reverse-Engineering-Werkzeug verfügbar in dieser Umgebung — alle Erkenntnisse
  stammen aus clib-Quellcode-Kommentaren + Live-Speicher-Dumps, nicht aus echtem Disassemble der
  Engine-Funktionen selbst. Ein Blick in Ghidra/IDA auf `AddChild`/`Weapon::Initialize` könnte die
  fehlende Vorbedingung direkt zeigen.

## Offene Punkte

- **Pose einfrieren: dritter Versuch (Brio-Technik, Bone-Overwrite) eingebaut, live-Verifikation
  ausstehend** (siehe Abschnitt oben).
- Transparenz bei dunklen Outfits (siehe Limitierung oben) — kein sauberer Fix in Sicht ohne echte
  Alpha-/Tiefendaten vom Spiel; auch kein Greenscreen-Backdrop-Feld verfügbar.
- Native-Slot-Übernahme (siehe Limitierung oben) — deckt jetzt bekannte 9 Agents ab, evtl. nicht
  alle.
- Pan-Skalierung (`PanCamera`-Aufrufe) ist geschätzt/ungetunt, kein exaktes Einheiten-Wissen.
