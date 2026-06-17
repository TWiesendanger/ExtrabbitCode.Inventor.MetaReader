# InventorMeta — read Inventor files without Inventor

Reads metadata out of Autodesk Inventor `.ipt` / `.iam` / `.idw` / `.ipn` files
directly from their bytes — **no Autodesk Inventor installation required**.

It exploits the fact that Inventor files are standard **OLE Compound File Binary**
containers and that all iProperties live in standard **OLE Property Sets**. See
[`docs/INVENTOR-FILE-FORMAT.md`](docs/INVENTOR-FILE-FORMAT.md) for the full
reverse-engineering write-up.

## What it extracts

- Document type (part / assembly / drawing / presentation) from the root CLSID
- **All iProperties** — Part Number, Designer, Material, Project, Status, Description,
  custom properties, dates, "Last Updated With", etc.
- Cached **mass / volume / surface area / density** (with the *Valid Mass Props* caveat)
- The embedded **preview thumbnail** (PNG/BMP)
- **Referenced files** (assembly components, the model a drawing documents)
- **Per-model-state iProperties** — read every model state's properties at once, *without
  switching states in Inventor*, including a diff of what changes between states.
  *(For complete data, enable Inventor 2027+'s **Generate All Model States on Save**
  — right-click the Model States browser node. Otherwise a state's values only appear
  once that state has been recomputed in Inventor; until then the tool shows `(not cached)`
  instead of guessing.)*
- **Model States** and the originating template / version provenance
- The raw CFB storage/stream tree, and any individual stream

## Projects

```
InventorReader.slnx
└─ src/
   ├─ InventorMeta/        cross-platform core library (the parser)  — net10.0
   ├─ InventorMeta.Cli/    command-line tool  ("invmeta")            — net10.0
   └─ InventorMeta.App/    WinUI 3 desktop viewer                    — net10.0-windows
```

`InventorMeta` has **zero external dependencies** and is the reusable piece — drop it
into your own apps or Inventor add-ins. The CLI and WinUI app are thin front-ends over
the same `InventorDocument` class.

## Build & run

### CLI (cross-platform)

```powershell
dotnet build src/InventorMeta.Cli -c Release

# friendly report
dotnet run --project src/InventorMeta.Cli -- info   SamplePart.ipt
# per-model-state iProperties + what differs between states
dotnet run --project src/InventorMeta.Cli -- states SamplePart.ipt
# everything as JSON
dotnet run --project src/InventorMeta.Cli -- json   SamplePart.ipt
# every property in every property set
dotnet run --project src/InventorMeta.Cli -- props Assembly1.iam
# extract the thumbnail
dotnet run --project src/InventorMeta.Cli -- thumb SamplePart.idw out\thumb
# raw container tree / one stream / dump all streams
dotnet run --project src/InventorMeta.Cli -- tree    SamplePart.ipt
dotnet run --project src/InventorMeta.Cli -- cat     SamplePart.ipt /UFRxDoc
dotnet run --project src/InventorMeta.Cli -- extract SamplePart.ipt out\streams
```

After `dotnet build`, the binary is `invmeta(.exe)` in the project's `bin/Release`.

### WinUI 3 viewer (Windows)

```powershell
dotnet build src/InventorMeta.App -c Release -r win-x64
# launch (optionally with a file to open immediately):
src/InventorMeta.App/bin/Release/net10.0-windows10.0.19041.0/win-x64/InventorMeta.App.exe SamplePart.ipt
```

Open a file (button or drag-and-drop) to see the thumbnail, key iProperties, a grouped
view of every property, references & model states, and the raw file structure. The
**Export JSON** button writes the full metadata. The app is built self-contained
(`WindowsAppSDKSelfContained`), so the Windows App Runtime does not need to be
pre-installed.

## Library usage

```csharp
using InventorMeta;

var doc = new InventorDocument(@"C:\path\SamplePart.ipt");
Console.WriteLine(doc.DocumentType);                 // "Inventor Part (.ipt)"
Console.WriteLine(doc.Summary["Part Number"]);       // "SamplePart"
foreach (var r in doc.References) Console.WriteLine(r);
if (doc.Thumbnail != null)
    File.WriteAllBytes("preview." + doc.ThumbnailExt, doc.Thumbnail);
string json = doc.ToJson();
```

## Scope

Metadata, properties, references and the preview are fully and stably readable. The
parametric model and B-Rep geometry live in the proprietary `RSeStorage` database and
are **not** decoded — for geometry, export to STEP/SAT or use the Inventor API. See the
format doc for the full picture.

## Requirements

.NET 10 SDK. The WinUI app additionally needs the Windows App SDK packages (restored
automatically) and a Windows build target.
