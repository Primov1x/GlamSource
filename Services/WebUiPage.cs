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
.row:hover{border-color:var(--accent);background:var(--panel2)}
.row img{width:28px;height:28px;border-radius:4px}
.row img.rowpreview{width:40px;height:40px;border-radius:6px;margin-left:auto;object-fit:cover}
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
.ok{color:var(--success)}.short{color:var(--muted)}
button.act{background:var(--panel2);border:1px solid var(--border);color:var(--text);padding:4px 12px;border-radius:6px;cursor:pointer;font-size:12px}
button.act:hover{border-color:var(--accent);color:var(--accent)}
.header{display:flex;align-items:center;gap:14px;margin:14px 0}
.header img{width:48px;height:48px;border-radius:8px}
.header .name{font-size:18px;font-weight:600}
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
#chardetail{max-height:660px;min-height:200px;overflow-y:auto;background:var(--panel);border:1px solid var(--border);border-radius:10px;padding:10px}
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
  <button id="btn-min" title="Minimieren — nur die Titelleiste bleibt sichtbar" onclick="toggleMinimize()">–</button>
  <button id="btn-lock" title="Overlay-Position sperren — nötig, um das Modell per Ziehen zu drehen (entsperrt verschiebt Ziehen das ganze Fenster)" onclick="toggleLock()"><svg viewBox="0 0 24 24"><path d="M12 2a5 5 0 0 0-5 5v3H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7a5 5 0 0 0-5-5zm-3 5a3 3 0 0 1 6 0v3H9V7z"/></svg></button>
  <button title="Ausblenden (über das GlamSource-Fenster wieder öffnen)" onclick="post('/api/action/overlay/hide')">×</button>
</div>
<div id="app">
<nav>
  <button id="tab-lookup" class="active" onclick="showTab('lookup')">Item Search</button>
  <button id="tab-character" onclick="showTab('character')">Charakter</button>
  <button id="tab-settings" onclick="showTab('settings')">Settings</button>
</nav>

<section id="view-lookup">
  <input type="search" id="q" placeholder="Search any item… or paste an item ID" autofocus>
  <div class="results" id="results"></div>
  <div id="detail"></div>
</section>

<section id="view-character" style="display:none">
  <!-- three columns like the in-game character sheet: gear slots | live model | item lookup panel.
       Clicking a slot opens the lookup RIGHT THERE (no tab switch, panel scrolls internally, the
       page itself never scrolls). -->
  <div id="charlayout">
    <div id="charslots"><div class="empty"><span class="spinner"></span></div></div>
    <div>
      <canvas id="preview3d"></canvas>
      <div class="empty" id="p3dhint" style="display:none;font-size:12px">Overlay ist entsperrt — Schloss oben rechts sperren, dann per Ziehen drehen.</div>
    </div>
    <div id="chardetail"><div class="empty">Slot anklicken für Herkunft &amp; Quellen</div></div>
  </div>
  <div id="recentsFooter">
    <div class="label">Zuletzt angesehen</div>
    <div id="charrecents"><div class="empty"><span class="spinner"></span></div></div>
  </div>
  <div class="row p3dtoolbar" style="margin-top:6px;margin-bottom:10px;flex-wrap:wrap;gap:8px;align-items:center">
    <span class="tbl">Ansicht</span>
    <button id="p3dspin" onclick="toggleAutoSpin()" title="Dreht das Model automatisch — Ziehen oder Zurücksetzen stoppt">Drehen</button>
    <button onclick="resetPreview3D()" title="Kompletter Neuaufbau — falls das Bild feststeckt oder was Falsches zeigt">Zurücksetzen</button>
    <span class="tbl">Pose</span>
    <button id="p3dweapon" onclick="toggleWeapon()" title="Waffe ziehen (Standard: aus) — für geglamte Waffen; setzt ein aktives Emote zurück">Waffe</button>
    <button id="p3dweapononly" onclick="toggleWeaponOnly()" title="Waffen-Studio: Waffe gezogen, alle andere Ausrüstung ausgeblendet">Nur Waffe</button>
    <select id="p3demote" onchange="setEmote(this.value)" title="Statische Emote-Pose (rein clientseitig — auch nicht freigeschaltete funktionieren); steckt die Waffe weg"><option value="0">Emote: Idle</option></select>
    <a href="#" onclick="loadPreview3DDebug();return false" style="font-size:12px" title="fps, Fehler, Frame-Größe">Debug</a>
  </div>
  <pre id="p3ddebug" style="display:none;font-size:11px;background:var(--panel);border:1px solid var(--border);border-radius:6px;padding:8px;margin-top:6px;white-space:pre-wrap"></pre>
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
// icons come from the plugin's own /api/icon (game data via Lumina) — xivapi's CDN is frozen
// and 404s on anything newer than its snapshot
const icon=id=>id?`/api/icon/${id}`:'';
const esc=t=>(t??'').toString().replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const img=(id,size)=>`<img src="${icon(id)}" width="${size}" height="${size}" loading="lazy" onerror="this.style.visibility='hidden'">`;
function typeIcon(cls){return ''} // emoji render as empty boxes in Browsingway — the text badge is enough

