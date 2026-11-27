using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using Go2Bed;

namespace Go2Bed.Grasshopper;

internal record PreparedKit(List<Component> Parts, BoundingBox SourceBounds, UnitSystem Units, uint Document,
    double Clearance = JointOptions.DefaultClearance)
{
    public override string ToString() => $"Go2Bed prepared kit ({Parts.Count} parts)";
}

internal record PackedKit(PreparedKit Source, List<Placement> Layout, double Width, double Depth, double Height)
{
    internal string? Printer => PrinterBeds.Matching(Width,Depth,Height)?.Name;

    public override string ToString() => $"Go2Bed packed kit ({Layout.Count} pieces)";

    internal IEnumerable<ExportPlate> Plates()
    {
        var numbered = Layout.Select((entry, i) => (entry, piece: new ExportPiece($"P{i+1:00} {entry.Part.Name}",entry.Placed)));
        return numbered.GroupBy(item => item.entry.Plate).Select(group =>
            new ExportPlate($"Plate-{group.Key:00}", Width, Depth, group.Select(item => item.piece), Point3d.Origin, 1, Printer));
    }

    internal static void SetPreview(IGH_DataAccess da,List<Placement> layout,double width,double depth,UnitSystem units)
    {
        var meshes = new GH_Structure<GH_Mesh>();
        var names = new GH_Structure<GH_String>();
        var beds = new GH_Structure<GH_Curve>();
        double u = RhinoMath.UnitScale(UnitSystem.Millimeters,units);
        for (int i=0;i<layout.Count;i++)
        {
            var entry=layout[i];
            var path=new GH_Path(entry.Plate-1);
            var origin=new Point3d((entry.Plate-1)*(width+15)*u,0,0);
            if (!beds.PathExists(path))
                beds.Append(new GH_Curve(new Rectangle3d(new Plane(origin,Vector3d.ZAxis),width*u,depth*u).ToNurbsCurve()),path);
            var mesh=Go2Bed.Parts.Shaded(entry.Placed.DuplicateMesh());
            mesh.Scale(u);
            mesh.Translate(origin-Point3d.Origin);
            meshes.Append(new GH_Mesh(mesh),path);
            names.Append(new GH_String($"P{i+1:00} {entry.Part.Name}"),path);
        }
        da.SetDataTree(0,meshes); da.SetDataTree(1,names); da.SetDataTree(2,beds);
    }
}
