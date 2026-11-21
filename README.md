# ThreeDeePrint for Rhino 8

## Run from Rider on macOS

Open [ThreeDeePrint.sln](ThreeDeePrint.sln) in Rider. Quit any running Rhino 8 session after saving your work, then press **Play** with **Rhino 8 — Flat-pack preview** selected. Rider builds the plug-in, launches Rhino 8 with a build-folder copy of the barn, and starts **FlatPackPreview**. Select the components to print and press **Enter**. Inspect **3D Printer → Kit 01** and run **FlatPackExportSTL** when ready. Quit the test Rhino session before the next Play to load your latest build.

## Install a packaged build

**Install on Mac** by double-clicking [ThreeDeePrint.macrhi](dist/ThreeDeePrint.macrhi), then quit and reopen Rhino. The installer contains the compiled C# plug-in and its geometry libraries. Use Rhino 8.21 or later. [McNeel’s Mac installer instructions](https://developer.rhino3d.com/guides/rhinocommon/plugin-installers-mac/)

## Bake and review

1. Run **FlatPackPreview** in Rhino. Select the wall, slab, or other component objects to print and press **Enter** to open the settings dialog. Only the selected components go into this kit.
2. Set the scale denominator and build volume. To add alignment joints, enable **Add wall-to-slab fingers** and choose the **clearance per side** in millimetres. Click **Bake preview**. Defaults are 1:50 and the Prusa MK4S build volume of 250 × 210 × 220 mm. The packer uses projected part outlines, quarter turns, 3 mm bed margins and at least 2 mm between footprints.
3. Expand **3D Printer → Kit 01** in Layers. Each **Plate** layer contains its bed outline, label and numbered meshes at physical print size. The viewport zooms to the plates. Toggle the plate layers to inspect them individually. Turn on **Assembly reference**, select its objects and use Zoom Selected to see the numbered pieces in their assembled positions. The original building stays in the document.
4. Save the Rhino document to keep the preview. Each new bake creates a separate numbered kit. Preview creates no STL files.

### Alignment joints

Plain glue joints are the default. Finger mode adds rows of 5 mm wide tabs to eligible lower panel edges and cuts matching blind sockets in the horizontal slab underneath. The tabs enter vertically during assembly. The default 0.20 mm clearance on each side makes a 5.40 mm socket for a 5 mm finger. The same allowance applies at the tip. Short edges, thin slabs, openings and contacts blocked by another part can limit where joints are placed. Rhino reports the number of completed pairs. Roof seams and wall-to-wall corners retain their trimmed glue faces.

## Export the reviewed kit

Select the baked meshes you want to print, then run **FlatPackExportSTL**. For a whole plate, use **Select Objects** on its layer. Window selections can include bed outlines and labels. With an empty selection the command prompts you to select parts. **Parts** writes one STL per selected mesh. **GroupedPlates** writes one STL per selected plate containing exactly its selected pieces in their reviewed positions. Selecting parts across kits creates a separate subfolder per kit. The destination is a new **FlatPack-STL** folder. You can move or rotate pieces flat inside their plate outline before export. Keep each piece on its plate layer and its base at bed height. Mesh names carry the part IDs between the Rhino preview and the slicer.

STL stores coordinates without units. Import in **millimetres**. Import a plate’s files together with relative positions preserved to reproduce its Rhino layout. Review thin details and local overhangs in your slicer before printing.

[Open the baked barn preview](Barn-plugin-preview/Preview.3dm) or [browse its exported STLs](Barn-plugin-preview/). This example uses 21 alignment fingers, 0.20 mm clearance per side, and 34 parts on five MK4S plates. Its part IDs match the layers in the preview.

## Export directly for the Prusa MK4S

Select the baked pieces and run **FlatPackExportPrusa**. **Project** writes one editable PrusaSlicer project (`.3mf`) per selected plate. **ProjectAndBGCode** also runs the installed Mac PrusaSlicer and writes the matching binary G-code (`.bgcode`) for the printer. Both modes use the positions on the Rhino plate layers. The project embeds the Original Prusa MK4S high-flow 0.4 mm nozzle, Generic PLA and 0.20 mm STRUCTURAL presets. If your hardware or material differs, open the project, change the preset, and slice again.

[Example PrusaSlicer projects and printer files](Barn-plugin-preview/PrusaSlicer/)

## Source and build

Build [ThreeDeePrint.csproj](Plugin/ThreeDeePrint.csproj) using `dotnet build Plugin -c Release`. The build produces the managed RHP under `Plugin/bin/Release/net7.0` and the Mac installer under `dist`.

The plug-in uses Manifold to subtract previously allocated material with 0.05 mm clearance on each axis. Floors take precedence at intersections, followed by walls, roofs and decks, then details. RhinoCommon prepares components, scores planar orientations for bed contact and overhang, splits oversized panels with straight glue seams and packs the part silhouettes onto plates. For the barn example, select components from the simplified exterior and interior layers. The two open exterior panels are recovered from their projected render geometry. For other BIM models, select separate solid components with a supporting planar face. Explode blocks first.


The project includes a universal macOS geometry library for Apple silicon and Intel. A normal Rider build copies it beside the RHP. To rebuild that library from its pinned, checksum-verified source, run [build-mac.sh](Plugin/Native/build-mac.sh) with CMake and the Xcode command-line tools installed.

---

# Earlier Python prototype

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
