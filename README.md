# CAD Tools — Standalone

A standalone WPF desktop app that replicates the AutoCAD plugin commands for Belgian cable network drawings. Works on DXF files without requiring AutoCAD — compatible with AutoCAD LT and any DXF-capable viewer.

## Features

| Tool | Description |
|---|---|
| **Street View** | Reads cross-section marker points from the DXF, converts Belgian Lambert 72 coordinates to WGS84, and downloads left/right Google Street View images next to the file |
| **Cross Section** | Builds a labelled road cross-section profile (zone picker, presets, street view thumbnails) and writes two diagrams — *vóór* and *gedurende* — into the DXF |
| **Layer Cleanup** | Turns off target layers (ADN, ADT, HOT, WPI\*, WRI\*, GVP\*…) and greys all remaining entities |
| **Fill Table** | Scans cable polylines on the `PDDucts BO` layer, aggregates lengths by cable type and digging method, and fills the values into the summary table block |

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

Copy `config.example.txt` to `config.txt` and fill in your Google Street View API key:

```
STREETVIEW_API_KEY=your_key_here
```

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
│   └── ZoneModels.cs                      — ZoneType, LayoutZone, SectionInfo, ZonePreset
├── Dialogs/
│   └── CrossSectionDialog.xaml/.cs        — zone builder dialog with street view picker
├── Services/
│   ├── DxfService.cs                      — all DXF read/write operations (netDxf 3.x)
│   └── StreetViewService.cs               — Google Street View image downloads (ProjNet)
├── config.example.txt                     — API key template (safe to commit)
└── config.txt                             — real API key (gitignored, never committed)
```

## Dependencies

- [netDxf](https://github.com/haplokuon/netDxf) 3.0.1 — DXF read/write
- [ProjNet](https://github.com/NetTopologySuite/ProjNet4GeoAPI) 2.0 — Belgian Lambert 72 → WGS84 coordinate transform
