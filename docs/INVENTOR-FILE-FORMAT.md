# Autodesk Inventor File Format — Reverse-Engineering Notes

How `.ipt` (part), `.iam` (assembly), `.idw` (drawing) and `.ipn` (presentation)
files are structured, and exactly what you can read out of them **without Autodesk
Inventor installed**.

These notes were produced by dissecting three real files
(`SamplePart.ipt`, `Assembly1.iam`, `SamplePart.idw`, all written by Inventor 2027)
byte-by-byte with the tooling in this repo. Findings are split into
**Documented / verified** vs **Inferred / opaque** so you know what to trust.

---

## 0. TL;DR — what is realistically readable

| Data | Readable without Inventor? | Where |
|------|----------------------------|-------|
| Document type (part/assembly/drawing) | ✅ Yes, trivially | Root storage CLSID |
| All iProperties (Part Number, Author, Material, Project, Status, Description, custom props…) | ✅ Yes, fully | OLE Property Sets |
| Preview thumbnail (512×512 PNG) | ✅ Yes, fully | Property Set, VT_CF blob |
| Cached mass / volume / surface area / density | ⚠️ Yes, **but possibly stale** | Design Tracking PIDs 58–61 |
| Referenced files (assembly components, drawing's model) | ✅ Yes (paths) | `UFRxDoc` stream |
| Model States / representations | ✅ Yes (names) | `UFRxDoc` + `MemberDocs` |
| **iProperties per model state** (without switching states!) | ✅ Yes, fully | each `MemberDocs/<state>` sub-doc — see §7 |
| Originating template, save/version provenance | ✅ Yes | `UFRxDoc` |
| Parametric feature tree, sketches, parameters | ❌ Opaque | `RSeStorage` (proprietary B-Rep DB) |
| Exact solid geometry (B-Rep) | ❌ Opaque | `RSeStorage` segment streams |

The **green rows are the high-value, stable target** and are what the tooling in
this repo extracts. The geometry/feature DB (`RSeStorage`) is a proprietary
serialization of Inventor's modeling kernel and is not practically decodable; if you
need geometry, export to STEP/SAT/IGES or use the Inventor API / Apprentice Server.

---

## 1. The container: OLE Compound File Binary (CFB)

Every Inventor document is a **Microsoft Compound File Binary** file (a.k.a. OLE2
Structured Storage / "a FAT filesystem inside one file"). This is the same container
used by legacy `.doc`/`.xls`/`.msi`.

* **Magic bytes** (offset 0): `D0 CF 11 E0 A1 B1 1A E1`
* All three sample files: **CFB major version 3**, **512-byte sectors**,
  64-byte mini-sectors, 4096-byte mini-stream cutoff.

A CFB file is a tree of **storages** (≈ directories) and **streams** (≈ files).
The format is fully documented by Microsoft as **[MS-CFB]**. Parsing it requires:

1. The 512-byte header → sector size, FAT/DIFAT/miniFAT locations, first directory sector.
2. The **FAT** (sector allocation table) and **DIFAT** (FAT-of-FATs) to follow sector chains.
3. The **directory stream**: 128-byte entries forming a red-black tree per storage.
4. The **mini stream** + **miniFAT** for streams smaller than 4096 bytes.

`src/InventorMeta/Cfb.cs` is a complete, dependency-free implementation.

> Because it's plain CFB, generic tools also work: 7-Zip can open these files, and
> Windows' own `StgOpenStorage`/`IStorage` COM API enumerates them. The custom parser
> exists so we can also *interpret* the stream contents, not just list them.

---

## 2. Document type — the root CLSID

The CFB **root storage's CLSID** identifies the document type unambiguously:

| CLSID | Document |
|-------|----------|
| `4D29B490-49B2-11D0-93C3-7E0706000000` | Part — `.ipt` |
| `E60F81E1-49B3-11D0-93C3-7E0706000000` | Assembly — `.iam` |
| `BBF9FDF1-52DC-11D0-8C04-0800090BE8EC` | Drawing — `.idw` |
| `76283A80-50DD-11D3-A7E3-00C04F79D7BC` | Presentation — `.ipn` |

So you can detect the real type from the bytes even if the extension is wrong.

---

## 3. Storage tree map

Top level of a typical part (`.ipt`), annotated:

```
/                                  root  (CLSID = part)
├─ \x01<scrambled name>            ─┐  seven OLE Property Sets  (see §4)
│  …seven of these…                 │  names are obfuscated; identify by FMTID
├─ \x05<scrambled> (77 KB)         ─┘  one of them holds the thumbnail
├─ Protein               (4 B)     material/appearance engine marker
├─ UFRxDoc            (~18 KB)     document refs, model states, version  (see §5)
├─ RSeStorage                      "Robust Storage Engine" — the model DB (see §6)
│  ├─ Bxxxxxxxxxxxxxxxxxxxxxxxxx   data segment streams (paired with M…)
│  ├─ Mxxxxxxxxxxxxxxxxxxxxxxxxx   segment metadata/maps
│  ├─ RSeDbRevisionInfo           per-segment revision/version table
│  ├─ RSeSegInfo                  segment directory
│  ├─ RSeDb (under V1/V2)         database root
│  ├─ RSeEmbeddings/              embedded objects
│  ├─ RefdFiles/                  cached copies of referenced docs (esp. in .idw)
│  └─ V1/ V2/ Templates/          versioned sub-storages
└─ MemberDocs/                     embedded sub-documents = Model States (see §7)
   └─ x<scrambled>/                each is a *complete* nested document
```

Assemblies add `CacheGraphics/` (cached display meshes: `OGSCache`, `LWUFRx`).
Drawings put large cached referenced-model data under `RSeStorage/RefdFiles/RefdFile_*`.

> **Why the scrambled stream names?** Inventor hashes the names of the property-set
> streams (e.g. `Zrxrt4arFafyu34gYa3l3ohgHg`). The leading byte is a control char
> (`\x01`/`\x05`, the classic OLE property-set prefix). **Don't rely on the names** —
> identify each set by the FMTID inside it.

---

## 4. iProperties = standard OLE Property Sets  ⭐ the main prize

Every iProperty Inventor shows in *File → iProperties* is stored using the **standard
Microsoft OLE Property Set** format (**[MS-OLEPS]**, the same mechanism as
`\x05SummaryInformation` in Office files). A stream is a property set when it starts
with `FE FF`. CodePage is **1200 (UTF-16LE)**.

Identify each set by its **section FMTID**:

| FMTID | Set | Contents |
|-------|-----|----------|
| `F29F85E0-4FF9-1068-AB91-08002B27B3D9` | **Summary Information** (MS standard) | Title, Subject, Author, Keywords, Comments, Revision Number |
| `D5CDD502-2E9C-101B-9397-08002B2CF9AE` | Document Summary Information (MS standard) | Category, Manager, Company |
| `D5CDD505-2E9C-101B-9397-08002B2CF9AE` | User-Defined Properties (MS standard) | custom props |
| `32853F0F-3444-11D1-9E93-0060B03C1CA6` | **Design Tracking Properties** ⭐ | the bulk of Inventor's iProperties (table below) |
| `3D38DE39-0588-4C14-BB37-18F4D5DD31C7` | Inventor Summary Information | **Thumbnail** (PID 17), Author |
| `9929ADB8-6407-413E-B3DC-CB9AD2F564B7` | Inventor User Defined Properties | custom iProperties (via dictionary) |
| `8CF58000-DA66-4AE6-8FF0-7B58406FB049` | Inventor Document Summary Information | doc-level summary |
| `D861FB30-3136-11D1-9E92-0060B03C1CA6` | Design Tracking Control *(internal)* | save/version bookkeeping |
| `BB586990-AF3E-11D3-95A9-00A0C9B6E37A` | Private Model Information *(internal)* | cached model/appearance info |

### 4.1 Design Tracking Properties — PID → name

Authoritative: these PIDs come from the Inventor API enum
`PropertiesForDesignTrackingPropertiesEnum`, where the enum integer **is** the OLE PID.
Verified against observed file data.

| PID | iProperty | PID | iProperty |
|----:|-----------|----:|-----------|
| 4 | Creation Date | 41 | **Designer** |
| 5 | **Part Number** | 42 | Engineer |
| 7 | Project | 43 | Authority |
| 9 | Cost Center | 44 | Parameterized Template (bool) |
| 10 | Checked By | 46 | External Property Revision Id (CLSID) |
| 11 | Date Checked | 47 | Standard Revision |
| 12 | Engr Approved By | 48 | Manufacturer |
| 13 | Engr Date Approved | 49 | Standards Organization |
| 17 | User Status | 50 | Language |
| 20 | **Material** (name) | 55 | Stock Number |
| 21 | Part Property Revision Id (CLSID) | 56 | Categories |
| 23 | Catalog Web Link | 57 | Weld Material |
| 28 | Part Icon | 58 | **Mass** |
| 29 | **Description** | 59 | **Surface Area** |
| 30 | Vendor | 60 | **Volume** |
| 31 | Document SubType (CLSID) | 61 | **Density** |
| 32 | Document SubType Name | 62 | **Valid Mass Props** (flag) |
| 33 | Proxy Refresh Date | 63–65 | Flat-pattern extents (sheet-metal) |
| 34 | Mfg Approved By | 66 | Sheet Metal Rule |
| 35 | Date Mfg Approved | 67 | **Last Updated With** (app/build) |
| 36 | Cost | 71 | Material Identifier (library GUID/path) |
| 37 | Standard | 72 | Appearance |
| 40 | **Design Status** (1=WIP, 2=Pending, 3=Released) | 73 | Flat Pattern Defer Update |

Example values pulled from `SamplePart.ipt`:
`Part Number = "SamplePart"`, `Designer = "tobia"`, `Material = "Generisch"`,
`Design Status = Work In Progress`, `Last Updated With = "2027 (Build 310192060, 192F)"`,
`Material Identifier = "…\InventorMaterialLibrary.adsklib#1:Generic#MaterialInv_072"`.

### 4.2 Summary Information — PID → name (MS standard)

| PID | Name | PID | Name |
|----:|------|----:|------|
| 2 | Title | 9 | Revision Number |
| 3 | Subject | 12 | Creation Time |
| 4 | Author | 13 | Last Saved Time |
| 5 | Keywords | 17 | Thumbnail |
| 6 | Comments | 18 | Application Name |
| 8 | Last Saved By | | |

### 4.3 Mass / physical properties — important caveat

Mass (58), Surface Area (59), Volume (60), Density (61) are stored **as cached values**
in Design Tracking Properties — readable directly. **But they may be stale or default:**
Inventor only writes correct numbers after a mass update. **PID 62 "Valid Mass Props"
is the validity gate** — check it before trusting 58–61. In the sample file Density
reads `1` (default) and Valid Mass Props is partial, i.e. mass had not been computed.
**Bounding box is *not* stored** as an iProperty — it is computed on demand from
geometry via the API and never persisted in a property set.

### 4.4 Property-set binary layout (for implementers)

```
Header:  FE FF | version(2) | systemId(4) | CLSID(16) | cSections(4)
         then cSections × { FMTID(16) | offsetToSection(4) }
Section: cb(4) | cProperties(4) | cProperties × { propID(4) | offset(4) }
         then the typed values.
Special property IDs: 0 = dictionary (propID→name, for custom props),
                      1 = codepage (1200 here), 0x80000000 = behaviour flags.
Values are VT-typed: VT_LPWSTR(31), VT_FILETIME(64), VT_R8(5), VT_I4(3),
                     VT_BOOL(11), VT_CLSID(72), VT_CF(71, clipboard/thumbnail)…
```

`src/InventorMeta/OlePropertySet.cs` implements this.

---

## 5. `UFRxDoc` — references, model states, provenance

`UFRxDoc` is an Inventor-proprietary binary stream, but the useful parts are plain
UTF-16 strings and reliably scrapeable:

* **Referenced documents** — full paths of assembly components / the drawing's model,
  e.g. the assembly lists `C:\…\SamplePart.ipt`. (Trailing separator bytes mean you
  should match the path *non-anchored*, not to end-of-string.)
* **Originating template** — a watermark line: `Document <path> was created using…`.
* **Version provenance** — labelled lines:
  `File Schema:`, `Software Schema:`, `Saved From:`, `Saved On:`.
* **Model State names** — `Model State1`, `Model State11`, … plus representation names
  (DesignView, view orientations like `Isometrisch`/`Vorne`/`Rechts`, `___LODFactoryRep`).

> Note: the schema/`Saved On` strings can carry the *template's* origin (e.g.
> "11.0 Internal / Feb 2006"); the authoritative writing application is Design Tracking
> **PID 67 Last Updated With** (`"2027 (Build …)"`).

