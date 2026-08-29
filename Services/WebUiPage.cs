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
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect width='32' height='32' rx='7' fill='%230d0f14'/%3E%3Ctext x='16' y='23' font-size='20' text-anchor='middle' fill='%23d4af5a'%3E✦%3C/text%3E%3C/svg%3E">
<style>
:root{
  --bg:#0d0f14; --panel:#161a22; --panel2:#1d222d; --border:#2a3040;
  --text:#d8dce6; --muted:#8a91a3; --accent:#d4af5a; --success:#6fbf73; --warn:#e0a03c;
}
*{box-sizing:border-box;margin:0;padding:0}
body{background:transparent;color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif;margin:0}
#titlebar{display:flex;align-items:center;gap:10px;background:var(--panel2);border-bottom:1px solid var(--border);padding:6px 12px;user-select:none}
#titlebar .brand{color:var(--accent);font-weight:600;font-size:14px;letter-spacing:.5px}
#titlebar .sub{color:var(--muted);font-size:11px}
#titlebar .spacer{flex:1}
#titlebar button{background:none;border:1px solid var(--border);color:var(--muted);width:22px;height:22px;border-radius:5px;cursor:pointer;font-size:13px;line-height:1}
#titlebar button:hover{border-color:var(--accent);color:var(--accent)}
#app{background:var(--bg);padding:18px 24px;max-width:1100px;min-height:calc(100vh - 36px)}
nav{display:flex;gap:8px;margin-bottom:18px}
nav button{background:var(--panel);border:1px solid var(--border);color:var(--text);padding:8px 18px;border-radius:8px;cursor:pointer;font-size:14px;transition:.15s}
nav button:hover{border-color:var(--accent)}
nav button.active{background:var(--accent);color:#14161c;font-weight:600}
input[type=search]{width:100%;max-width:420px;background:var(--panel);border:1px solid var(--border);color:var(--text);padding:10px 14px;border-radius:8px;font-size:15px;outline:none}
input[type=search]:focus{border-color:var(--accent)}
.results{margin-top:10px;display:flex;flex-direction:column;gap:4px;max-width:420px}
.row{display:flex;align-items:center;gap:10px;background:var(--panel);border:1px solid transparent;border-radius:8px;padding:6px 10px;cursor:pointer;transition:.12s}
.row:hover{border-color:var(--accent);background:var(--panel2)}
.row img{width:28px;height:28px;border-radius:4px}
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
.empty{color:var(--muted);margin-top:14px;display:flex;align-items:center;gap:8px}
.spinner{width:16px;height:16px;border:2px solid var(--border);border-top-color:var(--accent);border-radius:50%;animation:spin .7s linear infinite;display:inline-block}
@keyframes spin{to{transform:rotate(360deg)}}
.row img,.matrow img,.slot img,.header img{background:var(--panel2);object-fit:contain}
.snapgrid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:10px;margin-top:14px}
.slot{display:flex;align-items:center;gap:10px;background:var(--panel);border:1px solid var(--border);border-radius:8px;padding:8px 10px;cursor:pointer;transition:.12s}
.slot:hover{border-color:var(--accent)}
.slot img{width:32px;height:32px;border-radius:5px}
.slot .g{color:var(--success);font-size:12px}
.slot .s{color:var(--muted);font-size:11px}
</style>
</head>
<body>
<div id="titlebar">
  <span class="brand">GlamSource</span>
  <span class="sub">web ui</span>
  <span class="spacer"></span>
  <button id="btn-min" title="Minimize" onclick="toggleMin()">–</button>
  <button title="Close" onclick="post('/api/action/overlay/hide')">×</button>
</div>
<div id="app">
<nav>
  <button id="tab-lookup" class="active" onclick="showTab('lookup')">Lookup</button>
  <button id="tab-character" onclick="showTab('character')">Character</button>
</nav>

<section id="view-lookup">
  <input type="search" id="q" placeholder="Search any item…" autofocus>
  <div class="results" id="results"></div>
  <div id="detail"></div>
</section>

<section id="view-character" style="display:none">
  <div class="empty" id="snapinfo">Loading…</div>
  <div class="snapgrid" id="snap"></div>
</section>

