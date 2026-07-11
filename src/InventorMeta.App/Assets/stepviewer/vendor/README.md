# Vendored: occt-import-js (STEP importer)

WASM build of Open CASCADE Technology's STEP import, used by the STEP 3D viewer
(`../step-viewer.html`). Both OCCT and occt-import-js are licensed **LGPL-2.1**
(license texts alongside these files); see also `THIRD-PARTY-NOTICES.md` at the
repo root.

## Provenance

Taken **unmodified** from the npm release (files byte-identical to the package
`dist/` contents):

| File | Version | SHA-256 |
|---|---|---|
| `occt-import-js.js`   | 0.0.23 | `3fb44ce11d00611f9b3f3c5775d520ebab48930c1f08279b7b1316f05f0d3379` |
| `occt-import-js.wasm` | 0.0.23 | `33391fc9d94ea5c869a6718488bf0a9a464222bac9bdc764dfe1690cef281952` |

- Upstream: <https://github.com/kovacsv/occt-import-js> (tag `0.0.23`)
- npm: <https://www.npmjs.com/package/occt-import-js>
- The exact OCCT sources are pinned by the `occt` git submodule of that tag.

## Updating / replacing (LGPL §6)

The module is loaded dynamically at runtime — it is not linked into the app
binary — so it can be replaced without rebuilding MetaReader:

1. `npm pack occt-import-js@<version>` (or build from source upstream).
2. Replace `occt-import-js.js` + `occt-import-js.wasm` here with the new `dist/` files.
3. Update the hashes/version above and in `THIRD-PARTY-NOTICES.md`.

Keep `license_occt.txt` and `license_occt_import_js.txt` next to the binaries —
they are packaged with the app and linked from the About dialog.