---

## 6. `RSeStorage` — the model database (mostly opaque)

`RSe` = **Robust Storage Engine**, Inventor's transactional object database that holds
the actual parametric model: feature tree, sketches, constraints, parameters, and the
B-Rep solid geometry. Structure we can see:

* Streams come in **`B…`/`M…` pairs** — a data **segment** and its metadata/map.
  Each scrambled suffix is a segment id (consistent between B and M).
* `RSeSegInfo` — directory of segments.
* `RSeDbRevisionInfo` — per-segment revision/version table (supports Inventor's
  undo/rollback and partial loading).
* `RSeDb` (under `V1/`, `V2/`) — database roots; the `V1`/`V2` storages are
  versioned snapshots.
* `RSeEmbeddings/` — embedded objects; `RefdFiles/` — cached copies of referenced docs
  (in drawings, `RefdFile_1` can be > 1 MB: the cached model graphics).

The **segment payloads are a proprietary serialization of Inventor's modeling kernel**
(custom binary, version-specific, not self-describing). Decoding them to recover
features/geometry is not practical and not stable across releases. **This is the wall**:
for geometry, use a neutral export or the Inventor/Apprentice API instead.

---

## 7. `MemberDocs` — Model States (and iPart/iAssembly members)  ⭐ per-state properties

