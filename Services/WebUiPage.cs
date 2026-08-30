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
<script type="importmap">{"imports":{"three":"https://unpkg.com/three@0.160.0/build/three.module.js","three/addons/":"https://unpkg.com/three@0.160.0/examples/jsm/"}}</script>
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
#preview3d{background:var(--panel);border:1px solid var(--border);border-radius:8px;margin-bottom:14px;cursor:grab;display:none;width:256px;height:256px;object-fit:contain}
#preview3d.active{cursor:grabbing}
</style>
</head>
<body>
<div id="titlebar">
  <span class="brand">GlamSource</span>
  <span class="sub">web ui</span>
  <span class="spacer"></span>
  <button id="btn-lock" title="Lock overlay position — needed to drag-rotate the 3D preview (Browsingway has no title bar; unlocked, any drag moves the whole window instead)" onclick="toggleLock()">🔓</button>
  <button title="Hide (reopen from the GlamSource window)" onclick="post('/api/action/overlay/hide')">×</button>
</div>
<div id="app">
<nav>
  <button id="tab-lookup" class="active" onclick="showTab('lookup')">Lookup</button>
  <button id="tab-character" onclick="showTab('character')">Character</button>
  <button id="tab-viewer" onclick="showTab('viewer')">3D Viewer</button>
</nav>

<section id="view-lookup">
  <input type="search" id="q" placeholder="Search any item…" autofocus>
  <div class="results" id="results"></div>
  <div id="detail"></div>
</section>

<section id="view-character" style="display:none">
  <img id="preview3d" alt="">
  <div class="empty" id="p3dhint" style="display:none;font-size:12px">Click 🔓 above to lock the overlay, then drag the model to rotate.</div>
  <div class="empty" id="snapinfo">Loading…</div>
  <div class="snapgrid" id="snap"></div>
</section>

<section id="view-viewer" style="display:none">
  <div class="empty" id="viewerinfo"><span class="spinner"></span>Loading model…</div>
  <div id="viewer3d" style="width:100%;height:70vh;border:1px solid var(--border);border-radius:8px;overflow:hidden;display:none"></div>
  <div id="pose-toggle" style="display:none;margin-top:6px;gap:4px" class="row">
    <button id="pose-idle" onclick="setViewerPose('idle')" title="The model's own bind pose — no live game data read at all, so nothing here can glitch from a bad capture">🧍 Idle</button>
    <button id="pose-weapon" onclick="setViewerPose('weapon')" title="First time the character was seen with weapon drawn — captured once, frozen">⚔️ Waffe</button>
    <button id="pose-live" onclick="setViewerPose('live')" title="Whatever the character is doing right this second">🔴 Live</button>
    <button onclick="resetPose()" title="Bad capture? Clear the frozen Idle/Weapon snapshots so they get re-captured next time you're in that state">♻️ Neu erfassen</button>
    <button id="btn-eyedrop" onclick="toggleEyedrop()" title="Click a spot on the model to read its exact rendered pixel color (hex) — for reporting a specific color that looks wrong">🎨 Farbe messen</button>
    <span id="eyedrop-result" style="margin-left:6px"></span>
  </div>
  <div style="margin-top:4px"><a href="/api/model3d/textures" target="_blank" style="font-size:12px">🖼️ Rohe Texturen ansehen (unbeleuchtet)</a></div>
</section>

