# Third-party notices

Inventor MetaReader is licensed under the MIT license (see [LICENSE](LICENSE)).
It uses the third-party components below. **Each component remains governed by its
own license**; nothing in MetaReader's license adds restrictions to them.

## occt-import-js (bundled)

The STEP viewer converts `.stp`/`.step` geometry with **occt-import-js**, the
Emscripten/WebAssembly build of Open CASCADE Technology's import functionality.

- Version: **0.0.23**, used **unmodified** (byte-identical to the npm release)
- License: **GNU Lesser General Public License v2.1** —
  bundled at `Assets/stepviewer/vendor/license_occt_import_js.txt`
- Source code: <https://github.com/kovacsv/occt-import-js> (tag `0.0.23`),
  package: <https://www.npmjs.com/package/occt-import-js>
- Copyright © Viktor Kovacs and contributors

## Open CASCADE Technology (OCCT) (bundled, inside the WASM module)

`occt-import-js.wasm` embeds a compiled subset of **OCCT**, the open-source
CAD kernel that performs the actual STEP reading and triangulation.

- License: **LGPL v2.1** (OCCT upstream applies it with the *Open CASCADE
  exception*, which only grants additional permissions) —
  bundled at `Assets/stepviewer/vendor/license_occt.txt`
- Source code: <https://github.com/Open-Cascade-SAS/OCCT>. The exact OCCT
  sources compiled into the module are pinned by the `occt` git submodule of
  the occt-import-js `0.0.23` tag.
- Copyright © Open Cascade SAS

### LGPL relinking / replacement (LGPL §6)

The LGPL components ship as **separate, dynamically loaded files** — they are
not statically linked into MetaReader's executable:

- `Assets/stepviewer/vendor/occt-import-js.js`
- `Assets/stepviewer/vendor/occt-import-js.wasm`

You may replace them with your own (modified or newer) build of
occt-import-js/OCCT by swapping these files; MetaReader loads whatever is at
those paths. Provenance and integrity hashes are recorded in
[`Assets/stepviewer/vendor/README.md`](src/InventorMeta.App/Assets/stepviewer/vendor/README.md).

## vis-network (bundled)

The reference graph is rendered with **vis-network**.

- Version: **9.1.9** (`Assets/vis/vis-network.min.js`), unmodified
- License: dual **Apache License 2.0** / **MIT** (either may be chosen)
- Source: <https://github.com/visjs/vis-network>
- Copyright © 2011-2017 Almende B.V., © 2017- vis.js contributors

## Autodesk Viewer (loaded from Autodesk's CDN, not bundled)

The 3D view renders models with the **Autodesk Viewer** (`viewer3D.min.js`),
loaded at runtime from `developer.api.autodesk.com`. It is **proprietary
Autodesk software**, not open source, and its use is governed by the
[Autodesk Platform Services terms](https://aps.autodesk.com/terms-of-service).

## NuGet packages

| Package | License |
|---|---|
| CommunityToolkit.Mvvm | MIT |
| Serilog, Serilog.Sinks.File | Apache-2.0 |
| Microsoft.WindowsAppSDK / Windows SDK BuildTools | Microsoft Software License |
