# CAD Tools — Standalone

A standalone WPF desktop app that replicates the AutoCAD plugin commands for Belgian cable network drawings. Works on DXF files without requiring AutoCAD — compatible with AutoCAD LT and any DXF-capable viewer.

## Features

| Tool | Description |
|---|---|
| **Street View** | Reads cross-section marker points from the DXF, converts Belgian Lambert 72 coordinates to WGS84, and downloads left/right Google Street View images next to the file |
| **Cross Section** | Builds a labelled road cross-section profile (zone picker, presets, street view thumbnails) and writes two diagrams — *vóór* and *gedurende* — into the DXF |
| **Layer Cleanup** | Turns off target layers (ADN, ADT, HOT, WPI\*, WRI\*, GVP\*…) and greys all remaining entities |
| **Create Plans** | Builds a complete permit sheet in one pass: an A0 layout at a true 1/500, the quantity table (cable lengths by type, trench lengths by method, pit count), the title-block fields, the trench-depth variant, and the *Ligging* locator map — all reviewed in one dialog before anything is written |

### Create Plans

Replaces the former separate *Fill Table* and *Create Layout* buttons. Before running it, draw a
closed rectangle on a layer named `LAYOUT_BOX` around the area the sheet should show — that
rectangle is the only input the tool cannot infer.

What it produces:

- **Sheet** — a new `Overzicht N` layout, template inserted 1:1 on A0, viewport set to a true
  1/500. If the marked area is too large for 1/500 it steps up to the next standard scale and
  relabels both the `SCHAAL 1/x` note and the scale-bar ticks so they cannot contradict the drawing.
- **Quantity table** — cable lengths summed per entity (a `2xDB7` run is two cables), trench
  lengths summed per physical route (parallel ducts in one trench count once), and
  `Glasvezelput (Beton/CTB)` as a unit count (`1st`).
- **Title block** — `Dossiernummer`, street/municipality, `Opgemaakt`, `Getekend door`,
  `Nagekeken`, `Uitgevoerd door` and the works dates. Defaults are parsed from the DXF filename.
  Values go on a `TITLEBLOCK_FILL` layer, so re-running replaces them instead of stacking copies.
- **Trench depth** — 60 cm or 80 cm, applied to the cross-section detail text and dimensions.
- **Ligging map** — a Google Static Maps roadmap with the routes traced in red, saved as a PNG next
  to the DXF and referenced by the sheet. Requires the **Maps Static API** to be enabled on your
  key (it is a separate API from Street View Static); if it is not, the rest of the sheet is still
  written and the log says why.

Nothing is written before you confirm the review dialog, and nothing is saved until you click
**Save**.

## Requirements

- Windows 10/11 x64
- No AutoCAD installation needed
- .NET 10 runtime — or use the self-contained publish (see below)

## Setup

### 1. Clone the repo

```
git clone <repo-url>
cd CadToolsApp
```

### 2. Add your API key

Copy `config.example.txt` to `config.txt` and fill in your Google Maps Platform API key:

```
STREETVIEW_API_KEY=your_key_here
```

The same key serves both Google features. Enable **Street View Static API** (for *Street View*) and
**Maps Static API** (for the *Ligging* locator map) on the key — they are separate APIs, and a key
with only one enabled returns `403` for the other.

`config.txt` is gitignored and will never be committed.

### 3. Run in development

```
dotnet run
```

### 4. Publish as a single `.exe`

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The output is `publish\CadToolsApp.exe`. Copy `config.txt` into the same `publish\` folder before distributing.

## File format

The app reads and writes **DXF** files only. DWG files must be converted to DXF first (e.g. using the free [ODA File Converter](https://www.opendesign.com/guestfiles/oda_file_converter)). After editing, open or insert the DXF in AutoCAD LT — LT handles saving back to DWG.

## Project structure

```
CadToolsApp/
├── App.xaml / App.xaml.cs
├── MainWindow.xaml / MainWindow.xaml.cs   — file picker + 4 action buttons + log
├── Models/
│   ├── ZoneModels.cs                      — ZoneType, LayoutZone, SectionInfo, ZonePreset
│   ├── CableModels.cs                     — CalloutAnnotation, ClassifiedCable
│   └── SheetInfo.cs                       — title-block fields, scale, depth (+ filename parsing)
├── Dialogs/
│   ├── CrossSectionDialog.xaml/.cs        — zone builder dialog with street view picker
│   └── CreatePlansDialog.xaml/.cs         — cable review grid + sheet-info form
├── Services/
│   ├── DxfService.cs                      — all DXF read/write operations (netDxf 3.x)
│   ├── CableClassifier.cs                 — per-polyline type/stripe/method classification
│   ├── LegendReader.cs                    — colour/linetype → cable type from the drawing's legend
│   ├── MultiLeaderReader.cs               — MULTILEADER callout text (netDxf cannot read these)
│   ├── TitleBlockWriter.cs                — title-block fields, depth and scale annotations
│   ├── LiggingMapService.cs               — Static Maps locator inset with the route traced
│   ├── StreetViewService.cs               — Google Street View image downloads
│   ├── GoogleApi.cs                       — shared API key + HttpClient
│   └── BelgianCrs.cs                      — Belgian Lambert 72 → WGS84 transform (ProjNet)
├── Templates/
│   └── table_layout_template.dxf          — A0 title block / legend / cross-section details
├── config.example.txt                     — API key template (safe to commit)
└── config.txt                             — real API key (gitignored, never committed)
```

## Dependencies

- [netDxf](https://github.com/haplokuon/netDxf) 3.0.1 — DXF read/write
- [ProjNet](https://github.com/NetTopologySuite/ProjNet4GeoAPI) 2.0 — Belgian Lambert 72 → WGS84 coordinate transform
