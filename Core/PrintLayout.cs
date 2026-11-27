using System.Drawing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Go2Bed;

internal static class PrintLayout
{
    internal static List<Placement> Solve(IEnumerable<SourcePart> objects, UnitSystem units, double scale,
        double width, double depth, double height, JointOptions joints)
    {
        return Pack(Prepare(objects,units,scale,joints),width,depth,height,joints.Clearance);
    }

    internal static List<Component> Prepare(IEnumerable<SourcePart> objects, UnitSystem units, double scale, JointOptions joints)
    {
        if (!double.IsFinite(scale) || scale <= 0) throw new ArgumentException("Invalid scale.");
        joints.Validate();
        if (units is UnitSystem.None or UnitSystem.CustomUnits or UnitSystem.Unset)
            throw new ArgumentException("Set the document's physical units first.");
        var mm = RhinoMath.UnitScale(units,UnitSystem.Millimeters);
        return Parts.Clean(objects,mm/scale,joints);
    }

    internal static List<Placement> Pack(List<Component> components, double width, double depth, double height, double seamClearance = .2)
    {
        if (!double.IsFinite(width) || !double.IsFinite(depth) || !double.IsFinite(height) ||
            width <= 6 || depth <= 6 || height <= 0) throw new ArgumentException("Invalid build volume.");
        var parts = Parts.Orient(components,width-2*PlatePacking.Margin,depth-2*PlatePacking.Margin,
            Math.Clamp(seamClearance,.05,.60));
        var layout = PlatePacking.Pack(parts,width,depth,height);
        if (layout.Count==0) throw new ArgumentException("No printable pieces at this scale.");
        return layout;
    }

    internal static Layer Bake(RhinoDoc doc, List<Placement> layout, double width, double depth,
        BoundingBox placementBounds, bool useSourceColors, List<Guid>? bakedIds = null)
    {
        double mm = RhinoMath.UnitScale(doc.ModelUnitSystem,UnitSystem.Millimeters);
        var addedLayers = new List<int>();
        var addedObjects = new List<Guid>();
        Layer AddLayer(string name, Guid parent, Color color, bool visible=true)
        {
            int idx = doc.Layers.Add(new Layer {Name=name,ParentLayerId=parent,Color=color,IsVisible=visible});
            if (idx<0) throw new IOException("Could not add layer "+name);
            addedLayers.Add(idx); return doc.Layers[idx];
        }
        void Add(GeometryBase geometry,Layer l,string name,string kind,Guid kit,Guid source=default,Color? color=null)
        {
            var attr = new ObjectAttributes {LayerIndex=l.Index,Name=name};
            if (color is Color objectColor && !objectColor.IsEmpty)
            {
                attr.ColorSource = ObjectColorSource.ColorFromObject;
                attr.ObjectColor = objectColor;
            }
            attr.SetUserString("FlatPack.Kit",kit.ToString());
            attr.SetUserString("FlatPack.Kind",kind);
            if (source!=Guid.Empty) attr.SetUserString("FlatPack.Source",source.ToString());
            if (geometry is Mesh mesh && !mesh.IsValid)
            {
                // Rhino refuses invalid meshes; drop degenerate faces and unused vertices before giving up.
                mesh.Faces.CullDegenerateFaces();
                mesh.Vertices.CullUnused();
                mesh.Compact();
            }
            var id = doc.Objects.Add(geometry,attr);
            if (id==Guid.Empty)
            {
                string where = kind switch { "Print" => "its plate mesh", "Reference" => "its assembly reference mesh", _ => kind.ToLowerInvariant() };
                string why = geometry.IsValidWithLog(out string log) ? "Rhino refused it without saying why"
                    : "Rhino reports: " + string.Join(" ",log.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Take(4));
                string size = geometry is Mesh m ? $" ({m.Faces.Count} faces, {m.Vertices.Count} vertices, closed {m.IsClosed})" : "";
                throw new PartException($"Could not bake {name}: {where}{size} is not valid. {why}.",
                    source==Guid.Empty ? Array.Empty<Guid>() : new[] { source });
            }
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
                var sourceId = entry.Part.Source;
                var source = useSourceColors && sourceId != Guid.Empty ? doc.Objects.FindId(sourceId) : null;
                var color = source?.Attributes.DrawColor(doc);
                var printable = Parts.Shaded(entry.Placed.DuplicateMesh()); printable.Scale(1/mm); printable.Translate(origin-Point3d.Origin);
                Add(printable,plate,name,"Print",root.Id,sourceId,color);
                visible.Union(printable.GetBoundingBox(true));
                var assembled = Parts.Shaded(entry.Part.Assembled.DuplicateMesh()); assembled.Scale(1/mm);
                Add(assembled,assembly,name,"Reference",root.Id,sourceId,color);
            }
            doc.Views.ActiveView?.ActiveViewport.ZoomBoundingBox(visible);
            doc.Views.Redraw();
            RhinoApp.WriteLine($"{layout.Count} printable parts on {placedParts.Count} plates.");
            bakedIds?.AddRange(addedObjects);
            return root;
        }
        catch
        {
            foreach (var id in addedObjects) doc.Objects.Delete(id,true);
            foreach (int i in addedLayers.AsEnumerable().Reverse()) doc.Layers.Delete(i,true);
            throw;
        }
    }

}
