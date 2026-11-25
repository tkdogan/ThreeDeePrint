# Go2Bed for Rhino and Grasshopper

Turn separate architectural solids into flat-packed print kits. Preview each Prusa MK4S plate in Rhino, then export selected parts or complete plates.

## Rhino commands

| Command | What it does | Availability |
| --- | --- | --- |
| `ToBedPreview` | Heals Brep copies, then scales and packs the selected components onto print beds. Bakes numbered plate meshes and an assembly reference into the Rhino document. Settings include scale, build volume, optional finger joints, and source colors. | All builds |
| `ToBedHealBreps` | Repairs selected Breps, joins matching naked edges, caps planar openings, and merges coplanar faces. Replaces document geometry when the result is a valid solid. | All builds |
| `ToBedExportSTL` | Exports selected baked print meshes as one STL per piece (`Parts`) or one STL per plate (`GroupedPlates`), using the reviewed positions on the plate. | All builds |
| `ToBedExportPrusa` | Writes an editable PrusaSlicer 3MF project for each selected plate using the MK4S preset. `ProjectAndBGCode` also runs the installed PrusaSlicer to generate binary G-code. | All builds |
| `ToBedDEBUG` | Opens the bundled barn sample and bakes its simplified exterior and interior components at 1:50 for the MK4S without selection or settings prompts. Each run creates a numbered kit. | Debug builds only |

## Run from Rider on macOS

Open [Go2Bed.sln](Go2Bed.sln) in Rider, choose **Debug** and the **Rhino 8 — Go2Bed** launch profile, then press **Play**. Rhino starts normally. If it shows the start window, open a blank model to get a command prompt. Restart the test Rhino session after rebuilding to load the latest plug-in.

## Install a packaged build

The [Go2Bed 1.1.0 Yak package](dist/go2bed-1.1.0-rh8_21-mac.yak) includes the Rhino commands and five Grasshopper components for Rhino 8.21 or later on macOS. From this repository, install it locally and restart Rhino.

```sh
"/Applications/Rhino 8.app/Contents/Resources/bin/yak" install "$PWD/dist/go2bed-1.1.0-rh8_21-mac.yak"
```

