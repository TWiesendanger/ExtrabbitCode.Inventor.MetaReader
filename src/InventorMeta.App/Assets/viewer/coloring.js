// Extrabbit.Coloring - give every body its own colour so parts of an assembly are easy to tell
// apart. Golden-angle hue spacing keeps neighbouring bodies separated; soft, CAD-ish saturation
// (low S, high V) keeps the palette muted rather than neon. Loaded by viewer.html; uses the
// page-global report() bridge.
"use strict";

function hsvToRgb(h, s, v){
  let r = v, g = v, b = v;
  if (s > 0){
    const hh = h * 6, i = Math.floor(hh) % 6, f = hh - Math.floor(hh);
    const p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
    if (i === 0){ r = v; g = t; b = p; }
    else if (i === 1){ r = q; g = v; b = p; }
    else if (i === 2){ r = p; g = v; b = t; }
    else if (i === 3){ r = p; g = q; b = v; }
    else if (i === 4){ r = t; g = p; b = v; }
    else { r = v; g = p; b = q; }
  }
  return [r, g, b];
}

function bodyColor(i){
  const rgb = hsvToRgb((i * 0.6180339887) % 1, 0.56, 0.80);   // golden-ratio hue, soft CAD saturation
  // w < 1 blends the tint with the surface's own shading so it stays muted, not neon - theming
  // paints over an already-lit surface, so a full-intensity tint reads much brighter than a baked colour.
  return new THREE.Vector4(rgb[0], rgb[1], rgb[2], 0.86);
}

class ColoringExtension extends Autodesk.Viewing.Extension {
  load(){
    this._on = false;
    // Coloring can run before the model is complete (the toolbar exists while fragments are
    // still streaming in, and the object tree often arrives after the geometry - always for the
    // built-in converter's raw SVF loads). So on BOTH "now it's complete" signals, re-apply if
    // already on (an early pass only caught the bodies loaded so far) or apply the initial mode.
    this._refresh = () => {
      if (this._on){ this._applyColors(); return; }
      this._maybeInitial();                                   // may switch coloring on
      if (!this._on && this.options && this.options.bakedPalette){ this._applyNeutral(); }
    };
    this.viewer.addEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, this._refresh);
    this.viewer.addEventListener(Autodesk.Viewing.OBJECT_TREE_CREATED_EVENT, this._refresh);
    this._disposeWire = extrabbitWireToolbar(this.viewer, (tb) => this.onToolbarCreated(tb));
    return true;
  }
  unload(){
    if (this._refresh){
      this.viewer.removeEventListener(Autodesk.Viewing.GEOMETRY_LOADED_EVENT, this._refresh);
      this.viewer.removeEventListener(Autodesk.Viewing.OBJECT_TREE_CREATED_EVENT, this._refresh);
    }
    if (this._disposeWire){ this._disposeWire(); }
    if (this._onHk){ document.removeEventListener("hotkeys-changed", this._onHk); }
    extrabbitToolbarRemove(this.viewer.toolbar, this._button);
    this._button = null;
    return true;
  }
  _tooltip(){
    let suffix = " (C)";
    try { suffix = window.Hotkeys.suffix("coloring"); } catch (e) { /* registry not loaded */ }
    return "Body coloring: give every body its own colour" + suffix;
  }
  onToolbarCreated(toolbar){
    if (this._button) { return; }                             // guard against a double call
    this._button = new Autodesk.Viewing.UI.Button("extrabbit-coloring-btn");
    this._button.setToolTip(this._tooltip());
    this._onHk = () => { if (this._button){ this._button.setToolTip(this._tooltip()); } };
    document.addEventListener("hotkeys-changed", this._onHk);
    this._button.onClick = () => this.setEnabled(!this._on);
    extrabbitToolbarGroup(toolbar).addControl(this._button);  // shared Extrabbit button group
    this._sync();
    this._maybeInitial();
  }
  _maybeInitial(){                                            // apply the app's chosen starting mode, once the model is ready
    if (this.options && this.options.initial && !this._on && this.viewer.model){ this.setEnabled(true); }
  }
  toggle(){ this.setEnabled(!this._on); }                     // public entry point for the C hotkey
  setEnabled(on){
    this._on = on;
    // The built-in converter BAKES a palette colour per component into the SVF materials, so just
    // clearing the theming would still show a multicoloured model. For those, "off" means a
    // neutral grey theme over every body - the converter carries no real appearances to reveal.
    if (on){ this._applyColors(); }
    else if (this.options && this.options.bakedPalette){ this._applyNeutral(); }
    else { this.viewer.clearThemingColors(this.viewer.model); }
    this._sync();
    report("coloring " + (on ? "on" : "off"));
  }
  _sync(){
    if (!this._button) { return; }
    const S = Autodesk.Viewing.UI.Button.State;
    this._button.setState(this._on ? S.ACTIVE : S.INACTIVE);
  }
  // Every body's dbId: the UNION of instance-tree leaves and the dbIds the fragments actually
  // reference. Both sources are needed - a raw-SVF load may never grow a tree, and the built-in
  // converter's single-body parts link their fragment to the ROOT node (dbId 1) while the tree's
  // leaf is dbId 2, so theming only the leaves would leave the mesh untouched. The fragment
  // lookup tries the API first, then the raw fragId2dbId array leaner model representations use.
  _collectIds(model){
    const seen = new Set();
    const tree = model.getInstanceTree && model.getInstanceTree();
    if (tree){
      tree.enumNodeChildren(tree.getRootId(), (dbId) => {
        if (tree.getChildCount(dbId) === 0){ seen.add(dbId); }   // a leaf node is one body
      }, true);
    }
    try {
      const fl = model.getFragmentList();
      const map = fl && fl.fragments && fl.fragments.fragId2dbId;
      const n = (fl && fl.getCount) ? fl.getCount() : (map ? map.length : 0);
      for (let f = 0; f < n; f++) {
        const d = (fl && fl.getDbIds) ? fl.getDbIds(f) : (map ? map[f] : 0);
        (Array.isArray(d) ? d : [d]).forEach(x => { if (x > 0) seen.add(x); });
      }
    } catch (e) { report("fragment dbId scan: " + e); }
    return { ids: [...seen].sort((a, b) => a - b), fromTree: !!tree };
  }
  // One theming pass, parameterized by the per-body colour and a log verb. It swallows its own
  // errors: it can run at toolbar-creation time against a still-streaming model where
  // setThemingColor may throw - and an exception inside a TOOLBAR_CREATED listener aborts LMV's
  // dispatch, silently killing every listener after this one (that once cost the redline button its
  // toolbar wiring). The GEOMETRY_LOADED refresh re-applies later, so failing quietly loses nothing.
  _applyTheming(colorOf, verb){
    try {
      const model = this.viewer.model;
      if (!model){ return; }
      const { ids, fromTree } = this._collectIds(model);
      try { this.viewer.clearThemingColors(model); } catch (e) { /* fresh model */ }
      ids.forEach((dbId, i) => this.viewer.setThemingColor(dbId, colorOf(i), model, true));
      report(verb + " " + ids.length + " bodies" + (fromTree ? "" : " (from fragments)"));
    } catch (e) { report("coloring " + verb + " failed (will retry on geometry-loaded): " + e); }
  }
  _applyColors(){ this._applyTheming(bodyColor, "colored"); }
  _applyNeutral(){
    const grey = new THREE.Vector4(0.72, 0.72, 0.72, 1.0);
    this._applyTheming(() => grey, "neutralized");
  }
}

Autodesk.Viewing.theExtensionManager.registerExtension("Extrabbit.Coloring", ColoringExtension);
