// Extrabbit.Redline - draw markup over the model, organised into LAYERS.
//
// A layer owns its 2D shapes (freehand, rectangle, circle, arrow, text on an SVG overlay), its 3D
// pen strokes (raycast onto the model surface, world-space lines that rotate with it) and a stored
// camera perspective - screen-space markup only lines up from the viewpoint it was drawn in. The
// layer browser (docked right) renames, deletes, shows/hides layers, restores a layer's stored
// perspective and re-saves it from the current view. Layers persist into the model's cache entry
// (redline-layers.json, via the host bridge) and the active layer can be exported as a PNG
// screenshot (3D view + markup composited).
//
// One stroke at a time: tool and pointerId are LATCHED at pointerdown, so a mid-stroke tool switch
// or a second finger can't corrupt the stroke state. Non-left buttons and the wheel are forwarded
// to the LMV canvas so the camera stays navigable while marking up.
"use strict";

const RL_NS = "http://www.w3.org/2000/svg";
const RL_COLORS = ["#e53935", "#fb8c00", "#fdd835", "#43a047", "#1e88e5", "#ffffff", "#000000"];

// line-art icons (stroke follows the button colour)
const RL_SVG = (inner) =>
  '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" ' +
  'stroke-linecap="round" stroke-linejoin="round">' + inner + "</svg>";
const RL_ICON_EYE = RL_SVG('<path d="M1 12s4-7 11-7 11 7 11 7-4 7-11 7S1 12 1 12z"/><circle cx="12" cy="12" r="3"/>');
const RL_ICON_EYE_OFF = RL_SVG('<path d="M17.94 17.94A10.4 10.4 0 0 1 12 19c-7 0-11-7-11-7a18.5 18.5 0 0 1 5.06-5.94"/>'
  + '<path d="M9.9 4.24A9.1 9.1 0 0 1 12 5c7 0 11 7 11 7a18.5 18.5 0 0 1-2.16 3.19"/><line x1="1" y1="1" x2="23" y2="23"/>');
const RL_ICON_NAV = RL_SVG('<polyline points="5 9 2 12 5 15"/><polyline points="9 5 12 2 15 5"/>'
  + '<polyline points="15 19 12 22 9 19"/><polyline points="19 9 22 12 19 15"/>'
  + '<line x1="2" y1="12" x2="22" y2="12"/><line x1="12" y1="2" x2="12" y2="22"/>');
const RL_ICON_ERASE = RL_SVG('<path d="m7 21-4.3-4.3c-1-1-1-2.5 0-3.4l9.6-9.6c1-1 2.5-1 3.4 0l5.6 5.6c1 1 1 2.5 0 3.4L13 21"/>'
  + '<path d="M22 21H7"/><path d="m5 11 9 9"/>');
const RL_ICON_PLUS = RL_SVG('<line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/>');
const RL_ICON_CAM_RESTORE = RL_SVG('<circle cx="12" cy="12" r="7"/><circle cx="12" cy="12" r="1.5"/>'
  + '<line x1="12" y1="2" x2="12" y2="5"/><line x1="12" y1="19" x2="12" y2="22"/>'
  + '<line x1="2" y1="12" x2="5" y2="12"/><line x1="19" y1="12" x2="22" y2="12"/>');
const RL_ICON_PIN = RL_SVG('<path d="M12 17v5"/>'
  + '<path d="M9 10.76a2 2 0 0 1-1.11 1.79l-1.78.9A2 2 0 0 0 5 15.24V16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-.76a2 2 0 0 0-1.11-1.79l-1.78-.9A2 2 0 0 1 15 10.76V6h1a2 2 0 0 0 0-4H8a2 2 0 0 0 0 4h1z"/>');
const RL_ICON_TRASH = RL_SVG('<path d="M3 6h18"/><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6"/>'
  + '<path d="M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><line x1="10" y1="11" x2="10" y2="17"/><line x1="14" y1="11" x2="14" y2="17"/>');
const RL_ICON_INFO = RL_SVG('<circle cx="12" cy="12" r="9"/><line x1="12" y1="11" x2="12" y2="16"/><line x1="12" y1="7.5" x2="12" y2="7.51"/>');

// Tools with their own palette button. "nav" is a tool id without a button: clicking any of
// Autodesk's own navigation tools switches to it, so a second move button would be redundant.
const RL_TOOLS = [
  ["free",    "✎",  "Freehand"],
  ["paint3d", "\u{1F58C}", "Paint on the model (experimental) - strokes stick to the surface"],
  ["text",    "T",       "Text"],
  ["erase",   RL_ICON_ERASE, "Eraser - click or drag over markup to remove it", true],
];
// current binding for a rebindable action, as a tooltip suffix like " (F)"
function rlHotkeySuffix(id){
  try { return " (" + window.Hotkeys.get(id).toUpperCase() + ")"; } catch (e) { return ""; }
}
// shape tools share one dropdown; its trigger shows the last-used shape
const RL_SHAPES = [
  ["rect",    "▭",  "Rectangle"],
  ["circle",  "◯",  "Circle"],
  ["arrow",   "↗",  "Arrow"],
];

// LMV tools whose activation means "the user wants to navigate now" - picking one from the viewer
// toolbar while redlining flips us into nav mode so their drags reach the canvas.
const RL_NAV_TOOLS = ["orbit", "freeorbit", "pan", "dolly", "zoom", "fov", "worldup", "bimwalk"];

function rlHex(rgb){                                   // "rgb(r, g, b)" -> "#rrggbb" for swatch compare
  const m = /rgb\((\d+),\s*(\d+),\s*(\d+)\)/.exec(rgb);
  return m ? "#" + [m[1], m[2], m[3]].map(n => (+n).toString(16).padStart(2, "0")).join("") : rgb;
}

// stroke widths: 2D stroke-width in px / 3D tube diameter in SCREEN pixels at draw time (converted
// to world units via the camera, then fixed in world space so the paint scales with the model)
const RL_WIDTHS = { 1: { px: 2, tube: 3 }, 2: { px: 3, tube: 6 }, 3: { px: 5, tube: 12 } };

// One pass of Chaikin corner-cutting: smooths the raw pointer samples into a rounder path
// without pulling far away from the raycast surface points.
function rlChaikin(points){
  if (points.length < 3){ return points; }
  const out = [points[0]];
  for (let i = 0; i < points.length - 1; i++){
    const a = points[i], b = points[i + 1];
    out.push(a.clone().lerp(b, 0.25), a.clone().lerp(b, 0.75));
  }
  out.push(points[points.length - 1]);
  return out;
}

