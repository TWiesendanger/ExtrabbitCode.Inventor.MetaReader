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
const RL_TOOLS = [
  ["free",    "✎",  "Freehand"],
  ["rect",    "▭",  "Rectangle"],
  ["circle",  "◯",  "Circle"],
  ["arrow",   "↗",  "Arrow"],
  ["text",    "T",       "Text"],
  ["paint3d", "\u{1F58C}", "Paint on the model (experimental) - strokes stick to the surface"],
];

function rlHex(rgb){                                   // "rgb(r, g, b)" -> "#rrggbb" for swatch compare
  const m = /rgb\((\d+),\s*(\d+),\s*(\d+)\)/.exec(rgb);
  return m ? "#" + [m[1], m[2], m[3]].map(n => (+n).toString(16).padStart(2, "0")).join("") : rgb;
}

class RedlineExtension extends Autodesk.Viewing.Extension {
  load(){
    this._active = false;
    this._tool = "free";
    this._color = RL_COLORS[0];
    this._scene = "extrabbit-redline3d";
    this._layers = [];                                 // {id,name,visible,camera,g,strokes3d,undo}
    this._activeId = null;
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
      if (e.key === "Escape"){ this.setActive(false); }
      else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "z"){ e.preventDefault(); this.undo(); }
    }, { signal: this._ac.signal });
    if (this.viewer.toolbar){ this.onToolbarCreated(this.viewer.toolbar); }
    else { this._onTb = () => this.onToolbarCreated(this.viewer.toolbar); this.viewer.addEventListener(Autodesk.Viewing.TOOLBAR_CREATED_EVENT, this._onTb); }
    // Fallback wiring: TOOLBAR_CREATED dispatch is fragile - an exception in ANY earlier listener
    // aborts the rest, and this extension registers late. By GEOMETRY_LOADED the toolbar exists,
    // so wire up then if the event never reached us.
    this._onGeo = () => { if (!this._button && this.viewer.toolbar){ this.onToolbarCreated(this.viewer.toolbar); } };
    this.viewer.addEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, this._onGeo);
    this._loadFromDisk();
    return true;
  }
  unload(){
    this.setActive(false);                             // release the overlay + hide the palette
    this._ac.abort();                                  // svg / input / window listeners
    if (this._onTb){ this.viewer.removeEventListener(Autodesk.Viewing.TOOLBAR_CREATED_EVENT, this._onTb); }
    if (this._onGeo){ this.viewer.removeEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, this._onGeo); }
    if (this._group && this.viewer.toolbar){ this.viewer.toolbar.removeControl(this._group); }
    this._group = this._button = null;
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
      this._button.setToolTip("Redline — draw on the model");
      this._button.onClick = () => this.setActive(!this._active);
      this._group = new Autodesk.Viewing.UI.ControlGroup("extrabbit-redline-group");
      this._group.addControl(this._button);
      toolbar.addControl(this._group);
    } catch (e) { report("redline: toolbar wiring failed: " + (e && e.stack || e)); }
  }
  setActive(on){
    this._active = on;
    if (on){ this._ensureLayer(); }
    this._svg.style.pointerEvents = on ? "auto" : "none";
    this._svg.classList.toggle("drawing", on);
    this._panel.style.display = on ? "flex" : "none";
    this._browser.style.display = on ? "block" : "none";
    if (!on){ this._finishStroke(); this._commitText(); }
    if (this._button){
      const S = Autodesk.Viewing.UI.Button.State;
      this._button.setState(on ? S.ACTIVE : S.INACTIVE);
    }
    report("redline " + (on ? "on" : "off"));
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
                camera: this._cameraState(), g, strokes3d: [], undo: [] };
    this._layers.push(l);
    this._activeId = id;
    this._syncBrowser();
    this._persist();
    return l;
  }
  _activateLayer(id){
    this._finishStroke(); this._commitText();
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
    const head = document.createElement("div");
    head.className = "rl-layers-head";
    const title = document.createElement("span");
    title.textContent = "Layers";
    const add = document.createElement("button");
    add.className = "rl-tool"; add.title = "New layer"; add.textContent = "＋";
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

      rowBtn(l.visible ? "Hide layer" : "Show layer", l.visible ? "\u{1F441}" : "\u{1F648}",
        () => { this._setVisible(l, !l.visible); this._syncBrowser(); this._persist(); });

      const name = document.createElement("span");
      name.className = "rl-layer-name";
      name.textContent = l.name;
      name.title = "Double-click to rename";
      name.addEventListener("dblclick", (e) => { e.stopPropagation(); this._beginRename(l, name); });
      row.appendChild(name);

      rowBtn("Restore this layer's camera perspective", "⌖", () => this._restoreCamera(l));
      rowBtn("Re-save perspective from the current view", "\u{1F4CC}", () => { this._resaveCamera(l); });
      rowBtn("Delete layer (and its markup)", "\u{1F5D1}", () => this._deleteLayer(l.id));

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
                  camera: raw.camera || null, g, strokes3d: [], undo: [] };
      for (const t of (raw.strokes3d || [])){
        if (!t.points || t.points.length < 2){ continue; }
        const pts = t.points.map(p => new THREE.Vector3(p[0], p[1], p[2]));
        const obj = this._makeLine(pts, t.color);
        this._ensureScene();
        this.viewer.impl.addOverlay(this._scene, obj);
        l.strokes3d.push({ color: t.color, points: t.points, obj });
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
  _persist(){
    clearTimeout(this._saveT);
    this._saveT = setTimeout(() => {
      const data = {
        version: 2,
        active: this._activeId,
        layers: this._layers.map(l => ({
          id: l.id, name: l.name, visible: l.visible, camera: l.camera,
          svg: l.g.innerHTML,
          strokes3d: l.strokes3d.map(t => ({ color: t.color, points: t.points })),
        })),
      };
      try { window.chrome.webview.postMessage("redline-save:" + JSON.stringify(data)); }
      catch (e) { /* no host bridge (dev page) */ }
    }, 600);
  }
  // ---------- screenshot export ----------
  _exportShot(){
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
          const dataUrl = cv.toDataURL("image/png");
          try {
            window.chrome.webview.postMessage("redline-shot:" + JSON.stringify({ name: l.name, data: dataUrl }));
            report("redline: screenshot posted (" + Math.round(dataUrl.length / 1024) + " KB)");
          } catch (e) { report("redline: screenshot export needs the app host"); }
        };
        svgImg.onerror = () => report("redline: svg rasterise failed");
        svgImg.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(new XMLSerializer().serializeToString(clone));
      };
      base.src = blobUrl;
    });
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
    this._toolBtns = {};
    for (const [id, glyph, title] of RL_TOOLS){
      this._toolBtns[id] = btn(title, glyph, "rl-tool", () => this._selectTool(id));
    }
    sep();
    this._swatches = RL_COLORS.map(c => {
      const s = btn("Colour " + c, "", "rl-swatch", () => this._selectColor(c));
      s.style.background = c;
      return s;
    });
    sep();
    btn("Export active layer as PNG", "\u{1F4F7}", "rl-tool", () => this._exportShot());
    btn("Undo (Ctrl+Z)", "↩", "rl-tool", () => this.undo());
    btn("Clear this layer", "\u{1F5D1}", "rl-tool", () => this.clear());
    btn("Close (Esc)", "✕", "rl-tool", () => this.setActive(false));
    this._selectTool("free");
    this._selectColor(RL_COLORS[0]);
  }
  _selectTool(id){
    this._tool = id;
    for (const k in this._toolBtns){ this._toolBtns[k].classList.toggle("sel", k === id); }
  }
  _selectColor(c){
    this._color = c;
    this._swatches.forEach(s => s.classList.toggle("sel", s.style.background === c || rlHex(s.style.background) === c));
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
      if (this._strokeTool === "paint3d"){ this._pts3d = []; this._line3d = null; this._add3D(p); }
      else { this._el = this._begin2D(p); layer.g.appendChild(this._el); }
      e.preventDefault();
    }, sig);
    svg.addEventListener("pointermove", (e) => {
      if (!this._active || !this._start || e.pointerId !== this._pointerId){ return; }
      if ((e.buttons & 1) === 0){ this._finishStroke(); return; }   // lost pointerup (focus steal etc.)
      const p = pos(e);
      if (Math.abs(p.x - this._start.x) > 2 || Math.abs(p.y - this._start.y) > 2){ this._moved = true; }
      if (this._strokeTool === "paint3d"){ this._add3D(p); }
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
        layer.undo.push({ kind: "2d", el: this._el });
        this._persist();
      }
      else { this._el.remove(); }                      // a bare click draws nothing visible
      this._el = null;
    }
    this._start = null;
    this._pointerId = undefined;
    this._strokeLayer = null;
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
      if (this._active){ svg.style.pointerEvents = "auto"; }
    };
    window.addEventListener("pointerup", restore, { capture: true, signal: this._ac.signal });
    window.addEventListener("pointercancel", restore, { capture: true, signal: this._ac.signal });
  }
  _shape(tag){
    const el = document.createElementNS(RL_NS, tag);
    el.setAttribute("stroke", this._color);
    el.setAttribute("stroke-width", "3");
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
      layer.undo.push({ kind: "2d", el });
      this._persist();
    }
    this._textPos = null;
  }
  // ---------- 3D pen (experimental) ----------
  _add3D(p){
    const hit = this.viewer.impl.hitTest(p.x, p.y, false);
    if (!hit || !hit.intersectPoint){ return; }
    // lift the point slightly toward the camera so the stroke doesn't z-fight the surface
    const q = hit.intersectPoint.clone();
    const toCam = this.viewer.impl.camera.position.clone().sub(q).normalize();
    const size = this.viewer.model ? this.viewer.model.getBoundingBox().getSize(new THREE.Vector3()).length() : 100;
    q.add(toCam.multiplyScalar(size * 0.004));
    this._pts3d.push(q);
    if (this._pts3d.length >= 2){ this._refresh3D(); }
  }
  _makeLine(points, color){
    const arr = new Float32Array(points.length * 3);
    points.forEach((q, i) => { arr[i * 3] = q.x; arr[i * 3 + 1] = q.y; arr[i * 3 + 2] = q.z; });
    const geom = new THREE.BufferGeometry();
    const attr = new THREE.BufferAttribute(arr, 3);
    if (geom.setAttribute){ geom.setAttribute("position", attr); } else { geom.addAttribute("position", attr); }
    const mat = new THREE.LineBasicMaterial({ color: new THREE.Color(color || this._color), depthTest: true, depthWrite: false });
    return new THREE.Line(geom, mat);
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
    this._line3d = this._makeLine(this._pts3d);
    impl.addOverlay(this._scene, this._line3d);
    impl.invalidate(false, false, true);
  }
  _commit3D(){
    const layer = this._strokeLayer;
    if (this._line3d && layer){
      const stroke = { color: this._color, points: this._pts3d.map(q => [q.x, q.y, q.z]), obj: this._line3d };
      layer.strokes3d.push(stroke);
      layer.undo.push({ kind: "3d", stroke });
      this._persist();
    }
    else if (this._line3d){ this.viewer.impl.removeOverlay(this._scene, this._line3d, true); this._line3d.material.dispose(); }
    else { report("3d stroke discarded (no surface under the cursor)"); }
    this._pts3d = null; this._line3d = null;
  }
  // ---------- undo / clear (per active layer) ----------
  undo(){
    const layer = this._layer();
    if (!layer){ return; }
    const item = layer.undo.pop();
    if (!item){ return; }
    if (item.kind === "2d"){ item.el.remove(); }
    else {
      const i = layer.strokes3d.indexOf(item.stroke);
      if (i >= 0){ layer.strokes3d.splice(i, 1); }
      this.viewer.impl.removeOverlay(this._scene, item.stroke.obj, true);   // true = dispose geometry
      item.stroke.obj.material.dispose();
      this.viewer.impl.invalidate(false, false, true);
    }
    this._persist();
  }
  clear(){
    const layer = this._layer();
    if (!layer){ return; }
    while (layer.undo.length){ this.undo(); }
    this._persist();
  }
}

Autodesk.Viewing.theExtensionManager.registerExtension("Extrabbit.Redline", RedlineExtension);