`MemberDocs/` contains **complete nested documents** — each child storage is itself a
full Inventor document (own property sets, own `RSeStorage`). These correspond to
**Model States** (and historically Levels of Detail / iPart/iAssembly members).

This is the basis of a killer capability: **you can read each model state's iProperties
without switching states in Inventor.** Each member storage holds its own full set of
property sets, so the per-state values (Part Number, Material, mass, custom props,
suppression-driven differences, etc.) are all sitting in the file at once.

How the reader does it:

1. **Enumerate member storages** — every `Type=storage` directly under `/MemberDocs/`.
2. **Map storage → state name** from `UFRxDoc`: each member-storage id sits next to its
   `Model State<n>` name in the token stream. The order varies by document
   (*name before id* in parts, *id before name* in assemblies), so the reader searches
   outward from each storage id for the nearest `Model State<n>` token. *(Heuristic:
   the single default state of an assembly can show a trailing-digit artifact, e.g.
   `Model State11`, because Inventor packs a count byte right after the name with no
   separator. Multi-state parts resolve cleanly, e.g. `Model State1`, `Model State2`.)*
3. **Parse each member's property sets** with the same OLE-property-set code as the
   primary document, producing a full property list + key-iProperty summary per state.
4. **Identify the active state** — the top-level document *is* the active state's data.
   The reader matches each member against the primary using a **stable signature** that
   excludes per-save volatile fields (revision-id GUIDs, save timestamps, the Design
   Tracking Control counters); the member that matches is flagged active.

