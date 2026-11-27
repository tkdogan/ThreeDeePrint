# Go2Bed for Rhino and Grasshopper

Turn a BIM or solid model into a printable, buildable model kit. Go2Bed checks the input, trims where elements overlap, adds joints and pins so the pieces go back together, packs them onto your printer's bed, and writes one 3MF project per plate for your slicer. It runs in Rhino 7, 8 and 9 on Windows and Mac, as Rhino commands and as Grasshopper components.

## Rhino commands

| Command | What it does | Availability |
| --- | --- | --- |
| `BedLayout` | Checks the selected solids and blocks, heals Brep copies, then scales and packs them onto print beds. Bakes numbered plate meshes and an assembly reference into the Rhino document. Settings include scale, printer or build volume, finger joints, filament pins and their hole diameter, assembly marks, and source colors. | All builds |
| `BedHeal` | Repairs selected Breps, joins matching naked edges, caps planar openings, and merges coplanar faces. Replaces document geometry when the result is a valid solid. | All builds |
| `BedSTL` | Exports selected baked print meshes as one STL per piece (`Parts`) or one STL per plate (`GroupedPlates`), using the reviewed positions on the plate. | All builds |
| `Bed3MF` | Writes one 3MF slicer project per selected plate, with every piece named and in place. Slice it in PrusaSlicer, Bambu Studio, OrcaSlicer, Cura or another slicer. (`BedPrusa`, the 1.1 name, still works.) | All builds |
| `BedTest` | Opens the bundled barn sample and bakes its simplified exterior and interior components at 1:50 for the MK4S without selection or settings prompts. Each run creates a numbered kit. | Debug builds only |

## Run from Rider on macOS

Open [Go2Bed.sln](Go2Bed.sln) in Rider, choose **Debug** and the **Rhino 8 — Go2Bed** launch profile, then press **Play**. Rhino starts normally. If it shows the start window, open a blank model to get a command prompt. Restart the test Rhino session after rebuilding to load the latest plug-in.

## Install a packaged build

Go2Bed ships through Rhino's **Package Manager** as two distributions of the same version, built by `packaging/build-yak.sh`. Each one contains the Rhino commands and the Grasshopper components and works on both Windows and Mac:

- `go2bed-1.2.0-rh7_13-any.yak` for Rhino 7.13 or later. This is the .NET Framework 4.8 build.
- `go2bed-1.2.0-rh8_21-any.yak` for Rhino 8.21 or later and Rhino 9. It holds a .NET 7 build and a .NET Framework 4.8 build. Rhino loads the one that matches its runtime, including Rhino 8 on Windows set to .NET Framework.

Push both to the public server with `yak push`. Package Manager then offers each Rhino version the right one. To install from this repository instead, run Rhino's `yak` with the matching file and restart Rhino:

```sh
"/Applications/Rhino 8.app/Contents/Resources/bin/yak" install "$PWD/dist/go2bed-1.2.0-rh8_21-any.yak"
```