// Build a tube mesh along a polyline: an unindexed triangle soup (maximum compatibility with
// LMV's bundled three.js) swept with a parallel-transported ring frame, so paint strokes have
// real 3D thickness and shade/occlude like geometry lying on the model.
function rlTube(points, radius, segments = 8){
  const rings = [];
  let normal = null;
  for (let i = 0; i < points.length; i++){
    const prev = points[Math.max(0, i - 1)], next = points[Math.min(points.length - 1, i + 1)];
    const tangent = next.clone().sub(prev);
    if (tangent.lengthSq() < 1e-12){ tangent.set(0, 0, 1); }
    tangent.normalize();
    if (!normal){
      const pick = Math.abs(tangent.x) < 0.9 ? new THREE.Vector3(1, 0, 0) : new THREE.Vector3(0, 1, 0);
      normal = pick.sub(tangent.clone().multiplyScalar(pick.dot(tangent))).normalize();
    } else {
      normal = normal.sub(tangent.clone().multiplyScalar(normal.dot(tangent)));   // parallel transport
      if (normal.lengthSq() < 1e-12){ normal = new THREE.Vector3(1, 0, 0); }
      normal.normalize();
    }
    const binormal = tangent.clone().cross(normal);
    const ring = [];
    for (let s = 0; s < segments; s++){
      const a = (s / segments) * Math.PI * 2;
      ring.push(points[i].clone()
        .add(normal.clone().multiplyScalar(Math.cos(a) * radius))
        .add(binormal.clone().multiplyScalar(Math.sin(a) * radius)));
    }
    rings.push(ring);
  }
  const tris = [];
  const push = (v) => tris.push(v.x, v.y, v.z);
  for (let i = 0; i < rings.length - 1; i++){
    for (let s = 0; s < segments; s++){
      const s2 = (s + 1) % segments;
      push(rings[i][s]); push(rings[i + 1][s]); push(rings[i + 1][s2]);
      push(rings[i][s]); push(rings[i + 1][s2]); push(rings[i][s2]);
    }
  }
  // fan caps so stroke ends look painted, not hollow
  for (const [ring, center] of [[rings[0], points[0]], [rings[rings.length - 1], points[points.length - 1]]]){
    for (let s = 0; s < segments; s++){
      push(center); push(ring[s]); push(ring[(s + 1) % segments]);
    }
  }
  const geom = new THREE.BufferGeometry();
  const attr = new THREE.BufferAttribute(new Float32Array(tris), 3);
  if (geom.setAttribute){ geom.setAttribute("position", attr); } else { geom.addAttribute("position", attr); }
  return geom;
}