### 7.1 The `[Primary]` state is the top-level document

A multi-state file has N states but only **N−1 member storages** under `/MemberDocs/`.
The missing one is the **active / `[Primary]`** state, whose data is the **top-level
document itself** (the factory). So the reader presents the top-level doc as the first
state (named from the bracketed `[Primary]` token in `UFRxDoc`) and the member storages
as the rest.

### 7.2 How an overridden custom property is stored (VT_ARRAY `0x200C`)

A custom iProperty that is **given a different value in a model state** is *not* stored
as a plain value in that member. Instead the member's User Defined property set stores it
as a **`VT_ARRAY`-flagged variant (type `0x200C`)** that bundles, in order:

```
<type 0x200C> <16-byte header> <inner variant: the state's value> <inner variant: the base value>
```

Each inner variant is `{ DWORD vt, value }` (e.g. `vt=8 VT_BSTR` → `DWORD byteLen` + UTF-16).
The reader returns the **first** inner value as the property's value for that state. In
the unchanged `[Primary]`/top-level document the same property is a plain `VT_LPWSTR`.

Worked example — custom property **`ThisIsATest`** in `SamplePart.ipt`:

| State | `ThisIsATest` | stored as |
|-------|---------------|-----------|
| `[Primary]` (active) | `Primary` | plain `VT_LPWSTR` in the top-level doc |
| `Model State1` | `1` | `0x200C` array `["1", "Primary"]` in member |
| `Model State2` | `2` | `0x200C` array `["2", "Primary"]` in member |

