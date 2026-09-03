namespace GlamSource.Services;

// ponytail: single embedded page, vanilla JS, no build step. Icons via xivapi CDN.
internal static class WebUiPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>GlamSource</title>
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect width='32' height='32' rx='7' fill='%23141312'/%3E%3Ctext x='16' y='23' font-size='20' text-anchor='middle' fill='%23c8a75e'%3E✦%3C/text%3E%3C/svg%3E">
<style>
/* FFXIV-inspired, modernized: warm charcoal panels with a faint vertical gradient, thin gold
   hairline borders, small-caps gold labels — the in-game UI language without its bitmap frames.
   Emoji are BANNED from UI chrome: Browsingway's embedded Chromium has no emoji font, they
   rendered as empty boxes (seen live). */
:root{
  --bg:#141312; --panel:#1e1c1a; --panel2:#262320; --border:#3a352c; --gold:#c8a75e;
  --gold-dim:#8a7443; --text:#e2dccb; --muted:#948c7a; --accent:#c8a75e; --success:#8fbf73; --warn:#e0a03c;
}
*{box-sizing:border-box;margin:0;padding:0}
body{background:transparent;color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif;margin:0}
#titlebar{display:flex;align-items:center;gap:10px;background:linear-gradient(180deg,#2a2723,#1e1c19);border-bottom:1px solid var(--gold-dim);padding:6px 12px;user-select:none}
#titlebar .brand{color:var(--gold);font-weight:600;font-size:14px;letter-spacing:1.5px;text-transform:uppercase}
#titlebar .sub{color:var(--muted);font-size:11px}
#titlebar .spacer{flex:1}
#titlebar button{background:none;border:1px solid var(--border);color:var(--muted);width:22px;height:22px;border-radius:4px;cursor:pointer;font-size:13px;line-height:1;display:flex;align-items:center;justify-content:center}
#titlebar button:hover{border-color:var(--gold);color:var(--gold)}
#titlebar button.active{border-color:var(--gold);color:var(--gold);background:rgba(200,167,94,.12)}
#titlebar button svg{width:12px;height:12px;fill:currentColor}
/* FIXED standard size ("Standard-Größe festsetzen"): the content is a hard pixel layout —
   resizing the Browsingway overlay only reveals/clips transparent padding, the UI itself never
   reflows or scales. No min-height: the panel ends right after the toolbar. */