</div>
<script>
const $=s=>document.querySelector(s);
const icon=id=>{if(!id)return'';const f=String(Math.floor(id/1000)*1000).padStart(6,'0');const n=String(id).padStart(6,'0');return`https://xivapi.com/i/${f}/${n}.png`};
const esc=t=>(t??'').toString().replace(/[&<>"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
const img=(id,size)=>`<img src="${icon(id)}" width="${size}" height="${size}" loading="lazy" onerror="this.style.visibility='hidden'">`;
const TYPE_ICON={craft:'🔨',vendor:'🛒',quest:'❗',duty:'⚔️'};
function typeIcon(cls){return TYPE_ICON[cls]??'✦'}

function showTab(t){
  for(const x of['lookup','character','viewer']){
    $('#view-'+x).style.display=x===t?'':'none';
    $('#tab-'+x).classList.toggle('active',x===t);
  }
  if(t==='character'){loadSnapshot();startPreview3D()}else{stopPreview3D()}
  if(t==='viewer')startViewer();
}

// --- 3D preview (opt-in, see Settings > 3D Preview) ---
// Native MJPEG stream (multipart/x-mixed-replace) — the <img> element decodes and repaints itself,
// no poll loop or raw-pixel JS decode needed anymore (server side: WebUiService.StreamPreviewMjpeg).
let p3dDragging=false, p3dLastX=0, p3dLastY=0;
function startPreview3D(){
  const img=$('#preview3d');
  img.onerror=()=>{ img.style.display='none'; $('#p3dhint').style.display='none'; };
  img.style.display='block';
  $('#p3dhint').style.display=overlayLocked?'none':'flex';
  img.src='/api/preview3d/stream?_='+Date.now(); // cache-bust: force a fresh connection every tab entry
}
function stopPreview3D(){
  const img=$('#preview3d');
  img.onerror=null;
  img.removeAttribute('src'); // aborts the in-flight stream, closing the server-side connection
  img.style.display='none';
}

(function initPreview3DDrag(){
  const img=$('#preview3d');
  img.addEventListener('mousedown',e=>{p3dDragging=true;p3dLastX=e.clientX;p3dLastY=e.clientY;img.classList.add('active')});
  window.addEventListener('mouseup',()=>{p3dDragging=false;img.classList.remove('active')});
  window.addEventListener('mousemove',e=>{
    if(!p3dDragging)return;
    const dx=e.clientX-p3dLastX, dy=e.clientY-p3dLastY;
    p3dLastX=e.clientX;p3dLastY=e.clientY;
    post(`/api/action/preview3d/rotate?dx=${(dx*0.75).toFixed(2)}&dy=${(dy*0.75).toFixed(2)}`);
  });
  img.addEventListener('wheel',e=>{
    e.preventDefault();
    post(`/api/action/preview3d/zoom?delta=${(-e.deltaY*0.002).toFixed(3)}`);
  },{passive:false});
})();

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

// --- glTF mesh viewer (live skeleton pose baked server-side; equipment from current snapshot) ---
// three.js from CDN; the whole scene is client-side WebGL — smooth camera, zero game load.
let viewerStarted=false;
async function startViewer(){
  if(viewerStarted){reloadViewerModel();return}
  viewerStarted=true;
  const info=$('#viewerinfo');
  try{
    const THREE=await import('three');
    const {GLTFLoader}=await import('three/addons/loaders/GLTFLoader.js');
    const {OrbitControls}=await import('three/addons/controls/OrbitControls.js');
    const {RoomEnvironment}=await import('three/addons/environments/RoomEnvironment.js');
    const box=$('#viewer3d');
    box.style.display='block';
    // preserveDrawingBuffer: the eyedropper reads pixels back from this canvas after each frame
    const renderer=new THREE.WebGLRenderer({antialias:true,alpha:true,preserveDrawingBuffer:true});
    renderer.setSize(box.clientWidth,box.clientHeight);
    // ponytail: lights at 2.0+2.0 with no tone mapping blew every mid-tone toward white/saturated
    // — a near-black dyed material (baseColorFactor ~0.12) still rendered as bright vivid color.
    // ACES + realistic light levels is the standard three.js fix for "everything looks washed out".
    renderer.toneMapping=THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure=1.55;
    renderer.outputColorSpace=THREE.SRGBColorSpace;
    box.appendChild(renderer.domElement);
    const scene=new THREE.Scene();
    scene.background=new THREE.Color(0x555555);
    // ponytail: "fehlendes Gold"/"Glas-Effekt fehlt" — metal trim now bakes real metallicFactor=1
    // (see GltfBuilder's metal/roughness texture), but PBR metal only shows an environment
    // REFLECTION, no diffuse color of its own — with zero environment data it renders flat black
    // (or blown-out white at a grazing highlight angle) regardless of the baked gold hue
    // underneath. A simple procedural room environment gives metal/glass surfaces something to
    // reflect, the standard three.js fix for exactly this ("PBR metal looks black").
    const pmrem=new THREE.PMREMGenerator(renderer);
    scene.environment=pmrem.fromScene(new RoomEnvironment(),0.04).texture;
    const camera=new THREE.PerspectiveCamera(45,box.clientWidth/box.clientHeight,0.01,100);
    camera.position.set(0,1.2,2.2);
    const controls=new OrbitControls(camera,renderer.domElement);
    controls.target.set(0,1.0,0);
    // ponytail: original single-key setup left the face/chin/under-brow in heavy shadow (screenshot
    // comparison against the real in-game render: ours read dark/muddy, "fitting room" flat) — with
    // correct diffuse colors now shipped, the remaining gap was pure lighting: too little fill,
    // exposure too low. Softer key, brighter ambient, an added front fill (real portrait 3-point
    // setup), and a touch more exposure.
    // ponytail: "sharp/kein Ingame-Look" — three hard DirectionalLights + a flat AmbientLight give
    // crisp, unshaded-feeling specular hotspots real in-game portrait lighting doesn't have. A
    // HemisphereLight (sky/ground gradient) replaces the flat ambient for a softer base, key/fill
    // get a slight warm/cool split (matches the game's warm key + cool bounce), and key intensity
    // drops since the hemisphere now carries part of the fill duty.
    // ponytail: the previous pass overcorrected — HemisphereLight's dark ground color (0x3a3530)
    // plus a weaker key/fill crushed the face into near-black at this close camera angle ("Horror
    // game aus den 80ern" vs the real screenshot). Softness came from ditching hard multi-light
    // specular, not from starving overall brightness — keep the hemisphere for the gradient feel,
    // but lift its ground floor and put key/fill intensity back near the original working levels.
    scene.add(new THREE.HemisphereLight(0xfff4e6,0x6b6058,0.9));
    const key=new THREE.DirectionalLight(0xfff1e0,1.3);
    key.position.set(2,3,2.5);
    scene.add(key);
    const fill=new THREE.DirectionalLight(0xe6eeff,0.75);
    fill.position.set(-1.5,1.2,2.5);
    scene.add(fill);
    const rim=new THREE.DirectionalLight(0xffffff,0.4);
    rim.position.set(-2,1.5,-2);
    scene.add(rim);
    window._glamViewer={THREE,GLTFLoader,scene,renderer,camera,controls,box,model:null};
    $('#pose-toggle').style.display='flex';
    $('#pose-idle').style.fontWeight='bold';
    await reloadViewerModel();
    (function animate(){requestAnimationFrame(animate);controls.update();renderer.render(scene,camera)})();
    new ResizeObserver(()=>{renderer.setSize(box.clientWidth,box.clientHeight);camera.aspect=box.clientWidth/box.clientHeight;camera.updateProjectionMatrix()}).observe(box);
    renderer.domElement.addEventListener('click',onViewerClick);
  }catch(e){
    info.textContent='⚠️ Viewer failed to load: '+e.message;
    viewerStarted=false;
  }
}

async function resetPose(){await post('/api/action/pose/reset');reloadViewerModel()}

let eyedropActive=false;
function toggleEyedrop(){
  eyedropActive=!eyedropActive;
  $('#btn-eyedrop').style.fontWeight=eyedropActive?'bold':'normal';
  if(!eyedropActive)$('#eyedrop-result').textContent='';
}
function onViewerClick(ev){
  if(!eyedropActive)return;
  const v=window._glamViewer;
  const rect=v.renderer.domElement.getBoundingClientRect();
  const x=Math.round(ev.clientX-rect.left);
  const yTop=Math.round(ev.clientY-rect.top);
  const yGl=Math.round(rect.height-yTop); // WebGL reads bottom-up
  const gl=v.renderer.getContext();
  const px=new Uint8Array(4);
  gl.readPixels(x,yGl,1,1,gl.RGBA,gl.UNSIGNED_BYTE,px);
  const hex='#'+[px[0],px[1],px[2]].map(c=>c.toString(16).padStart(2,'0')).join('');
  $('#eyedrop-result').innerHTML=`<span style="display:inline-block;width:12px;height:12px;background:${hex};border:1px solid #fff;vertical-align:middle"></span> ${hex} rgb(${px[0]},${px[1]},${px[2]})`;
}

let viewerPose='idle';
function setViewerPose(p){
  viewerPose=p;
  ['idle','weapon','live'].forEach(x=>$('#pose-'+x).style.fontWeight=(x===p?'bold':'normal'));
  reloadViewerModel();
}

async function reloadViewerModel(){
  const v=window._glamViewer;
  if(!v)return;
  const info=$('#viewerinfo');
  info.innerHTML='<span class="spinner"></span>Loading model…';
  info.style.display='flex';
  try{
    const r=await fetch('/api/model3d.glb?pose='+viewerPose+'&t='+Date.now());
    if(!r.ok){info.textContent='🤷 No model — open the Character tab in-game first.';return}
    const buf=await r.arrayBuffer();
    const gltf=await new Promise((res,rej)=>new v.GLTFLoader().parse(buf,'',res,rej));
    if(v.model)v.scene.remove(v.model);
    v.model=gltf.scene;
    // ponytail: first step toward actually-FFXIV-like shading instead of flat glTF PBR — materials
    // tagged with a "role" (skin/hair/eye, see GltfBuilder.cs's extras field) get upgraded from the
    // loader's default MeshStandardMaterial to MeshPhysicalMaterial with the property that role
    // actually needs: sheen for skin's soft/waxy look, anisotropic specular for hair's strand
    // highlight streak, clearcoat for a wet-eye look. GLTFLoader copies glTF "extras" straight onto
    // material.userData (parser.assignExtrasToUserData) — no manual JSON parsing needed here.
    // materials are cache-shared across meshes by the loader (same glTF material index -> same
    // instance) — upgrade each distinct instance once via a lookup, or a shared material would get
    // replaced-and-disposed on the first mesh and leave later meshes pointing at a disposed one.
    const upgraded=new Map();
    v.model.traverse(o=>{
      const role=o.material?.userData?.role;
      if(!role||!(o.material instanceof v.THREE.MeshStandardMaterial))return;
      const old=o.material;
      if(upgraded.has(old)){o.material=upgraded.get(old);return}
      // ponytail: "cannot read properties of undefined (reading 'x')" — MeshPhysicalMaterial.copy()
      // unconditionally copies Vector2/Color sub-props (e.g. clearcoatNormalScale) that only exist
      // on ANOTHER MeshPhysicalMaterial; `old` here is a plain MeshStandardMaterial from the
      // loader, so those are undefined and .copy() dies reaching into them. Copy just the fields
      // that actually exist on a MeshStandardMaterial instead of a blind .copy().
      const phys=new v.THREE.MeshPhysicalMaterial({
        map:old.map,normalMap:old.normalMap,normalScale:old.normalScale,
        metalnessMap:old.metalnessMap,roughnessMap:old.roughnessMap,
        color:old.color,metalness:old.metalness,roughness:old.roughness,
        alphaTest:old.alphaTest,alphaMap:old.alphaMap,transparent:old.transparent,
        side:old.side,
      });
      if(role==='skin'){phys.sheen=0.35;phys.sheenColor=new v.THREE.Color(0xffe0cc);phys.sheenRoughness=0.6}
      else if(role==='hair'){if('anisotropy' in phys){phys.anisotropy=0.6;phys.anisotropyRotation=Math.PI/2}phys.clearcoat=0.15;phys.clearcoatRoughness=0.4}
      // ponytail: clearcoat=0.8/roughness=0.05 (near-mirror) blew the iris out to solid white under
      // the key light — "Gläser" complaint, eyes read as glass marbles instead of colored irises.
      // Much lower clearcoat keeps a wet-eye highlight without drowning the underlying color.
      else if(role==='eye'){phys.clearcoat=0.25;phys.clearcoatRoughness=0.2}
      upgraded.set(old,phys);
      o.material=phys;
    });
    v.scene.add(v.model);
    info.style.display='none';
  }catch(e){info.textContent='⚠️ '+e.message}
}

let overlayLocked=false;
function toggleLock(){
  overlayLocked=!overlayLocked;
  $('#btn-lock').textContent=overlayLocked?'🔒':'🔓';
  $('#btn-lock').title=overlayLocked
    ?'Unlock — drag the overlay by its title bar to move it'
    :'Lock overlay position — needed to drag-rotate the 3D preview (Browsingway has no title bar; unlocked, any drag moves the whole window instead)';
  const hint=$('#p3dhint');
  if(hint)hint.style.display=(!overlayLocked&&$('#preview3d').style.display!=='none')?'flex':'none';
  post('/api/action/overlay/lock?locked='+overlayLocked);
}
</script>
</body>
</html>
""";
}