let currentTab='lookup';
function showTab(t){
  currentTab=t;
  for(const x of['lookup','character','settings']){
    $('#view-'+x).style.display=x===t?'':'none';
    $('#tab-'+x).classList.toggle('active',x===t);
  }
  if(t==='character'){loadSnapshot(true);loadRecents();startPreview3D()}else{stopPreview3D()}
  if(t==='settings')loadSettings();
  updateOverlayCompactness();
}

async function loadSettings(){
  const s=await fetch('/api/settings').then(r=>r.json());
  $('#settingsBody').innerHTML=`
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%"><input type="checkbox" id="set-craft" ${s.showCraftingSavings?'checked':''} onchange="saveSetting('showcraftingsavings',this.checked)"> Show Crafting Savings<span style="margin-left:auto;color:var(--muted);font-size:12px">Compare market price vs. crafting cost</span></label></div>
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%"><input type="checkbox" id="set-debug" ${s.debugApiEnabled?'checked':''} onchange="saveSetting('debugapi',this.checked)"> Debug API<span style="margin-left:auto;color:var(--muted);font-size:12px">Read-only HTTP API on localhost:23423</span></label></div>
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%"><input type="checkbox" id="set-overlay" ${s.webUiAutoOverlay?'checked':''} onchange="saveSetting('autooverlay',this.checked)"> Auto-Overlay<span style="margin-left:auto;color:var(--muted);font-size:12px">Browsingway overlay shows/hides with this window</span></label></div>
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%"><input type="checkbox" id="set-cmweb" ${s.contextMenuOpensInWebUi?'checked':''} onchange="saveSetting('contextmenuweb',this.checked)"> Item-Quelle im Web-UI<span style="margin-left:auto;color:var(--muted);font-size:12px">Examine-Rechtsklick "Item Source" öffnet hier statt im ImGui-Fenster</span></label></div>
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%"><input type="checkbox" id="set-3d" ${s.webUiLive3DPreview?'checked':''} onchange="saveSetting('live3dpreview',this.checked)"> 3D Preview (experimental)<span style="margin-left:auto;color:var(--muted);font-size:12px">Riskier GPU readback path — disable if you see crashes</span></label></div>
    <div class="row" style="cursor:default"><label style="display:flex;align-items:center;gap:8px;width:100%">Mount-up distance<input type="range" min="0" max="100" value="${s.mountUpDistance}" style="flex:1" oninput="$('#set-mountdist-val').textContent=this.value+'m'" onchange="saveSetting('mountupdistance',this.value)"><span id="set-mountdist-val" style="color:var(--muted);font-size:12px;min-width:36px">${Math.round(s.mountUpDistance)}m</span></label></div>
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
  btn.textContent='⏹️ Drehen stoppen';
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
  btn.textContent='🎠 Auto-Drehen';
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
  pre.textContent='Loading…';
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

let deb;
$('#q').addEventListener('input',e=>{
  clearTimeout(deb);
  deb=setTimeout(async()=>{
    const q=e.target.value.trim();
    const box=$('#results');
    const isId=/^\d+$/.test(q); // pure-digit query = item ID lookup, skip the 3-char name minimum
    if(!isId&&q.length<3){box.innerHTML='';return}
    if(q.length<1){box.innerHTML='';return}
    box.innerHTML='<div class="empty"><span class="spinner"></span>Searching…</div>';
    const r=await fetch('/api/search?q='+encodeURIComponent(q)).then(r=>r.json());
    box.innerHTML=r.length?r.map(x=>`<div class="row" onclick="openItem(${x.id})">${img(x.iconId,28)}<span>${esc(x.name)}</span><img class="rowpreview" src="/api/itemimage/${x.id}" loading="lazy" onerror="this.remove()"></div>`).join(''):'<div class="empty">Keine Items gefunden.</div>';
    updateOverlayCompactness();
  },250);
});

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
  let h=`<div class="header">${img(d.iconId,48)}<div><div class="name">${esc(d.name)}</div><div class="meta">Item ID ${d.itemId} · iLvl ${d.itemLevel}${d.isMarketable?' · marketable':''}${d.setName?` · Set: ${esc(d.setName)}`:''}</div></div></div><div class="preview"><img src="/api/itemimage/${d.itemId}" loading="lazy" onerror="this.parentElement.style.display='none'"></div>`;
  if((d.setMembers??[]).length){
    h+=`<div class="tbl">Rest of the set</div><div style="display:flex;flex-wrap:wrap;gap:8px;margin:6px 0 14px">`;
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
  for(const group of groups.values())h+=renderSource(group,d.itemId);
  h+='</div>';
  if(!(d.sources??[]).length)h+='<div class="empty">Keine bekannte Quelle gefunden.</div>';
  return h;
}

async function openItem(id){
  $('#results').innerHTML='';$('#q').value='';
  $('#detail').innerHTML='<div class="empty"><span class="spinner"></span>Loading…</div>';
  updateOverlayCompactness();
  const d=await fetch('/api/item/'+id).then(r=>r.ok?r.json():null);
  $('#detail').innerHTML=d?buildItemHtml(d):'<div class="empty">Nicht gefunden.</div>';
}

// group: one or more sources sharing the same description+cost (see buildItemHtml) — one card,
// one NPC/location table row per entry instead of a fully repeated card per location.
function renderSource(group,itemId){
  const s=group[0];
  const t=(s.type??'').toString();
  const cls=/craft/i.test(t)?'crafted':/vendor|shop/i.test(t)?'vendor':/quest/i.test(t)?'quest':/trial|raid|dungeon/i.test(t)?'duty':'';
  let h=`<div class="card ${cls}"><h3><span class="badge">${typeIcon(cls)} ${esc(t).toUpperCase()}</span> ${esc(s.description??'')}</h3>`;
  if(/craft/i.test(t))h+=`<button class="act" onclick="post('/api/action/craftlog/${itemId}')">Open Crafting Log</button>`;
  if(s.cfcRowId)h+=` <button class="act" onclick="post('/api/action/dutyfinder/${s.cfcRowId}')">Duty Finder</button>`;
  const withNpc=group.filter(g=>g.npcName);
  if(withNpc.length){
    h+='<table><tr><th>NPC</th><th>Location</th><th></th></tr>';
    for(const g of withNpc)h+=npcRow(g);
    h+='</table>';
  }
  for(const key of['materials','costs']){
    const list=s[key];
    if(list&&list.length){
      h+=`<div style="margin-top:8px;color:var(--muted);font-size:12px">${key==='materials'?'Materials':'Cost'}</div>`;
      for(const m of list){
        h+=`<div class="matrow">${img(m.iconId,22)}<span>${esc(m.name)||(m.itemId===0?'Gil':'#'+m.itemId)} × ${m.count.toLocaleString()}</span></div>`;
      }
    }
  }
  return h+'</div>';
}

function npcRow(s){
  const loc=[s.zoneName,s.mapX!=null?`(${s.mapX.toFixed(1)}, ${s.mapY.toFixed(1)})`:null].filter(Boolean).join(' ');
  const map=s.mapX!=null&&s.territoryTypeId?`<button class="act" onclick="post('/api/action/map?territory=${s.territoryTypeId}&map=${s.mapId}&x=${s.mapX}&y=${s.mapY}')">Map</button>`:'';
  return`<tr><td>${esc(s.npcName)}</td><td class="muted">${esc(loc)}</td><td>${map}</td></tr>`;
}

// slot enum names -> in-game style German labels
const SLOT_DE={MainHand:'Hauptwaffe',OffHand:'Nebenwaffe',Head:'Kopf',Body:'Rumpf',Hands:'Hände',
  Legs:'Beine',Feet:'Füße',Ears:'Ohrringe',Necklace:'Halskette',Neck:'Halskette',
  Bracelets:'Armreif',Wrists:'Armreif',RingRight:'Ring (rechts)',RingLeft:'Ring (links)'};

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
    const lbl=SLOT_DE[s.slot]??s.slot;
    // opens the lookup in the RIGHT-SIDE panel — no tab switch, no page scroll
    return`<div class="slot" onclick="showItemPanel(${id})">${img(s.iconId,40)}<div><div class="lbl">${esc(lbl)}</div><div class="nm">${esc(name)}</div></div>${s.isGlamoured?'<span class="glam">Glam</span>':''}</div>`;
  }).join('');
  const head=d.activeRecentName?`<div class="empty">Ansicht: ${esc(d.activeRecentName)}</div>`
    :(slots?'':'<div class="empty">Keine Daten — Charakter noch nicht erfasst.</div>');
  $('#charslots').innerHTML=head+slots;
}

// same list/click as the native ImGui sidebar (GlamSourceShellWindow.DrawRecentSidebar) — view a
// stored outfit snapshot of a previously-inspected player, index-addressed.
async function loadRecents(){
  const recents=await fetch('/api/recents').then(r=>r.json());
  const box=$('#charrecents');
  if(!recents.length){box.innerHTML='<div class="empty">(noch keine)</div>';return}
  box.innerHTML=recents.map(r=>`<div class="recent${r.active?' active':''}" onclick="activateRecent(${r.index})">${esc(r.name)}${r.world?`<span class="world">${esc(r.world)}</span>`:''}<span class="del" title="Remove from Recent" onclick="event.stopPropagation();removeRecent(${r.index})">×</span></div>`).join('');
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
  box.innerHTML='<div class="empty"><span class="spinner"></span>Loading…</div>';
  const d=await fetch('/api/item/'+id).then(r=>r.ok?r.json():null);
  box.innerHTML=d?buildItemHtml(d,'showItemPanel'):'<div class="empty">Nicht gefunden.</div>';
}

async function post(url){await fetch(url,{method:'POST'})}

// locked is the DEFAULT now (the plugin sets it at startup: position+size frozen, and drag-rotate
// needs it anyway) — the button just toggles for repositioning. SVG lock, no emoji (no emoji font).
let overlayLocked=true;
const LOCK_CLOSED='<svg viewBox="0 0 24 24"><path d="M12 2a5 5 0 0 0-5 5v3H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7a5 5 0 0 0-5-5zm-3 5a3 3 0 0 1 6 0v3H9V7z"/></svg>';
const LOCK_OPEN='<svg viewBox="0 0 24 24"><path d="M12 2a5 5 0 0 0-5 5h2a3 3 0 0 1 6 0v3H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-8a2 2 0 0 0-2-2h-1V7a5 5 0 0 0-5-5z"/></svg>';
function toggleLock(){
  overlayLocked=!overlayLocked;
  $('#btn-lock').innerHTML=overlayLocked?LOCK_CLOSED:LOCK_OPEN;
  $('#btn-lock').title=overlayLocked
    ?'Entsperren — Overlay verschieben/skalieren'
    :'Sperren — Position & Größe fixieren (nötig, um das Modell per Ziehen zu drehen)';
  const hint=$('#p3dhint');
  if(hint)hint.style.display=(!overlayLocked&&$('#preview3d').style.display!=='none')?'flex':'none';
  post('/api/action/overlay/lock?locked='+overlayLocked);
}
updateOverlayCompactness(); // initial load starts on the Suche tab, empty
</script>
</body>
</html>
""";
}