#app{background:linear-gradient(180deg,#17161494,#141312f0),var(--bg);padding:16px 22px 10px;width:1190px}
#titlebar{width:1190px}
nav{display:flex;gap:2px;margin-bottom:16px;border-bottom:1px solid var(--border)}
nav button{background:transparent;border:0;border-bottom:2px solid transparent;color:var(--muted);padding:8px 20px;cursor:pointer;font-size:13px;letter-spacing:1px;text-transform:uppercase;transition:.15s}
nav button:hover{color:var(--text)}
nav button.active{color:var(--gold);border-bottom-color:var(--gold);font-weight:600}
input[type=search]{width:100%;max-width:420px;background:var(--panel);border:1px solid var(--border);color:var(--text);padding:10px 14px;border-radius:8px;font-size:15px;outline:none}
input[type=search]:focus{border-color:var(--accent)}
.results{margin-top:10px;display:flex;flex-direction:column;gap:4px;max-width:420px}
.row{display:flex;align-items:center;gap:10px;background:var(--panel);border:1px solid transparent;border-radius:8px;padding:6px 10px;cursor:pointer;transition:.12s}
.row:hover,.row:focus{border-color:var(--accent);background:var(--panel2);outline:none}
.row img{width:28px;height:28px;border-radius:4px}
.row img.rowpreview{width:40px;height:40px;border-radius:6px;margin-left:auto;object-fit:cover}
.row .ilvl{color:var(--muted);font-size:11px;margin-left:6px}
#filters{display:flex;gap:6px;margin-top:6px;max-width:420px;flex-wrap:wrap}
#filters select,#filters input{background:var(--panel);border:1px solid var(--border);color:var(--text);padding:5px 8px;border-radius:6px;font-size:12px;outline:none}
#filters select:focus,#filters input:focus{border-color:var(--accent)}
#filters input{width:72px}
.row.active{border-color:var(--gold)}
.row .type{color:var(--muted);font-size:11px;margin-left:auto;white-space:nowrap}
/* Duty Drops tab: duty list left, drop grid + item detail right */
#dutylayout{display:flex;gap:18px;align-items:flex-start}
#dutyside{width:320px;flex-shrink:0}
#dutyside input[type=search]{max-width:none;margin-top:8px}
#dutylist{max-width:none;max-height:520px;overflow-y:auto}
#dutylist.grid{display:grid;grid-template-columns:1fr 1fr;gap:6px}
#dutycrumb{margin:8px 0 6px;font-size:12px;color:var(--muted)}
.crumb[onclick]{color:var(--gold);cursor:pointer}
/* folder tiles: a representative duty banner as background, darkened on the text side */
.ntile{background:#1c1a16 center/cover no-repeat;border:1px solid var(--border);border-radius:8px;cursor:pointer;transition:.12s;min-height:66px;overflow:hidden}
.ntile:hover,.ntile:focus{border-color:var(--gold);box-shadow:0 0 8px rgba(200,167,94,.3);outline:none}
.ntile .nshade{display:flex;align-items:center;gap:10px;height:100%;min-height:64px;padding:10px 14px;background:linear-gradient(90deg,rgba(20,19,18,.93) 0%,rgba(20,19,18,.75) 55%,rgba(20,19,18,.25) 100%)}
.ntile .ticon{width:30px;height:30px;flex-shrink:0}
.ntile .nm{font-size:14px;color:var(--gold)}
.ntile .meta{color:var(--muted);font-size:11px}
#dutymain{flex:1;min-width:0}
/* Duty Finder style tiles: banner thumbnail (ContentFinderCondition.Image via /api/icon) + name/meta */
.dtile{display:flex;gap:10px;align-items:center;background:var(--panel);border:1px solid transparent;border-radius:8px;padding:6px 8px;cursor:pointer;transition:.12s}
.dtile:hover,.dtile:focus,.dtile.active{border-color:var(--gold);background:var(--panel2);outline:none}
.dtile img{width:96px;height:31px;object-fit:cover;border-radius:4px;flex-shrink:0;border:1px solid var(--gold-dim);background:var(--panel2)}
.dtile .nm{font-size:13px;line-height:1.25}
.dtile .meta{color:var(--muted);font-size:11px}
.dutybanner{display:flex;gap:16px;align-items:center;margin-bottom:6px}
.dutybanner img{width:376px;height:120px;object-fit:cover;border-radius:8px;border:1px solid var(--gold-dim)}
.dsec{margin-top:14px}
.dsech{color:var(--gold);font-size:12px;letter-spacing:1px;text-transform:uppercase;border-bottom:1px solid var(--border);padding-bottom:4px}
.dsub{color:var(--muted);font-size:11px;margin-top:8px}
.dgrid{max-width:none;flex-direction:row;flex-wrap:wrap;margin-top:6px}
.dgrid .row{width:calc(50% - 4px)}
#dutydrops{max-height:460px;overflow-y:auto;padding-right:4px}
.cards{display:flex;flex-direction:column;gap:14px;margin-top:16px}
.card{background:var(--panel);border:1px solid var(--border);border-left:4px solid var(--accent);border-radius:10px;padding:14px 16px;box-shadow:0 2px 10px rgba(0,0,0,.35)}
.card h3{display:flex;align-items:center;gap:8px;font-size:14px;margin-bottom:8px}
.badge{font-size:10px;font-weight:700;letter-spacing:1px;padding:3px 10px;border-radius:99px;color:#fff;background:#3c4560}
.card.crafted{border-left-color:#ff9d33}.card.crafted .badge{background:#7a4a12}
.card.vendor{border-left-color:#5c6cc0}.card.vendor .badge{background:#2c3170}
.card.quest{border-left-color:#4ecb5e}.card.quest .badge{background:#1c5a24}
.card.duty{border-left-color:#e05555}.card.duty .badge{background:#5c1c1c}
table{width:100%;border-collapse:collapse;margin-top:6px}
td,th{padding:5px 8px;text-align:left;font-size:13px}
tr:nth-child(even) td{background:rgba(255,255,255,.025)}
td.muted{color:var(--muted)}
.matrow{display:flex;align-items:center;gap:8px;padding:3px 0}
.matrow img{width:22px;height:22px;border-radius:3px}
.matrow .where{color:var(--muted);font-size:11px;margin-left:8px}
.applystatus{color:var(--muted);font-size:11px;margin-left:6px}
.ok{color:var(--success)}.short{color:var(--muted)}
button.act{background:var(--panel2);border:1px solid var(--border);color:var(--text);padding:4px 12px;border-radius:6px;cursor:pointer;font-size:12px}
button.act:hover{border-color:var(--accent);color:var(--accent)}
.header{display:flex;align-items:center;gap:14px;margin:14px 0}
.header img{width:48px;height:48px;border-radius:8px}
.header .name{font-size:18px;font-weight:600;margin:0}
.header .meta{color:var(--muted);font-size:12px}
.preview:empty{display:none}
.preview img{max-width:280px;max-height:280px;border-radius:8px;border:1px solid var(--border);margin-bottom:10px}
/* Charakter tab: every preview image landing in #chardetail (slot click, set-member chips) —
   centered, a touch smaller than the Suche tab's left-aligned one. */
#chardetail .preview{text-align:center}
#chardetail .preview img{max-width:220px;max-height:220px;margin-left:auto;margin-right:auto}
.empty{color:var(--muted);margin-top:14px;display:flex;align-items:center;gap:8px}
.spinner{width:16px;height:16px;border:2px solid var(--border);border-top-color:var(--accent);border-radius:50%;animation:spin .7s linear infinite;display:inline-block}
@keyframes spin{to{transform:rotate(360deg)}}
.row img,.matrow img,.slot img,.header img{background:var(--panel2);object-fit:contain}
.snapgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:10px;margin-top:14px}
/* gear slot tiles, in-game character-sheet look: framed square icon on a gradient tile,
   small-caps slot label above the item name, subtle gold glow on hover */
.slot{display:flex;align-items:center;gap:11px;background:linear-gradient(180deg,#28251f,#1c1a16);border:1px solid var(--border);border-radius:6px;padding:7px 10px;cursor:pointer;transition:.12s;position:relative}
.slot:hover{border-color:var(--gold);box-shadow:0 0 8px rgba(200,167,94,.3);background:linear-gradient(180deg,#2e2a22,#211e18)}
.slot img{width:40px;height:40px;border-radius:4px;border:1px solid var(--gold-dim);flex-shrink:0;box-shadow:inset 0 0 6px rgba(0,0,0,.7);background:radial-gradient(circle at 32% 26%,#343026,#181510)}
.slot .lbl{color:var(--gold-dim);font-size:9px;text-transform:uppercase;letter-spacing:1.2px;line-height:1.1}
.slot .nm{font-size:12.5px;line-height:1.25;color:var(--text)}
.slot .glam{position:absolute;top:6px;right:8px;color:var(--success);font-size:9px;letter-spacing:.5px;text-transform:uppercase}
/* aspect-ratio = the WORLD aspect the server camera renders (PreviewRenderer.PreviewAspect, 0.8 —
   wider than the RT's native 0.6 for more side room, "bei Emotes sind Teile abgehakt"), with
   object-fit:fill stretching the horizontally-squeezed pixels back out. RT edge = canvas edge
   stays true, so clipping still happens at the viewport border like any 3D product viewer. */
#preview3d{background:transparent;border:0;margin:0 auto 14px;cursor:grab;display:none;height:640px;aspect-ratio:0.8;object-fit:fill}
/* character tab layout: slots | model | detail — panel scrolls internally, page never does */
#charlayout{display:grid;grid-template-columns:minmax(190px,230px) 1fr minmax(280px,360px);gap:14px;align-items:start}
#charslots{display:flex;flex-direction:column;gap:6px;max-height:660px;overflow-y:auto}
#charslots .slot{padding:6px 8px}
#chardetail{max-height:660px;min-height:200px;overflow-y:auto;background:var(--panel);border:1px solid var(--border);border-radius:10px;padding:10px 16px}
#chardetail>.empty{justify-content:center;margin-top:80px}
#chardetail .header img{width:40px;height:40px}
.tbl{color:var(--gold-dim);font-size:10px;text-transform:uppercase;letter-spacing:1.5px;margin-left:10px}
/* recents: separate footer strip below the layout, above the view toolbar — own block, not
   mixed into either */
#recentsFooter{margin-top:14px;padding-top:10px;border-top:1px solid var(--border)}
#recentsFooter .label{color:var(--gold-dim);font-size:10px;text-transform:uppercase;letter-spacing:1.5px;margin-bottom:6px}
#charrecents{display:flex;gap:8px;overflow-x:auto;padding-bottom:2px}
#charrecents .recent{flex:0 0 auto;padding:6px 14px;border-radius:20px;cursor:pointer;font-size:12px;white-space:nowrap;background:var(--panel);border:1px solid var(--border)}
#charrecents .recent:hover{border-color:var(--gold-dim)}
#charrecents .recent.active{border-color:var(--accent);color:var(--gold)}
#charrecents .recent .world{color:var(--muted);font-size:11px;margin-left:6px}
#charrecents .recent .del{color:var(--muted);margin-left:8px;padding:0 2px;border-radius:4px}
#charrecents .recent .del:hover{color:var(--text);background:var(--panel2)}
.p3dtoolbar{background:linear-gradient(180deg,var(--panel2),var(--panel));border:1px solid var(--border);border-radius:8px;padding:8px 12px}
.p3dtoolbar .tbl:first-child{margin-left:0}
.p3dtoolbar button,.p3dtoolbar select{background:#151310;border:1px solid var(--border);color:var(--text);padding:5px 12px;border-radius:5px;cursor:pointer;font-size:12px;transition:.12s}
.p3dtoolbar button:hover,.p3dtoolbar select:hover{border-color:var(--gold)}
.p3dtoolbar button.active{color:var(--gold);border-color:var(--gold-dim);background:#211d15;font-weight:600}
.p3dtoolbar a{color:var(--muted)}
#preview3d.active{cursor:grabbing}
#preview3d.panning{cursor:ns-resize}
</style>
</head>
<body>
<div id="titlebar">
  <span class="brand">GlamSource</span>
  <span class="sub">web ui</span>
  <span class="spacer"></span>
  <button id="btn-lang-en" onclick="setLang('en')" style="width:auto;padding:0 6px;font-size:11px">EN</button>
  <button id="btn-lang-de" onclick="setLang('de')" style="width:auto;padding:0 6px;font-size:11px">DE</button>
  <button id="btn-settings" data-i18n-title="tab_settings" onclick="showTab(currentTab==='settings'?'character':'settings')"><svg viewBox="0 0 24 24"><path d="M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.488.488 0 0 0-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54a.484.484 0 0 0-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z"/></svg></button>
  <button id="btn-min" data-i18n-title="min" onclick="toggleMinimize()">–</button>
  <button id="btn-lock" data-i18n-title="lock" onclick="toggleLock()"><svg viewBox="0 0 24 24"><path d="M12 2a5 5 0 0 0-5 5h2a3 3 0 0 1 6 0v3H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7a5 5 0 0 0-5-5z"/></svg></button>
  <button data-i18n-title="hide" onclick="post('/api/action/overlay/hide')">×</button>
</div>
<div id="app">
<nav>
  <button id="tab-character" class="active" data-i18n="tab_character" onclick="showTab('character')"></button>
  <button id="tab-lookup" data-i18n="tab_lookup" onclick="showTab('lookup')"></button>
  <button id="tab-duties" data-i18n="tab_duties" onclick="showTab('duties')"></button>
</nav>

<section id="view-lookup" style="display:none">
  <input type="search" id="q" data-i18n-ph="search_ph" autofocus>
  <div id="filters">
    <select id="f-slot"></select>
    <select id="f-job"></select>
    <input id="f-min" type="number" min="1" max="999" data-i18n-ph="ilvl_min">
    <input id="f-max" type="number" min="1" max="999" data-i18n-ph="ilvl_max">
  </div>
  <div class="results" id="results"></div>
  <div id="resback" class="empty" style="display:none;cursor:pointer" onclick="backToResults()"></div>
  <div id="detail"></div>
</section>

<section id="view-character">
  <!-- three columns like the in-game character sheet: gear slots | live model | item lookup panel.
       Clicking a slot opens the lookup RIGHT THERE (no tab switch, panel scrolls internally, the
       page itself never scrolls). -->
  <div id="charlayout">
    <div id="charslots"><div class="empty"><span class="spinner"></span></div></div>
    <div>
      <canvas id="preview3d"></canvas>
      <div class="empty" id="p3dhint" data-i18n="p3dhint" style="display:none;font-size:12px"></div>
    </div>
    <div id="chardetail"><div class="empty" data-i18n="chardetail_hint"></div></div>
  </div>
  <div id="recentsFooter">
    <div class="label" data-i18n="recent_label"></div>
    <div id="charrecents"><div class="empty"><span class="spinner"></span></div></div>
  </div>
  <div class="row p3dtoolbar" style="margin-top:6px;margin-bottom:10px;flex-wrap:wrap;gap:8px;align-items:center">
    <span class="tbl" data-i18n="view_label"></span>
    <button onclick="showShoppingList()" data-i18n="shopping_btn" data-i18n-title="shopping_tt"></button>
    <button onclick="glamourerPost('/api/action/glamourer/apply',this)" data-i18n="apply_btn" data-i18n-title="apply_tt"></button>
    <button id="p3dspin" onclick="toggleAutoSpin()" data-i18n="spin" data-i18n-title="spin_tt"></button>
    <button onclick="resetPreview3D()" data-i18n="reset" data-i18n-title="reset_tt"></button>
    <span class="tbl" data-i18n="pose_label"></span>
    <!-- ponytail: beide wieder deaktiviert — Pfad #21 (minimaler 254er-Mechanismus) zeigte die
         Waffe kurz an, dann erneut Duplikate (Orphan-Muster, überlebt Plugin-Reload) und weiterhin
         keine Effekte. Nach 21 Anläufen final geparkt, siehe doku/character-preview.md. -->
    <button id="p3dweapon" disabled title="Vorübergehend deaktiviert — siehe doku/character-preview.md" data-i18n="weapon"></button>
    <button id="p3dweapononly" disabled title="Vorübergehend deaktiviert — siehe doku/character-preview.md" data-i18n="weapon_only"></button>
    <select id="p3demote" onchange="setEmote(this.value)" data-i18n-title="emote_tt"><option value="0" data-i18n="emote_idle"></option></select>
    <a href="#" onclick="loadPreview3DDebug();return false" style="font-size:12px" data-i18n="debug" data-i18n-title="debug_tt"></a>
  </div>
  <pre id="p3ddebug" style="display:none;font-size:11px;background:var(--panel);border:1px solid var(--border);border-radius:6px;padding:8px;margin-top:6px;white-space:pre-wrap"></pre>
</section>

<section id="view-duties" style="display:none">
  <div id="dutylayout">
    <div id="dutyside">
      <div id="dutycurrent" class="empty" style="margin-top:0"></div>
      <input type="search" id="dq" data-i18n-ph="duty_search_ph">
      <div id="dutycrumb"></div>
      <div class="results" id="dutylist"></div>
    </div>
    <div id="dutymain">
      <div id="dutyhead"></div>
      <div id="dutydrops"></div>
      <div id="dutydetail"></div>
    </div>
  </div>
</section>

<section id="view-settings" style="display:none">
  <!-- ponytail: mirrors GlamSourceShellWindow.DrawSettingsTab's Configuration-backed toggles —
       not "Web UI" itself (self-disable) or "Movable Window" (ImGui-only), and not the
       Gearset/Mount pickers (need live native reads, still native-window-only for now). -->
  <div id="settingsBody"><div class="empty"><span class="spinner"></span></div></div>
</section>


</div>
<script>
const $=s=>document.querySelector(s);

// ponytail: chrome-only i18n — same scope decision as the ImGui side's Loc.cs (see
// doku/item-source-detection.md). Item/game data stays whatever language the game data came in;
// only our own static labels/tooltips/buttons are covered. Keyed by short id (not the English
// text itself, unlike Loc.cs — this dict also has to double as JS template-string lookups where
// a readable-string key would be unwieldy).
const I18N={
  min:{en:'Minimize — only the title bar stays visible',de:'Minimieren — nur die Titelleiste bleibt sichtbar'},
  lock:{en:'Lock — fix position & size (needed to drag-rotate the model)',de:'Sperren — Position & Größe fixieren (nötig, um das Modell per Ziehen zu drehen)'},
  unlock:{en:'Unlock — move/resize the overlay',de:'Entsperren — Overlay verschieben/skalieren'},
  hide:{en:'Hide (reopen from the GlamSource window)',de:'Ausblenden (über das GlamSource-Fenster wieder öffnen)'},
  tab_lookup:{en:'Item Search',de:'Item-Suche'},
  tab_character:{en:'Character',de:'Charakter'},
  tab_settings:{en:'Settings',de:'Einstellungen'},
  tab_duties:{en:'Duty Drops',de:'Duty-Drops'},
  duty_current:{en:'Current duty',de:'Aktuelle Duty'},
  duty_none:{en:'Not inside a duty — pick one from the list',de:'Nicht in einer Duty — eine aus der Liste wählen'},
  duty_search_ph:{en:'Search duty or boss (e.g. Susano)…',de:'Duty oder Boss suchen (z.B. Susano)…'},
  duty_pick:{en:'Pick a duty to see its drops',de:'Duty wählen, um die Drops zu sehen'},
  duty_nodrops:{en:'No drops known for this duty.',de:'Keine Drops für diese Duty bekannt.'},
  boss:{en:'Boss',de:'Boss'},
  chest:{en:'Chest',de:'Truhe'},
  duty_general:{en:'Elsewhere in the duty (chests & mobs)',de:'Unterwegs in der Duty (Truhen & Gegner)'},
  drops:{en:'drops',de:'Drops'},
  duty_coffers:{en:'Treasure chests along the way (Garland Tools)',de:'Truhen unterwegs (Garland Tools)'},
  duty_featured:{en:'Mounts & minions',de:'Reittiere & Begleiter'},
  duty_garland_drops:{en:'Drops (Garland Tools)',de:'Drops (Garland Tools)'},
  duty_exchange:{en:'Exchange',de:'Tausch'},
  duty_exchange_for:{en:'Hand in:',de:'Einlösen:'},
  duty_open:{en:'Open in Duty Finder',de:'Duty öffnen'},
  duty_garland:{en:'drops via Garland',de:'Drops via Garland'},
  mount:{en:'Mount',de:'Reittier'},
  minion:{en:'Minion',de:'Begleiter'},
  shopping_btn:{en:'Shopping list',de:'Einkaufsliste'},
  shopping_tt:{en:'Everything needed for the shown outfit: best source per piece, grouped by NPC / craft / duty, with what you already own',de:'Alles für das gezeigte Outfit: beste Quelle je Teil, gruppiert nach NPC / Craft / Duty, mit dem, was du schon hast'},
  shopping_title:{en:'Outfit shopping list',de:'Outfit-Einkaufsliste'},
  shopping_stops:{en:'stops',de:'Stationen'},
  shopping_total:{en:'Total cost',de:'Gesamtkosten'},
  shopping_items:{en:'Pieces here',de:'Teile hier'},
  rest_of_set:{en:'Rest of the set',de:'Rest des Sets'},
  open_craftlog:{en:'Open Crafting Log',de:'Herstellungsliste öffnen'},
  npc:{en:'NPC',de:'NPC'},
  location:{en:'Location',de:'Ort'},
  cost_label:{en:'Cost',de:'Kosten'},
  materials_label:{en:'Materials',de:'Materialien'},
  pieces_label:{en:'Pieces',de:'Teile'},
  glam_badge:{en:'Glam',de:'Glam'},
  item_id:{en:'Item ID',de:'Item-ID'},
  marketable:{en:'marketable',de:'handelbar'},
  set_label:{en:'Set',de:'Set'},
  bags:{en:'Bags',de:'Taschen'},
  saddlebag:{en:'Saddlebag',de:'Satteltasche'},
  apply_btn:{en:'Apply to Self',de:'Auf mich anwenden'},
  apply_tt:{en:'Put the shown outfit on your own character via Glamourer (weapons skipped)',de:'Gezeigtes Outfit per Glamourer auf den eigenen Charakter legen (Waffen ausgenommen)'},
  apply_item_tt:{en:'Put this piece on your own character via Glamourer',de:'Dieses Teil per Glamourer auf den eigenen Charakter legen'},
  ev_recurring:{en:'Recurring event',de:'Wiederkehrendes Event'},
  ev_onetime:{en:'One-time event',de:'Einmaliges Event'},
  ev_active:{en:'active now',de:'läuft gerade'},
  ev_inactive:{en:'not running right now',de:'läuft gerade nicht'},
  ev_unknown:{en:'live status unknown — check in-game',de:'Live-Status unbekannt — im Spiel prüfen'},
  ev_gone:{en:'no longer obtainable',de:'nicht mehr erhältlich'},
  market_world:{en:'World:',de:'World:'},
  market_dc:{en:'DC',de:'DC'},
  dtype_Dungeon:{en:'Dungeons',de:'Dungeons'},
  'dtype_Deep Dungeon':{en:'Deep Dungeons',de:'Deep Dungeons'},
  dtype_Trial:{en:'Trials',de:'Prüfungen'},
  dtype_Raid:{en:'Raids',de:'Raids'},
  dtype_Ultimate:{en:'Ultimates',de:'Ultimates'}, // kept English per user request, not "Fatale"
  dtype_Duty:{en:'Other duties',de:'Sonstige'},
  duty_all:{en:'All',de:'Alle'},
  diff_Normal:{en:'Normal',de:'Normal'},
  diff_Extreme:{en:'Extreme',de:'Extrem'},
  diff_Savage:{en:'Savage',de:'Episch'},
  diff_Unreal:{en:'Unreal',de:'Fatal'},
  diff_Alliance:{en:'Alliance',de:'Allianz'},
  duties:{en:'duties',de:'Duties'},
  map:{en:'Map',de:'Karte'},
  search_ph:{en:'Search any item… or paste an item ID',de:'Beliebiges Item suchen… oder Item-ID einfügen'},
  p3dhint:{en:'Overlay is unlocked — lock it top-right, then drag-rotate.',de:'Overlay ist entsperrt — Schloss oben rechts sperren, dann per Ziehen drehen.'},
  chardetail_hint:{en:'Click a slot for source & details',de:'Slot anklicken für Herkunft & Quellen'},
  recent_label:{en:'Recently viewed',de:'Zuletzt angesehen'},
  view_label:{en:'View',de:'Ansicht'},
  spin:{en:'Rotate',de:'Drehen'},
  spin_tt:{en:'Auto-rotates the model — dragging or Reset stops it',de:'Dreht das Model automatisch — Ziehen oder Zurücksetzen stoppt'},
  spin_stop:{en:'⏹️ Stop rotating',de:'⏹️ Drehen stoppen'},
  spin_start:{en:'🎠 Auto-rotate',de:'🎠 Auto-Drehen'},
  reset:{en:'Reset',de:'Zurücksetzen'},
  reset_tt:{en:'Full rebuild — if the image gets stuck or shows the wrong thing',de:'Kompletter Neuaufbau — falls das Bild feststeckt oder was Falsches zeigt'},
  pose_label:{en:'Pose',de:'Pose'},
  weapon:{en:'Weapon',de:'Waffe'},
  weapon_tt:{en:'Draw weapon (default: off) — for glamoured weapons; clears an active emote',de:'Waffe ziehen (Standard: aus) — für geglamte Waffen; setzt ein aktives Emote zurück'},
  weapon_only:{en:'Weapon Only',de:'Nur Waffe'},
  weapon_only_tt:{en:'Weapon studio: weapon drawn, all other gear hidden',de:'Waffen-Studio: Waffe gezogen, alle andere Ausrüstung ausgeblendet'},
  emote_idle:{en:'Emote: Idle',de:'Emote: Idle'},
  emote_tt:{en:'Static emote pose (client-side only — even locked ones work); sheathes the weapon',de:'Statische Emote-Pose (rein clientseitig — auch nicht freigeschaltete funktionieren); steckt die Waffe weg'},
  debug:{en:'Debug',de:'Debug'},
  debug_tt:{en:'fps, errors, frame size',de:'fps, Fehler, Frame-Größe'},
  searching:{en:'Searching…',de:'Suche läuft…'},
  loading:{en:'Loading…',de:'Lädt…'},
  no_items:{en:'No items found.',de:'Keine Items gefunden.'},
  back_results:{en:'← Back to results',de:'← Zurück zu den Ergebnissen'},
  f_all_slots:{en:'All slots',de:'Alle Slots'},
  f_all_jobs:{en:'All jobs',de:'Alle Jobs'},
  ilvl_min:{en:'iLvl from',de:'iLvl ab'},
  ilvl_max:{en:'iLvl to',de:'iLvl bis'},
  open_item:{en:'Open item',de:'Item öffnen'},
  no_sources:{en:'No known source found.',de:'Keine bekannte Quelle gefunden.'},
  not_found:{en:'Not found.',de:'Nicht gefunden.'},
  viewing:{en:'Viewing',de:'Ansicht'},
  no_char_data:{en:'No data — character not seen yet.',de:'Keine Daten — Charakter noch nicht erfasst.'},
  none_yet:{en:'(none yet)',de:'(noch keine)'},
  remove_recent:{en:'Remove from Recent',de:'Aus Verlauf entfernen'},
  set_craft:{en:'Show Crafting Savings',de:'Handwerks-Ersparnis anzeigen'},
  set_craft_d:{en:'Compare market price vs. crafting cost',de:'Marktpreis vs. Herstellungskosten vergleichen'},
  set_debug:{en:'Debug API',de:'Debug-API'},
  set_debug_d:{en:'Read-only HTTP API on localhost:23423',de:'Nur-Lese-HTTP-API auf localhost:23423'},
  set_overlay:{en:'Auto-Overlay',de:'Auto-Overlay'},
  set_overlay_d:{en:'Browsingway overlay shows/hides with this window',de:'Browsingway-Overlay zeigt/versteckt sich mit diesem Fenster'},
  set_cmweb:{en:'Item Source in Web UI',de:'Item-Quelle im Web-UI'},
  set_cmweb_d:{en:'Examine right-click "Item Source" opens here instead of the ImGui window',de:'Examine-Rechtsklick "Item Source" öffnet hier statt im ImGui-Fenster'},
  set_3d:{en:'3D Preview (experimental)',de:'3D-Vorschau (experimentell)'},
  set_3d_d:{en:'Riskier GPU readback path — disable if you see crashes',de:'Riskanterer GPU-Readback-Pfad — bei Abstürzen deaktivieren'},
  set_mountdist:{en:'Mount-up distance',de:'Aufsitz-Distanz'},
};
let lang=localStorage.getItem('gs_lang')||'en';
const t=k=>(I18N[k]||{})[lang]??I18N[k]?.en??k;
function applyI18n(){
  document.querySelectorAll('[data-i18n]').forEach(el=>el.textContent=t(el.dataset.i18n));
  document.querySelectorAll('[data-i18n-title]').forEach(el=>el.title=t(el.dataset.i18nTitle));
  document.querySelectorAll('[data-i18n-ph]').forEach(el=>el.placeholder=t(el.dataset.i18nPh));
  $('#btn-lang-en')?.classList.toggle('active',lang==='en');
  $('#btn-lang-de')?.classList.toggle('active',lang==='de');
}
function setLang(l){
  lang=l;
  localStorage.setItem('gs_lang',l);
  applyI18n();
  fillFilters();
  if(currentTab==='settings')loadSettings();
  if(currentTab==='character'){loadRecents();loadSnapshot(true)}
}
// icons come from the plugin's own /api/icon (game data via Lumina) — xivapi's CDN is frozen
// and 404s on anything newer than its snapshot
const icon=id=>id?`/api/icon/${id}`:'';
const GIL_ICON=65002; // Item 1 "Gil" — cost rows carry itemId 0 / iconId 0
const esc=t=>(t??'').toString().replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const img=(id,size)=>`<img src="${icon(id)}" width="${size}" height="${size}" loading="lazy" onerror="this.style.visibility='hidden'">`;
function typeIcon(cls){return ''} // emoji render as empty boxes in Browsingway — the text badge is enough

let currentTab='character';
function showTab(t){
  currentTab=t;
  for(const x of['lookup','character','duties','settings']){
    $('#view-'+x).style.display=x===t?'':'none';
    $('#tab-'+x)?.classList.toggle('active',x===t); // settings has no nav tab, only the title-bar cog
  }
  $('#btn-settings').classList.toggle('active',t==='settings');
  if(t==='character'){loadSnapshot(true);loadRecents();startPreview3D()}else{stopPreview3D()}
  if(t==='settings')loadSettings();
  if(t==='duties')loadDuties();
  updateOverlayCompactness();
}

async function loadSettings(){
  const s=await fetch('/api/settings').then(r=>r.json());
  $('#settingsBody').innerHTML=`
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%"><input type="checkbox" id="set-craft" ${s.showCraftingSavings?'checked':''} onchange="saveSetting('showcraftingsavings',this.checked)"> ${t('set_craft')}<span style="margin-left:auto;color:var(--muted);font-size:12px">${t('set_craft_d')}</span></label></div>
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%"><input type="checkbox" id="set-debug" ${s.debugApiEnabled?'checked':''} onchange="saveSetting('debugapi',this.checked)"> ${t('set_debug')}<span style="margin-left:auto;color:var(--muted);font-size:12px">${t('set_debug_d')}</span></label></div>
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%"><input type="checkbox" id="set-overlay" ${s.webUiAutoOverlay?'checked':''} onchange="saveSetting('autooverlay',this.checked)"> ${t('set_overlay')}<span style="margin-left:auto;color:var(--muted);font-size:12px">${t('set_overlay_d')}</span></label></div>
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%"><input type="checkbox" id="set-cmweb" ${s.contextMenuOpensInWebUi?'checked':''} onchange="saveSetting('contextmenuweb',this.checked)"> ${t('set_cmweb')}<span style="margin-left:auto;color:var(--muted);font-size:12px">${t('set_cmweb_d')}</span></label></div>
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%"><input type="checkbox" id="set-3d" ${s.webUiLive3DPreview?'checked':''} onchange="saveSetting('live3dpreview',this.checked)"> ${t('set_3d')}<span style="margin-left:auto;color:var(--muted);font-size:12px">${t('set_3d_d')}</span></label></div>
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%">${t('set_mountdist')}<input type="range" min="0" max="100" value="${s.mountUpDistance}" style="flex:1" oninput="$('#set-mountdist-val').textContent=this.value+'m'" onchange="saveSetting('mountupdistance',this.value)"><span id="set-mountdist-val" style="color:var(--muted);font-size:12px;min-width:36px">${Math.round(s.mountUpDistance)}m</span></label></div>
  `;
}
async function saveSetting(key,value){
  await post('/api/action/settings/'+key+'?value='+encodeURIComponent(value));
}

// ponytail: "push" an item from a native trigger (Examine-window right-click, /glamsource mount) —
// see WebUiService.PushItemToWeb/api/pendingitem. Polled independent of which tab is showing so it
// works no matter where the user currently is; switches to Suche and opens it when one arrives.
setInterval(async()=>{
  const r=await fetch('/api/pendingitem').then(r=>r.ok?r.json():null).catch(()=>null);
  if(r?.itemId){showTab('lookup');openItem(r.itemId)}
},1500);

// minimize to the title bar (Dalamud-style collapse isn't possible for a Browsingway page — the
// overlay window keeps its size, but the content below the bar disappears and the stream stops)
let uiMinimized=false;
function toggleMinimize(){
  uiMinimized=!uiMinimized;
  $('#app').style.display=uiMinimized?'none':'';
  $('#btn-min').textContent=uiMinimized?'▢':'–';
  post('/api/action/overlay/minimize?on='+uiMinimized); // shrink the actual ImGui window too
  if(uiMinimized)stopPreview3D();
  else if(currentTab==='character'){loadSnapshot(true);startPreview3D()}
}

// --- 3D preview (opt-in, see Settings > 3D Preview) ---
// multipart/x-mixed-replace inside a plain <img> has no guaranteed repaint cadence in modern
// Chromium — the stream can arrive at 70+ fps (confirmed via /api/preview3d/debug) while the
// browser only repaints the element far less often internally, no JS hook exposed to even measure
// it. Parsing the multipart stream ourselves and drawing each decoded frame to a canvas puts US in
// control of exactly when a repaint happens — one drawImage per arrived frame, not "whenever
// Chromium feels like it".
let p3dDragging=false, p3dLastX=0, p3dLastY=0, p3dAbort=null;

let snapshotTimer=null;
function startPreview3D(){
  stopPreview3D();
  const canvas=$('#preview3d');
  canvas.style.display='block';
  $('#p3dhint').style.display=overlayLocked?'none':'flex';
  p3dAbort=new AbortController();
  runPreview3DStream(p3dAbort.signal);
  loadEmoteList();
  // ponytail: loadSnapshot() otherwise only ran once on tab-open — a char/gear switch while the
  // tab stayed open never showed up ("Items in der Übersicht laden garnicht"). Poll it alongside
  // the preview stream; 3s matches the backend's own reseed cadence, no point going faster.
  snapshotTimer=setInterval(loadSnapshot,3000);
}

let emoteListLoaded=false;
async function loadEmoteList(){
  if(emoteListLoaded)return;
  try{
    const emotes=await fetch('/api/preview3d/emotes').then(r=>r.json());
    const sel=$('#p3demote');
    for(const e of emotes){
      const o=document.createElement('option');
      o.value=e.timelineId;o.textContent=e.name;
      sel.appendChild(o);
    }
    emoteListLoaded=true;
  }catch(e){/* list stays idle-only */}
}

async function setEmote(id){
  await post(`/api/action/preview3d/emote?timelineId=${id}`);
  $('#p3dweapon').classList.remove('active'); // server sheathes on emote select — mirror it
}
function stopPreview3D(){
  if(p3dAbort){p3dAbort.abort();p3dAbort=null}
  $('#preview3d').style.display='none';
  stopAutoSpin();
  if(snapshotTimer){clearInterval(snapshotTimer);snapshotTimer=null}
}

// --- turntable auto-rotate, like a product viewer on a shop site ---
let p3dSpinTimer=null;
function toggleAutoSpin(){ p3dSpinTimer?stopAutoSpin():startAutoSpin() }
function startAutoSpin(){
  stopAutoSpin();
  const btn=$('#p3dspin');
  btn.classList.add('active');
  btn.textContent=t('spin_stop');
  p3dSpinTimer=setInterval(()=>post('/api/action/preview3d/rotate?dx=3&dy=0'),50);
}
async function toggleWeapon(){
  const btn=$('#p3dweapon');
  const on=!btn.classList.contains('active');
  await post(`/api/action/preview3d/weapon?on=${on}`);
  btn.classList.toggle('active',on);
  $('#p3demote').value='0'; // server clears the emote when the weapon toggles — mirror it
}

async function toggleWeaponOnly(){
  const btn=$('#p3dweapononly');
  const on=!btn.classList.contains('active');
  await post(`/api/action/preview3d/weapononly?on=${on}`);
  btn.classList.toggle('active',on);
  $('#p3dweapon').classList.toggle('active',on); // drawn stance implied (glow/effects need it)
  $('#p3demote').value='0';
}

function stopAutoSpin(){
  if(p3dSpinTimer){clearInterval(p3dSpinTimer);p3dSpinTimer=null}
  const btn=$('#p3dspin');
  if(!btn)return;
  btn.classList.remove('active');
  btn.textContent=t('spin_start');
}

function findBytes(hay,needle,from){
  outer: for(let i=from;i<=hay.length-needle.length;i++){
    for(let j=0;j<needle.length;j++) if(hay[i+j]!==needle[j]) continue outer;
    return i;
  }
  return -1;
}

async function runPreview3DStream(signal){
  const canvas=$('#preview3d'), ctx=canvas.getContext('2d');
  const CRLFCRLF=new Uint8Array([13,10,13,10]);
  try{
    const res=await fetch('/api/preview3d/stream?_='+Date.now(),{signal});
    if(!res.ok||!res.body){ canvas.style.display='none'; return }
    const boundaryMatch=/boundary=([^;]+)/i.exec(res.headers.get('Content-Type')||'');
    const boundary=new TextEncoder().encode('--'+(boundaryMatch?boundaryMatch[1]:'glamsourceframe'));
    const reader=res.body.getReader();
    let buf=new Uint8Array(0);
    while(true){
      const {value,done}=await reader.read();
      if(done)break;
      const merged=new Uint8Array(buf.length+value.length);
      merged.set(buf);merged.set(value,buf.length);
      buf=merged;
      // drain every complete part currently sitting in the buffer before waiting on more network data
      for(;;){
        const partStart=findBytes(buf,boundary,0);
        if(partStart<0)break;
        const headerEnd=findBytes(buf,CRLFCRLF,partStart);
        if(headerEnd<0)break; // header not fully arrived yet
        const header=new TextDecoder().decode(buf.subarray(partStart,headerEnd));
        const lenMatch=/Content-Length:\s*(\d+)/i.exec(header);
        if(!lenMatch){ buf=buf.subarray(headerEnd+4); continue } // malformed part — skip past it
        const len=parseInt(lenMatch[1],10);
        const bodyStart=headerEnd+4;
        if(buf.length<bodyStart+len)break; // body not fully arrived yet
        const frameBytes=buf.slice(bodyStart,bodyStart+len);
        buf=buf.slice(bodyStart+len);
        // transparent-backdrop mode (see PreviewRenderer.SetTransparentBackdrop) sends PNG parts
        // instead of JPEG — read the part's own declared type rather than assuming.
        const typeMatch=/Content-Type:\s*([\w/-]+)/i.exec(header);
        const mimeType=typeMatch?typeMatch[1]:'image/jpeg';
        try{
          const bitmap=await createImageBitmap(new Blob([frameBytes],{type:mimeType}));
          if(canvas.width!==bitmap.width)canvas.width=bitmap.width;
          if(canvas.height!==bitmap.height)canvas.height=bitmap.height;
          // keep the latest frame — digital zoom/pan (p3dRedraw) re-renders it locally without
          // needing ANY new frame from the server, so the idle-throttled 1fps stream still zooms
          // and pans at full smoothness ("Box"-Fix: camera stays wide, viewport moves client-side)
          if(p3dBitmap)p3dBitmap.close();
          p3dBitmap=bitmap;
          p3dRedraw();
        }catch(e){ /* one corrupt/truncated frame — skip it, next one will be fine */ }
      }
    }
  }catch(e){ if(e.name!=='AbortError'){ console.warn('[preview3d] stream error',e); canvas.style.display='none' } }
}

async function resetPreview3D(){
  stopAutoSpin();
  p3dResetView(); // digital zoom/pan back to 1:1
  await post('/api/action/preview3d/reset');
  $('#p3demote').value='0'; // reinit builds a fresh clone without the emote override
  startPreview3D(); // reconnect the stream — the old one keeps serving frames until reset lands
}

async function loadPreview3DDebug(){
  const pre=$('#p3ddebug');
  pre.style.display='block';
  pre.textContent=t('loading');
  try{ pre.textContent=JSON.stringify(await fetch('/api/preview3d/debug').then(r=>r.json()),null,2) }
  catch(e){ pre.textContent='Fehler: '+e }
}

// Real-camera 3D viewer ("wie ein 3D-Produkt: char fest, Kamera bewegt sich"): rotate, zoom AND
// pan all drive the GAME camera server-side — perspective actually changes and the image stays
// sharp at every zoom (renders at native RT resolution; the earlier digital canvas-zoom just
// magnified pixels, "unscharf wie sau"). The "box" feeling is handled in CSS instead: the canvas
// aspect matches the render target, so any clipping happens at the viewport border like in every
// real 3D product viewer (see #preview3d's comment).
let p3dBitmap=null;
// Lupe (hold middle mouse): magnified circle at the cursor. The stream now carries a 2x-res
// render target (server-side CreateTexture2D hook) shown downscaled — the loupe draws those
// pixels 1:1-plus, so it's native-sharp, not digital mush.
let p3dLoupe=false,p3dLoupeX=0,p3dLoupeY=0;
const P3D_LOUPE_R=150,P3D_LOUPE_MAG=2;
function p3dRedraw(){
  const canvas=$('#preview3d');
  const ctx=canvas.getContext('2d');
  // clear — a transparent PNG frame would otherwise leave the previous frame's pixels showing
  ctx.clearRect(0,0,canvas.width,canvas.height);
  if(!p3dBitmap)return;
  // no spotlight, no backdrop of any kind — "kein Schein, soll ins Webview integriert sein":
  // the transparent char composites straight onto the page like any other element
  ctx.drawImage(p3dBitmap,0,0);
  if(p3dLoupe){
    const r=P3D_LOUPE_R,m=P3D_LOUPE_MAG,x=p3dLoupeX,y=p3dLoupeY;
    ctx.save();
    ctx.beginPath();ctx.arc(x,y,r,0,7);ctx.clip();
    // clear the circle FIRST — with a transparent frame, the magnified crop's transparent pixels
    // otherwise let the unmagnified base image show through underneath ("seh sachen doppelt")
    ctx.clearRect(x-r,y-r,2*r,2*r);
    // smoothing ON: with the RT still at native 576 res (2x realloc only catches a fresh boot's
    // first allocation), nearest-neighbor was pure pixel blocks ("sieht pixelig aus")
    ctx.imageSmoothingEnabled=true;ctx.imageSmoothingQuality='high';
    ctx.drawImage(p3dBitmap,x-r/m,y-r/m,2*r/m,2*r/m,x-r,y-r,2*r,2*r);
    ctx.restore();
    ctx.beginPath();ctx.arc(x,y,r,0,7);
    ctx.lineWidth=3;ctx.strokeStyle='rgba(255,255,255,.55)';ctx.stroke();
  }
}
function p3dResetView(){p3dRedraw()}

(function initPreview3DDrag(){
  const canvas=$('#preview3d');
  let p3dPanning=false;
  function loupePos(e){
    const rect=canvas.getBoundingClientRect();
    p3dLoupeX=(e.clientX-rect.left)*canvas.width/rect.width;
    p3dLoupeY=(e.clientY-rect.top)*canvas.height/rect.height;
  }
  canvas.addEventListener('contextmenu',e=>e.preventDefault());
  canvas.addEventListener('mousedown',e=>{
    stopAutoSpin();
    p3dLastX=e.clientX;p3dLastY=e.clientY;
    if(e.button===1){e.preventDefault();p3dLoupe=true;loupePos(e);p3dRedraw();return} // middle = Lupe
    if(e.button===2){p3dPanning=true;canvas.classList.add('panning')}
    else{p3dDragging=true;canvas.classList.add('active')}
  });
  window.addEventListener('mouseup',()=>{
    p3dDragging=false;p3dPanning=false;canvas.classList.remove('active','panning');
    if(p3dLoupe){p3dLoupe=false;p3dRedraw()}
  });
  window.addEventListener('mousemove',e=>{
    if(p3dLoupe){loupePos(e);p3dRedraw();return}
    if(!p3dDragging&&!p3dPanning)return;
    const dx=e.clientX-p3dLastX, dy=e.clientY-p3dLastY;
    p3dLastX=e.clientX;p3dLastY=e.clientY;
    // STRICT turntable ("er soll fest auf einem Punkt sitzen"): no pan of any kind — even the
    // height-only ride let the char move in the frame. Both buttons orbit; wheel zooms. That's it.
    post(`/api/action/preview3d/rotate?dx=${(dx*0.75).toFixed(2)}&dy=${(dy*0.75).toFixed(2)}`);
  });
  canvas.addEventListener('wheel',e=>{
    e.preventDefault();
    // plain center zoom — zoom-to-cursor shifted the char around the frame ("nicht festgepinnt")
    post(`/api/action/preview3d/zoom?delta=${(-e.deltaY*0.002).toFixed(3)}`);
  },{passive:false});
})();

// query + slot/job/iLvl filters -> /api/search params. A filter alone (no query) browses, e.g.
// "every Head item for PLD from iLvl 700" — the server sorts filtered hits by iLvl desc.
function searchParams(){
  const p=new URLSearchParams({q:$('#q').value.trim()});
  for(const [id,key] of [['f-slot','slot'],['f-job','job'],['f-min','ilvlmin'],['f-max','ilvlmax']]){const v=$('#'+id).value;if(v)p.set(key,v)}
  return p;
}
async function runSearch(){
  const p=searchParams(),q=p.get('q'),box=$('#results');
  const isId=/^\d+$/.test(q); // pure-digit query = item ID lookup, skip the 3-char name minimum
  const hasFilter=[...p.keys()].length>1;
  if(!isId&&q.length<3&&!hasFilter){box.innerHTML='';updateOverlayCompactness();return}
  box.innerHTML=`<div class="empty"><span class="spinner"></span>${t('searching')}</div>`;
  const r=await fetch('/api/search?'+p).then(r=>r.json());
  box.style.display='';$('#resback').style.display='none';
  box.innerHTML=r.length?r.map(x=>`<div class="row" tabindex="0" role="button" onclick="openItem(${x.id})">${img(x.iconId,28)}<span>${esc(x.name)}${x.ilvl?`<span class="ilvl">iLvl ${x.ilvl}</span>`:''}</span><img class="rowpreview" src="/api/itemimage/${x.id}" loading="lazy" onerror="this.remove()"></div>`).join(''):`<div class="empty">${t('no_items')}</div>`;
  updateOverlayCompactness();
}
let deb;
const queueSearch=()=>{clearTimeout(deb);deb=setTimeout(runSearch,250)};
$('#q').addEventListener('input',queueSearch);
for(const id of['f-slot','f-job'])$('#'+id).addEventListener('change',queueSearch);
for(const id of['f-min','f-max'])$('#'+id).addEventListener('input',queueSearch);
// keyboard: ↓ from the search box into the list, ↑/↓ between rows, Enter opens (Enter in the box = first hit)
$('#q').addEventListener('keydown',e=>{
  const first=$('#results .row');
  if(!first)return;
  if(e.key==='ArrowDown'){first.focus();e.preventDefault()}
  else if(e.key==='Enter')first.click();
});
$('#results').addEventListener('keydown',e=>{
  const rows=[...$('#results').querySelectorAll('.row')],i=rows.indexOf(document.activeElement);
  if(i<0)return;
  if(e.key==='Enter')rows[i].click();
  else if(e.key==='ArrowDown'&&i<rows.length-1){rows[i+1].focus();e.preventDefault()}
  else if(e.key==='ArrowUp'){(i>0?rows[i-1]:$('#q')).focus();e.preventDefault()}
});
// Duty Drops tab (doku TODO): duty list from /api/duties (once), current duty from
// /api/duty/current on every tab open (auto-select), drops per duty, item detail in-tab.
let dutyList=null,dutySelected=0;
async function loadDuties(){
  if(!dutyList){dutyList=await fetch('/api/duties').then(r=>r.json());renderDutyList()}
  if(!dutySelected)$('#dutyhead').innerHTML=`<div class="empty" style="margin-top:0">${t('duty_pick')}</div>`;
  const cur=await fetch('/api/duty/current').then(r=>r.ok?r.json():{id:0}).catch(()=>({id:0}));
  const d=dutyList.find(x=>x.id===cur.id);
  $('#dutycurrent').textContent=d?`${t('duty_current')}: ${d.name}`:t('duty_none');
  if(d&&dutySelected!==d.id)selectDuty(d.id);
}
// Drill-down like the Duty Finder ("kompakter, Kacheln zum Klicken"): content type tiles →
// expansion tiles → duty tiles → drops, with a breadcrumb back. A search term bypasses the
// drill-down and shows the flat grouped list.
const dutyNav={type:null,diff:null,exp:null};
function dutyNavTo(type,diff,exp){dutyNav.type=type;dutyNav.diff=diff;dutyNav.exp=exp;renderDutyList()}
const dutyTile=(x,boss)=>`<div class="dtile${x.id===dutySelected?' active':''}" tabindex="0" role="button" onclick="selectDuty(${x.id})"><img src="/api/icon/${x.imageId}" loading="lazy" onerror="this.style.visibility='hidden'"><div><div class="nm">${esc(x.name)}${boss?`<span class="ilvl">${esc(boss)}</span>`:''}</div><div class="meta">Lv.${x.level}${x.itemLevel?` · iLvl ${x.itemLevel}`:''} · ${x.drops?`${x.drops} ${t('drops')}`:t('duty_garland')}</div></div></div>`;
// banner of the newest (highest level, then newest id) duty of a folder as the tile background
const dutyRep=rows=>rows.reduce((a,b)=>(b.level>a.level||(b.level===a.level&&b.id>a.id))?b:a);
const folderTile=(label,rows,onclick,icon)=>{const r=dutyRep(rows);return `<div class="ntile" tabindex="0" role="button" onclick="${onclick}" style="background-image:url(/api/icon/${r.imageId})"><div class="nshade">${icon?`<img class="ticon" src="/api/icon/${icon}" onerror="this.remove()">`:''}<div><div class="nm">${label}</div><div class="meta">${rows.length} ${t('duties')}</div></div></div></div>`};
function renderDutyList(){
  const q=$('#dq').value.trim().toLowerCase(),all=dutyList||[],list=$('#dutylist'),crumb=$('#dutycrumb');
  if(q){
    crumb.innerHTML='';list.classList.remove('grid');
    let h='',lastType='',lastExp='';
    // duty name OR boss name ("susano" -> the Pool of Tribute); the matching boss is shown on the tile
    const bossHit=x=>(x.bosses||[]).find(b=>b.toLowerCase().includes(q));
    for(const x of all.filter(x=>x.name.toLowerCase().includes(q)||bossHit(x))){
      if(x.type!==lastType){h+=`<div class="dsech"${h?'':' style="margin-top:0"'}>${t('dtype_'+x.type)}</div>`;lastType=x.type;lastExp=''}
      if(x.expansion!==lastExp){h+=`<div class="dsub">${esc(x.expansion)}</div>`;lastExp=x.expansion}
      h+=dutyTile(x,x.name.toLowerCase().includes(q)?null:bossHit(x));
    }
    list.innerHTML=h||`<div class="empty">${t('no_items')}</div>`;
    return;
  }
  // drill-down: type -> difficulty (only when the type has more than one) -> expansion -> duties
  const inType=all.filter(x=>x.type===dutyNav.type);
  const diffs=[...new Set(inType.map(x=>x.difficulty))];
  const hasDiff=diffs.length>1;
  const inDiff=hasDiff?inType.filter(x=>x.difficulty===dutyNav.diff):inType;
  const seg=(label,fn)=>`<span class="crumb"${fn?` onclick="${fn}"`:''}>${label}</span>`;
  let c=seg(t('duty_all'),dutyNav.type?'dutyNavTo(null,null,null)':null);
  if(dutyNav.type)c+=' › '+seg(t('dtype_'+dutyNav.type),(dutyNav.diff||dutyNav.exp)?`dutyNavTo('${dutyNav.type}',null,null)`:null);
  if(dutyNav.diff)c+=' › '+seg(t('diff_'+dutyNav.diff),dutyNav.exp?`dutyNavTo('${dutyNav.type}','${dutyNav.diff}',null)`:null);
  if(dutyNav.exp)c+=' › '+seg(esc(dutyNav.exp),null);
  crumb.innerHTML=c;
  if(!dutyNav.type){
    list.classList.add('grid');
    const types=[...new Set(all.map(x=>x.type))];
    list.innerHTML=types.map(ty=>{const g=all.filter(x=>x.type===ty);return folderTile(t('dtype_'+ty),g,`dutyNavTo('${ty}',null,null)`,dutyRep(g).typeIcon)}).join('');
  }else if(hasDiff&&!dutyNav.diff){
    list.classList.add('grid');
    list.innerHTML=diffs.map(df=>folderTile(t('diff_'+df),inType.filter(x=>x.difficulty===df),`dutyNavTo('${dutyNav.type}','${df}',null)`)).join('');
  }else if(!dutyNav.exp){
    list.classList.add('grid');
    const exps=[...new Set(inDiff.map(x=>x.expansion))];
    list.innerHTML=exps.map(ex=>folderTile(esc(ex),inDiff.filter(x=>x.expansion===ex),`dutyNavTo('${dutyNav.type}',${hasDiff?`'${dutyNav.diff}'`:'null'},'${ex}')`)).join('');
  }else{
    list.classList.remove('grid');
    list.innerHTML=inDiff.filter(x=>x.expansion===dutyNav.exp).map(x=>dutyTile(x)).join('')||`<div class="empty">${t('no_items')}</div>`;
  }
}
$('#dq').addEventListener('input',renderDutyList);
const featSection=list=>`<div class="dsec" style="margin-top:0"><div class="dsech">${t('duty_featured')}</div><div class="results dgrid">${list.map(x=>`<div class="row" tabindex="0" role="button" onclick="openDutyItem(${x.itemId})">${img(x.iconId,28)}<span>${esc(x.name)}<span class="ilvl">${x.kind==='Mount'?t('mount'):t('minion')}</span></span><img class="rowpreview" src="/api/itemimage/${x.itemId}" loading="lazy" onerror="this.remove()"></div>`).join('')}</div></div>`;
const previewRows=list=>list.map(x=>`<div class="row" tabindex="0" role="button" onclick="openDutyItem(${x.itemId})">${img(x.iconId,28)}<span>${esc(x.name)}${x.itemLevel?`<span class="ilvl">iLvl ${x.itemLevel}</span>`:''}</span><img class="rowpreview" src="/api/itemimage/${x.itemId}" loading="lazy" onerror="this.remove()"></div>`).join('');
const dropRows=list=>list.map(x=>`<div class="row" tabindex="0" role="button" onclick="openDutyItem(${x.itemId})">${img(x.iconId,28)}<span>${esc(x.name)}${x.itemLevel?`<span class="ilvl">iLvl ${x.itemLevel}</span>`:''}</span></div>`).join('');
async function selectDuty(id){
  dutySelected=id;
  const nav=(dutyList||[]).find(y=>y.id===id);
  if(nav&&!$('#dq').value.trim()){dutyNav.type=nav.type;dutyNav.diff=new Set((dutyList||[]).filter(y=>y.type===nav.type).map(y=>y.difficulty)).size>1?nav.difficulty:null;dutyNav.exp=nav.expansion} // land in the duty's own folder
  renderDutyList();
  $('#dutydetail').innerHTML='';$('#dutyhead').innerHTML='';
  $('#dutydrops').innerHTML=`<div class="empty"><span class="spinner"></span>${t('loading')}</div>`;
  const d=await fetch('/api/duty/'+id).then(r=>r.ok?r.json():null);
  if(dutySelected!==id)return; // a newer selectDuty() already superseded this one (fast duty-switch race)
  if(!d){$('#dutydrops').innerHTML=`<div class="empty">${t('not_found')}</div>`;return}
  // Duty Finder style header: the game's own banner image, then one section per boss (drops +
  // chests after that boss), then whatever drops anywhere in the duty
  $('#dutyhead').innerHTML=`<div class="dutybanner"><img src="/api/icon/${d.imageId}" onerror="this.style.display='none'"><div><h2 class="name">${esc(d.name)}</h2><div class="meta">${esc(d.type)} · Lv.${d.level}${d.itemLevel?` · iLvl ${d.itemLevel}`:''}</div><button class="act" style="margin-top:6px" onclick="post('/api/action/dutyfinder/${d.id}')">${t('duty_open')}</button></div></div>`;
  let h='';
  // mounts / minions first, with the wiki preview picture — that's what people farm Extremes for
  if(d.featured?.length)h+=featSection(d.featured);
  // exchange shops of the duty (totem -> weapons, books -> gear): with preview pictures
  for(const ex of d.exchanges||[])h+=`<div class="dsec"><div class="dsech">${t('duty_exchange')} — ${esc(ex.shop)}</div>${ex.token?`<div class="dsub">${t('duty_exchange_for')} ${esc(ex.token.name)}</div>`:''}<div class="results dgrid">${previewRows(ex.items)}</div></div>`;
  for(const b of d.bosses){
    h+=`<div class="dsec"><div class="dsech">${t('boss')} ${b.fightNo+1}${b.name?` — ${esc(b.name)}`:''}</div>`;
    if(b.drops.length)h+=`<div class="results dgrid">${dropRows(b.drops)}</div>`;
    for(const c of b.chests)h+=`<div class="dsub">${t('chest')}${b.chests.length>1?` ${c.cofferNo+1}`:''}</div><div class="results dgrid">${dropRows(c.items)}</div>`;
    h+='</div>';
  }
  if(d.general.length)h+=`<div class="dsec"><div class="dsech">${t('duty_general')}</div><div class="results dgrid">${dropRows(d.general)}</div></div>`;
  const hasLocal=!!h||!!(d.exchanges||[]).length;
  // no local table (post-7.1 content): Garland's fight coffers are the whole drop list — show a
  // spinner instead of "no drops" until they arrive
  $('#dutydrops').innerHTML=h||`<div class="empty"><span class="spinner"></span>${t('loading')}</div>`;
  const coffers=await fetch(`/api/duty/${id}/coffers`).then(r=>r.ok?r.json():[]).catch(()=>[]);
  if(dutySelected!==id)return;
  if(!coffers.length){if(!hasLocal)$('#dutydrops').innerHTML=`<div class="empty">${t('duty_nodrops')}</div>`;return}
  if(!hasLocal)$('#dutydrops').innerHTML='';
  // mounts / minions from the Garland coffers when the local table had none
  if(!d.featured?.length){
    const feat=[...new Map(coffers.flatMap(c=>c.items).filter(x=>x.kind).map(x=>[x.itemId,x])).values()];
    if(feat.length)$('#dutydrops').insertAdjacentHTML('beforeend',featSection(feat));
  }
  let c='';
  coffers.forEach((cf,i)=>{
    const placed=cf.fightNo<0;
    const map=placed&&d.mapId?` <button class="act" onclick="post('/api/action/map?territory=${d.territoryTypeId}&map=${d.mapId}&x=${cf.x}&y=${cf.y}')">${t('map')}</button>`:'';
    const label=placed?`${t('chest')} ${i+1} · (${cf.x.toFixed(1)}, ${cf.y.toFixed(1)})`:`${t('boss')} ${cf.fightNo+1} · ${t('chest')}`;
    c+=`<div class="dsub">${label}${map}</div><div class="results dgrid">${dropRows(cf.items)}</div>`;
  });
  $('#dutydrops').insertAdjacentHTML('beforeend',`<div class="dsec"${hasLocal?'':' style="margin-top:0"'}><div class="dsech">${hasLocal?t('duty_coffers'):t('duty_garland_drops')}</div>${c}</div>`);
}
async function openDutyItem(id){
  $('#dutydetail').innerHTML=`<div class="empty"><span class="spinner"></span>${t('loading')}</div>`;
  const d=await fetch('/api/item/'+id).then(r=>r.ok?r.json():null);
  $('#dutydetail').innerHTML=d?buildItemHtml(d,'openDutyItem'):`<div class="empty">${t('not_found')}</div>`;
  if(d){annotateInventory($('#dutydetail'));annotateEvent($('#dutydetail'),id)}
}

// "Suchergebnisse verschwinden beim Item-Klick": list is only hidden, the back bar restores it
function backToResults(){
  $('#detail').innerHTML='';$('#resback').style.display='none';$('#results').style.display='';
  updateOverlayCompactness();
}

// "Item Search darf gern klein bleiben bis man sachen sucht": the Browsingway overlay window has a
// fixed size (Plugin.PinBrowsingwayOverlaySize) since the page itself can't shrink it — tell the
// plugin to use the small pre-results height while on the Suche tab with nothing shown yet.
function updateOverlayCompactness(){
  const compact=currentTab==='lookup'&&!$('#results').innerHTML&&!$('#detail').innerHTML;
  post('/api/action/overlay/compact?on='+compact);
}

// openFn: which function re-opens an item from within this panel — 'openItem' (Suche tab, writes
// to #detail) or 'showItemPanel' (Charakter tab, writes to #chardetail). Set-member chips need to
// call back into whichever panel is actually showing, not always the Suche one.
function buildItemHtml(d,openFn='openItem'){
  let h=`<div class="header">${img(d.iconId,48)}<div><h2 class="name">${esc(d.name)}</h2><div class="meta">${t('item_id')} ${d.itemId} · iLvl ${d.itemLevel}${d.isMarketable?` · ${t('marketable')}`:''}${d.setName?` · ${t('set_label')}: ${esc(d.setName)}`:''}</div>${d.isEquippable?`<button class="act" style="margin-top:4px" title="${t('apply_item_tt')}" onclick="glamourerPost('/api/action/glamourer/item/${d.itemId}',this)">${t('apply_btn')}</button>`:''}</div></div><div class="preview"><img src="/api/itemimage/${d.itemId}" loading="lazy" onerror="this.parentElement.style.display='none'"></div>`;
  if((d.setMembers??[]).length){
    h+=`<div class="tbl">${t('rest_of_set')}</div><div style="display:flex;flex-wrap:wrap;gap:8px;margin:6px 0 14px">`;
    for(const m of d.setMembers)h+=`<div class="row" style="width:auto" onclick="${openFn}(${m.itemId})">${img(m.iconId,24)}<span>${esc(m.name)}</span></div>`;
    h+='</div>';
  }
  h+='<div class="cards">';
  // ponytail: same shop/vendor sold from several NPC locations used to render as one full repeated
  // card per location ("unübersichtlich" — a shop with 3 vendor spots meant 3 near-identical cards).
  // Group by description (+ cost, in case two shops share a name but differ in price) into ONE card
  // with a multi-row NPC table instead.
  const groups=new Map();
  for(const s of d.sources??[]){
    const key=(s.description??'')+'|'+JSON.stringify(s.costs??[]);
    if(!groups.has(key))groups.set(key,[]);
    groups.get(key).push(s);
  }
  for(const group of groups.values())h+=renderSource(group,d.itemId,openFn);
  h+='</div>';
  if(!(d.sources??[]).length)h+=`<div class="empty">${t('no_sources')}</div>`;
  return h;
}

// ponytail: "dumb user" click-A-then-B-fast race, actually reproduced (item 24599, not
// marketable, showed item 20524's price after a rapid double-click) — annotateEvent/annotateMarket
// each do their OWN async fetch, then insert into container.querySelector('.header .meta') at
// insert time, not render time. If a newer openItem/showItemPanel call already replaced the panel
// by then, that insert lands on the WRONG (newer) item's header. A simple incrementing token per
// container: bump it on every new call, annotate* functions check it's still theirs before
// touching the DOM after their await.
async function openItem(id){
  const el=$('#detail');
  const myToken=(el._reqToken=(el._reqToken||0)+1);
  const n=$('#results').querySelectorAll('.row').length;
  if(n){$('#results').style.display='none';$('#resback').style.display='';$('#resback').textContent=`${t('back_results')} (${n})`}
  el.innerHTML=`<div class="empty"><span class="spinner"></span>${t('loading')}</div>`;
  updateOverlayCompactness();
  const d=await fetch('/api/item/'+id).then(r=>r.ok?r.json():null);
  if(el._reqToken!==myToken)return; // a newer click already superseded this one
  el.innerHTML=d?buildItemHtml(d):`<div class="empty">${t('not_found')}</div>`;
  if(d){annotateInventory(el);annotateEvent(el,id,myToken);annotateMarket(el,id,d.isMarketable,myToken)}
}

// group: one or more sources sharing the same description+cost (see buildItemHtml) — one card,
// one NPC/location table row per entry instead of a fully repeated card per location.
function renderSource(group,itemId,openFn='openItem'){
  const s=group[0];
  // ponytail: named srcType, not t — a local "t" here shadows the global t() i18n function used
  // below (live crash: "TypeError: t is not a function" the moment any t('key') call ran)
  const srcType=(s.type??'').toString();
  const cls=/craft/i.test(srcType)?'crafted':/vendor|shop/i.test(srcType)?'vendor':/quest/i.test(srcType)?'quest':/trial|raid|dungeon/i.test(srcType)?'duty':'';
  let h=`<div class="card ${cls}"><h3><span class="badge">${typeIcon(cls)} ${esc(srcType).toUpperCase()}</span> ${esc(s.description??'')}</h3>`;
  // same jump ImGui's ItemDetailWindow offers: coffer / "Retired — replaced by Augmented X" -> that item
  if(s.sourceItemId)h+=`<button class="act" onclick="${openFn}(${s.sourceItemId})">${I18N.open_item[lang]}</button> `;
  if(/craft/i.test(srcType))h+=`<button class="act" onclick="post('/api/action/craftlog/${itemId}')">${t('open_craftlog')}</button>`;
  if(s.cfcRowId)h+=` <button class="act" onclick="post('/api/action/dutyfinder/${s.cfcRowId}')">${t('duty_open')}</button>`;
  const withNpc=group.filter(g=>g.npcName);
  if(withNpc.length){
    h+=`<table><tr><th>${t('npc')}</th><th>${t('location')}</th><th></th></tr>`;
    for(const g of withNpc)h+=npcRow(g);
    h+='</table>';
  }
  for(const key of['materials','costs']){
    const list=s[key];
    if(list&&list.length){
      // an "Other" card with materials is an outfit set (ItemDetailService 7f) — its rows are pieces
      h+=`<div style="margin-top:8px;color:var(--muted);font-size:12px">${key==='materials'?(srcType==='Other'?t('pieces_label'):t('materials_label')):t('cost_label')}</div>`;
      for(const m of list){
        // data-item + data-need: annotateInventory() fills in have/where AFTER this HTML lands in
        // the DOM (needs an async /api/inventory fetch per item — can't do that synchronously here).
        // rows with a real item id open that item (piece / material / currency source)
        const click=m.itemId?` style="cursor:pointer" onclick="${openFn}(${m.itemId})"`:'';
        h+=`<div class="matrow" data-item="${m.itemId}" data-need="${m.count}"${click}>${img(m.itemId===0?GIL_ICON:m.iconId,22)}<span class="matqty">${esc(m.name)||(m.itemId===0?'Gil':'#'+m.itemId)} × ${m.count.toLocaleString()}</span></div>`;
      }
    }
  }
  return h+'</div>';
}

// "die währung in grün" (ImGui already colors have>=need) + "man sieht welches mat wo liegt" — Web
// UI had neither. Runs after buildItemHtml's innerHTML lands: fetches ownership per unique item id,
// colors the qty text like ImGui (green=enough, muted=short), retainer breakdown goes in the title
// tooltip (same info ImGui shows on hover, no extra UI needed for it).
// "auch events prüfen, nicht mehr erhältlich markieren, oder ob's grad läuft": one best-effort
// badge under the item name — recurring events never say "gone", only live status (or "unknown" if
// the Lodestone check failed); one-time events say "gone" only once we positively know it's not on.
async function annotateEvent(container,itemId,token){
  const ev=await fetch(`/api/item/${itemId}/event`).then(r=>r.ok?r.json():null).catch(()=>null);
  if(!ev||container._reqToken!==token)return; // panel got replaced by a newer click while we awaited
  const kind=ev.recurring?t('ev_recurring'):t('ev_onetime');
  let status;
  if(ev.active===true)status=t('ev_active');
  else if(ev.active===false)status=ev.recurring?t('ev_inactive'):t('ev_gone');
  else status=t('ev_unknown');
  const cls=ev.active===true?'ok':ev.active===false&&!ev.recurring?'short':'';
  container.querySelector('.header .meta')?.insertAdjacentHTML('afterend',`<div class="meta ${cls}">${kind}: ${esc(ev.eventName)} — ${status}</div>`);
}
// "universalis preise fehlen im webview" — ImGui's ItemDetailWindow always had this
// (DrawMarketPricesCompact), the web panel never fetched it at all. Same insert-after-header
// pattern as annotateEvent above; skipped entirely for non-marketable items (no point hitting
// Universalis for something that can't be traded).
async function annotateMarket(container,itemId,isMarketable,token){
  if(!isMarketable)return;
  const m=await fetch(`/api/market/${itemId}`).then(r=>r.ok?r.json():null).catch(()=>null);
  if(!m||container._reqToken!==token)return; // panel got replaced by a newer click while we awaited
  const dc=m.dcWorldName?` · ${t('market_dc')} (${esc(m.dcWorldName)}): <b style="color:var(--success)">${m.dcMinPrice.toLocaleString()} Gil</b>`:'';
  container.querySelector('.header .meta')?.insertAdjacentHTML('afterend',
    `<div class="meta">${t('market_world')} <b style="color:var(--accent)">${m.worldMinPrice.toLocaleString()} Gil</b>${dc}</div>`);
}
async function annotateInventory(container){
  const rows=[...container.querySelectorAll('.matrow[data-item]')];
  const ids=[...new Set(rows.map(r=>r.dataset.item).filter(id=>id!=='0'))];
  const results=await Promise.all(ids.map(id=>fetch('/api/inventory/'+id).then(r=>r.ok?r.json():null).catch(()=>null)));
  const byId=Object.fromEntries(ids.map((id,i)=>[id,results[i]]));
  for(const row of rows){
    const inv=byId[row.dataset.item];
    if(!inv)continue;
    const need=parseInt(row.dataset.need,10);
    const sufficient=inv.total>=need;
    const span=row.querySelector('.matqty');
    span.style.color=sufficient?'var(--success)':'var(--muted)';
    const parts=[];
    if(inv.bags)parts.push(`${t('bags')} ${inv.bags}`);
    if(inv.saddlebag)parts.push(`${t('saddlebag')} ${inv.saddlebag}`);
    for(const r of inv.retainers??[])parts.push(`${esc(r.name)} ${r.count}`);
    // "man sieht welches mat wo liegt": inline, not just in a hover tooltip
    row.querySelector('.where')?.remove();
    if(parts.length)span.insertAdjacentHTML('afterend',`<span class="where">${parts.join(' · ')}</span>`);
  }
}

function npcRow(s){
  const loc=[s.zoneName,s.mapX!=null?`(${s.mapX.toFixed(1)}, ${s.mapY.toFixed(1)})`:null].filter(Boolean).join(' ');
  const map=s.mapX!=null&&s.territoryTypeId?`<button class="act" onclick="post('/api/action/map?territory=${s.territoryTypeId}&map=${s.mapId}&x=${s.mapX}&y=${s.mapY}')">${t('map')}</button>`:'';
  return`<tr><td>${esc(s.npcName)}</td><td class="muted">${esc(loc)}</td><td>${map}</td></tr>`;
}

// EquipmentSlotType names -> in-game style labels per UI language (was German-only, and missed
// "Earrings" — the enum name — so that one slot showed up untranslated)
const SLOT_LBL={MainHand:{en:'Main Hand',de:'Hauptwaffe'},OffHand:{en:'Off Hand',de:'Nebenwaffe'},
  Head:{en:'Head',de:'Kopf'},Body:{en:'Body',de:'Rumpf'},Hands:{en:'Hands',de:'Hände'},
  Legs:{en:'Legs',de:'Beine'},Feet:{en:'Feet',de:'Füße'},Earrings:{en:'Earrings',de:'Ohrringe'},Ear:{en:'Earrings',de:'Ohrringe'},
  Necklace:{en:'Necklace',de:'Halskette'},Bracelets:{en:'Bracelets',de:'Armreif'},
  RingRight:{en:'Ring (right)',de:'Ring (rechts)'},RingLeft:{en:'Ring (left)',de:'Ring (links)'},Waist:{en:'Waist',de:'Gürtel'}};

// search filter dropdowns — slots from SLOT_LBL (minus the Ear alias and the long-gone Waist),
// jobs from /api/jobs (game's own abbreviations + localized names). Rebuilt on language toggle.
let jobList=[];
function fillFilters(){
  const s=$('#f-slot'),j=$('#f-job'),sv=s.value,jv=j.value;
  s.innerHTML=`<option value="">${t('f_all_slots')}</option>`+Object.keys(SLOT_LBL).filter(k=>k!=='Ear'&&k!=='Waist').map(k=>`<option value="${k}">${SLOT_LBL[k][lang]}</option>`).join('');
  j.innerHTML=`<option value="">${t('f_all_jobs')}</option>`+jobList.map(x=>`<option value="${esc(x.abbr)}">${esc(x.abbr)} · ${esc(x.name.replace(/^\w/,c=>c.toUpperCase()))}</option>`).join('');
  s.value=sv;j.value=jv;
}
fillFilters();
fetch('/api/jobs').then(r=>r.json()).then(l=>{jobList=l;fillFilters()}).catch(()=>{});

let lastSnapshotHash=null;
async function loadSnapshot(force){
  const d=await fetch('/api/snapshot').then(r=>r.json());
  // only touch the DOM (and reload every icon <img>) on an actual change — class/gear switch or a
  // Recent/person click — not on every poll tick ("icons aktualisieren nur bei aktivem switch")
  if(!force && d.hash===lastSnapshotHash)return;
  lastSnapshotHash=d.hash;
  const slots=(d.slots??[]).map(s=>{
    const id=s.glamourItemId??s.actualItemId;
    if(!id)return'';
    const name=s.glamourItemName??s.actualItemName??'';
    const lbl=SLOT_LBL[s.slot]?.[lang]??s.slot;
    // opens the lookup in the RIGHT-SIDE panel — no tab switch, no page scroll
    return`<div class="slot" onclick="showItemPanel(${id})">${img(s.iconId,40)}<div><div class="lbl">${esc(lbl)}</div><div class="nm">${esc(name)}</div></div>${s.isGlamoured?`<span class="glam">${t('glam_badge')}</span>`:''}</div>`;
  }).join('');
  const head=d.activeRecentName?`<div class="empty">${t('viewing')}: ${esc(d.activeRecentName)}</div>`
    :(slots?'':`<div class="empty">${t('no_char_data')}</div>`);
  $('#charslots').innerHTML=head+slots;
}

// same list/click as the native ImGui sidebar (GlamSourceShellWindow.DrawRecentSidebar) — view a
// stored outfit snapshot of a previously-inspected player, index-addressed.
async function loadRecents(){
  const recents=await fetch('/api/recents').then(r=>r.json());
  const box=$('#charrecents');
  if(!recents.length){box.innerHTML=`<div class="empty">${t('none_yet')}</div>`;return}
  box.innerHTML=recents.map(r=>`<div class="recent${r.active?' active':''}" onclick="activateRecent(${r.index})">${esc(r.name)}${r.world?`<span class="world">${esc(r.world)}</span>`:''}<span class="del" title="${t('remove_recent')}" onclick="event.stopPropagation();removeRecent(${r.index})">×</span></div>`).join('');
}
async function activateRecent(index){
  await post('/api/action/recent/'+index);
  await loadRecents();
  await loadSnapshot(true);
}
async function removeRecent(index){
  await post('/api/action/recent/'+index+'/remove');
  await loadRecents();
}

async function showItemPanel(id){
  const box=$('#chardetail');
  const myToken=(box._reqToken=(box._reqToken||0)+1); // same race guard as openItem — see its comment
  box.innerHTML=`<div class="empty"><span class="spinner"></span>${t('loading')}</div>`;
  const d=await fetch('/api/item/'+id).then(r=>r.ok?r.json():null);
  if(box._reqToken!==myToken)return;
  box.innerHTML=d?buildItemHtml(d,'showItemPanel'):`<div class="empty">${t('not_found')}</div>`;
  if(d){annotateInventory(box);annotateEvent(box,id,myToken);annotateMarket(box,id,d.isMarketable,myToken)}
}

async function post(url){await fetch(url,{method:'POST'})}
// Glamourer apply (outfit or single piece): POST, show the plugin's status text next to the button
async function glamourerPost(url,btn){
  const r=await fetch(url,{method:'POST'}).then(r=>r.ok?r.json():null).catch(()=>null);
  let s=btn.nextElementSibling;
  if(!s||!s.classList.contains('applystatus')){s=document.createElement('span');s.className='applystatus';btn.insertAdjacentElement('afterend',s)}
  s.textContent=r?.status||t('not_found');
}

// Outfit shopping list (prototype, see doku): one best source per shown slot, merged into stops.
// Rows are .matrow[data-item] so annotateInventory() marks owned pieces green for free.
const shopRow=(m,openFn)=>`<div class="matrow" data-item="${m.itemId}" data-need="${m.count}"${m.itemId&&openFn?` style="cursor:pointer" onclick="${openFn}(${m.itemId})"`:''}>${img(m.itemId===0?GIL_ICON:m.iconId,22)}<span class="matqty">${esc(m.name)||(m.itemId===0?'Gil':'#'+m.itemId)}${m.count>1?` × ${m.count.toLocaleString()}`:''}</span></div>`;
async function showShoppingList(){
  const box=$('#chardetail');
  box.innerHTML=`<div class="empty"><span class="spinner"></span>${t('loading')}</div>`;
  const d=await fetch('/api/shoppinglist').then(r=>r.ok?r.json():null);
  if(!d){box.innerHTML=`<div class="empty">${t('not_found')}</div>`;return}
  let h=`<div class="header"><div><h2 class="name">${t('shopping_title')}</h2><div class="meta">${d.lines.length} ${t('shopping_stops')}</div></div></div>`;
  if(d.totals.length)h+=`<div class="tbl">${t('shopping_total')}</div>`+d.totals.map(c=>shopRow(c)).join('');
  h+='<div class="cards">';
  for(const l of d.lines){
    const cls=l.kind==='Craft'?'crafted':l.kind==='Vendor'?'vendor':l.kind==='Duty'?'duty':'';
    h+=`<div class="card ${cls}"><h3><span class="badge">${esc(l.kind).toUpperCase()}</span> ${esc(l.title)}</h3>`;
    if(l.npcName)h+=`<table><tr><th>${t('npc')}</th><th>${t('location')}</th><th></th></tr>${npcRow(l)}</table>`;
    if(l.cfcRowId)h+=`<button class="act" onclick="post('/api/action/dutyfinder/${l.cfcRowId}')">${t('duty_open')}</button>`;
    h+=`<div style="margin-top:8px;color:var(--muted);font-size:12px">${t('shopping_items')}</div>`+l.items.map(m=>shopRow(m,'showItemPanel')).join('');
    if(l.costs.length)h+=`<div style="margin-top:8px;color:var(--muted);font-size:12px">${t('cost_label')}</div>`+l.costs.map(c=>shopRow(c)).join('');
    if(l.materials.length)h+=`<div style="margin-top:8px;color:var(--muted);font-size:12px">${t('materials_label')}</div>`+l.materials.map(c=>shopRow(c,'showItemPanel')).join('');
    h+='</div>';
  }
  box.innerHTML=h+'</div>';
  annotateInventory(box);
}

// UNLOCKED is the default now ("bei start/öffnen nicht locked als standard, damit man verschieben
// kann") — the plugin sends "locked off" at startup too, this just mirrors that initial state so
// the button icon/tooltip match on load. SVG lock, no emoji (no emoji font).
let overlayLocked=false;
const LOCK_CLOSED='<svg viewBox="0 0 24 24"><path d="M12 2a5 5 0 0 0-5 5v3H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7a5 5 0 0 0-5-5zm-3 5a3 3 0 0 1 6 0v3H9V7z"/></svg>';
const LOCK_OPEN='<svg viewBox="0 0 24 24"><path d="M12 2a5 5 0 0 0-5 5h2a3 3 0 0 1 6 0v3H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7a5 5 0 0 0-5-5z"/></svg>';
function toggleLock(){
  overlayLocked=!overlayLocked;
  $('#btn-lock').innerHTML=overlayLocked?LOCK_CLOSED:LOCK_OPEN;
  $('#btn-lock').title=overlayLocked?t('unlock'):t('lock');
  const hint=$('#p3dhint');
  if(hint)hint.style.display=(!overlayLocked&&$('#preview3d').style.display!=='none')?'flex':'none';
  post('/api/action/overlay/lock?locked='+overlayLocked);
}
applyI18n();
showTab('character'); // Character is the landing tab: starts snapshot polling + preview stream
updateOverlayCompactness(); // initial load starts on the Suche tab, empty
</script>
</body>
</html>
""";
}
