// Extrabbit.Hotkeys - central registry for the viewer's keyboard shortcuts plus a small dialog
// to view and rebind them. Bindings persist in localStorage (WebView2 keeps it in the app's user
// data folder, so they survive restarts like every other viewer setting). Loaded BEFORE
// coloring.js / redline.js, which read bindings through the window.Hotkeys global.
"use strict";

const HK_STORE = "extrabbit-hotkeys";
const HK_ACTIONS = [
  { id: "redline",  label: "Toggle redlining",            def: "e" },
  { id: "coloring", label: "Toggle body coloring",        def: "c" },
  { id: "free",     label: "Freehand tool (redlining)",   def: "f" },
  { id: "paint3d",  label: "Paint on model (redlining)",  def: "p" },
  { id: "orbit",    label: "Orbit / navigate (redlining)", def: "o" },
];
// conventions shown in the dialog but not rebindable
const HK_FIXED = [
  { label: "Undo (redlining)",  key: "Ctrl+Z" },
  { label: "Close redlining",   key: "Esc" },
];

function hkLoad(){
  try { return JSON.parse(localStorage.getItem(HK_STORE)) || {}; } catch (e) { return {}; }
}

window.Hotkeys = {
  get(id){
    const a = HK_ACTIONS.find(x => x.id === id);
    const k = hkLoad()[id] || (a && a.def) || "";
    return String(k).toLowerCase();
  },
  set(id, key){
    const o = hkLoad();
    o[id] = String(key).toLowerCase();
    try { localStorage.setItem(HK_STORE, JSON.stringify(o)); } catch (e) { /* private mode */ }
    document.dispatchEvent(new CustomEvent("hotkeys-changed"));
  },
  reset(){
    try { localStorage.removeItem(HK_STORE); } catch (e) { /* private mode */ }
    document.dispatchEvent(new CustomEvent("hotkeys-changed"));
  },
  // true when the keydown event e is this action's binding (plain press, no modifiers)
  matches(id, e){
    return !e.ctrlKey && !e.metaKey && !e.altKey && !!e.key && e.key.toLowerCase() === this.get(id);
  },
};

// One shared toolbar group for every Extrabbit button (coloring, redline, hotkeys), so they sit
// together in a single box instead of three separate one-button groups. First caller creates it.
// Defined here because hotkeys.js loads before the other extension scripts.
window.extrabbitToolbarGroup = function(toolbar){
  let g = toolbar.getControl("extrabbit-group");
  if (!g){
    g = new Autodesk.Viewing.UI.ControlGroup("extrabbit-group");
    toolbar.addControl(g);
  }
  return g;
};
// detach a button and drop the shared group once the last button is gone
window.extrabbitToolbarRemove = function(toolbar, button){
  const g = toolbar && toolbar.getControl("extrabbit-group");
  if (!g || !button){ return; }
  g.removeControl(button);
  if (g.getNumberOfControls() === 0){ toolbar.removeControl(g); }
};

