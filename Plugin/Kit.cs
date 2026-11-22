using System.Drawing;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Go2Bed;

public static class Kit
{
    public static Layer Bake(RhinoDoc doc, RhinoObject[] objects, double scale, double width, double depth, double height, bool barn)
        => BakeWithJoints(doc, objects, scale, width, depth, height, barn, false);

    public static Layer BakeWithJoints(RhinoDoc doc, RhinoObject[] objects, double scale, double width, double depth, double height, bool barn, bool fingers)
        => BakeSettings(doc,objects,new PreviewSettings(scale,width,depth,height,fingers,.20),barn);

    internal static Layer BakeSettings(RhinoDoc doc, RhinoObject[] objects, PreviewSettings settings, bool barn = false)
    {
        var (scale,width,depth,height,fingers,clearance,useSourceColors) = settings;
        var sources = objects.Select(o => SourcePart.FromObject(o,barn));
        var layout = PrintLayout.Solve(sources,doc.ModelUnitSystem,scale,width,depth,height,fingers,clearance);
        var bounds = BoundingBox.Empty;
        foreach (var obj in objects) bounds.Union(obj.Geometry.GetBoundingBox(true));
        return PrintLayout.Bake(doc,layout,width,depth,bounds,useSourceColors);
    }

    public static string Export(RhinoDoc doc, Guid kitId, string parent)
        => ExportStls(doc,kitId,parent,false);

    public static string ExportStls(RhinoDoc doc, Guid kitId, string parent, bool groupByPlate)
    {
        var root = doc.Layers.FindId(kitId) ?? throw new ArgumentException("Kit layer no longer exists.");
        var parts = doc.Layers.Where(l => !l.IsDeleted && l.ParentLayerId == root.Id)
            .SelectMany(l => doc.Objects.FindByLayer(l) ?? Array.Empty<RhinoObject>())
            .Where(o => IsPrintPart(o) && o.Attributes.GetUserString("FlatPack.Kit") == kitId.ToString());
        return ExportSelection(doc, parts, parent, groupByPlate);
    }

    internal static bool IsPrintPart(RhinoObject obj) => obj.Geometry is Mesh &&
        obj.Attributes.GetUserString("FlatPack.Kind") == "Print" &&
        Guid.TryParse(obj.Attributes.GetUserString("FlatPack.Kit"), out _);

    public static string ExportSelection(RhinoDoc doc, IEnumerable<RhinoObject> selection, string parent, bool groupByPlate)
        => ExportFiles(doc,selection,parent,groupByPlate,false);

    public static string ExportPrusa(RhinoDoc doc, IEnumerable<RhinoObject> selection, string parent, bool slice = false)
        => ExportFiles(doc,selection,parent,false,true,slice);

    private static string ExportFiles(RhinoDoc doc, IEnumerable<RhinoObject> selection, string parent, bool groupByPlate, bool prusa, bool slice = false)
    {
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        var parts = selection.Where(IsPrintPart).DistinctBy(o => o.Id).ToArray();
        if (parts.Length == 0) throw new ArgumentException("No baked print meshes selected.");
        bool multipleKits = parts.Select(o => o.Attributes.GetUserString("FlatPack.Kit")).Distinct().Count() > 1;
        double mm = RhinoMath.UnitScale(doc.ModelUnitSystem, UnitSystem.Millimeters);
        IEnumerable<ExportPlate> Plates()
        {
            foreach (var group in parts.GroupBy(o => o.Attributes.LayerIndex).OrderBy(g => g.Key))
            {
                var plate = doc.Layers[group.Key];
                var pieces = group.OrderBy(o => o.Attributes.Name).ThenBy(o => o.Id).ToArray();
                string kit = plate.ParentLayerId.ToString();
                if (pieces.Any(o => o.Attributes.GetUserString("FlatPack.Kit") != kit))
                    throw new ArgumentException("Return the selected pieces to their kit's print plate layers.");
                var bed = doc.Objects.FindByLayer(plate)?.SingleOrDefault(o =>
                    o.Attributes.GetUserString("FlatPack.Kind") == "Bed" && o.Attributes.GetUserString("FlatPack.Kit") == kit)
                    ?? throw new ArgumentException("Missing bed outline on " + plate.FullPath);
                var box = bed.Geometry.GetBoundingBox(true);
                string directory = multipleKits ? $"Kit-{doc.Layers.FindId(plate.ParentLayerId).Index}" : "";
                // Keep the baked plate number even when earlier plates are unselected.
                string plateName = plate.Name.StartsWith("Plate ", StringComparison.Ordinal) &&
                    int.TryParse(plate.Name.Substring(6), out int number) && number > 0
                    ? $"Plate-{number:00}" : $"Layer-{plate.Index}";
                yield return new ExportPlate(Path.Combine(directory,plateName), box.Diagonal.X*mm, box.Diagonal.Y*mm,
                    pieces.Select(obj => new ExportPiece(obj.Name ?? "Part",(Mesh)obj.Geometry)), box.Min, mm);
            }
        }
        return PrintFiles.Write(Plates(),parent,groupByPlate,prusa,slice);
    }
}
