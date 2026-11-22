using System.Drawing;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ThreeDeePrint;

public static class Kit
{
    public static Layer Bake(RhinoDoc doc, RhinoObject[] objects, double scale, double width, double depth, double height, bool barn)
        => BakeWithJoints(doc, objects, scale, width, depth, height, barn, false);

    public static Layer BakeWithJoints(RhinoDoc doc, RhinoObject[] objects, double scale, double width, double depth, double height, bool barn, bool fingers)
        => BakeSettings(doc,objects,new PreviewSettings(scale,width,depth,height,fingers,.20),barn);

    internal static Layer BakeSettings(RhinoDoc doc, RhinoObject[] objects, PreviewSettings settings, bool barn = false)
    {
        var (scale,width,depth,height,fingers,clearance) = settings;
        var sources = objects.Select(o => SourcePart.FromObject(o,barn));
        var layout = PrintLayout.Solve(sources,doc.ModelUnitSystem,scale,width,depth,height,fingers,clearance);
        var bounds = BoundingBox.Empty;
        foreach (var obj in objects) bounds.Union(obj.Geometry.GetBoundingBox(true));
        return PrintLayout.Bake(doc,layout,width,depth,bounds);
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
        string staging = Path.Combine(parent, ".flatpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            int count = 0;
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
                string directory = multipleKits ? Path.Combine(staging, $"Kit-{doc.Layers.FindId(plate.ParentLayerId).Index}") : staging;
                Directory.CreateDirectory(directory);
                // Keep the baked plate number even when earlier plates are unselected.
                string plateName = plate.Name.StartsWith("Plate ", StringComparison.Ordinal) &&
                    int.TryParse(plate.Name.Substring(6), out int number) && number > 0
                    ? $"Plate-{number:00}" : $"Layer-{plate.Index}";
                string folder = Path.Combine(directory, plateName);
                using var grouped = groupByPlate ? new Mesh() : null;
                var project = prusa ? new PrusaProject() : null;
                if (!groupByPlate && !prusa) Directory.CreateDirectory(folder);
                foreach (var obj in pieces)
                {
                    var mesh = (obj.Geometry as Mesh)?.DuplicateMesh() ?? throw new ArgumentException("Expected a baked mesh.");
                    mesh.Translate(Point3d.Origin-box.Min);
                    mesh.Scale(mm);
                    mesh.Faces.ConvertQuadsToTriangles();
                    if (!mesh.IsValid || !mesh.IsClosed || mesh.SolidOrientation()==0) throw new ArgumentException(obj.Name+" must be a closed oriented solid.");
                    if (mesh.SolidOrientation()<0) mesh.Flip(true,true,true);
                    var bounds = mesh.GetBoundingBox(true);
                    if (Math.Abs(bounds.Min.Z)>.02 || bounds.Min.X<-.02 || bounds.Min.Y<-.02 ||
                        bounds.Max.X>box.Diagonal.X*mm+.02 || bounds.Max.Y>box.Diagonal.Y*mm+.02)
                        throw new ArgumentException(obj.Name+" is off the bed on "+plate.FullPath);
                    var label = new string((obj.Name ?? "Part").Select(c=>char.IsLetterOrDigit(c)||c=='-'?c:'_').ToArray());
                    count++;
                    if (project != null) project.Add(obj.Name ?? "Part",mesh);
                    else if (grouped != null) grouped.Append(mesh);
                    else WriteStl(Path.Combine(folder,$"{count:00}-{label}.stl"),mesh);
                }
                if (project != null)
                {
                    string projectPath = folder+".3mf";
                    project.Write(projectPath);
                    if (slice) PrusaProject.Slice(projectPath);
                }
                if (grouped != null) WriteStl(folder+".stl",grouped);
            }
            string baseName = prusa ? "FlatPack-Prusa" : "FlatPack-STL";
            string dest = Path.Combine(parent,baseName);
            for (int i=2;Directory.Exists(dest)||File.Exists(dest);i++) dest=Path.Combine(parent,$"{baseName}-{i}");
            Directory.Move(staging,dest);
            return dest;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging,true); }
    }

    private static void WriteStl(string path,Mesh mesh)
    {
        using var writer = new BinaryWriter(File.Open(path,FileMode.CreateNew),Encoding.ASCII);
        writer.Write(new byte[80]); writer.Write((uint)mesh.Faces.Count);
        foreach (var f in mesh.Faces)
        {
            var a = mesh.Vertices[f.A]; var b = mesh.Vertices[f.B]; var c = mesh.Vertices[f.C];
            var n = Vector3d.CrossProduct((Vector3d)(b-a),(Vector3d)(c-a));
            if (!n.Unitize()) throw new ArgumentException("Degenerate STL triangle in "+Path.GetFileName(path));
            writer.Write((float)n.X); writer.Write((float)n.Y); writer.Write((float)n.Z);
            foreach (var p in new[]{a,b,c}) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
            writer.Write((ushort)0);
        }
    }
}
