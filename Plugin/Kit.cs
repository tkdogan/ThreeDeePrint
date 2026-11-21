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
    {
        if (objects.Length==0) throw new ArgumentException("No components found. Use Select for other models.");
        if (!(scale>0 && width>6 && depth>6 && height>0)) throw new ArgumentException("Invalid scale or build volume.");
        if (doc.ModelUnitSystem is UnitSystem.None or UnitSystem.CustomUnits or UnitSystem.Unset)
            throw new ArgumentException("Set the document's physical units first.");
        double mm = RhinoMath.UnitScale(doc.ModelUnitSystem,UnitSystem.Millimeters);
        var parts = Parts.Prepare(objects,mm/scale,width-2*PlatePacking.Margin,depth-2*PlatePacking.Margin,barn);
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
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        var root = doc.Layers.FindId(kitId) ?? throw new ArgumentException("Kit layer no longer exists.");
        double mm = RhinoMath.UnitScale(doc.ModelUnitSystem,UnitSystem.Millimeters);
        string staging = Path.Combine(parent,".flatpack-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            int count = 0, plateCount = 0;
            foreach (var plate in doc.Layers.Where(l => !l.IsDeleted && l.ParentLayerId==kitId))
            {
                var objects = doc.Objects.FindByLayer(plate) ?? Array.Empty<RhinoObject>();
                var pieces = objects.Where(o => o.Attributes.GetUserString("FlatPack.Kind")=="Print" &&
                    o.Attributes.GetUserString("FlatPack.Kit")==kitId.ToString()).OrderBy(o=>o.Attributes.Name).ToArray();
                if (pieces.Length==0) continue;
                var bed = objects.SingleOrDefault(o => o.Attributes.GetUserString("FlatPack.Kind")=="Bed") ??
                    throw new ArgumentException("Missing bed outline on "+plate.FullPath);
                var box = bed.Geometry.GetBoundingBox(true);
                string folder = Path.Combine(staging,$"Plate-{++plateCount:00}");
                using var grouped = groupByPlate ? new Mesh() : null;
                if (!groupByPlate) Directory.CreateDirectory(folder);
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
                    if (grouped != null) grouped.Append(mesh);
                    else WriteStl(Path.Combine(folder,$"{count:00}-{label}.stl"),mesh);
                }
                if (grouped != null) WriteStl(folder+".stl",grouped);
            }
            if (count==0) throw new ArgumentException("The kit has no baked print meshes.");
            string dest = Path.Combine(parent,"FlatPack-STL");
            for (int i=2;Directory.Exists(dest)||File.Exists(dest);i++) dest=Path.Combine(parent,$"FlatPack-STL-{i}");
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
