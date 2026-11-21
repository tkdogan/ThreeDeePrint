using System.Drawing;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ThreeDeePrint;

public static class Kit
{
    public static RhinoObject[] BarnObjects(RhinoDoc doc)
    {
        return doc.Layers.Where(l => !l.IsDeleted && (l.FullPath.StartsWith("COMPARE - Simplified full size::")
            || l.FullPath.StartsWith("INTERIOR - Simplified full size::")) && !l.FullPath.EndsWith("::Printable union"))
            .SelectMany(l => doc.Objects.FindByLayer(l) ?? Array.Empty<RhinoObject>()).Where(o => !o.IsInstanceDefinitionGeometry).ToArray();
    }

    public static Layer Bake(RhinoDoc doc, RhinoObject[] objects, double scale, double width, double depth, double height, bool barn)
        => BakeWithJoints(doc, objects, scale, width, depth, height, barn, false);

    public static Layer BakeWithJoints(RhinoDoc doc, RhinoObject[] objects, double scale, double width, double depth, double height, bool barn, bool fingers)
        => BakeSettings(doc,objects,new PreviewSettings(scale,width,depth,height,fingers,.20),barn);

    internal static Layer BakeSettings(RhinoDoc doc, RhinoObject[] objects, PreviewSettings settings, bool barn = false)
    {
        var (scale,width,depth,height,fingers,clearance) = settings;
        if (objects.Length==0) throw new ArgumentException("No printable components selected.");
        if (!(scale>0 && width>6 && depth>6 && height>0)) throw new ArgumentException("Invalid scale or build volume.");
        if (doc.ModelUnitSystem is UnitSystem.None or UnitSystem.CustomUnits or UnitSystem.Unset)
            throw new ArgumentException("Set the document's physical units first.");
        double mm = RhinoMath.UnitScale(doc.ModelUnitSystem,UnitSystem.Millimeters);
        var parts = Parts.Prepare(objects,mm/scale,width-2*PlatePacking.Margin,depth-2*PlatePacking.Margin,barn,fingers,clearance);
        var layout = PlatePacking.Pack(parts,width,depth,height);
        if (layout.Count==0) throw new ArgumentException("No printable pieces at this scale.");
        var addedLayers = new List<int>();
        var addedObjects = new List<Guid>();
        Layer AddLayer(string name, Guid parent, Color color, bool visible=true)
        {
            int idx = doc.Layers.Add(new Layer {Name=name,ParentLayerId=parent,Color=color,IsVisible=visible});
            if (idx<0) throw new IOException("Could not add layer "+name);
            addedLayers.Add(idx); return doc.Layers[idx];
        }
        void Add(GeometryBase geometry,Layer l,string name,string kind,Guid kit,Guid source=default)
        {
            var attr = new ObjectAttributes {LayerIndex=l.Index,Name=name};
            attr.SetUserString("FlatPack.Kit",kit.ToString());
            attr.SetUserString("FlatPack.Kind",kind);
            if (source!=Guid.Empty) attr.SetUserString("FlatPack.Source",source.ToString());
            var id = doc.Objects.Add(geometry,attr);
            if (id==Guid.Empty) throw new IOException("Could not bake "+name);
            addedObjects.Add(id);
        }

        try
        {
            var top = doc.Layers.FirstOrDefault(l => !l.IsDeleted && l.FullPath=="3D Printer") ??
                AddLayer("3D Printer",Guid.Empty,Color.Teal);
            int number = 1;
            while (doc.Layers.Any(l=>!l.IsDeleted && l.ParentLayerId==top.Id && l.Name==$"Kit {number:00}")) number++;
            string kitName = $"Kit {number:00}";
            var root = AddLayer(kitName,top.Id,Color.Teal);
            var assembly = AddLayer("Assembly reference",root.Id,Color.SlateGray,false);
            var placementBounds = BoundingBox.Empty;
            foreach (var o in objects) placementBounds.Union(o.Geometry.GetBoundingBox(true));
            foreach (var o in doc.Objects.GetObjectList(ObjectType.Curve).Where(o => o.Attributes.GetUserString("FlatPack.Kind")=="Bed"))
                placementBounds.Union(o.Geometry.GetBoundingBox(true));
            var start = new Point3d(placementBounds.Max.X+15/mm,placementBounds.Min.Y,0);
            var visible = BoundingBox.Empty;
            var placedParts = new Dictionary<int,Layer>();
            int sequence = 0;
            foreach (var entry in layout)
            {
                var origin = start + new Vector3d((entry.Plate-1)*(width+15)/mm,0,0);
                if (!placedParts.TryGetValue(entry.Plate,out var plate))
                {
                    plate = AddLayer($"Plate {entry.Plate:00}",root.Id,Color.FromArgb(31,130,125));
                    placedParts.Add(entry.Plate,plate);
                    var outline = new Rectangle3d(new Plane(origin,Vector3d.ZAxis),width/mm,depth/mm).ToNurbsCurve();
                    Add(outline,plate,"Build plate outline","Bed",root.Id);
                    var tag = new TextDot(plate.Name,origin-new Vector3d(0,5/mm,0));
                    Add(tag,plate,"Plate label","Label",root.Id);
                    visible.Union(outline.GetBoundingBox(true));
                }
                string name = $"P{++sequence:00} {entry.Part.Name}";
                var printable = entry.Placed.DuplicateMesh(); printable.Scale(1/mm); printable.Translate(origin-Point3d.Origin);
                Add(printable,plate,name,"Print",root.Id,entry.Part.Source);
                visible.Union(printable.GetBoundingBox(true));
                var assembled = entry.Part.Assembled.DuplicateMesh(); assembled.Scale(1/mm);
                Add(assembled,assembly,name,"Reference",root.Id,entry.Part.Source);
            }
            doc.Views.ActiveView?.ActiveViewport.ZoomBoundingBox(visible);
            doc.Views.Redraw();
            RhinoApp.WriteLine($"{layout.Count} printable parts on {placedParts.Count} plates.");
            return root;
        }
        catch
        {
            foreach (var id in addedObjects) doc.Objects.Delete(id,true);
            foreach (int i in addedLayers.AsEnumerable().Reverse()) doc.Layers.Delete(i,true);
            throw;
        }
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