To browse it in Rhino’s **PackageManager**, append the absolute path of this repository’s `dist` folder to the semicolon-separated **Rhino.Options.PackageManager.Sources** setting under Advanced preferences, then search for **Go2Bed**. The release is distributed locally. [McNeel’s package source guide](https://developer.rhino3d.com/guides/yak/package-sources/)

### Grasshopper

Use the five components under **Go2Bed → Print**.

1. **Prepare Parts** accepts model geometry and scale. It trims overlaps, applies optional finger joints, and outputs a prepared kit plus the solids for inspection.
2. Connect **Prepared kit** to **Flat Pack**. Adjust the build volume here to orient, split and pack the pieces. The default bed is the Prusa MK4S.
3. Connect **Packed kit** to **Preview**. Meshes, names and outlines are grouped by plate. Right-click Preview and choose **Bake** for a numbered Rhino kit with editable plate layers.
4. Connect **Reviewed kit** to **Export STL**, **Export Prusa**, or both. Set an existing destination folder and connect a Grasshopper **Button** to each **Write** input. STL supports individual pieces or grouped plates. Prusa writes one 3MF project per plate, with optional BGCode slicing. Each exporter returns its last output folder.

Grasshopper exports use the wired layout directly. To export manual edits to baked Rhino meshes, use the Rhino selection-based export commands. Existing definitions with the original all-in-one Flat Pack component continue to use their saved wiring.

### Rhino-only installer

**Install on Mac** by double-clicking [Go2Bed.macrhi](dist/Go2Bed.macrhi), then quit and reopen Rhino. The installer contains the compiled C# plug-in and its geometry libraries. Use Rhino 8.21 or later. [McNeel’s Mac installer instructions](https://developer.rhino3d.com/guides/rhinocommon/plugin-installers-mac/)

## Bake and review

1. Select the wall, slab, or other component objects and run **ToBedPreview** to open the settings dialog. If you start the command without a component selection, select the objects at the prompt and press **Enter**.
2. Set the scale denominator and build volume. **Add wall-to-slab fingers** and **Add filament pin holes** are enabled by default. Adjust the **clearance per side** in millimetres or uncheck either box. Click **Bake preview**. Defaults are 1:50 and the Prusa MK4S build volume of 250 × 210 × 220 mm. The packer uses projected part outlines, quarter turns, 3 mm bed margins and at least 2 mm between footprints.
3. Expand **3D Printer → Kit 01** in Layers. Each **Plate** layer contains its bed outline, label and numbered meshes at physical print size. The viewport zooms to the plates. Toggle the plate layers to inspect them individually. Turn on **Assembly reference**, select its objects and use Zoom Selected to see the numbered pieces in their assembled positions. The original building stays in the document.
4. Save the Rhino document to keep the preview. Each new bake creates a separate numbered kit. Preview creates no STL files.

Go2Bed keeps existing object names. For unnamed objects, it numbers parts by their Rhino layer, such as `Walls 01` and `Walls 02`. Geometry without a source layer gets names such as `Part 01`. The same name and piece ID appear on the baked plate and assembly meshes, in the Grasshopper names output, and in individual STL and PrusaSlicer exports.

**Use source colors** is on by default in the Rhino preview dialog and the Grasshopper Preview component. When you bake, the plate and assembly meshes copy their source object's Rhino display color, including color inherited from its layer. Turn the setting off to use the kit's layer colors. Grasshopper geometry without a Rhino source uses the kit's layer colors.

### Alignment joints

Finger joints are the default. Eligible lower panel edges get rows of 5 mm wide tabs and matching blind sockets in the horizontal slab underneath. The tabs enter vertically during assembly. The default 0.20 mm clearance on each side makes a 5.40 mm socket for a 5 mm finger. The same allowance applies at the tip. Short edges, thin slabs, openings and contacts blocked by another part can limit where joints are placed.

Filament pins cover the contacts that fingers do not reach, in any orientation. Each pair of planar mating faces gets up to two matching blind holes, placed as far apart as the contact allows. Push a short offcut of 1.75 mm filament into each hole before gluing. The holes are 2.0 mm across at the default clearance and up to 3 mm deep, and always keep 0.6 mm of material around and behind them. Contacts with fingers get no pins.

Contacts too narrow for a pin get matching 1.2 mm square recesses instead, 0.3 mm deep at print scale. Use the recesses to line up the pieces during dry fitting. Rhino reports the number of finger joints, pin positions and pairs of assembly marks.

### Seams in split parts

A part longer than the bed is split into pieces named with a letter each, such as `Roof 01 a` and `Roof 01 b`. Each seam gets up to two dovetail keys where the part is solid on both sides. The keys run the full height of the part, so they print without overhangs. Lay the pieces face down on a flat surface and slide the keys together from above. The socket is larger than the key by the joint clearance on every side. Where no key fits, such as a seam through a row of openings, the seam stays plain and Rhino reports it. The Grasshopper components always add pins and seam keys.

## Export the reviewed kit

Select the baked meshes you want to print, then run **ToBedExportSTL**. For a whole plate, use **Select Objects** on its layer. Window selections can include bed outlines and labels. With an empty selection the command prompts you to select parts. **Parts** writes one STL per selected mesh. **GroupedPlates** writes one STL per selected plate containing exactly its selected pieces in their reviewed positions. Selecting parts across kits creates a separate subfolder per kit. The destination is a new **FlatPack-STL** folder. You can move or rotate pieces flat inside their plate outline before export. If you move a piece onto another baked bed, export updates its plate layer. Export also levels small Z offsets and nudges pieces inside the bed outline by up to 0.25 mm total. These position corrections apply to the exported files. Mesh names carry the part IDs between the Rhino preview and the slicer.

STL stores coordinates without units. Import in **millimetres**. Import a plate’s files together with relative positions preserved to reproduce its Rhino layout. Review thin details and local overhangs in your slicer before printing.

[Open the baked barn preview](Barn-plugin-preview/Preview.3dm) or [browse its exported STLs](Barn-plugin-preview/). This example uses 21 alignment fingers, 0.20 mm clearance per side, and 34 parts on five MK4S plates. Its part IDs match the layers in the preview.

## Export directly for the Prusa MK4S

Select the baked pieces and run **ToBedExportPrusa**. **Project** writes one editable PrusaSlicer project (`.3mf`) per selected plate. **ProjectAndBGCode** also runs the installed Mac PrusaSlicer and writes the matching binary G-code (`.bgcode`) for the printer. Both modes use the positions on the Rhino plate layers. The project embeds the Original Prusa MK4S high-flow 0.4 mm nozzle, Generic PLA and 0.20 mm STRUCTURAL presets. If your hardware or material differs, open the project, change the preset, and slice again.

[Example PrusaSlicer projects and printer files](Barn-plugin-preview/PrusaSlicer/)

## Source and build

Run [packaging/build-mac.sh](packaging/build-mac.sh) to build both plug-ins and generate the Yak distribution under `dist`. The [Go2Bed icon set](brand/go2bed-icon-set.png) uses interlocking walls for the package and Grasshopper category, and separated panels for the Flat Pack component. In Rhino, open the **Go2Bed** toolbar for Preview, Export STL and Export for Prusa. The package includes the [Rhino toolbar file](Plugin/Go2Bed.rui).

Run `dotnet build Go2Bed.sln -c Release` to build the solution. The Rhino plug-in is written to `Plugin/bin/Release/net7.0/Go2Bed.rhp` and the Mac installer to `dist/Go2Bed.macrhi`.

The plug-in uses RhinoCommon mesh booleans to subtract previously allocated material with 0.05 mm clearance on each axis. Floors take precedence at intersections, followed by walls, roofs and decks, then details. RhinoCommon prepares components, scores planar orientations for bed contact and overhang, splits oversized panels with straight glue seams and packs the part silhouettes onto plates. For the barn example, select components from the simplified exterior and interior layers. The two open exterior panels are recovered from their projected render geometry. For other BIM models, select separate solid components with a supporting planar face. Explode blocks first.


---

# Go2Bed Python prototype

Open [the barn kit](Barn-kit/Flatpack.3dm) in Rhino 8 and run Zoom Extents. The six visible layers are print layouts in millimetres. The hidden Assembly reference layer contains the matching parts in their assembled positions. For printing, open the numbered 3MF plates directly in your slicer. Each object has the same ID in Rhino, the 3MF, the individual STL filename and the [assembly guide](Barn-kit/Assembly.md).

The example uses a 1:50 scale and a 220 × 220 × 250 mm build volume. It draws from 33 elements in the simplified comparison and interior layers. The west and north walls have open source Breps. The converter accepts a closed render mesh when available and reconstructs a constant-depth through-opening profile when needed. The original detailed BIM blocks and the duplicate exterior union are excluded.

## Generate another kit

In Rhino 8, open [flatpack.py](flatpack.py) in ScriptEditor and run it as Python 3. Choose a saved model, a new output folder, the component layer expression, scale denominator and bed dimensions. Enter **Barn** for the supplied model. The script declares its Python dependencies for the editor to install. The same generator runs from a terminal:

```sh
python3 -m pip install 'rhino3dm>=8.35' 'manifold3d>=3.5' numpy
python3 flatpack.py Barn_original_with_simplified_interior.3dm MyKit --scale 50 --bed 220 220 250
python3 flatpack.py House.3dm HouseKit --layers '^(Walls|Floors|Roof)$' --scale 75 --bed 180 180 180
```

Use Python 3.9 or later with compatible package wheels. Select layers containing individual component solids. Supported inputs include closed polygonal extrusions with parallel end cuts, closed triangle or quad meshes, and Breps saved with suitable render meshes. Explode nested BIM blocks into the intended components first. A fused building mesh has no recorded wall boundaries. The script reports unsupported geometry by layer and name.

## Geometry and fit

The generator converts document units to model-scale millimetres. Floors take precedence at intersections, followed by walls, roofs and decks, then details. It cuts away shared volume and applies a 0.05 mm allowance along each axis of a cutter at overlapping joints. Components may separate into several pieces after these cuts. Sub-0.4 mm remnants are listed in the assembly guide.

Among orientations with at least 65% of the maximum planar bed contact, it chooses the one with the least projected steep overhang, then favours larger contact and lower height. Oversized pieces get straight glue seams. Rectangular packing allows a quarter turn and reserves 3 mm around the bed and between pieces. The output uses plain glue joints and digital part IDs.