To browse it in Rhino’s **PackageManager**, append the absolute path of this repository’s `dist` folder to the semicolon-separated **Rhino.Options.PackageManager.Sources** setting under Advanced preferences, then search for **Go2Bed**. The release is distributed locally. [McNeel’s package source guide](https://developer.rhino3d.com/guides/yak/package-sources/)

### Grasshopper

Use the six components under **Go2Bed → Print**. [examples/Go2Bed-Example.ghx](examples/Go2Bed-Example.ghx) wires them all together with a slider, toggle or list for every setting. Open it, right-click **Model solids** and choose **Set Multiple Geometries**.

1. **Prepare Parts** accepts model geometry and scale: solids, closed meshes and referenced blocks. It checks the input (see [Input checks](#input-checks)), trims overlaps and cuts the joints you switch on: **Finger joints**, **Filament pins** with their **Pin hole diameter**, and **Assembly marks**. **Joint clearance** applies to fingers and to the seam keys of split parts. It outputs a prepared kit plus the solids for inspection.
2. **Printer Bed** turns a printer name into **Bed width**, **Bed depth** and **Build height**. Connect a Value List of the names in its **Printers** output, or type a name.
3. Connect **Prepared kit** and the bed size to **Flat Pack** to orient, split and pack the pieces. The default bed is the Prusa MK4S.
4. Connect **Packed kit** to **Preview Plates**. Meshes, names and outlines are grouped by plate. Right-click Preview Plates and choose **Bake** for a numbered Rhino kit with editable plate layers.
5. Connect **Reviewed kit** to **Export 3MF**, **Export STL**, or both. Set an existing destination folder and connect a Grasshopper **Button** to each **Write** input. Export 3MF writes one slicer project per plate. STL supports individual pieces or grouped plates. Each exporter returns its last output folder.

Grasshopper exports use the wired layout directly. To export manual edits to baked Rhino meshes, use the Rhino selection-based export commands. Existing definitions with the original all-in-one Flat Pack component continue to use their saved wiring. Definitions with the 1.1 **Export for Prusa** component still open. It now writes the same 3MF projects as Export 3MF and ignores its **Slice** input. Replace it with Export 3MF.

## Bake and review

1. Select the wall, slab, or other component objects, or the blocks of a BIM model, and run **BedLayout** to open the settings dialog. If you start the command without a component selection, select the objects at the prompt and press **Enter**.
2. Set the scale denominator and choose a **Printer**, or type the bed width, bed depth and build height. **Finger joints**, **Filament pins** and **Assembly marks** are on by default; uncheck any of them. Adjust the **Pin hole diameter** and the **Joint clearance per side** in millimetres. Click **Bake plates**. Defaults are 1:50 and the Prusa MK4S build volume of 250 × 210 × 220 mm. The packer uses projected part outlines, quarter turns, 3 mm bed margins and at least 2 mm between footprints. Each part lies on its largest flat face. If slivers or small modelling steps sit up to 0.5 mm below that face, they are trimmed off, as long as the trim removes at most 2% of the part and gives it at least 10% more bed contact.
3. Expand **3D Printer → Kit 01** in Layers. Each **Plate** layer contains its bed outline, label and numbered meshes at physical print size. The viewport zooms to the plates. Toggle the plate layers to inspect them individually. Turn on **Assembly reference**, select its objects and use Zoom Selected to see the numbered pieces in their assembled positions. The original building stays in the document.
4. Save the Rhino document to keep the preview. Each new bake creates a separate numbered kit. Preview creates no STL files.

Go2Bed keeps existing object names. For unnamed objects, it numbers parts by their Rhino layer, such as `Walls 01` and `Walls 02`. Geometry without a source layer gets names such as `Part 01`. The same name and piece ID appear on the baked plate and assembly meshes, in the Grasshopper names output, in individual STL files and in the objects of each 3MF project.

**Use source colors** is on by default in the Rhino preview dialog and the Grasshopper Preview component. When you bake, the plate and assembly meshes copy their source object's Rhino display color, including color inherited from its layer. Turn the setting off to use the kit's layer colors. Grasshopper geometry without a Rhino source uses the kit's layer colors.

### Alignment joints

Finger joints are the default. Eligible lower edges of walls (parts whose largest flat side is vertical) get rows of 5 mm wide tabs and matching blind sockets in the horizontal slab underneath. The tabs enter vertically during assembly. The default 0.20 mm clearance on each side makes a 5.40 mm socket for a 5 mm finger. The same allowance applies at the tip. Short edges, thin slabs, openings and contacts blocked by another part can limit where joints are placed.

Filament pins cover the contacts that fingers do not reach, in any orientation. Each pair of planar mating faces gets up to two matching blind holes, placed as far apart as the contact allows. Push a short offcut of 1.75 mm filament into each hole before gluing. The **Pin hole diameter** is 2.0 mm by default and can be set from 1.75 to 3.5 mm: raise it if pins don't go in, lower it if they fall out. Holes are up to 3 mm deep and always keep 0.6 mm of material around and behind them. Contacts with fingers get no pins.

**Assembly marks**: contacts too narrow for a pin get matching 1.2 mm square recesses instead, 0.3 mm deep at print scale, on both mating faces. Use them to line up the pieces during dry fitting. Turn **Assembly marks** off to keep those faces plain. Rhino reports the number of finger joints, pin positions and pairs of assembly marks.

### Printer library

The **Printer** list in BedLayout and the Grasshopper **Printer Bed** component share one library of build volumes (width × depth × height, mm): Prusa MK4S, MK3S+, CORE One, MINI+ and XL; Bambu Lab X1C / P1S / A1, A1 mini and H2D; Creality Ender-3 V3, K1 and K1 Max; Elegoo Neptune 4 Pro; Anycubic Kobra 3; Ultimaker S5; Voron 2.4 (350). Choose **Custom** or edit any size for other printers. The 3MF and STL exports work for every printer. Baked kits remember which printer they were packed for.

To script the Rhino command, every setting is a command-line option: `-BedLayout Printer=PrusaMINI PinHole=2.1 AssemblyMarks=Off _Enter`.

### Seams in split parts

A part longer than the bed is split into pieces named with a letter each, such as `Roof 01 a` and `Roof 01 b`. Each seam gets up to two dovetail keys where the part is solid on both sides. The keys run the full height of the part, so they print without overhangs. Lay the pieces face down on a flat surface and slide the keys together from above. The socket is larger than the key by the joint clearance on every side. Where no key fits, such as a seam through a row of openings, the seam stays plain and Rhino reports it. The Grasshopper components always add seam keys.

## Export the reviewed kit

Select the baked meshes you want to print, then run **BedSTL**. For a whole plate, use **Select Objects** on its layer. Window selections can include bed outlines and labels. With an empty selection the command prompts you to select parts. **Parts** writes one STL per selected mesh. **GroupedPlates** writes one STL per selected plate containing exactly its selected pieces in their reviewed positions. Selecting parts across kits creates a separate subfolder per kit. The destination is a new **FlatPack-STL** folder. You can move or rotate pieces flat inside their plate outline before export. If you move a piece onto another baked bed, export updates its plate layer. Export also levels small Z offsets and nudges pieces inside the bed outline by up to 0.25 mm total. These position corrections apply to the exported files. Mesh names carry the part IDs between the Rhino preview and the slicer.

STL stores coordinates without units. Import in **millimetres**. Import a plate’s files together with relative positions preserved to reproduce its Rhino layout. Review thin details and local overhangs in your slicer before printing.

[Open the baked barn preview](Barn-plugin-preview/Preview.3dm) or [browse its exported STLs](Barn-plugin-preview/). This example uses 21 alignment fingers, 0.20 mm clearance per side, and 34 parts on five MK4S plates. Its part IDs match the layers in the preview.

## Export 3MF projects for your slicer

Select the baked pieces and run **Bed3MF**, or use **Export 3MF** in Grasshopper. Each selected plate becomes one `.3mf` project in a new **FlatPack-3MF** folder, such as `Plate-01.3mf`. Every piece is a separate named object (`P07 Roof 02`) at its position on the Rhino plate. Go2Bed doesn't slice: open the project in your slicer, pick the printer, filament and print settings there, and slice.

- **PrusaSlicer, Bambu Studio, OrcaSlicer, Cura** and other slicers open the projects as plain 3MF geometry with object names. Bambu Studio and OrcaSlicer ask whether to load geometry only; choose yes.
- **Prusa MK4S** kits also carry the Original Prusa MK4S HF 0.4 mm nozzle, Generic PLA and 0.20 mm STRUCTURAL presets, so PrusaSlicer opens them ready to slice. For other Prusa printers, PrusaSlicer keeps the printer you have selected.

Kits baked with an earlier version, which don't record the printer, get the MK4S preset only if the bed outline matches exactly one printer in the library.

[Example PrusaSlicer projects and printer files](Barn-plugin-preview/PrusaSlicer/)

## Input checks

Before anything is cut, Go2Bed reads every selected object and lists all the problems at once, so you can fix the model in one pass. In Rhino the list is printed on the command line and the affected objects are selected. In Grasshopper each problem is a separate error on Prepare Parts. An object is rejected if:

- it is an open polysurface, a single surface or an uncapped extrusion. The message gives the number of naked edges and suggests ShowEdges, Join, Cap or OffsetSrf.
- it is an open or non-manifold mesh (try FillMeshHoles or MeshRepair), or a SubD (convert with ToNURBS).
- it is invalid geometry (run Check and ExtractBadSrf).
- it comes out thinner than 0.8 mm, about two 0.4 mm nozzle widths, at the chosen scale. The check measures the bounding box, both world-aligned and aligned to the part's largest flat face. Print at a larger scale, thicken the element, or leave it out.
- it is a duplicate: same position, size and volume as another selected solid, as BIM exports often produce (SelDup finds them).

**Blocks** from BIM imports are read directly, and nested blocks too. A block's solids print as one part where they touch, like a Rhino group. Curves, text, hatches and other annotation inside blocks are ignored.

## Source and build

Run `packaging/build-yak.sh` to build both plug-ins for both frameworks and write the two Yak packages to `dist`. It runs `dotnet build Plugin/Go2Bed.csproj -c Release -t:Yak` and uses the `yak` tool from Rhino 8 (on Windows, `C:\Program Files\Rhino 8\System\Yak.exe`); pass `-p:YakExe=<path>` to use another one. The [Go2Bed icon set](brand/go2bed-icon-set.png) uses interlocking walls for the package and Grasshopper category, and separated panels for the Flat Pack component. In Rhino, open the **Go2Bed** toolbar for Lay out print plates, Export STL and Export 3MF projects. The package includes the [Rhino toolbar file](Plugin/Go2Bed.rui).

Run `dotnet build Go2Bed.sln -c Release` to build the solution. Every project targets `net48` (Rhino 7, compiled against RhinoCommon 7.13) and `net7.0` (Rhino 8.21 and later). The Rhino plug-in is written to `Plugin/bin/Release/<framework>/Go2Bed.rhp`. The Rhino 7 build uses mesh booleans without a tolerance setting, because Rhino 7 has none.

The plug-in uses RhinoCommon mesh booleans to subtract previously allocated material with 0.05 mm clearance on each axis. Rhino 8's mesh boolean sometimes leaves a face out of the result, even where a wall simply stands on a slab. When that happens, Go2Bed fills holes that lie flat in one plane, then retries the cut one neighbour at a time, and finally falls back to a Brep boolean for that part. Floors take precedence at intersections, followed by walls, roofs and decks, then details. RhinoCommon prepares components, scores planar orientations for bed contact and overhang, splits oversized panels with straight glue seams and packs the part silhouettes onto plates. For the barn example, select components from the simplified exterior and interior layers. The two open exterior panels are recovered from their projected render geometry. For other BIM models, select the elements as separate solids or blocks.