</div>
<script>
const $=s=>document.querySelector(s);
function toggleMin(){
  const app=$('#app');
  const min=app.style.display==='none';
  app.style.display=min?'':'none';
  $('#btn-min').textContent=min?'–':'+';
}
const icon=id=>{if(!id)return'';const f=String(Math.floor(id/1000)*1000).padStart(6,'0');const n=String(id).padStart(6,'0');return`https://xivapi.com/i/${f}/${n}.png`};
const esc=t=>(t??'').toString().replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const img=(id,size)=>`<img src="${icon(id)}" width="${size}" height="${size}" loading="lazy" onerror="this.style.visibility='hidden'">`;
const TYPE_ICON={craft:'🔨',vendor:'🛒',quest:'❗',duty:'⚔️'};
function typeIcon(cls){return TYPE_ICON[cls]??'✦'}

function showTab(t){
  for(const x of['lookup','character']){
    $('#view-'+x).style.display=x===t?'':'none';
    $('#tab-'+x).classList.toggle('active',x===t);
  }
  if(t==='character')loadSnapshot();
}

let deb;
$('#q').addEventListener('input',e=>{
  clearTimeout(deb);
  deb=setTimeout(async()=>{
    const q=e.target.value.trim();
    const box=$('#results');
    if(q.length<3){box.innerHTML='';return}
    box.innerHTML='<div class="empty"><span class="spinner"></span>Searching…</div>';
    const r=await fetch('/api/search?q='+encodeURIComponent(q)).then(r=>r.json());
    box.innerHTML=r.length?r.map(x=>`<div class="row" onclick="openItem(${x.id})">${img(x.iconId,28)}<span>${esc(x.name)}</span></div>`).join(''):'<div class="empty">🔍 No items found.</div>';
  },250);
});

async function openItem(id){
  $('#results').innerHTML='';$('#q').value='';
  $('#detail').innerHTML='<div class="empty"><span class="spinner"></span>Loading…</div>';
  const d=await fetch('/api/item/'+id).then(r=>r.ok?r.json():null);
  if(!d){$('#detail').innerHTML='<div class="empty">⚠️ Not found.</div>';return}
  let h=`<div class="header">${img(d.iconId,48)}<div><div class="name">${esc(d.name)}</div><div class="meta">Item ID ${d.itemId} · iLvl ${d.itemLevel}${d.isMarketable?' · marketable':''}</div></div></div><div class="cards">`;
  for(const s of d.sources??[]){
    h+=renderSource(s,d.itemId);
  }
  h+='</div>';
  if(!(d.sources??[]).length)h+='<div class="empty">🤷 No known source found.</div>';
  $('#detail').innerHTML=h;
}

function renderSource(s,itemId){
  const t=(s.type??'').toString();
  const cls=/craft/i.test(t)?'crafted':/vendor|shop/i.test(t)?'vendor':/quest/i.test(t)?'quest':/trial|raid|dungeon/i.test(t)?'duty':'';
  let h=`<div class="card ${cls}"><h3><span class="badge">${typeIcon(cls)} ${esc(t).toUpperCase()}</span> ${esc(s.description??'')}</h3>`;
  if(/craft/i.test(t))h+=`<button class="act" onclick="post('/api/action/craftlog/${itemId}')">Open Crafting Log</button>`;
  if(s.cfcRowId)h+=` <button class="act" onclick="post('/api/action/dutyfinder/${s.cfcRowId}')">Duty Finder</button>`;
  if(s.npcName){
    h+='<table><tr><th>NPC</th><th>Location</th><th></th></tr>';
    h+=npcRow(s);
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

async function loadSnapshot(){
  $('#snapinfo').innerHTML='<span class="spinner"></span>Loading…';
  const d=await fetch('/api/snapshot').then(r=>r.json());
  $('#snapinfo').textContent=d.activeRecentName?`Viewing: ${d.activeRecentName}`:(d.slots?.length?'Live snapshot':'No snapshot — open the Character tab in-game first.');
  $('#snap').innerHTML=(d.slots??[]).map(s=>{
    const id=s.glamourItemId??s.actualItemId;
    if(!id)return'';
    const name=s.glamourItemName??s.actualItemName??'';
    return`<div class="slot" onclick="showTab('lookup');openItem(${id})">${img(s.iconId,32)}<div><div>${esc(name)}</div><div class="${s.isGlamoured?'g':'s'}">${esc(s.slot)}${s.isGlamoured?' · glamoured':''}</div></div></div>`;
  }).join('');
}

async function post(url){await fetch(url,{method:'POST'})}
</script>
</body>
</html>
""";
}
