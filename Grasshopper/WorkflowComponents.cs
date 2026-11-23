using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Go2Bed;

namespace Go2Bed.Grasshopper;

public sealed class PreparePartsComponent : GH_Component
{
    public PreparePartsComponent() : base("Prepare Parts","Prepare","Scale model solids, trim overlaps and optionally cut alignment fingers.","Go2Bed","Print") { }
    public override Guid ComponentGuid => new("ba996d32-f0e3-495e-8ff4-edf27b405356");
    protected override Bitmap Icon => Go2BedInfo.LoadIcon();

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGeometryParameter("Components","G","Model solids in Rhino document units.",GH_ParamAccess.list);
        p[0].DataMapping = GH_DataMapping.Flatten;
        p.AddNumberParameter("Scale","S","Scale denominator, e.g. 50 for 1:50.",GH_ParamAccess.item,50);
        p.AddBooleanParameter("Finger joints","J","Cut eligible wall-to-slab alignment joints.",GH_ParamAccess.item,true);
        p.AddNumberParameter("Clearance","C","Joint clearance per side in mm.",GH_ParamAccess.item,.2);
    }
    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("Prepared kit","K","Connect to Flat Pack.",GH_ParamAccess.item);
        p.AddBrepParameter("Prepared solids","S","Trimmed assembly at print scale in Rhino document units.",GH_ParamAccess.list);
    }
    protected override void SolveInstance(IGH_DataAccess da)
    {
        var geometry = new List<IGH_GeometricGoo>();
        double scale = 50, clearance = .2;
        bool fingers = true;
        if (!da.GetDataList(0,geometry) || !da.GetData(1,ref scale) || !da.GetData(2,ref fingers) || !da.GetData(3,ref clearance)) return;
        try
        {
            var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Open a Rhino document first.");
            var bounds = BoundingBox.Empty;
            var sources = new List<SourcePart>();
            foreach (var item in geometry)
            {
                if (item?.ScriptVariable() is not GeometryBase g) throw new ArgumentException("Supply solid geometry.");
                var obj = item.IsReferencedGeometry ? doc.Objects.FindId(item.ReferenceID) : null;
                sources.Add(obj == null ? new SourcePart(g,null,Guid.Empty) : SourcePart.FromObject(obj) with { Geometry = g });
                bounds.Union(g.GetBoundingBox(true));
            }
            var parts = PrintLayout.Prepare(sources,doc.ModelUnitSystem,scale,fingers,clearance);
            da.SetData(0,new GH_ObjectWrapper(new PreparedKit(parts,bounds,doc.ModelUnitSystem,doc.RuntimeSerialNumber)));
            double units = RhinoMath.UnitScale(UnitSystem.Millimeters,doc.ModelUnitSystem);
            da.SetDataList(1,parts.Select(part =>
            {
                var solid = part.Solid.DuplicateBrep();
                solid.Transform(Transform.Scale(Point3d.Origin,units));
                return solid;
            }));
        }
        catch (Exception e) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,e.Message); }
    }
}

public sealed class PackBedsComponent : GH_Component
{
    public PackBedsComponent() : base("Flat Pack","Pack","Orient prepared pieces, split oversized parts and nest them onto beds.","Go2Bed","Print") { }
    public override Guid ComponentGuid => new("21981cba-70c0-467b-a577-0bf27ef501b2");
    protected override Bitmap Icon => Go2BedInfo.LoadIcon("Go2Bed.FlatPack.png");
    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Prepared kit","K","Prepared kit from Prepare Parts.",GH_ParamAccess.item);
        p.AddNumberParameter("Width","W","Bed width in mm.",GH_ParamAccess.item,250);
        p.AddNumberParameter("Depth","D","Bed depth in mm.",GH_ParamAccess.item,210);
        p.AddNumberParameter("Height","H","Build height in mm.",GH_ParamAccess.item,220);
    }
    protected override void RegisterOutputParams(GH_OutputParamManager p) =>
        p.AddGenericParameter("Packed kit","K","Connect to Preview or an exporter.",GH_ParamAccess.item);
    protected override void SolveInstance(IGH_DataAccess da)
    {
        GH_ObjectWrapper input = new();
        double width = 250, depth = 210, height = 220;
        if (!da.GetData(0,ref input) || !da.GetData(1,ref width) || !da.GetData(2,ref depth) || !da.GetData(3,ref height)) return;
        try
        {
            var source = input.Value as PreparedKit ?? throw new ArgumentException("Connect the Prepared kit output from Prepare Parts.");
            var layout = PrintLayout.Pack(source.Parts,width,depth,height);
            da.SetData(0,new GH_ObjectWrapper(new PackedKit(source,layout,width,depth)));
        }
        catch (Exception e) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,e.Message); }
    }
}

public sealed class PreviewPlatesComponent : GH_Component
{
    private readonly List<(PackedKit Kit,bool Colors)> kits = new();
    public PreviewPlatesComponent() : base("Preview","Preview","Review packed plates and bake the component to create a numbered Rhino kit.","Go2Bed","Print") { }
    public override Guid ComponentGuid => new("b168be01-74a7-4c8f-8354-df09eca9882c");
    protected override Bitmap Icon => Go2BedInfo.LoadIcon("Go2Bed.preview.png");
    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Packed kit","K","From Flat Pack.",GH_ParamAccess.item);
        p.AddBooleanParameter("Source colors","SC","Apply source object colors when baking.",GH_ParamAccess.item,true);
    }
    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddMeshParameter("Packed parts","P","Print meshes by plate.",GH_ParamAccess.tree);
        p.AddTextParameter("Part names","N","Matching IDs and names.",GH_ParamAccess.tree);
        p.AddCurveParameter("Beds","B","Build plate outlines.",GH_ParamAccess.tree);
        p.AddGenericParameter("Reviewed kit","K","Packed layout for either exporter.",GH_ParamAccess.item);
    }
    protected override void BeforeSolveInstance() => kits.Clear();
    protected override void SolveInstance(IGH_DataAccess da)
    {
        GH_ObjectWrapper input = new();
        bool colors = true;
        if (!da.GetData(0,ref input) || !da.GetData(1,ref colors)) return;
        if (input.Value is not PackedKit kit) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,"Connect a Packed kit from Flat Pack."); return; }
        PackedKit.SetPreview(da,kit.Layout,kit.Width,kit.Depth,kit.Source.Units);
        da.SetData(3,input);
        kits.Add((kit,colors));
    }
    public override bool IsBakeCapable => kits.Count > 0;
    public override void BakeGeometry(RhinoDoc doc,List<Guid> ids) => BakeGeometry(doc,null,ids);
    public override void BakeGeometry(RhinoDoc doc,ObjectAttributes? attributes,List<Guid> ids)
    {
        foreach (var (kit,colors) in kits)
        {
            if (doc.RuntimeSerialNumber != kit.Source.Document || doc.ModelUnitSystem != kit.Source.Units)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,"Recompute in the destination document before baking."); return; }
            PrintLayout.Bake(doc,kit.Layout,kit.Width,kit.Depth,kit.Source.SourceBounds,colors,ids);
        }
    }
}
