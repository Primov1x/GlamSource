# Item Source Detection — Coverage, Fixes, New Lookups

## Fix: fast-click race leaked stale market price/event onto the wrong item (1.0.20.0)

Found via "invent dumb-user scenarios and test them" — rapid double-click A-then-B before A's
price finished loading. Reproduced live: item 24599 (Far Eastern Schoolboy's Hat, NOT marketable)
showed item 20524's price after clicking 20524 then immediately 24599.

Root cause: `annotateEvent`/`annotateMarket` each run their own `await fetch(...)`, then insert
into `container.querySelector('.header .meta')` — the CURRENT content of the container at insert
time, not render time. If a newer `openItem`/`showItemPanel` call already replaced the panel by
the time the older one's fetch resolves, that insert lands on the wrong (newer) item.
`annotateInventory` doesn't have this problem — it mutates row elements captured in a NodeList
snapshot, which just go harmlessly orphaned if superseded, not re-queried live.

Fix: a simple incrementing token per container (`el._reqToken`, bumped on every new
`openItem`/`showItemPanel` call). `annotateEvent`/`annotateMarket` take the token and check it's
still current right after their own await, before touching the DOM. Verified live: same repro
(20524 then 24599) now shows only 24599's own data, no leak; single-click happy path (20524 alone)
still shows its own price correctly — guard doesn't suppress the normal case.

## Fix: Mock hung ("Not Responding") on any Duty Drops item click (1.0.19.0)

Live report: clicking a mount/minion in the new Duty Drops tab hung the whole Mock process
(confirmed via `tasklist /V`: status "Not Responding"). Root-caused via systematic debugging —
`DrawDutyFinderRow` called `CheckUnlockStatus(cfcRowId)` → `QuestManager.IsQuestComplete()` on
EVERY Draw() frame for any Dungeon/Trial/Raid-type source, not gated behind the Duty Finder
button click at all. `QuestManager.IsQuestComplete` is a raw FFXIVClientStructs `Service<T>` call
— it HANGS (not throws) outside a real `ffxiv_dx11.exe` process, exactly the class of bug
`GlamSource.Mock/Program.cs`'s own header comment already documented (why the real Plugin can't
be loaded in Mock at all). A try/catch already wrapped the call but can't rescue a hang.

Not mount/minion-specific in itself — ANY item whose Sources include a Dungeon/Trial/Raid entry
with a `CfcRowId` would trigger it. The brand-new Duty Drops tab just guarantees hitting one on
literally every click (that's the tab's whole premise), so it surfaced immediately there where it
never had before.

Fix: `CheckUnlockStatus` now short-circuits when `_plugin == null` — set only by the real
`Plugin.cs` (`SetPlugin`), never in Mock, so it's the existing "is a live game actually running"
signal, no new state needed. Real-game behavior (where `_plugin` is always set) is unchanged.

**Flagged, not fixed here**: `GetItemCount`/`GetInventoryBreakdown` (material/cost rows) call
`InventoryManager.Instance()` the same unconditional, unguarded-against-Mock way — same hang risk,
just not yet confirmed to actually fire (the specific item that crashed today had no cost/material
rows). Worth the same guard if it turns out to bite too.

## Fix: bogus 54-item "Rest of the set" for minions (1.0.18.0)

Live report (screenshot): item 20531 "Road Sparrow" (a minion) showed "Rest of the set" listing
Odder Otter, Wind-up Susano, Capybara Pup, and 51 other completely unrelated minions — clearly
wrong, minions have no glamour-set concept the way gear does. Root-caused via `/api/item/20531` in
the mock: TWO separate paths in `GetDetail` both compute `setName`/`setMembers` for ANY item
without checking equippability —
- `Item.ItemSeries` (the real Mog Station bundle field, correct for gear like Abes Attire) matched
  minions to some shared series row too.
- The coffer-unlocked-glamour fallback (`_itemToCofferMap`) also matched: "Materiel Container 4.0"
  is a minion-batch Island Sanctuary coffer, not a glamour one, but the fallback doesn't
  distinguish — fixing only the ItemSeries path left this second path still producing the exact
  same bogus set.

Both gated on `item.EquipSlotCategory.RowId > 0` (the same check `IsEquippable` already uses for
"no Apply to Self on mounts/minions") — sets are gear-only now. Verified live via mock: item 20531
now returns `setName: null`, while a real gear set (16042 Abes Jacket → "Abes Attire", 3 members)
is untouched.

**Not changed, flagged for the user to decide**: the SOURCES list for the same item shows real
structured entries (`Dungeon Drop: Bardam's Mettle`, `HeavenOnHigh: ...-haloed Sack`, `Coffer:
Materiel Container 4.0...`) alongside near-duplicate `Minion: ... (via FFXIV Collect)` restatements
of the SAME facts. That's deliberate (see `BuildSources` "8b." comment: FFXIV Collect entries are
"always shown additively" as a safety net in case a structured source is stale/wrong) — not
touched here without confirming that's still wanted now that it's visibly redundant in practice.

## Item detail inline (no more separate window) + Universalis prices in the web UI (1.0.17.0)

Two ImGui/web parity gaps, both live-reported:

- **"kein extra Fenster"**: `ItemDetailWindow` used to be its own floating `WindowSystem` window —
  every item click popped a SEPARATE draggable window next to the shell, unlike the web UI's
  `#chardetail`, which has always shown item detail inline in the same page. `Draw()` never called
  `ImGui.Begin/End` itself (Dalamud's `WindowSystem` supplied that around registered windows), so
  its content was already a plain, embeddable render method — no rewrite needed, just a different
  caller. Now `GlamSourceShellWindow` calls `_detailWindow.Draw()` directly inside its own
  `Begin/End`, in a side child region that appears next to the tab bar whenever an item is open (new
  `ItemDetailWindow.CloseInline()` replaces the native title-bar X, since there isn't one anymore).
  `Plugin.cs` no longer registers it with `WindowSystem`; the one external trigger (Examine
  right-click, `/glamsource mount`) now also force-opens the shell itself (`shellWindow.IsOpen =
  true`) since the detail has nowhere to render if the shell isn't up. `GlamSource.Mock` mirrors the
  same change (`MockShellWindow`/`Program.cs`) — it used to call `itemDetailWindow.Draw()` as a
  fully separate top-level call with no window boundary of its own at all, arguably worse than the
  real plugin's floating-window behavior.
- **"universalis preise fehlen im webview"**: ImGui's `ItemDetailWindow` has shown World/DC market
  prices since early on (its own `UniversalisService` instance via `Plugin.cs`); the web UI never
  had an equivalent call wired in at all — `WebUiService` had a comment acknowledging the gap but no
  actual `UniversalisService` field. New `GET /api/market/{itemId}` (404 for non-marketable items,
  matches `WebPreviewServer`'s mirror for local testing), new `annotateMarket()` in `WebUiPage.cs`
  following the exact same "fetch after render, insert after `.header .meta`" pattern already used
  for event status/inventory. Verified live against the real Universalis API via the mock: item 5
  (Earth Shard) → `World: 67 Gil · DC (Raiden): 29 Gil`, non-marketable items 404 cleanly and render
  nothing.

## Silent-failure UX: image error surfaced, duty cache prefetched on entry (1.0.16.0)

Two "user gets stressed and uninstalls" scenarios, both root-caused to the same pattern: a network
call that works fine in the common case fails or is slow in an uncommon one, and the user has no
way to tell it's not the plugin's fault.

- **Image fetch never worked, no visible reason** (`ItemImageService`): `LastError` existed since
  1.0.something but only ever surfaced via the hidden `/api/debug/imageerror` endpoint — a user on
  a network/AV that blocks the GAME PROCESS's outbound HTTPS to
  `ffxiv.consolegameswiki.com` (the plugin's own code is fine; it's the exe being blocked
  per-process) saw permanently-blank previews with zero indication why. New optional
  `Action<string>? onError` ctor param, fired once (not per-failure — a permanently blocked
  connection would otherwise spam chat) via a `ChatGui.PrintError` naming network/firewall as the
  likely cause. Wired into both `ItemImageService` instances (Plugin's own for ImGui, WebUiService's
  for the web UI) via the same reporter, so whichever surface hits it first tells the user.
- **Duty Drops felt like it lagged mid-content** (`Plugin.cs`): `GlamSourceShellWindow.DrawDutiesTab`
  already auto-selects the duty you're standing in via `FindDutyByTerritory`, but only while that
  tab is open — opening it for the first time mid-progression triggered the Garland fetch (+
  1 req/sec rate limit) right then, felt like a hitch during a pull. New `ClientState.TerritoryChanged`
  handler (`PrefetchDutyCoffers`) fires `GetDutyCoffersAsync` in the background the moment a duty is
  entered, tab open or not — same disk cache from 1.0.15.0, just warmed earlier. Confirmed via code
  read that image/coffer fetches already run on `Task.Run`, never block the ImGui draw thread — the
  "lag" was real wait time inside the window, not an actual game-thread freeze, but still worth
  killing since it happens at the worst possible moment.

## Outfit shopping list — prototype (1.0.1.0)

Why: the honest "why would nobody use this" review after 1.0 put an outfit-level view first — the
plugin answered slot by slot, click by click, while the real question is "I want that whole look,
what do I need and where". Prototype, deliberately small:

- `GlamSource.Core/ShoppingListBuilder.cs` (pure, 2 unit tests): for every shown slot pick ONE best
  source (vendor with location > craft > nameless exchange > duty > quest > gathering > rest), merge
  items sharing a stop (same NPC = one visit with summed costs, same duty = one run), sum vendor
  costs per currency across the outfit. Items are `CostEntry(count 1)` so the existing inventory
  annotation marks owned pieces green (web via `annotateInventory`, ImGui via
  `RetainerInventoryCache.GetTotal`).
- Web: "Shopping list" button in the Character tab's View row → renders into the right panel
  (`#chardetail`): totals, then one card per stop with NPC row + Map button, pieces (click → item
  detail), cost, materials. `GET /api/shoppinglist` (plugin: `_shell.DebugSnapshot`; mock: the
  editor outfit).
- ImGui: "Shopping list" / "Back to preview" toggle in the Character toolbar; the list replaces the
  center preview while active, same content.
- Verified in the mock (5 stops for the demo outfit: Trophy Crystal / Cosmocredit / Valentione
  chocolate / gil vendors + one Mog Station note, totals per currency). **Needs in-game
  verification**: ImGui toggle + owned/missing colours, Map buttons, real snapshot with mixed
  crafted/duty pieces.
- Known prototype gaps (next steps if it earns its keep): no "already owned → skip" filter, no
  glamour dresser / armoire lookup (only bags/saddlebag/retainers via the existing cache), dyes not
  included, no same-model alternatives when the best source is unobtainable, "Other" stops are
  just the source text.

## Disk cache for wiki images and Garland duty data (1.0.15.0)

Follow-up to 1.0.14's browser-side caching: "was können wir cachen ohne unnötig groß zu werden".
Two things genuinely worth persisting across plugin reloads (their answer never changes for a
given id, unlike Lodestone's live event status, which stays session-only on purpose):

- **Wiki item preview images** (`ItemImageService`): bytes now written to
  `pluginConfigs/GlamSource/ImageCache/{itemId}.img` on first fetch, read from there on every
  later request — skips BOTH the wiki page scrape and the image download entirely, not just the
  browser round trip. Capped at 50 MB, oldest files (by last-write time) evicted back down to 80%
  of budget once exceeded — a few thousand item portraits, can't grow unbounded over months of
  use. `EvictOldestIfOverBudget` is `public static` and unit-tested directly against a real temp
  directory (`ItemImageServiceCacheTests`, no network needed). Verified live: first fetch of item
  24599 took 1.14s (network), second took 0.001s (disk) — file `24599.img` matches the same 3695
  bytes. WebUiService's own separate `ItemImageService` instance points at the same folder (only
  the in-memory URL cache stays per-instance, unchanged from before).
- **Garland Tools duty coffer data** (`GarlandInstanceService`): raw JSON written to
  `pluginConfigs/GlamSource/GarlandCache/{instanceId}.json`, read from there before ever hitting
  the network. No size cap needed — ~800 duties tops, a few KB each. Verified live: 277ms → 3ms
  on a repeat request for the same duty.
- **Deliberately NOT disk-cached**: game icons (`/api/icon/`) — free, instant, straight from local
  Lumina data, caching to disk would only duplicate game files for no gain. Lodestone's event
  headline list — must reflect the CURRENT moment, a disk cache would serve stale "is it running"
  answers across sessions; stays in-memory, one fetch per plugin session, as designed.
- Mock gets the same two folders under its own `bin/Debug/` (gitignored), so the disk-cache path
  is exercised locally too — `WebPreviewServer` and the Mock's own `ItemDetailWindow` instance now
  take an `imageCacheDir` param instead of a bare `new ItemImageService(...)`.

## Icon/preview images now cached by the browser (1.0.14.0)

"Doof jedes mal 'neu' zu laden": every response, including `/api/icon/{id}` and
`/api/itemimage/{id}`, sent `Cache-Control: no-store` — deliberately, per the existing comment,
because Browsingway's CEF page is long-lived and the HTML/JS itself must never go stale across a
plugin update. But that blanket rule also hit icon/preview bytes, which ARE safe to cache: the
same numeric id always returns the same bytes (a search re-render, duty tiles, the same item
opened twice — all refetched the identical image every time). Icon/itemimage responses now send
`Cache-Control: public, max-age=604800, immutable` (one week); every other route (the page itself,
all JSON APIs) keeps `no-store` unchanged. Same split in the mock server. Verified via response
headers: icon endpoint cacheable, `/api/search` and `/` still `no-store`.

## Fix: item panel hung forever on "Lädt..." (1.0.13.0)

Live regression from the 1.0.11 translation sweep, caught by the user clicking a real slot
("hab item geklickt und lädt sich tot"): `renderSource()` already had a local
`const t=(s.type??'').toString()` (the source's type string, e.g. "Vendor") — my i18n patch added
`t('open_craftlog')`, `t('duty_open')`, `t('npc')`, `t('location')`, `t('cost_label')` etc. calls
inside that SAME function scope, which now called the local string instead of the global `t()`
translation function → `TypeError: t is not a function`, thrown synchronously before the fetch
even started (no network request, spinner never resolves — reproduced exactly: clicking "Cosmic
Explorer's Jacket" left "Lädt..." spinning forever, zero `/api/item/` request in the network log).
Renamed the local variable to `srcType` (kept every use, including `esc(t)` → `esc(srcType)`);
`t('key')` calls in that function now correctly reach the i18n table again. Verified live:
the jacket click now renders NPC/Ort/Kosten table (translated) plus its own preview picture.
Checked the rest of the page for the same shadowing pattern (`grep` for other local `t` bindings)
— only `showTab(t)`'s tab-name parameter, which never calls `t(...)` as a function, harmless.

## Wiki preview image: apostrophes broke the per-item name match (1.0.12.0)

User report: item 24599 (Far Eastern Schoolboy's Hat) showed the whole 4-piece outfit group shot
instead of its own portrait — same failure class as the already-documented Abes Attire bug below,
but that fix didn't catch this one. Root cause found and reproduced with a live probe against the
real wiki page: `ItemImageService`'s name-match compared the RAW (still percent-encoded) filename
from the HTML — `Far_Eastern_Schoolboy%27s_Hat_Male.jpeg` — against the plain item name
`Far_Eastern_Schoolboy's_Hat` (literal apostrophe). `%27` never starts with `'`, so the match
always failed for any name containing an apostrophe (or any other char MediaWiki encodes), silently
falling through to "widest image wins" — the exact bug the name-match was written to prevent, just
for names the file-name comparison couldn't see past. Fix: `Uri.UnescapeDataString(file)` before
the `StartsWith` check. Verified live: 24599/24600/24601 (Hat/Hakama/Zori, same set) now return
3695B/8147B/5030B — three distinct real portraits, not the same shared group image.

## "Ultimates" kept English, not "Fatale" (1.0.11.0)

User correction: "Fatale" (our German translation for the Ultimates duty-type folder) should stay
English — `dtype_Ultimate` (web) and `["Ultimates"]` (ImGui `Loc.cs`) now map to `"Ultimates"` in
German too. Checked "Dungeons" while at it: the real German game client already calls the
Dungeon `ContentType` "Dungeons" (verified via `ContentType` sheet, `Language.German`, row 2) —
matches what we already had, no change needed there. Also fixed a real `CS8604` nullable warning
in the event-status endpoint (`WebUiService.cs`, a `null : new {...}` ternary) found while
rebuilding for this change.

## Missing web translations + parked weapon toggle removed (1.0.10.0)

- **"Übersetzungen fehlen überall"**: a systematic sweep (`grep -noE '>[A-Z][a-zA-Z ]{2,30}<'`) of
  `WebUiPage.cs` found literal English strings that never went through the `t()` I18N table —
  "Item ID", "marketable", "Set:", "Rest of the set", "Open Crafting Log", "Duty Finder" (item
  cards), "NPC"/"Location" (both the item-detail and shopping-list source tables), "Map" button,
  "Cost"/"Materials"/"Pieces" section labels (both the item-detail cards and the shopping list),
  "Glam" badge. All now route through new I18N keys (`item_id`, `marketable`, `set_label`,
  `rest_of_set`, `open_craftlog`, `npc`, `location`, `cost_label`, `materials_label`,
  `pieces_label`, `glam_badge`) or the existing `duty_open`. Verified live: DE toggle now shows
  "Item-ID", "Kosten", "Teile hier" etc. **Still out of scope, unchanged, documented already**:
  the item *source description* sentences themselves ("Trade-in only — handed over at...") are
  generated dynamically in `ItemDetailService` from game data and stay English regardless of the
  toggle — translating hundreds of composed sentences was never in scope, only the UI chrome.
- **Parked "Waffe/Nur Waffe" toggle removed** (ImGui Character tab): the disabled "Show
  Weapon/Tool" checkbox (dead since the weapon-preview mechanic was parked, see
  doku/character-preview.md) is gone along with its now-dead `_weaponDrawn` field and Loc entry.
  The parked renderer mechanic itself is untouched — only the inert UI control is gone.

## Duty names capitalized, mount wiki images fixed (1.0.9.0)

Two bugs found live testing 1.0.8.0 ("the Minstrel's Ballad: Zodiark's Fall" — no image, lowercase "the"):

- **Duty display names were lowercase** ("the minstrel's ballad...") because `ContentFinderCondition.Name`
  is written to be embedded mid-sentence elsewhere in the game's own UI — the real Duty Finder
  capitalizes the leading word for its own list. New `CapitalizeFirst` helper (also now used for
  the boss-name capitalization that already existed) applied at both `DutyInfo`/`DutyDetail`
  construction sites — the ONE place duty names are built, so every consumer (web JSON, ImGui) got
  it for free. `DifficultyOf`'s Minstrel's-Ballad check already used `OrdinalIgnoreCase`, unaffected.
- **Mount item preview pictures were missing** (204, e.g. the Lynx of Eternal Darkness Flute):
  `GetWikiPageName` only capitalized the mount name's first LETTER ("Lynx of eternal darkness"),
  but MediaWiki page titles are case-sensitive past the first letter and the real page is
  "Lynx of Eternal Darkness" (verified against the wiki) — every word needs capitalizing except
  minor words ("of", "the", "a", ...). `.NET`'s `TextInfo.ToTitleCase` capitalizes ALL words
  ("Lynx Of Eternal Darkness") which 404s just the same, so a small `TitleCaseWikiName` helper
  does real title case instead. This likely fixes most/all mount preview images, not just this one.

## Duty Drops open button + event availability (1.0.8.0)

- **"Duty öffnen" fehlte im Duty-Drops-Tab**: the banner header now has an "Open in Duty Finder" /
  "Duty Finder" button (web + ImGui) calling the same `AgentContentsFinder.OpenRegularDuty` /
  `POST /api/action/dutyfinder/{id}` the item-detail cards already used.
- **Event item availability** (`GetEventStatusAsync`, `LodestoneEventService`): FFXIV Collect's
  bundled `SourceType=="Event"` entries carry `"<name> (<year>)"` for seasonal events that recur
  every year, or a bare name for one-time collabs/promos — parsed via `EventYearRx`. Live "is it
  running right now": no Lumina sheet or bundled CSV has a calendar, so this does a best-effort
  fetch of the Lodestone's own Atom news feed (`news.xml`, verified reachable + parseable
  2026-09-03 — the HTML news page's CSS classes aren't documented/stable, so the feed was used
  instead) and checks whether the event name appears in a current headline. A fetch failure or no
  match returns an honest "unknown" / "not running", never a wrong guess. Recurring events never
  say "gone" (they come back); one-time events say "no longer obtainable" only once confirmed not
  currently active. Shown under the item name in web (`annotateEvent`, all three item-detail call
  sites) and ImGui (`ItemDetailWindow`, polled `Task<EventStatus?>`, same pattern as duty coffers).
  **Not built**: showing the actual trade NPC for a running event — no sheet reliably links event
  content to its NPC/shop locally, would need per-event manual data.

## Round 3 after the 1.0 review (1.0.2.0)

- **Inventory "where is it" inline** (web + ImGui): the per-item breakdown (bags / saddlebag /
  each retainer) was tooltip-only and easy to miss — now shown muted inline after every
  material, cost and shopping-list row (`annotateInventory` → `.where` span; `DrawEntryRow` +
  `DrawShoppingEntry`). Mock got a fake deterministic `/api/inventory/{id}` so it's visible there.
- **Apply to Self via Glamourer, both UIs**: web Character tab got the outfit button
  (`POST /api/action/glamourer/apply` → `GlamSourceShellWindow.ApplyToSelfFromWeb`, framework
  thread); item details (ImGui `ItemDetailWindow` + web header) got a per-piece button
  (`ApplyItemToSelf`: slot from `EquipSlotCategory`, weapons skipped, `SetItem` IPC, status text
  next to the button). Mock returns a "not available" status.
- **Duty Drops list grouped** like the Duty Finder: content type (Dungeons / Trials / Raids /
  Ultimates / Other) → expansion → level → name. Expansion from the required level (ARR ≤50,
  HW ≤60, SB ≤70, ShB ≤80, EW ≤90, DT ≤100) — correct for every duty incl. ultimates and needs no
  `TerritoryType.ExVersion` read (DalaMock can't resolve TerritoryType).
- **Needs in-game verification**: per-piece Glamourer apply (slot mapping, status), web outfit
  apply, inline breakdown with real retainer names.
- **Duty Drops drill-down (1.0.3.0)**: "kompakter, Kacheln zum Klicken" — the long grouped list
  became a Duty-Finder-like drill-down: content-type tiles (with duty counts) → expansion tiles →
  duty tiles → drops, breadcrumb "All › Dungeons › Heavensward" to go back up. A search term
  bypasses it (flat grouped list). Selecting a duty (click or auto-detect) lands the list in that
  duty's folder. ImGui mirrors it with Selectables per level and SmallButton breadcrumbs.
- **Tile pictures + mounts on top (1.0.4.0)**: folder tiles carry the Duty Finder category icon
  (`ContentType.Icon`: 61801 dungeons, 61804 trials, 61802 raids, 61832 ultimates) and the banner
  of the folder's newest duty as background; ImGui rows get the same banner thumbnail. Mount and
  minion drops are lifted out of the boss/chest lists into a "Mounts & minions" section at the top
  with the wiki preview picture (`DutyDrop.Kind`, `DutyDetail.Featured`). Mount detection =
  reverse of the bundled `MountItemMap.csv` — NOT `ItemUICategory 63`, which is the generic
  "Other" bucket (gil, seals, whistles alike); minions = `ItemUICategory 81`. Verified on The
  Whorleater (Extreme): Enbarr Whistle featured, Leviathan's 22 chest items stay below.
- Glamourer note (asked): its IPC covers equipment + customize only — mounts and fashion
  accessories (e.g. Wings of Resolve) can't be applied; the game itself has no mount glamour.
  Possible later: "open in the Mount Guide" button for mount items.
- **Tab order + cog (1.0.5.0)**: Character is the landing tab (web `showTab('character')` on load,
  ImGui first tab), then Item Search, then Duty Drops; Settings left the nav and became a cog —
  web: title-bar button toggling `view-settings`, ImGui: trailing tab item drawn with
  `UiBuilder.IconFont` (`FontAwesomeIcon.Cog`, tooltip "Settings"). Gil cost rows (itemId 0) now
  show the real Gil icon (Item 1, icon 65002) in web + both ImGui row renderers.
- **Boss search, mount previews, no Apply on mounts, cache race (1.0.6.0)**: Duty Drops search
  matches boss names too ("susano" → the Pool of Tribute; `DutyInfo.Bosses` from `DungeonBoss` →
  `BNpcName`, 321/364 duties have boss data), the matching boss shows on the tile / ImGui row.
  Mount items' preview picture now comes from the wiki's MOUNT page (`GetWikiPageName`: reverse
  `MountItemMap` → `Mount.Singular` (EN) — "Enbarr" has `Enbarr_Image.png`, "Enbarr Whistle" only
  an icon). `ItemDetail.IsEquippable` (EquipSlotCategory > 0) gates the "Apply to Self" button in
  both UIs — mounts, minions, materials don't get one (Glamourer can't apply them anyway).
  `LuminaItemSourceService.GetSources` and `ItemDetailService.GetDetail` now lock their caches:
  the mock crashed on a corrupted Dictionary when the draw thread and a web request thread wrote
  at the same time — the plugin has the same two threads (framework + WebUiService requests).
  Duty drill-down gained the Duty Finder's sub-folders between type and expansion — Trials →
  Normal / Extreme / Unreal, Raids → Normal / Savage / Alliance (`DutyInfo.Difficulty`: name suffix,
  alliance via `ContentFinderCondition.AllianceRoulette`); the level is skipped for types with a
  single difficulty (dungeons). Both UIs.
- **All duties + sheet-derived drops (1.0.7.0)** — "Dawntrail Extreme nur 3 Stück": the list only
  had duties with LuminaSupplemental drop rows (tables end ~7.1). Now every dungeon / trial / raid /
  ultimate from `ContentFinderCondition` is listed (release order = row id order, "nach Release
  sortieren"); duties without local rows get: (1) Garland fight coffers — but Garland has NOTHING for
  7.2+ either (checked 20103 Windward Wilds EX: no fights/coffers), (2) mounts/minions FFXIV Collect
  attributes to the duty by English name (`DutyDetail.Featured`), (3) **exchange shops from the
  game's own `SpecialShop` sheet** (`DutyDetail.Exchanges`): tokens the duty drops (Dreadwyrm Totem)
  and the cost item that buys the duty's mount are the totems; everything they buy is the weapon /
  gear list. Verified: UCoB → "Totem Gear (Bahamut)" 15 weapons, Everkeep EX → Zoraal Ja 20 +
  Wings of Resolve, Windward Wilds EX (MH Wilds collab) → Arkveld certificate 21 + Felyne mount. The
  old boss-name heuristics (`MatchCfcForBoss` stage 3 partial match) mis-filed new trials (Cloud of
  Darkness → Windward Wilds) and are NOT used here. Extremes named "The Minstrel's Ballad: …" now
  count as Extreme (were under Normal). Per-boss chests merged into one (the coffer 1/2/3 split
  means nothing to a player). xivapi v2 checked on request: Boilmaster = sheet/search/asset, raw
  game sheets only — nothing beyond the local client. Weapons can now be applied via Glamourer
  (`ApiEquipSlot.MainHand/OffHand`; Glamourer reports job mismatches itself).

## Duty Drops tab (0.0.0.312) — current-duty auto-detect + Duty Finder style browse

Was the TODO above this line; built for ImGui and Web UI in the same change.

- **Data**: `ItemDetailService.ListDutiesWithDrops()` inverts the existing item→duty map
  (`_itemToDutyMap`) once; `GetDutyDetail(cfcId)` keeps the four LuminaSupplemental CSVs whole
  (`DungeonBoss` → boss name via `BNpcName`, `DungeonBossDrop`, `DungeonBossChest` grouped by
  `FightNo`/`CofferNo`, `DungeonDrop` = duty-wide) and returns banner (`ContentFinderCondition.Image`,
  e.g. 112001 for Sastasha — same `ui/icon/` path as item icons, so `/api/icon/` and
  `GetFromGameIcon` both serve it), level, per-boss sections, general list, territory + map ids.
  `FindDutyByTerritory(territoryTypeId)` = auto-detect (prefers a CFC we have drops for).
- **Chests along the way** (`GetDutyCoffersAsync`): Garland Tools' instance doc
  (`garlandtools.org/db/doc/instance/en/2/{id}.json`, `GarlandInstanceService`, cached per id)
  lists every treasure coffer WITH map coordinates — nothing local has that. Verified: Garland's
  instance id == `ContentFinderCondition.Content` (Sastasha 4→4, Syrcus Tower 102→30011, Castrum
  Fluminis 537→20055). Coffers whose items are fully covered by a boss chest are dropped (they're
  the boss coffers again); the rest show as "Chest N · (x, y)" with the existing map-flag button
  on the web side. xivapi v2 was checked too: sheet data only, no drop tables — not used.
- **Web**: new "Duty Drops" tab — duty tiles with banner thumbnail + type/level/drop count,
  search box, selected duty = big banner header, then Boss 1 — Chopper / Chest … sections,
  "Elsewhere in the duty", then the Garland chests; item click opens the detail in-tab.
  `/api/duties`, `/api/duty/current`, `/api/duty/{id}`, `/api/duty/{id}/coffers` (plugin + mock;
  mock's current is always 0, and its DalaMock Lumina can't resolve `TerritoryType→Map`, so
  `MapId` is 0 there — `Safe()`-guarded).
- **ImGui**: `TabId.Duties` — current-duty line, filter box, duty list, banner, boss sections,
  chests loaded via a polled `Task` (no blocking on the draw thread). `Plugin.ClientState.TerritoryType`
  read directly in Draw (framework thread).
- **Needs in-game verification**: auto-select when entering a duty (web: on tab open; ImGui: on
  territory change), banner rendering via `GetFromGameIcon` for 112xxx ids, the map-flag button on
  chest coords inside the instance, Garland fetch latency (6 s timeout, failure = no chest section).
- **Not local**: the "what drops here" popup plugin the user mentioned isn't among the reference
  checkouts; Garland covered the need.

## Localization (DE/EN, manual toggle) — chrome done, ItemDetailWindow deferred

**Scope, confirmed correct**: item/data names (item names, source descriptions built from
`Item.Name`, NPC/zone names, etc.) are **already localized for free** — Dalamud's `IDataManager`
loads Lumina Excel sheets in the client's own game language, no code of ours involved (confirmed:
nowhere in this repo do we pass a `ClientLanguage` to `GetExcelSheet<T>()`). Only the **UI chrome**
— our own hardcoded labels/tooltips/section headers/button text — needed a translation table.

**Trigger decided**: manual toggle, not automatic from `ClientLanguage`/`UiLanguage` — per explicit
user spec ("buttons zum umschalten"). Persisted per surface: `Configuration.Language` (`"en"`/`"de"`)
on the ImGui side, `localStorage['gs_lang']` on the web UI side (separate processes, no shared
runtime — a browser page can't read the plugin's Dalamud-persisted config directly).

**Done:**
- `GlamSource.Core/Loc.cs` — chrome-only translation table for ImGui, keyed by the English string
  itself (no id bikeshedding, always a readable fallback). `Loc.T(en)` reads `Loc.Language`, synced
  from `Configuration.Language` every `Draw()`.
- `Windows/GlamSourceShellWindow.cs` — every literal (non-interpolated) chrome string wrapped in
  `Loc.T(...)`: tab labels, section headers, toolbar buttons/tooltips, equipment table headers,
  Settings tab, Recent sidebar. DE/EN toggle button top-right of the window (closest ImGui gets to
  "next to minimize" — the actual collapse button belongs to Dalamud's own title bar, plugins can't
  add to it).
- `Services/WebUiPage.cs` — separate JS `I18N` object (own runtime, own scope decision, see the
  file's own comment) covering titlebar tooltips, nav tabs, search placeholder, character-tab
  hints/labels, 3D-preview toolbar, Settings tab, and static empty/loading-state strings scattered
  through JS template literals. `data-i18n`/`data-i18n-title`/`data-i18n-ph` attributes for
  markup-side text, `t(key)` calls for JS-generated text. Toggle buttons (EN/DE) in the title bar
  next to the minimize/lock/hide buttons, as specced. Verified live in `GlamSource.Mock`: Lookup,
  Character, and Settings tabs all round-trip EN↔DE correctly, including the dynamically
  re-rendered Settings body.
- Dynamic/interpolated strings (item IDs, slot enum names, result counts) are **intentionally out
  of scope** — only literal chrome strings are translated. This was a deliberate scope cut, not an
  oversight.

**Also done** (follow-up pass, same session): `Windows/ItemDetailWindow.cs` — all `SourceStyles`
badge labels (CRAFTED/VENDOR/TRIAL/RAID/DUNGEON/QUEST/etc., wrapped once inside `DrawBadge()` so
all 4 call sites get it for free), header/meta line, set-member section, Wiki/Market/Back/Gather
buttons, slot-context row, market price box, materials/cost labels, gathering tooltips, NPC
row/map tooltips, Duty Finder/quest-chain/Mog-Station rows, crafting-savings comparison block.
Interpolated prefixes (`"Item ID {id}"`, `"iLvl {n}"`, `"Slot: {name}"`) got their literal word
translated too, not just skipped — only truly dynamic content (counts, names, IDs themselves)
stays untranslated. Verified: build clean, 46/46 tests, `GlamSource.Mock` runs without exception.
Not visually screenshotted (native ImGui window, no screenshot tool for it in this environment) —
code-reviewed against the already-proven `GlamSourceShellWindow.cs` pattern instead.

Running log of the item-detail/source pipeline (`GlamSource.Core/ItemDetailService.cs`) work
from this session. Audited via `GlamSource.Mock`'s local test server against real `D:\FF\game`
data (500-item random samples, and targeted spot-checks), not guesswork.

## Craft-button bug (root cause, fixed)

`ItemSourceDetail.Type` (an enum) was serializing as a bare number in both `WebUiService.cs` and
`GlamSource.Mock/WebPreviewServer.cs`. The frontend (`WebUiPage.cs`) sniffs source type via
regexes (`/craft/i`, `/vendor|shop/i`, `/quest/i`, `/trial|raid|dungeon/i`) against that string —
a number never matches, so the "Open Crafting Log" button and all vendor/quest/duty badge
coloring silently never appeared for **any** item, not just craftables. Fixed by adding
`JsonStringEnumConverter()` to both services' `JsonSerializerOptions`.

## Source-coverage audit — before/after

Baseline: random 500-item sample, 203 items (40%) landed on the generic "no known source"
fallback. After this session's fixes: 96 items (19%) — roughly half of the previously-unexplained
items now get an accurate, specific message instead of a shrug.

New detection strategies added to `BuildSources()`, each verified against a real example before
shipping (wiki cross-checks, live XIVAPI v2 lookups, or direct Lumina structure dumps via a
throwaway debug endpoint in `WebPreviewServer.cs`, removed after each check):

| Category | Signal | Example verified |
|---|---|---|
| **Unobtainable slot** | `Item.ItemUICategory.Name == "Unobtainable"` | All 21 hits in the audit were belts — Stormblood (4.0) removed the belt slot entirely |
| **Legacy 1.0 gear** | Name starts with `"Dated "` / `"Weathered "` | Predates patch 1.19; only players who transferred a 1.0 character have these, confirmed via Gamer Escape's Dated/Weathered categories |
| **Aetherial gear** | Name starts with `"Aetherial "` | ARR-era dungeon treasure chest / Battlecraft Leve loot pool — 2nd most common leftover pattern after Dated/Weathered |
| **Retired → Augmented** | `FindItemIdByExactName("Augmented " + item.Name)` finds a match | Verified for Ironworks Armguards of Maiming → retired patch 5.3, only the Augmented version is still sold (cross-checked against XIVAPI v2 live data AND the wiki, both agree) |
| **Retired dye** | `ItemUICategory == "Dye"` but `ItemSearchCategory.RowId == 0` | Patch 7.5 consolidated most named dyes into the Spectrum Dye system (Calamity Salvager exchange) |
| **Materia** | `ItemUICategory == "Materia"` | Not sourced from a place — converts out of 100%-spiritbonded gear |
| **Garden seeds** | `ItemUICategory == "Gardening"` | Cross-bred in a garden plot, not purchased |
| **Fishing** | New `FishingSpot` sheet cache (`BuildFishingCache`) | `FishingSpot.Item[]` holds raw Item RowIds directly (no `GatheringItem` indirection like Botanist/Miner nodes) — **can't be verified inside `GlamSource.Mock`**, DalaMock's bundled Lumina.Excel has a column-hash mismatch on this sheet. Needs in-game check after deploy. |
| **Triple Triad cards** | New `TripleTriadCardNpcs.csv` (scraped from [FFTriadBuddy](https://github.com/MgAl2O4/FFTriadBuddy), MIT) | Lumina's own `TripleTriadCard`/`TripleTriadResident` sheets don't expose reward→NPC linkage in this build (fields are just `Unknown0..5` / a bare `Order` column) — no safe way to derive it locally. Chain: `Item.ItemAction.Data[0]` → `TripleTriadCard` RowId → CSV → NPC name/zone/coords. Verified: item 9803 (Rhitahtyn sas Arvina Card) → "Indolent Imperial" @ Mor Dhona (11.9, 17.4), matches the wiki exactly. |
| **Minions & mounts** | New `CollectSources.csv` (FFXIV Collect API, non-commercial use, attribution in the source text) | No Lumina sheet exposes minion/mount unlock sources at all — every mechanism FFXIV has (FATE gold completion, Hunting Log, achievement, Gold Saucer currency, promo/collector's-edition item) shows up here. 930 items covered (583 minions + 353 mounts). |

### Confirmed-correct-as-is (not bugs)

- **Behemoth Barding** (id 6031) — ARR Collector's Edition bonus, genuinely never obtainable any
  other way. Generic fallback text is accurate.
- **Doman Whetstone** (id 10322) — retired patch 5.3, no successor item (unlike the
  Augmented-gear pattern), so falls to the generic fallback correctly.

### Explicitly not pursued (real, but low-value or genuinely blocked)

- **FC Workshop items** (Grade N Wheel of Company/Industry, Counterfoils) — separate Company
  Workshop crafting system, not the standard `Recipe` sheet. 1-4 items per pattern, not worth a
  dedicated detector.
- Ishgard Restoration / Bozja / Chocobo Racing one-offs — no shared `ItemUICategory` signal (all
  land under generic "Miscellany"/"Other"), each would need individual research for 1-2 items.

## Source-coverage audit #2 — full sheet (2026-09-02, 0.0.0.303)

Not a sample this time: every named item (50,360) through `GetDetail`, counting the ones that end
on the generic step-12 fallback. Probe lives in `tools/CoverageAudit/` (`dotnet run -c Release`,
~9 min, writes `nosource.tsv` + a per-UI-category histogram).

**Before: 8,895 items (17.7%) → after: 2,407 (4.8%) — 6,488 items now get a specific answer.**

Found along the way (all verified against the real sheets via throwaway RawRow dumps):

| Fix / detector | Signal | Verified example |
|---|---|---|
| **Nameless SpecialShops were skipped** (bug) | `FindSpecialShopSources` did `continue` on an empty shop name — the Eureka gear exchange, Padjali/Empyrean, +1/+2 gear all live in nameless rows | 22238 Anemos Pacifist's Armguards → row 1769824, name "" |
| **Crash: quest issuer as EObj** | `ENpcResident.GetRow(2011341)` threw `ArgumentOutOfRange` for 20795 Brightlily Seeds → `GetRowOrDefault`, plus a try/catch net around `BuildSources` and `Safe()` around every lazy index (DalaMock's older Lumina throws `MismatchedColumnHash` on some sheets) | 20795 |
| **Grand Company seal shop** | `GCScripShopItem` (subrow sheet) → parent `GCScripShopCategory.GrandCompany`; seal item ids 20/21/22 | 3588 Serpent Private's Bracers → Twin Adder, 1050 Serpent Seals, rank 3 |
| **Outfits ("X Attire")** | `MirageStoreSetItem` keyed by the outfit item id lists the pieces → card with a clickable "Pieces" list | 45416 Hidefiend's Costume Attire → 33063–33067 |
| **FC workshop** | `CompanyCraftSequence.ResultItem`; "Primed X" → base wheel via exact name | 9654 Grade 2 Wheel of Productivity |
| **Cosmic Exploration** | `WKSItemInfo` membership | 45963 Cosmic Chimera Worm, 50173 Craggy Sunfish |
| **Spearfishing** | `SpearfishingItem` (item, level, territory) | — |
| **Island Sanctuary** | `MJIItemPouch` + Isleworks/Islekeep's/Island prefixes | 37553 Island Branch |
| **Hidden gathering yields** | in `GatheringItem` but no `GatheringPointBase` references it | 6688 Timeworn Leather Map |
| **Fish log fallback** | `FishParameter.IsInLog` without a `FishingSpot` (ocean / Diadem / event) | — |
| **Trade-in only** | item appears only as a COST in SpecialShop entries → "handed over at X to receive Y" with a link to Y | 21393 Ryumyaku Bracelet → Dai-ryumyaku; 38211 Irregular Tomestone |
| **Name/description patterns** | PvP season kits/chits/trophies (`^Season …`), FRC/CCRC certifications, Tales of Adventure, chocobo registrations, retired/irregular tomestones, Diadem (`Rarity == 7`), Antiquated AF, Manderville/Anima/Resistance relic weapons, Skysteel/Splendorous/Cosmotool relic tools, Novice's tools, "Eureka gear." description, "society quests" description, Triple Triad fallback | see comments 11c–11j in `ItemDetailService` |
| **iLvl bug** (Nebenbefund) | `ItemDetail.ItemLevel` was `LevelEquip` (required level), UI labels it "iLvl" → `LevelItem.RowId` | Ironworks Helm: 50 → 120 |

**ImGui parity (0.0.0.304):** the Lookup tab got the same slot / job / iLvl filters
(`GlamSourceShellWindow.DrawLookupTab` → `RunLookup`, shared `ItemSearchIndex`), results show the
iLvl, pure-digit queries still resolve an item ID directly. `ItemDetailWindow` already rendered
`Materials` / `Costs` / `SourceItemId` generically, so the new cards (outfit pieces, GC seals,
trade-in link) needed only the "Pieces:" label. **Needs in-game verification** (the mock harness
draws its own windows): filter combos render/react, first filtered search's one-frame index-build
hitch is acceptable, outfit card shows "Pieces:" with clickable rows, GC quartermaster card shows
the seal cost.

`LuminaSupplemental.Excel` bumped 5.1.0 → 5.1.4 (no code change needed); its duty-drop data still
ends around item id 48k, so 7.2+ raid gear (Vana'dielian, Praemagitek — 90 items) stays on the
fallback. Also still open: Ornate Ironworks crafting/gathering gear (97), Augmented Ala Mhigan /
Dragonsung (68), Mistic/Mistwake 7.x sets (110), beast-tribe coffer keys (27), old Skybuilders'
fish (Ishgard Restoration Diadem) — no local sheet signal found for any of them.

## Item Set grouping (Mog Station bundles)

`Item.ItemSeries` is the game's own bundle grouping — verified against the real Mog Station store
(`store.finalfantasyxiv.com`, a fully client-rendered SPA with no search feature, so this couldn't
be scraped in bulk) and against Garland Tools' own item data (`seriesId` + member list matched
exactly). `ItemDetail.SetName` / `ItemDetail.SetMembers` surface this — no scraping needed, it's
already in the Lumina data everyone else was cross-referencing against.

The exact Mog Station **product page URL** remains unavailable: no site (Gamer Escape, Garland
Tools) has it compiled, and the store itself has no search to construct a deep link from. Skipped
as not worth ~300+ individual browser-rendered page scrapes for a link the set-name+member-list
already makes mostly redundant.

## Item preview images

`GlamSource.Core/ItemImageService.cs` — on-demand scrape of `ffxiv.consolegameswiki.com`'s
per-item infobox image (the "worn" screenshot, not the icon), cached in memory per item id. Not a
bulk pre-scrape: most of the ~40k items are never opened, so only fetch when a user actually views
one. New endpoint `GET /api/itemimage/{id}` (both `WebUiService.cs` and the Mock), lazy-loaded
into the item panel after the rest of the detail already rendered (`loadItemPreviewImage()` in
`WebUiPage.cs`) — never blocks the item view on a network round-trip.

## Mount lookup ("who's mount is this")

`GameDataService.GetMountId(IGameObject)` reads `Character.Mount.MountId` natively
(`FFXIVClientStructs.FFXIV.Client.Game.Character.Character`, field at `0x670`,
`IsMounted() == Mount.MountId != 0`). `ItemDetailService.ResolveMountItemId(mountId)` maps that to
the mount's unlock item via a new `MountItemMap.csv` — the **same** FFXIV Collect mounts dataset
already scraped for `CollectSources.csv`, its `id` field is literally the Mount sheet RowId
(cross-checked against the Triple Triad card id convention, same pattern, already verified there).

Two triggers, both resolve straight into the existing `ItemDetailWindow.ShowItem(itemId)` — no new
UI needed, source/set/image all come along for free:

- **Context menu**: right-click a mounted player → "Check Mount" (`ContextMenuService`, only
  appears when the target is actually mounted and the mount resolves)
- **Target-based**: `/glamsource mount` — same resolution against `TargetManager.Target`

Genuinely can't be tested in `GlamSource.Mock` (no live game, no real characters to mount) — the
mount→item resolution itself (CSV lookup) is verified though: mount id 1 (Company Chocobo) →
item 6001 (Chocobo Whistle) → real quest-reward sources, confirmed via the local test server.

**Where it shows up**: no dedicated mount UI — it rides the existing pending-item push (see "Web
UI polish" below) straight into the normal Lookup-tab item panel. Source line comes from the same
`CollectSources.csv` used for minions/mounts in general (quest/achievement/Gold Saucer/duty/etc.),
so it's a real source, not just "unbekannt". What it does **not** show: who was riding it — only
the mount-unlock item's own source, no "seen on player X" context.

## Recents: delete + cap

The native ImGui sidebar (`GlamSourceShellWindow.DrawRecentSidebar`) already had both a delete
button (×) and cap-at-10-drop-oldest behavior (`Configuration.cs`). Only the **web UI** Recents
strip was missing delete — added `RemoveRecent(int)` (extracted from the sidebar's inline handler,
same pattern as the existing `ActivateRecent` extraction), a new `POST
/api/action/recent/{index}/remove` endpoint, and a × button per chip in `WebUiPage.cs`. Faked in
`GlamSource.Mock` too (`FakeRecents`/`_fakeOutfits` became mutable lists instead of a fixed array).

## Examine-window item click

`ContextMenuService.ExtractItemIdFromAddon`'s `"CharacterInspect"` case called only
`ExtractHoveredItemId()` (generic hover fallback), silently ignoring an already-written
`ExtractCharacterInspectItemId()` that reads the Examine window's own `InventoryType.Examine`
container directly — more reliable for the paperdoll's equipped-gear icons. Fixed:
`"CharacterInspect" => ExtractCharacterInspectItemId() ?? ExtractHoveredItemId()`. Clicking an item
(glamoured or not) in someone's Examine window now opens our context-menu entry and pushes the
item into the web UI, same as any other item click.

## ORB (Opaque Response Blocking) — item preview images

Hotlinking the wiki image URL directly in an `<img src>` got blocked by Chromium's ORB inside the
real Browsingway overlay (`A resource is blocked by OpaqueResponseBlocking`). Root cause:
cross-origin `<img>` fetch of a third-party host. Fixed by proxying the actual image **bytes**
through our own server instead — `GET /api/itemimage/{id}` now returns `image/jpeg` bytes directly
(same-origin), matching the pre-existing `/api/icon/` pattern, instead of a JSON `{url}` for the
browser to fetch cross-origin itself. `ItemImageService.GetPreviewImageBytesAsync` added for this.
`WebUiPage.cs`'s `.preview` div now just does `<img src="/api/itemimage/${id}">` directly — no more
separate `loadItemPreviewImage()` fetch-and-set-src round trip.

## Settings in the web UI

Mirrors the straightforward `Configuration`-backed toggles from `GlamSourceShellWindow`'s Settings
tab — deliberately **excludes** "Web UI" itself (would self-disable the page you're viewing) and
"Movable Window" (ImGui-only concept). `GET /api/settings` + 5 `POST
/api/action/settings/{key}?value=` endpoints (`showcraftingsavings`, `debugapi`, `autooverlay`,
`live3dpreview`, `mountupdistance`). The debug-API toggle also starts/stops the actual
`DebugApiService` via `_shell.OnDebugApiToggle`, not just a bool flip. Gearset-picker and
Mount-picker (native `RaptureGearsetModule` / unlocked-mounts reads) explicitly **not** ported —
same kind of native-read work as the mount lookup above, just not built yet.

## Web UI polish

- **Recents delete** in the web UI (native sidebar already had it) — `RemoveRecent(int)` extracted
  from `DrawRecentSidebar`'s inline handler, `POST /api/action/recent/{index}/remove`, × button per
  chip.
- **Item ID search** — `/api/search?q=` short-circuits to a direct row lookup when the query is
  pure digits, bypassing the 3-character minimum, both frontend and backend.
- **Charakter-tab image centering** — `#chardetail .preview img` scoped CSS (centered, capped
  220px), left as-is (280px, left-aligned) in the Item Search tab per explicit instruction to scope
  the change to the Charakter tab only.
- **Item Search tab** renamed from "Suche", each result row now also shows a 40×40 preview
  thumbnail alongside the existing game icon.
- **Pending-item push** — `WebUiService.PushItemToWeb(uint)` / `GET /api/pendingitem` (one-shot
  get-and-clear), polled every 1500ms by the frontend. Lets any native trigger (mount lookup,
  Examine click) land the item in the web UI's Lookup tab without the user doing anything.

- **Browser walkthrough 2026-09-02 (0.0.0.302)** — findings from clicking through the mock:
  - Result list no longer vanishes on item click: hidden, restored via a "← Back to results (N)"
    bar above the detail (`#resback`, `backToResults()`); the query stays in the box.
  - Keyboard: rows are `tabindex=0` `role=button`; ↓ from the search box into the list, ↑/↓
    between rows, Enter opens (Enter in the box = first hit).
  - Character slot labels were German-only (`SLOT_DE`) and missed the actual enum name
    `Earrings` — now `SLOT_LBL` per UI language, re-rendered on language toggle.
  - Sources with `sourceItemId` (coffers, "Retired — replaced by Augmented X") get an
    "Open item" button, same jump ImGui's `ItemDetailWindow` already had.
  - `/api/itemimage/{id}` miss → 204 instead of 404 (both servers): the `<img onerror>` already
    hides it, the 404 only produced one console error per result row.
  - Item name in the detail header is an `<h2>` now (was a div; the source badge was the only heading).

## Native ImGui window — Set/Triple-Triad/preview-image parity

`ItemDetailWindow.cs` gained the same features as the web UI's item panel: Set-Name + clickable
"rest of the set" chips in the header, a `[ItemSourceType.TripleTriad]` entry in `SourceStyles`,
and **native texture loading** for the wiki preview image via
`ITextureProvider.CreateFromImageAsync(bytes)` (cached per item, disposed with the window).
Constructor gained an optional `IItemImageService? imageService` param.

## Plugin.cs — real `ContextMenu` injection bug

`[PluginService] internal static IContextMenu ContextMenu { get; private set; }` was the only one
of 15 `[PluginService]` properties with no matching constructor-parameter assignment (all others do
e.g. `PluginInterface = _pluginInterface;`). In the real game this went unnoticed — Dalamud's
reflection-based `[PluginService]` injection populates it regardless. Any host that loads the
plugin class directly (see below) instead of through that reflection step gets a permanently-null
`ContextMenu` and crashes constructing `ContextMenuService`. Fixed: added `IContextMenu
contextMenu` constructor parameter + `ContextMenu = contextMenu;`, matching the other 14. Real bug,
kept regardless of how the investigation below turned out.

## GlamSource.Mock — real-window investigation, and the visual rework that replaced it

Attempted loading the real `GlamSource.Plugin` in Mock via DalaMock's own `PluginLoader`
(`MockContainer.GetPluginLoader().AddPlugin(...)` / `StartPlugin(...)`) to get pixel-identical
in-game windows instead of hand-built ones. Got it constructing and starting cleanly (found the
`ContextMenu` bug above along the way; also needed hand-written `NullSigScanner` /
`NullGameInteropProvider` `IMockService` stubs — DalaMock.Core ships no mocks for
`ISigScanner`/`IGameInteropProvider`, and `PluginLoader.StartPlugin` builds an entirely separate
container that only pulls in things registered `.As<IMockService>()`). A `PluginStarted`
event-subscription ordering bug was also found and fixed (`MockDalamudUi` must be constructed via
`GetMockUi()` *before* `StartPlugin()`, or the plugin's draw callback never gets wired up).

Actually **drawing** the real `GlamSourceShellWindow` hangs hard, though: its tree touches native
FFXIVClientStructs accessors (`InventoryManager.Instance()`, `UIModule.Instance()`,
`AgentRecipeNote.Instance()`, `QuestManager.IsQuestComplete()`, plus `PreviewRenderer.cs`'s CharaView
hooks). ClientStructs' own `Service<T>` singleton resolution — separate from and bypassing the
injected Dalamud service interfaces DalaMock mocks — scans the *current process* for real game
memory structures on first touch, and hangs unpredictably outside an actual `ffxiv_dx11.exe`
process (confirmed: Windows reported the process as not responding). Hardening every native
touch-point across those files is a large, uncertain-outcome job — abandoned per explicit
instruction to prioritize stability. `NullSigScanner`/`NullGameInteropProvider` stay in the repo
(`GlamSource.Mock/NullNativeServices.cs`) as a reference if this gets revisited; unused today.

**What replaced it**: `MockShellWindow.cs` — a hand-built window that copies GlamSourceShellWindow's
*layout* (Lookup/Character/Settings tabs, 3-column slot grid + center preview, Recents sidebar)
using only mock-safe data — `EditableGlamourService` (no ClientStructs) for equipment, and
DalaMock's own `MockTextureProvider`/`MockDataManager` (real Dalamud interfaces, Lumina-backed icon
rendering, no native reads either — resolved from `mockContainer.GetContainer()`) for item icons.
Features that genuinely need a live game (Apply-to-Self/Fitting Room via Glamourer IPC, Gearset
combos via `RaptureGearsetModule`, Mount picker via unlocked-mounts reads) are shown disabled with
a tooltip instead of faked. Recents has no backing `Configuration` in Mock, so it's a plain
in-memory list seeded via a "Save as Recent" button (snapshots the current Player-Editor state).
Replaces the old flat `MockMainWindow` equipment table (deleted, fully superseded).

## Files touched

- `GlamSource.Core/ItemDetailService.cs`, `ItemSourceService.cs` (new `TripleTriad` enum value),
  `ItemImageService.cs` (new)
- `GlamSource.Core/LuminaSupplemental/`: `TripleTriadCardNpcs.csv`, `CollectSources.csv`,
  `MountItemMap.csv` (new, all embedded resources)
- `GameDataService.cs` — `GetMountId`
- `Services/WebUiService.cs`, `Services/WebUiPage.cs`, `Services/ContextMenuService.cs`
- `Windows/GlamSourceShellWindow.cs` — `RemoveRecent`
- `Plugin.cs` — `/glamsource mount`, wiring, `ContextMenu` constructor injection fix
- `Windows/ItemDetailWindow.cs` — Set chips, Triple Triad style, native preview-image loading
- `GlamSource.Mock/WebPreviewServer.cs` — mirrors every new endpoint for local testing
- `GlamSource.Mock/MockShellWindow.cs` (new) — Lookup/Character/Settings layout stand-in
- `GlamSource.Mock/MockMainWindow.cs` (deleted, superseded by `MockShellWindow`)
- `GlamSource.Mock/NullNativeServices.cs` (new, unused today) — PluginLoader investigation leftover