class RedlineExtension extends Autodesk.Viewing.Extension {
  load(){
    this._active = false;
    this._tool = "free";
    this._color = RL_COLORS[0];
    this._scene = "extrabbit-redline3d";
    this._layers = [];                                 // {id,name,visible,camera,g,strokes3d}
    this._undo = [];                                   // GLOBAL action history: {layer, kind, ...} -
    this._activeId = null;                             // draws and erases undo in order across layers
    this._ac = new AbortController();                  // one handle tears down every DOM listener
    this._svg = document.getElementById("redline2d");
    this._panel = document.getElementById("redlinePanel");
    this._browser = document.getElementById("redlineLayers");
    this._input = document.getElementById("redlineText");
    // Re-parent into LMV's container (a stacking context capped at z-index 1): as body-level
    // siblings the overlay would paint above the WHOLE viewer UI and block the toolbar/viewcube
    // while drawing. Inside the context the z-indexes layer it under LMV's controls.
    const host = (this.viewer.canvas && this.viewer.canvas.closest(".adsk-viewing-viewer")) || document.body;
    host.appendChild(this._svg); host.appendChild(this._panel); host.appendChild(this._browser); host.appendChild(this._input);
    this._buildPanel();
    this._buildBrowser();
    this._bindDraw();
    this._bindText();
    window.addEventListener("keydown", (e) => {
      if (!this._active){ return; }
      // Ignore keys typed into any text field FIRST - the viewer's own inputs (Model Browser
      // search) and redline's (text tool, layer rename) have their own handling, and Esc/Ctrl+Z
      // must not leak up to exit the mode or undo markup while someone is typing.
      const t = e.target;
      if (t && (t.tagName === "INPUT" || t.tagName === "TEXTAREA" || t.isContentEditable)){ return; }
      if (e.key === "Escape"){ this.setActive(false); return; }
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "z"){ e.preventDefault(); this.undo(); return; }
      // tool shortcuts (rebindable via the Hotkeys dialog) - never with modifiers
      if (e.ctrlKey || e.metaKey || e.altKey){ return; }
      if (Hotkeys.matches("free", e)){ this._selectTool("free"); }
      else if (Hotkeys.matches("paint3d", e)){ this._selectTool("paint3d"); }
      else if (Hotkeys.matches("orbit", e)){ this._selectTool("nav"); }   // hand input to the viewer's own tools
    }, { signal: this._ac.signal });
    document.addEventListener("hotkeys-changed", () => this._refreshHotkeyTitles(), { signal: this._ac.signal });
    this._disposeWire = extrabbitWireToolbar(this.viewer, (tb) => this.onToolbarCreated(tb));
    // Picking a navigation tool from the LMV toolbar while redlining switches us to nav mode, so
    // the orbit/pan drags actually reach the canvas instead of drawing strokes.
    this._onToolChange = (e) => {
      if (this._active && e.active && RL_NAV_TOOLS.includes(e.toolName) && this._tool !== "nav"){
        this._selectTool("nav");
      }
    };
    this.viewer.addEventListener(Autodesk.Viewing.TOOL_CHANGE_EVENT, this._onToolChange);
    this._loadFromDisk();
    return true;
  }
  unload(){
    this.setActive(false);                             // release the overlay + hide the palette
    this._ac.abort();                                  // svg / input / window listeners
    if (this._disposeWire){ this._disposeWire(); }
    if (this._onToolChange){ this.viewer.removeEventListener(Autodesk.Viewing.TOOL_CHANGE_EVENT, this._onToolChange); }
    extrabbitToolbarRemove(this.viewer.toolbar, this._button);
    this._button = null;
    for (const l of this._layers){ this._disposeLayer(l); }
    this._layers = [];
    try { if (this.viewer.impl.removeOverlayScene){ this.viewer.impl.removeOverlayScene(this._scene); } } catch (e) { /* never created */ }
    this._panel.replaceChildren();                     // a later load() rebuilds them fresh
    this._browser.replaceChildren();
    return true;
  }
  onToolbarCreated(toolbar){
    if (this._button){ return; }
    try {
      this._button = new Autodesk.Viewing.UI.Button("extrabbit-redline-btn");
      this._button.setToolTip("Redline: draw on the model" + rlHotkeySuffix("redline"));
      this._button.onClick = () => this.setActive(!this._active);
      extrabbitToolbarGroup(toolbar).addControl(this._button); // shared Extrabbit button group
      // The palette has no move button of its own - Autodesk's navigation tools take that role.
      // TOOL_CHANGE covers picking a DIFFERENT nav tool; clicking the one that is already active
      // fires no event, so a plain click on the nav group also releases the drawing overlay.
      const navGroup = document.getElementById("navTools");
      if (navGroup){
        navGroup.addEventListener("click", () => {
          if (this._active && this._tool !== "nav"){ this._selectTool("nav"); }
        }, { signal: this._ac.signal });
      }
    } catch (e) { report("redline: toolbar wiring failed: " + (e && e.stack || e)); }
  }
  setActive(on){
    this._active = on;
    if (on){ this._ensureLayer(); }
    this._updateOverlay();
    this._panel.style.display = on ? "flex" : "none";
    this._browser.style.display = on ? "block" : "none";
    if (!on){
      this._finishStroke(); this._commitText();
      this._closeMenus();
      this._flushPersist();   // leaving the mode is the last chance before the viewer may close
    }
    if (this._button){
      const S = Autodesk.Viewing.UI.Button.State;
      this._button.setState(on ? S.ACTIVE : S.INACTIVE);
    }
    report("redline " + (on ? "on" : "off"));
  }
  // The drawing surface only captures input when the mode is on AND a drawing tool is selected;
  // in nav mode the overlay goes transparent so LMV's own tools (orbit, pan, ...) work natively
  // while the palette and layer browser stay up.
  _updateOverlay(){
    const drawing = this._active && this._tool !== "nav";
    this._svg.style.pointerEvents = drawing ? "auto" : "none";
    this._svg.classList.toggle("drawing", drawing);
  }
  // ---------- layers ----------
  _layer(){ return this._layers.find(l => l.id === this._activeId) || null; }
  _ensureLayer(){
    if (!this._layer()){ this._newLayer(); }
    return this._layer();
  }
  _newLayer(){
    this._finishStroke(); this._commitText();
    const id = "l" + Date.now().toString(36) + Math.floor(Math.random() * 1e4).toString(36);
    const g = document.createElementNS(RL_NS, "g");
    this._svg.appendChild(g);
    const n = this._layers.reduce((max, l) => { const m = /^Layer (\d+)$/.exec(l.name); return m ? Math.max(max, +m[1]) : max; }, 0);
    const l = { id, name: "Layer " + (n + 1), visible: true,
                camera: this._cameraState(), g, strokes3d: [] };
    this._layers.push(l);
    this._activeId = id;
    this._syncBrowser();
    this._persist();
    return l;
  }
  _activateLayer(id){
    if (this._activeId === id){ return; }              // no-op re-click: keeps the row's DOM stable,
    this._finishStroke(); this._commitText();          // so a double-click's rename target survives
    const l = this._layers.find(x => x.id === id);
    if (!l){ return; }
    this._activeId = id;
    this._syncBrowser();
    this._persist();
  }
  _deleteLayer(id){
    const i = this._layers.findIndex(x => x.id === id);
    if (i < 0){ return; }
    this._finishStroke(); this._commitText();
    this._undo = this._undo.filter(e => e.layer !== this._layers[i]);
    this._disposeLayer(this._layers[i]);
    this._layers.splice(i, 1);
    if (this._activeId === id){
      this._activeId = this._layers.length ? this._layers[this._layers.length - 1].id : null;
    }
    this.viewer.impl.invalidate(false, false, true);
    this._syncBrowser();
    this._persist();
  }
  _renameLayer(id, name){
    const l = this._layers.find(x => x.id === id);
    if (!l || !name.trim()){ return; }
    l.name = name.trim();
    this._syncBrowser();
    this._persist();
  }
  _setVisible(l, on){
    l.visible = on;
    l.g.style.display = on ? "" : "none";
    for (const t of l.strokes3d){ if (t.obj){ t.obj.visible = on; } }
    this.viewer.impl.invalidate(false, false, true);
  }
  _restoreCamera(l){
    if (!l.camera){ return; }
    try { this.viewer.restoreState(l.camera, null, true); } catch (e) { report("camera restore: " + e); }
  }
  _resaveCamera(l){
    l.camera = this._cameraState();
    this._persist();
  }
  _disposeLayer(l){
    for (const t of l.strokes3d){
      if (t.obj){
        this.viewer.impl.removeOverlay(this._scene, t.obj, true);
        t.obj.material.dispose();
      }
    }
    l.strokes3d = [];
    l.g.remove();
  }
  _cameraState(){
    try { return this.viewer.getState({ viewport: true }); } catch (e) { return null; }
  }
  // ---------- layer browser (docked right) ----------
  _buildBrowser(){
    this._browser.replaceChildren();
    // resize grip on the left edge; the chosen width sticks via localStorage
    const grip = document.createElement("div");
    grip.className = "rl-resize";
    grip.title = "Drag to resize";
    grip.addEventListener("pointerdown", (e) => {
      e.preventDefault(); e.stopPropagation();
      try { grip.setPointerCapture(e.pointerId); } catch (err) { /* best effort */ }
      const right = this._browser.getBoundingClientRect().right;
      const move = (ev) => {
        const w = Math.min(480, Math.max(180, Math.round(right - ev.clientX)));
        this._browser.style.width = w + "px";
      };
      const up = () => {
        grip.removeEventListener("pointermove", move);
        grip.removeEventListener("pointerup", up);
        try { localStorage.setItem("extrabbit-redline-layers-width", parseInt(this._browser.style.width) || 240); } catch (err) { /* private mode */ }
      };
      grip.addEventListener("pointermove", move);
      grip.addEventListener("pointerup", up);
    });
    this._browser.appendChild(grip);
    try {
      const w = +localStorage.getItem("extrabbit-redline-layers-width");
      if (w){ this._browser.style.width = Math.min(480, Math.max(180, w)) + "px"; }
    } catch (e) { /* private mode */ }
    const head = document.createElement("div");
    head.className = "rl-layers-head";
    const title = document.createElement("span");
    title.textContent = "Layers";
    const add = document.createElement("button");
    add.className = "rl-layer-btn"; add.title = "New layer"; add.innerHTML = RL_ICON_PLUS;
    add.addEventListener("click", () => this._newLayer());
    head.appendChild(title); head.appendChild(add);
    this._browser.appendChild(head);
    this._rows = document.createElement("div");
    this._rows.className = "rl-layers-rows";
    this._browser.appendChild(this._rows);
    this._syncBrowser();
  }
  _syncBrowser(){
    if (!this._rows){ return; }
    this._rows.replaceChildren();
    for (const l of this._layers){
      const row = document.createElement("div");
      row.className = "rl-layer" + (l.id === this._activeId ? " sel" : "");
      row.addEventListener("click", () => this._activateLayer(l.id));

      const rowBtn = (title, label, onclick) => {
        const b = document.createElement("button");
        b.className = "rl-layer-btn"; b.title = title; b.textContent = label;
        b.addEventListener("click", (e) => { e.stopPropagation(); onclick(); });
        row.appendChild(b);
        return b;
      };

      const eye = rowBtn(l.visible ? "Hide layer" : "Show layer", "",
        () => { this._setVisible(l, !l.visible); this._syncBrowser(); this._persist(); });
      eye.innerHTML = l.visible ? RL_ICON_EYE : RL_ICON_EYE_OFF;

      const name = document.createElement("span");
      name.className = "rl-layer-name";
      name.textContent = l.name;
      name.title = "Double-click to rename";
      name.addEventListener("dblclick", (e) => { e.stopPropagation(); this._beginRename(l, name); });
      row.appendChild(name);

      rowBtn("Restore this layer's camera perspective", "", () => this._restoreCamera(l)).innerHTML = RL_ICON_CAM_RESTORE;
      rowBtn("Re-save perspective from the current view", "", () => { this._resaveCamera(l); }).innerHTML = RL_ICON_PIN;
      rowBtn("Delete layer (and its markup)", "", () => this._deleteLayer(l.id)).innerHTML = RL_ICON_TRASH;

      this._rows.appendChild(row);
    }
  }
  _beginRename(l, nameEl){
    const inp = document.createElement("input");
    inp.type = "text"; inp.value = l.name; inp.className = "rl-layer-rename";
    inp.addEventListener("click", (e) => e.stopPropagation());
    inp.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.isComposing){ inp.blur(); }
      else if (e.key === "Escape"){ inp.value = l.name; inp.blur(); }
      e.stopPropagation();
    });
    inp.addEventListener("blur", () => { this._renameLayer(l.id, inp.value); });
    nameEl.replaceWith(inp);
    inp.focus(); inp.select();
  }
  // ---------- persistence (redline-layers.json in the model's cache entry) ----------
  _loadFromDisk(){
    fetch("./redline-layers.json", { cache: "no-store" })
      .then(r => r.ok ? r.json() : null)
      .then(d => { if (d && Array.isArray(d.layers)){ this._restore(d); } })
      .catch(() => { /* no saved layers */ });
  }
  _restore(d){
    for (const raw of d.layers){
      const g = document.createElementNS(RL_NS, "g");
      g.innerHTML = raw.svg || "";
      this._svg.appendChild(g);
      const l = { id: raw.id, name: raw.name, visible: raw.visible !== false,
                  camera: raw.camera || null, g, strokes3d: [] };
      for (const t of (raw.strokes3d || [])){
        if (!t.points || t.points.length < 2){ continue; }
        const pts = t.points.map(p => new THREE.Vector3(p[0], p[1], p[2]));
        const obj = this._makeStroke(pts, t.color, t.width || 2, t.radius);
        this._ensureScene();
        this.viewer.impl.addOverlay(this._scene, obj);
        l.strokes3d.push({ color: t.color, width: t.width || 2, radius: t.radius, points: t.points, obj });
      }
      this._setVisible(l, l.visible);                  // applies g display + 3D visibility
      this._layers.push(l);
    }
    this._activeId = d.active && this._layers.some(l => l.id === d.active)
      ? d.active
      : (this._layers.length ? this._layers[this._layers.length - 1].id : null);
    this._syncBrowser();
    report("redline: restored " + this._layers.length + " layer(s)");
  }
  // Debounced by default so a burst of edits becomes one write; immediate=true flushes a pending
  // save right now - called when redline mode deactivates, so closing the viewer straight after an
  // edit can't lose it (the host disposes the page without waiting for timers).
  _persist(immediate){
    clearTimeout(this._saveT);
    const save = () => {
      this._saveT = null;
      const data = {
        version: 2,
        active: this._activeId,
        layers: this._layers.map(l => ({
          id: l.id, name: l.name, visible: l.visible, camera: l.camera,
          svg: l.g.innerHTML,
          strokes3d: l.strokes3d.map(t => ({ color: t.color, width: t.width || 2, radius: t.radius, points: t.points })),
        })),
      };
      try { window.chrome.webview.postMessage("redline-save:" + JSON.stringify(data)); }
      catch (e) { /* no host bridge (dev page) */ }
    };
    if (immediate){ save(); }
    else { this._saveT = setTimeout(save, 250); }
  }
  /// flush a PENDING save immediately; a no-op when nothing changed since the last write
  _flushPersist(){
    if (this._saveT){ this._persist(true); }
  }
  // ---------- screenshot export ----------
  /// mode: "save" opens the host's save dialog, "copy" puts the PNG on the clipboard.
  _exportShot(mode){
    const l = this._layer();
    if (!l){ return; }
    const vv = this.viewer;
    const w = vv.canvas.clientWidth, h = vv.canvas.clientHeight;
    vv.getScreenShot(w, h, (blobUrl) => {
      const base = new Image();
      base.onload = () => {
        const cv = document.createElement("canvas");
        cv.width = w; cv.height = h;
        const ctx = cv.getContext("2d");
        ctx.drawImage(base, 0, 0, w, h);
        URL.revokeObjectURL(blobUrl);
        // rasterise the SVG overlay on top; hidden layers carry display:none and don't render
        const clone = this._svg.cloneNode(true);
        clone.setAttribute("width", w); clone.setAttribute("height", h);
        clone.setAttribute("viewBox", "0 0 " + w + " " + h);
        const svgImg = new Image();
        svgImg.onload = () => {
          ctx.drawImage(svgImg, 0, 0);
          if (this._exportInfo){ this._drawInfoBlock(ctx, l, w, h); }
          const dataUrl = cv.toDataURL("image/png");
          try {
            window.chrome.webview.postMessage("redline-shot:" + JSON.stringify({ name: l.name, mode: mode || "save", data: dataUrl }));
            report("redline: screenshot posted (" + (mode || "save") + ", " + Math.round(dataUrl.length / 1024) + " KB)");
          } catch (e) { report("redline: screenshot export needs the app host"); }
        };
        svgImg.onerror = () => report("redline: svg rasterise failed");
        svgImg.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(new XMLSerializer().serializeToString(clone));
      };
      base.src = blobUrl;
    });
  }
  // A small caption block in the export's bottom-left corner: model file, layer, date, and the
  // best-effort note when the built-in converter produced the viewable.
  _drawInfoBlock(ctx, layer, w, h){
    const q = new URLSearchParams(location.search);
    const lines = [];
    const file = q.get("file");
    if (file){ lines.push(file); }
    lines.push("Layer: " + layer.name);
    lines.push(new Date().toLocaleString());
    if (q.get("engine") === "local"){ lines.push("Best-effort view (built-in converter)"); }
    const pad = 10, lh = 19, font = '13px "Segoe UI", sans-serif';
    ctx.font = font;
    const bw = Math.max(...lines.map(t => ctx.measureText(t).width)) + pad * 2;
    const bh = lines.length * lh + pad * 2 - 4;
    const x = 12, y = h - bh - 12;
    ctx.fillStyle = "rgba(20, 20, 20, 0.72)";
    if (ctx.roundRect){ ctx.beginPath(); ctx.roundRect(x, y, bw, bh, 8); ctx.fill(); }
    else { ctx.fillRect(x, y, bw, bh); }
    ctx.fillStyle = "#fff";
    lines.forEach((t, i) => ctx.fillText(t, x + pad, y + pad + 10 + i * lh));
  }
  // ---------- palette ----------
  _buildPanel(){
    this._panel.replaceChildren();                     // idempotent across load cycles
    const btn = (title, label, cls, onclick) => {
      const b = document.createElement("button");
      b.className = cls; b.title = title; b.textContent = label; b.addEventListener("click", onclick);
      this._panel.appendChild(b);
      return b;
    };
    const sep = () => this._panel.appendChild(Object.assign(document.createElement("div"), { className: "rl-sep" }));
    // Grouped commands live in dropdowns so the palette stays short. Each trigger previews its
    // current pick; a menu closes on pick, on an outside press, when a sibling opens, or when
    // redline mode ends.
    this._menus = [];
    const dropdown = (title) => {
      const menu = Object.assign(document.createElement("div"), { className: "rl-style-menu" });
      const trigger = btn(title, "", "rl-tool rl-style", () => {
        const open = !menu.classList.contains("open");
        this._closeMenus();
        if (open){
          menu.classList.add("open");
          const left = trigger.offsetLeft + trigger.offsetWidth / 2 - menu.offsetWidth / 2;
          menu.style.left = Math.max(4, left) + "px";
        }
      });
      trigger.appendChild(Object.assign(document.createElement("span"), { className: "rl-caret", textContent: "▾" }));
      this._panel.appendChild(menu);
      this._menus.push(menu);
      document.addEventListener("pointerdown", (e) => {
        if (!menu.contains(e.target) && !trigger.contains(e.target)){ menu.classList.remove("open"); }
      }, { signal: this._ac.signal });
      return { trigger, menu };
    };
    this._toolBtns = {};
    const toolBtn = ([id, glyph, title, isHtml]) => {
      const b = btn(title, isHtml ? "" : glyph, "rl-tool", () => this._selectTool(id));
      if (isHtml){ b.innerHTML = glyph; }
      this._toolBtns[id] = b;
      return b;
    };
    RL_TOOLS.slice(0, 2).forEach(toolBtn);             // freehand pen + 3D paint side by side
    this._refreshHotkeyTitles();
    const shapes = dropdown(RL_SHAPES[0][2]);
    this._shapeTrigger = shapes.trigger;
    this._shapeGlyph = Object.assign(document.createElement("span"), { textContent: RL_SHAPES[0][1] });
    shapes.trigger.insertBefore(this._shapeGlyph, shapes.trigger.firstChild);
    const shapeRow = Object.assign(document.createElement("div"), { className: "rl-row" });
    for (const [id, glyph, title] of RL_SHAPES){
      const b = document.createElement("button");
      b.className = "rl-tool"; b.title = title; b.textContent = glyph;
      b.addEventListener("click", () => this._selectTool(id));
      this._toolBtns[id] = b;
      shapeRow.appendChild(b);
    }
    shapes.menu.appendChild(shapeRow);
    RL_TOOLS.slice(2).forEach(toolBtn);                // text, eraser
    sep();
    try { this._width = +localStorage.getItem("extrabbit-redline-width") || 2; } catch (e) { this._width = 2; }
    const colors = dropdown("Colour");
    this._colorDot = Object.assign(document.createElement("span"), { className: "rl-width-dot" });
    this._colorDot.style.width = this._colorDot.style.height = "12px";
    colors.trigger.insertBefore(this._colorDot, colors.trigger.firstChild);
    const colorRow = Object.assign(document.createElement("div"), { className: "rl-row" });
    this._swatches = RL_COLORS.map(c => {
      const s = document.createElement("button");
      s.className = "rl-swatch"; s.title = "Colour " + c; s.style.background = c;
      s.addEventListener("click", () => this._selectColor(c));
      colorRow.appendChild(s);
      return s;
    });
    colors.menu.appendChild(colorRow);
    // stroke width: applies to 2D shapes (px) and the 3D pen (tube radius)
    const widths = dropdown("Stroke width");
    this._widthDot = Object.assign(document.createElement("span"), { className: "rl-width-dot" });
    widths.trigger.insertBefore(this._widthDot, widths.trigger.firstChild);
    const widthRow = Object.assign(document.createElement("div"), { className: "rl-row" });
    this._widthBtns = [1, 2, 3].map(wd => {
      const b = document.createElement("button");
      b.className = "rl-tool"; b.title = "Stroke width";
      b.addEventListener("click", () => this._selectWidth(wd));
      const dot = Object.assign(document.createElement("span"), { className: "rl-width-dot" });
      dot.style.width = dot.style.height = (4 + wd * 3) + "px";
      b.appendChild(dot);
      b.classList.toggle("sel", wd === this._width);
      widthRow.appendChild(b);
      return b;
    });
    widths.menu.appendChild(widthRow);
    sep();
    // screenshot export: save / copy / the info-block option share one dropdown
    const shot = dropdown("Screenshot of the active layer");
    shot.trigger.insertBefore(Object.assign(document.createElement("span"), { textContent: "\u{1F4F7}" }), shot.trigger.firstChild);
    const shotItem = (glyph, label, isHtml, onclick) => {
      const b = document.createElement("button");
      b.className = "rl-menu-item";
      const g = document.createElement("span");
      if (isHtml){ g.innerHTML = glyph; } else { g.textContent = glyph; }
      b.append(g, Object.assign(document.createElement("span"), { textContent: label }));
      b.addEventListener("click", onclick);
      shot.menu.appendChild(b);
      return b;
    };
    shotItem("\u{1F4F7}", "Save as PNG", false, () => { this._closeMenus(); this._exportShot("save"); });
    shotItem("\u{1F4CB}", "Copy to clipboard", false, () => { this._closeMenus(); this._exportShot("copy"); });
    try { this._exportInfo = localStorage.getItem("extrabbit-redline-export-info") === "1"; } catch (e) { this._exportInfo = false; }
    // a setting, not an action: toggling keeps the menu open so the state stays visible
    this._infoBtn = shotItem(RL_ICON_INFO, "Include info block (file, layer, date)", true, () => {
      this._exportInfo = !this._exportInfo;
      this._infoBtn.classList.toggle("sel", this._exportInfo);
      try { localStorage.setItem("extrabbit-redline-export-info", this._exportInfo ? "1" : "0"); } catch (e) { /* private mode */ }
    });
    this._infoBtn.classList.toggle("sel", this._exportInfo);
    btn("Undo (Ctrl+Z)", "↩", "rl-tool", () => this.undo());
    btn("Close (Esc)", "✕", "rl-tool", () => this.setActive(false));
    this._selectTool("free");
    this._selectColor(RL_COLORS[0]);
  }
  // tooltips carry the CURRENT key bindings; re-applied whenever the Hotkeys dialog changes one
  _refreshHotkeyTitles(){
    if (this._toolBtns){
      if (this._toolBtns.free){ this._toolBtns.free.title = "Freehand" + rlHotkeySuffix("free"); }
      if (this._toolBtns.paint3d){
        this._toolBtns.paint3d.title = "Paint on the model (experimental) - strokes stick to the surface" + rlHotkeySuffix("paint3d");
      }
    }
    if (this._button){ this._button.setToolTip("Redline: draw on the model" + rlHotkeySuffix("redline")); }
  }
  _selectTool(id){
    if (this._tool !== id){ this._finishStroke(); }
    this._tool = id;
    for (const k in this._toolBtns){ this._toolBtns[k].classList.toggle("sel", k === id); }
    // the shape dropdown's trigger mirrors whichever shape is in use
    const shape = RL_SHAPES.find(s => s[0] === id);
    if (this._shapeTrigger){
      this._shapeTrigger.classList.toggle("sel", !!shape);
      if (shape){ this._shapeGlyph.textContent = shape[1]; this._shapeTrigger.title = shape[2]; }
    }
    this._closeMenus();
    this._updateOverlay();
  }
  _selectColor(c){
    this._color = c;
    this._swatches.forEach(s => s.classList.toggle("sel", s.style.background === c || rlHex(s.style.background) === c));
    this._syncStyleBtn();
    this._closeMenus();
  }
  _selectWidth(wd){
    this._width = wd;
    this._widthBtns.forEach((x, i) => x.classList.toggle("sel", i + 1 === wd));
    try { localStorage.setItem("extrabbit-redline-width", wd); } catch (e) { /* private mode */ }
    this._syncStyleBtn();
    this._closeMenus();
  }
  _syncStyleBtn(){
    if (this._colorDot){ this._colorDot.style.background = this._color; }
    if (this._widthDot){ this._widthDot.style.width = this._widthDot.style.height = (4 + this._width * 3) + "px"; }
  }
  _closeMenus(){
    (this._menus || []).forEach(m => m.classList.remove("open"));
  }
  // ---------- drawing ----------
  _bindDraw(){
    const svg = this._svg;
    const sig = { signal: this._ac.signal };
    const pos = (e) => { const r = svg.getBoundingClientRect(); return { x: e.clientX - r.left, y: e.clientY - r.top }; };
    svg.addEventListener("pointerdown", (e) => {
      if (!this._active){ return; }
      if (e.button !== 0){ this._forwardNav(e); return; }
      if (this._start){ return; }                      // a second pointer doesn't start a stroke
      this._commitText();
      const layer = this._ensureLayer();
      if (!layer.visible){ this._setVisible(layer, true); this._syncBrowser(); }
      const p = pos(e);
      if (this._tool === "text"){ this._beginText(p); return; }
      try { svg.setPointerCapture(e.pointerId); } catch (err) { /* capture is best-effort */ }
      this._start = p;
      this._strokeTool = this._tool;
      this._strokeLayer = layer;
      this._pointerId = e.pointerId;
      this._moved = false;
      if (this._strokeTool === "erase"){ this._eraseAt(e.clientX, e.clientY); }
      else if (this._strokeTool === "paint3d"){
        this._pts3d = []; this._line3d = null; this._lastScreen = null;
        this._radius3d = null;                            // latched at the first surface hit
        this._add3D(p);
      }
      else { this._el = this._begin2D(p); layer.g.appendChild(this._el); }
      e.preventDefault();
    }, sig);
    svg.addEventListener("pointermove", (e) => {
      if (!this._active || !this._start || e.pointerId !== this._pointerId){ return; }
      if ((e.buttons & 1) === 0){ this._finishStroke(); return; }   // lost pointerup (focus steal etc.)
      const p = pos(e);
      if (Math.abs(p.x - this._start.x) > 2 || Math.abs(p.y - this._start.y) > 2){ this._moved = true; }
      if (this._strokeTool === "erase"){ this._eraseAt(e.clientX, e.clientY); }
      else if (this._strokeTool === "paint3d"){ this._add3D(p); }
      else if (this._el){ this._update2D(p); }
    }, sig);
    const finish = (e) => {
      if (!this._start || (e && e.pointerId !== this._pointerId)){ return; }
      this._finishStroke();
    };
    svg.addEventListener("pointerup", finish, sig);
    svg.addEventListener("pointercancel", finish, sig);
    // wheel-zoom keeps working over the drawing surface (LMV binds the legacy "mousewheel"
    // event on its canvas, not the standard "wheel")
    svg.addEventListener("wheel", (e) => {
      if (!this._active){ return; }
      e.preventDefault();
      try { this.viewer.canvas.dispatchEvent(new WheelEvent("mousewheel", e)); } catch (err) { /* no canvas */ }
    }, { passive: false, signal: this._ac.signal });
  }
  _finishStroke(){
    if (!this._start){ return; }
    const layer = this._strokeLayer;
    if (this._strokeTool === "paint3d"){ this._commit3D(); }
    else if (this._el){
      if (this._moved && layer){
        this._undo.push({ layer, kind: "2d", el: this._el });
        this._persist();
      }
      else { this._el.remove(); }                      // a bare click draws nothing visible
      this._el = null;
    }
    this._start = null;
    this._pointerId = undefined;
    this._strokeLayer = null;
  }
  // ---------- eraser ----------
  // Object eraser: removes the whole shape/stroke under the cursor. 2D markup is found via
  // hit-testing a small cross of points (a bare elementFromPoint would demand pixel-perfect aim
  // at a 3px stroke); 3D strokes are found by projecting their points to screen space. Erasing
  // pushes an "erase" entry onto the owning layer's undo stack, so Ctrl+Z restores the item.
  _eraseAt(clientX, clientY){
    const probes = [[0, 0], [4, 0], [-4, 0], [0, 4], [0, -4], [3, 3], [-3, 3], [3, -3], [-3, -3]];
    for (const [dx, dy] of probes){
      const el = document.elementFromPoint(clientX + dx, clientY + dy);
      if (el && el instanceof SVGElement && el !== this._svg && el.tagName !== "g"){
        const layer = this._layers.find(l => l.g.contains(el));
        if (layer && layer.visible){
          // the original draw entry STAYS in the stack: undo then walks back conventionally -
          // first un-erase (restore), later un-draw (remove again)
          el.remove();
          this._undo.push({ layer, kind: "erase2d", el });
          this._persist();
          return true;
        }
      }
    }
    // 3D strokes: compare against the strokes' points projected to client space
    const rect = this._svg.getBoundingClientRect();
    const px = clientX - rect.left, py = clientY - rect.top;
    for (const layer of this._layers){
      if (!layer.visible){ continue; }
      for (const stroke of layer.strokes3d){
        for (const p of stroke.points){
          const c = this.viewer.worldToClient(new THREE.Vector3(p[0], p[1], p[2]));
          if (Math.abs(c.x - px) < 7 && Math.abs(c.y - py) < 7){
            this.viewer.impl.removeOverlay(this._scene, stroke.obj, true);
            stroke.obj.material.dispose();
            stroke.obj = null;
            layer.strokes3d.splice(layer.strokes3d.indexOf(stroke), 1);
            this._undo.push({ layer, kind: "erase3d", stroke });   // the draw entry stays in the stack
            this.viewer.impl.invalidate(false, false, true);
            this._persist();
            return true;
          }
        }
      }
    }
    return false;
  }
  // Middle/right drags are navigation: replay the down on the LMV canvas and drop the overlay's
  // pointer-events until the drag ends, so the rest of the gesture reaches the viewer natively.
  _forwardNav(e){
    const svg = this._svg, canvas = this.viewer.canvas;
    if (!canvas){ return; }
    svg.style.pointerEvents = "none";
    try {
      canvas.dispatchEvent(new PointerEvent("pointerdown", e));
      canvas.dispatchEvent(new MouseEvent("mousedown", e));
    } catch (err) { /* forwarding is best-effort */ }
    const restore = () => {
      window.removeEventListener("pointerup", restore, true);
      window.removeEventListener("pointercancel", restore, true);
      // through _updateOverlay, not a raw "auto": the drag may have switched the tool to nav (orbit
      // hotkey, or clicking an LMV nav tool while held), and re-enabling the overlay in nav mode
      // would swallow the next drag as a stroke instead of letting it navigate.
      this._updateOverlay();
    };
    window.addEventListener("pointerup", restore, { capture: true, signal: this._ac.signal });
    window.addEventListener("pointercancel", restore, { capture: true, signal: this._ac.signal });
  }
  _shape(tag){
    const el = document.createElementNS(RL_NS, tag);
    el.setAttribute("stroke", this._color);
    el.setAttribute("stroke-width", String((RL_WIDTHS[this._width] || RL_WIDTHS[2]).px));
    el.setAttribute("fill", "none");
    el.setAttribute("stroke-linecap", "round");
    el.setAttribute("stroke-linejoin", "round");
    return el;
  }
  _begin2D(p){
    const t = this._strokeTool;
    if (t === "rect"){ const el = this._shape("rect"); el.setAttribute("x", p.x); el.setAttribute("y", p.y); return el; }
    if (t === "circle"){ const el = this._shape("ellipse"); el.setAttribute("cx", p.x); el.setAttribute("cy", p.y); return el; }
    const el = this._shape("path");                    // freehand + arrow are paths
    el.setAttribute("d", `M ${p.x} ${p.y}`);
    return el;
  }
  _update2D(p){
    const el = this._el, s = this._start, t = this._strokeTool;
    if (t === "free"){ el.setAttribute("d", el.getAttribute("d") + ` L ${p.x} ${p.y}`); }
    else if (t === "rect"){
      el.setAttribute("x", Math.min(s.x, p.x)); el.setAttribute("y", Math.min(s.y, p.y));
      el.setAttribute("width", Math.abs(p.x - s.x)); el.setAttribute("height", Math.abs(p.y - s.y));
    }
    else if (t === "circle"){
      el.setAttribute("cx", (s.x + p.x) / 2); el.setAttribute("cy", (s.y + p.y) / 2);
      el.setAttribute("rx", Math.abs(p.x - s.x) / 2); el.setAttribute("ry", Math.abs(p.y - s.y) / 2);
    }
    else if (t === "arrow"){
      const a = Math.atan2(p.y - s.y, p.x - s.x), h = 14;
      const h1x = p.x - h * Math.cos(a - 0.45), h1y = p.y - h * Math.sin(a - 0.45);
      const h2x = p.x - h * Math.cos(a + 0.45), h2y = p.y - h * Math.sin(a + 0.45);
      el.setAttribute("d", `M ${s.x} ${s.y} L ${p.x} ${p.y} M ${h1x} ${h1y} L ${p.x} ${p.y} L ${h2x} ${h2y}`);
    }
  }
  // ---------- text ----------
  _bindText(){
    const inp = this._input, sig = { signal: this._ac.signal };
    inp.addEventListener("keydown", (e) => {
      if (e.key === "Enter" && !e.isComposing){ this._commitText(); }   // IME Enter confirms the composition, not the annotation
      else if (e.key === "Escape"){ this._cancelText(); }
      e.stopPropagation();                             // typing must not trigger the mode's shortcuts
    }, sig);
    inp.addEventListener("blur", () => this._commitText(), sig);
  }
  _beginText(p){
    const inp = this._input;
    inp.style.display = "block";
    inp.style.left = p.x + "px"; inp.style.top = (p.y - 14) + "px";
    inp.style.color = this._color; inp.style.borderColor = this._color;
    inp.value = "";
    this._textPos = p;
    setTimeout(() => inp.focus(), 0);
  }
  _cancelText(){
    this._textPos = null;                              // discard: hide without committing
    this._input.style.display = "none";
    this._input.value = "";
  }
  _commitText(){
    const inp = this._input;
    if (inp.style.display === "none" || !this._textPos){ return; }
    const value = inp.value.trim();
    inp.style.display = "none";
    if (value){
      const layer = this._ensureLayer();
      const el = document.createElementNS(RL_NS, "text");
      el.setAttribute("x", this._textPos.x); el.setAttribute("y", this._textPos.y + 4);
      el.setAttribute("fill", this._color);
      el.setAttribute("style", 'font:18px "Segoe UI",system-ui,sans-serif;');
      el.textContent = value;
      layer.g.appendChild(el);
      this._undo.push({ layer, kind: "2d", el });
      this._persist();
    }
    this._textPos = null;
  }
  // ---------- 3D pen ----------
  // Paint on the model: pointer samples are raycast onto the surface, densified along the screen
  // path (every ~4px, so strokes hug curved geometry instead of cutting chords between samples),
  // lifted by the tube radius, smoothed and swept into a tube mesh with real 3D thickness.
  _modelSize(){
    try { return this.viewer.model.getBoundingBox().getSize(new THREE.Vector3()).length(); }
    catch (e) { return 100; }
  }
  // World units per screen pixel AT a given world point, camera-agnostic: project two points one
  // world unit apart and measure their pixel distance. Measuring at the point that is actually
  // being painted matters for perspective cameras, where world-per-pixel varies with depth - the
  // navigation target can sit far behind the surface and give a radius that is off by a factor.
  _worldPerPixel(at){
    try {
      const t = (at || this.viewer.navigation.getTarget()).clone();
      const up = this.viewer.navigation.getCameraUpVector().clone().normalize();
      const a = this.viewer.worldToClient(t);
      const b = this.viewer.worldToClient(t.clone().add(up));
      const px = Math.hypot(a.x - b.x, a.y - b.y);
      return px > 1e-6 ? 1 / px : this._modelSize() / 800;
    } catch (e) { return this._modelSize() / 800; }
  }
  _tubeRadius(width, at){
    const px = (RL_WIDTHS[width || this._width] || RL_WIDTHS[2]).tube;
    const r = this._worldPerPixel(at) * px / 2;
    // clamp to sane world bounds so extreme zoom levels can't produce invisible or absurd paint
    const size = this._modelSize();
    return Math.min(size * 0.05, Math.max(size * 0.0005, r));
  }
  _add3D(p){
    // densify: raycast intermediate screen points between the last sample and this one
    const steps = this._lastScreen
      ? Math.min(64, Math.ceil(Math.hypot(p.x - this._lastScreen.x, p.y - this._lastScreen.y) / 4))
      : 1;
    for (let k = 1; k <= steps; k++){
      const x = this._lastScreen ? this._lastScreen.x + (p.x - this._lastScreen.x) * k / steps : p.x;
      const y = this._lastScreen ? this._lastScreen.y + (p.y - this._lastScreen.y) * k / steps : p.y;
      const hit = this.viewer.impl.hitTest(x, y, false);
      if (!hit || !hit.intersectPoint){ continue; }
      // Lift by the tube radius so the tube LIES ON the surface instead of being buried in it -
      // along the SURFACE NORMAL, not the view direction: on a face tilted away from the camera a
      // view-direction lift leaves less than one radius of clearance and the tube sinks in, leaving
      // only a thin broken crest visible. The hit normal is fragment-local; transform it to world.
      const q = hit.intersectPoint.clone();
      // stroke width is fixed at the first touch, measured at the surface actually being painted
      if (this._radius3d == null){ this._radius3d = this._tubeRadius(this._width, q); }
      const toCam = this.viewer.impl.camera.position.clone().sub(q).normalize();
      let dir = null;
      try {
        if (hit.face && hit.face.normal && hit.model){
          const n = hit.face.normal.clone();
          const m = new THREE.Matrix4();
          hit.model.getFragmentList().getWorldMatrix(hit.fragId, m);
          n.applyMatrix3(new THREE.Matrix3().getNormalMatrix(m)).normalize();
          if (n.lengthSq() > 0.5){
            if (n.dot(toCam) < 0){ n.negate(); }         // never push the paint INTO the part
            dir = n;
          }
        }
      } catch (e) { /* fragment matrix unavailable - fall back to the view direction */ }
      q.add((dir || toCam).multiplyScalar(this._radius3d * 1.2));
      this._pts3d.push(q);
    }
    this._lastScreen = p;
    if (this._pts3d.length >= 2){ this._refresh3D(); }
  }
  // radius: the world-space tube radius latched when the stroke was drawn. Rebuilds (restore,
  // un-erase) MUST pass it - recomputing from the current camera would resize old strokes.
  _makeStroke(points, color, width, radius){
    radius = radius || this._tubeRadius(width);
    // Split the polyline where consecutive samples jump far apart in world space: that's the pen
    // crossing a gap between surfaces (very common on exploded assemblies), and one continuous
    // tube would bridge it with a straight bar through midair. The split is recomputed from the
    // stored points on every rebuild, so persistence only ever carries the raw samples.
    const maxSeg = Math.max(radius * 8, this._modelSize() * 0.03);
    const runs = [];
    let run = [points[0]];
    for (let i = 1; i < points.length; i++){
      if (points[i].distanceTo(points[i - 1]) > maxSeg){
        if (run.length >= 2){ runs.push(run); }
        run = [];
      }
      run.push(points[i]);
    }
    if (run.length >= 2){ runs.push(run); }
    if (!runs.length){ runs.push(points); }              // nothing but jumps: keep the old bridge
    const arrays = runs.map(r => rlTube(rlChaikin(r), radius).attributes.position.array);
    const merged = new Float32Array(arrays.reduce((n, a) => n + a.length, 0));
    let off = 0;
    for (const a of arrays){ merged.set(a, off); off += a.length; }
    const geom = new THREE.BufferGeometry();
    const attr = new THREE.BufferAttribute(merged, 3);
    if (geom.setAttribute){ geom.setAttribute("position", attr); } else { geom.addAttribute("position", attr); }
    const mat = new THREE.MeshBasicMaterial({ color: new THREE.Color(color || this._color), depthTest: true, depthWrite: false });
    return new THREE.Mesh(geom, mat);
  }
  _ensureScene(){
    const impl = this.viewer.impl;
    if (!impl.overlayScenes || !impl.overlayScenes[this._scene]){ impl.createOverlayScene(this._scene); }
  }
  _refresh3D(){
    const impl = this.viewer.impl;
    this._ensureScene();
    // the true flag disposes the replaced geometry - LMV's GL buffers are pool-allocated and only
    // freed via dispose(), so rebuilding per sample would otherwise leak GPU memory per move
    if (this._line3d){ impl.removeOverlay(this._scene, this._line3d, true); this._line3d.material.dispose(); }
    this._line3d = this._makeStroke(this._pts3d, this._color, this._width, this._radius3d);
    impl.addOverlay(this._scene, this._line3d);
    impl.invalidate(false, false, true);
  }
  _commit3D(){
    const layer = this._strokeLayer;
    if (this._line3d && layer){
      const stroke = { color: this._color, width: this._width, radius: this._radius3d,
                       points: this._pts3d.map(q => [q.x, q.y, q.z]), obj: this._line3d };
      layer.strokes3d.push(stroke);
      this._undo.push({ layer, kind: "3d", stroke });
      this._persist();
    }
    else if (this._line3d){ this.viewer.impl.removeOverlay(this._scene, this._line3d, true); this._line3d.material.dispose(); }
    else { report("3d stroke discarded (no surface under the cursor)"); }
    this._pts3d = null; this._line3d = null; this._lastScreen = null;
  }
  // ---------- undo / clear ----------
  // Undo walks ONE GLOBAL history, so it reverses draws and erases in the exact order they
  // happened - including erases on layers that aren't active. Entries of deleted layers are
  // purged when the layer goes; anything else replays on its owning layer.
  undo(){
    const item = this._undo.pop();
    if (!item){ return; }
    const layer = item.layer;
    if (item.kind === "2d"){ item.el.remove(); }
    else if (item.kind === "erase2d"){
      // un-erase: put the shape back; its original draw entry is still below in the stack
      layer.g.appendChild(item.el);
    }
    else if (item.kind === "erase3d"){
      const stroke = item.stroke;
      stroke.obj = this._makeStroke(stroke.points.map(p => new THREE.Vector3(p[0], p[1], p[2])), stroke.color, stroke.width, stroke.radius);
      stroke.obj.visible = layer.visible;
      this._ensureScene();
      this.viewer.impl.addOverlay(this._scene, stroke.obj);
      layer.strokes3d.push(stroke);
      this.viewer.impl.invalidate(false, false, true);
    }
    else {
      const i = layer.strokes3d.indexOf(item.stroke);
      if (i >= 0){ layer.strokes3d.splice(i, 1); }
      this.viewer.impl.removeOverlay(this._scene, item.stroke.obj, true);   // true = dispose geometry
      item.stroke.obj.material.dispose();
      this.viewer.impl.invalidate(false, false, true);
    }
    this._persist();
  }
  /// Clears ALL markup of the active layer - including markup restored from disk, which has no
  /// history entries - and drops the layer's entries from the global history.
  clear(){
    const layer = this._layer();
    if (!layer){ return; }
    this._finishStroke(); this._commitText();
    layer.g.replaceChildren();
    for (const t of layer.strokes3d){
      if (t.obj){ this.viewer.impl.removeOverlay(this._scene, t.obj, true); t.obj.material.dispose(); }
    }
    layer.strokes3d = [];
    this._undo = this._undo.filter(e => e.layer !== layer);
    this.viewer.impl.invalidate(false, false, true);
    this._persist();
  }
}

Autodesk.Viewing.theExtensionManager.registerExtension("Extrabbit.Redline", RedlineExtension);