class HotkeysExtension extends Autodesk.Viewing.Extension {
  load(){
    this._dlg = null;
    if (this.viewer.toolbar){ this.onToolbarCreated(this.viewer.toolbar); }
    else { this._onTb = () => this.onToolbarCreated(this.viewer.toolbar); this.viewer.addEventListener(Autodesk.Viewing.TOOLBAR_CREATED_EVENT, this._onTb); }
    // same fallback as the redline extension: TOOLBAR_CREATED dispatch can be aborted by an
    // exception in an earlier listener, but by GEOMETRY_LOADED the toolbar reliably exists
    this._onGeo = () => { if (!this._button && this.viewer.toolbar){ this.onToolbarCreated(this.viewer.toolbar); } };
    this.viewer.addEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, this._onGeo);
    return true;
  }
  unload(){
    if (this._onTb){ this.viewer.removeEventListener(Autodesk.Viewing.TOOLBAR_CREATED_EVENT, this._onTb); }
    if (this._onGeo){ this.viewer.removeEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, this._onGeo); }
    extrabbitToolbarRemove(this.viewer.toolbar, this._button);
    if (this._dlg){ this._dlg.remove(); this._dlg = null; }
    this._button = null;
    return true;
  }
  onToolbarCreated(toolbar){
    if (this._button){ return; }
    try {
      this._button = new Autodesk.Viewing.UI.Button("extrabbit-hotkeys-btn");
      this._button.setToolTip("Keyboard shortcuts");
      this._button.onClick = () => this._toggle();
      extrabbitToolbarGroup(toolbar).addControl(this._button);
    } catch (e) { report("hotkeys: toolbar wiring failed: " + (e && e.stack || e)); }
  }
  _toggle(){
    if (!this._dlg){ this._build(); }
    const open = this._dlg.style.display !== "block";
    if (open){ this._render(); }
    this._dlg.style.display = open ? "block" : "none";
  }
  _build(){
    this._dlg = Object.assign(document.createElement("div"), { id: "hotkeysDlg" });
    const host = (this.viewer.canvas && this.viewer.canvas.closest(".adsk-viewing-viewer")) || document.body;
    host.appendChild(this._dlg);
    document.addEventListener("hotkeys-changed", () => {
      if (this._dlg.style.display === "block"){ this._render(); }
    });
  }
  _render(){
    const dlg = this._dlg;
    dlg.replaceChildren();
    const head = Object.assign(document.createElement("div"), { className: "hk-head" });
    head.appendChild(Object.assign(document.createElement("span"), { textContent: "Keyboard shortcuts" }));
    const close = Object.assign(document.createElement("button"), { className: "hk-x", textContent: "✕", title: "Close" });
    close.addEventListener("click", () => { dlg.style.display = "none"; });
    head.appendChild(close);
    dlg.appendChild(head);
    for (const a of HK_ACTIONS){
      const row = Object.assign(document.createElement("div"), { className: "hk-row" });
      row.appendChild(Object.assign(document.createElement("span"), { className: "hk-label", textContent: a.label }));
      const chip = Object.assign(document.createElement("button"), { className: "hk-key", textContent: Hotkeys.get(a.id).toUpperCase() });
      chip.title = "Click, then press the new key";
      chip.addEventListener("click", () => this._rebind(a.id, chip));
      row.appendChild(chip);
      dlg.appendChild(row);
    }
    for (const f of HK_FIXED){
      const row = Object.assign(document.createElement("div"), { className: "hk-row" });
      row.appendChild(Object.assign(document.createElement("span"), { className: "hk-label", textContent: f.label }));
      const chip = Object.assign(document.createElement("span"), { className: "hk-key hk-fixed", textContent: f.key });
      chip.title = "Fixed shortcut";
      row.appendChild(chip);
      dlg.appendChild(row);
    }
    const foot = Object.assign(document.createElement("div"), { className: "hk-foot" });
    const reset = Object.assign(document.createElement("button"), { className: "hk-reset", textContent: "Reset to defaults" });
    reset.addEventListener("click", () => Hotkeys.reset());
    foot.appendChild(reset);
    dlg.appendChild(foot);
  }
  // Click a chip, press the new key. Letters and digits only; Esc cancels; a key already bound
  // to another action is refused with a shake so nothing can end up double-bound.
  _rebind(id, chip){
    if (this._capturing){ return; }
    this._capturing = true;
    const old = chip.textContent;
    chip.textContent = "…";
    chip.classList.add("hk-listen");
    const done = () => {
      this._capturing = false;
      chip.classList.remove("hk-listen");
      window.removeEventListener("keydown", onKey, true);
    };
    const onKey = (e) => {
      // swallow the press completely so the current bindings don't fire mid-rebind
      e.preventDefault(); e.stopImmediatePropagation();
      if (e.key === "Escape"){ chip.textContent = old; done(); return; }
      const k = (e.key || "").toLowerCase();
      if (!/^[a-z0-9]$/.test(k)){ return; }                      // wait for a usable key
      const clash = HK_ACTIONS.some(a => a.id !== id && Hotkeys.get(a.id) === k);
      if (clash){
        chip.classList.remove("hk-shake"); void chip.offsetWidth; // restart the animation
        chip.classList.add("hk-shake");
        return;
      }
      done();
      Hotkeys.set(id, k);                                        // re-renders via hotkeys-changed
    };
    window.addEventListener("keydown", onKey, true);
  }
}

Autodesk.Viewing.theExtensionManager.registerExtension("Extrabbit.Hotkeys", HotkeysExtension);