`invmeta states <file>` (or the viewer's **Model States** tab) prints exactly this,
separating user-facing differences (like `ThisIsATest`) from internal/volatile ones
(per-state revision-id GUIDs, save timestamps, Design Tracking Control counters).

### 7.3 Limitation: member caches can be incomplete (`(not cached)`)

The member-doc property sets are a **cache** that Inventor populates when each model
state is *computed*. Empirically (verified): editing a property that a member cache
already contains propagates to that cache, but **adding a new custom property does not
backfill the member caches** — its per-state values go to the active document and the
proprietary `RSeStorage` model DB, and only reach the readable member caches once each
state is recomputed (activated) and the file re-saved. Until then those values exist in
the file **only inside the opaque RSe B-segment streams** (header `9E C2 2B A4…`,
Inventor's own serialization — not decodable here), so the reader cannot recover them
and shows **`(not cached)`** for that state. This is a property-set-cache limit, not a
parser bug.

**The clean fix (Inventor 2027+): "Generate All Model States on Save".** Right-click the
**Model States** node in the browser and enable **Generate All Model States on Save**.
With it on, every save recomputes and re-caches *all* member states, so the file is
always complete and this reader recovers every per-state value (text, bool, number, …)
with **no manual state-switching at all**. Verified: a member's User Defined set grew
from 5 → 9 properties and all per-state values resolve correctly.

Without that option, the member caches only update for the states you actually
*recompute*. Workaround on older files / when the option is off: activate each model
state once (double-click it in the browser so it rebuilds) and save — a plain Save while
the primary state is active is **not** enough, the non-active states must be recomputed.
Whatever is uncached shows as `(not cached)` rather than a wrong value.

---

## 8. The thumbnail

The 512×512 preview is a property in the **Inventor Summary Information** set
(FMTID `3D38DE39-…`, **PID 17**, type **VT_CF**). The VT_CF blob is:
`format-tag(4) | small Inventor preamble | <image bytes>`. In the 2027 files the image
bytes are a **PNG** (older Inventor versions used a Windows **DIB/BMP** — the reader
detects PNG/BMP/DIB by magic and wraps a bare DIB in a BMP header). Strip the preamble
(scan the first ~64 bytes for the image magic) and you have a directly viewable image.

---

## 9. What you can build on this

* **Reliable & forward-stable:** type detection, all iProperties, custom properties,
  thumbnail, references, model-state names, version/provenance. These use *documented*
  Microsoft container/property-set formats + a stable Inventor PID map, so they keep
  working across Inventor versions.
* **Don't attempt:** parsing `RSeStorage` segment payloads for geometry/features.

---

## 10. Sources

* **[MS-CFB]** Compound File Binary File Format — Microsoft Open Specifications.
* **[MS-OLEPS]** Object Linking and Embedding Property Set Data Structures — Microsoft.
* Inventor API: `PropertiesForDesignTrackingPropertiesEnum`,
  `PropertiesForSummaryInformationEnum`, `DocumentProperties` overview
  (help.autodesk.com → Inventor API).
* `PropertySetFormatID` / Inventor internal property names — see the open-source
  `Inventor.InternalNames` project (string constants; no PIDs).
* The two *internal* sets (`D861FB30-…`, `BB586990-…`) are undocumented; the PIDs noted
  for them are **inferred** from observed data, not authoritative.
