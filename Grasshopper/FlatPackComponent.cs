using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Go2Bed;

namespace Go2Bed.Grasshopper;

public sealed class Go2BedInfo : GH_AssemblyInfo
{
    public override string Name => "Go2Bed";
    public override Guid Id => new("CC798366-ADBD-43B4-AD58-539BFCFD4BFC");
    public override string AuthorName => "Timur Dogan";
    public override string Description => "Prepare flat-packed architectural models for printing.";
    public override Bitmap Icon => LoadIcon();
    internal static Bitmap LoadIcon(string resource = "Go2Bed.Icon.png")
    {
        using var stream = typeof(Go2BedInfo).Assembly.GetManifestResourceStream(resource);
        using var image = new Bitmap(stream!);
        return new Bitmap(image);
    }

    // Shows a failure on the component. Input problems get one message each, so each is readable.
    internal static void Report(GH_Component component, Exception e)
    {
        if (e is InputException input)
            foreach (var p in input.Problems) component.AddRuntimeMessage(GH_RuntimeMessageLevel.Error,$"{p.Name}: {p.Reason}");
        else component.AddRuntimeMessage(GH_RuntimeMessageLevel.Error,e.Message);
    }
}

public sealed class Go2BedPriority : GH_AssemblyPriority
{
    public override GH_LoadingInstruction PriorityLoad()
    {
        global::Grasshopper.Instances.ComponentServer.AddCategoryIcon("Go2Bed", Go2BedInfo.LoadIcon());
        return GH_LoadingInstruction.Proceed;
    }
}

public sealed class FlatPackComponent : GH_Component
{
    private List<Placement>? solved;
    private BoundingBox sourceBounds;
    private double width, depth;
    private bool useSourceColors;
    private uint documentId;
    private UnitSystem units;
    public override GH_Exposure Exposure => GH_Exposure.hidden;
    public FlatPackComponent() : base("Flat Pack (Legacy)","FlatPack",
        "Trim and orient components, then pack them onto build plates. Bake this component to create an exportable Rhino kit.",
        "Go2Bed","Print") { }
    public override Guid ComponentGuid => new("CE19F9B4-B880-4903-ADF8-F47892485583");
    protected override Bitmap Icon => Go2BedInfo.LoadIcon("Go2Bed.FlatPack.png");

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGeometryParameter("Components","G","Separate solid Breps, extrusions or closed meshes in Rhino document units.",GH_ParamAccess.list);
        p[0].DataMapping = GH_DataMapping.Flatten;
        p.AddNumberParameter("Scale","S","Scale denominator (50 means 1:50).",GH_ParamAccess.item,50);
        p.AddNumberParameter("Bed width","W","Bed width in mm.",GH_ParamAccess.item,250);
        p.AddNumberParameter("Bed depth","D","Bed depth in mm.",GH_ParamAccess.item,210);
        p.AddNumberParameter("Build height","H","Maximum height in mm.",GH_ParamAccess.item,220);
        p.AddBooleanParameter("Finger joints","J","Add alignment fingers to eligible wall-to-slab joints.",GH_ParamAccess.item,true);
        p.AddNumberParameter("Clearance","C","Finger clearance per side in mm.",GH_ParamAccess.item,.20);
        int colors = p.AddBooleanParameter("Use source colors","SC","Copy the Rhino source colors to the plate and assembly meshes when baking.",GH_ParamAccess.item,true);
        p[colors].Optional = true;
    }
    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddMeshParameter("Packed parts","P","Meshes in Rhino units, one branch per plate. Bake the component to retain the kit metadata.",GH_ParamAccess.tree);
        p.AddTextParameter("Part names","N","Part IDs and names matching the mesh tree.",GH_ParamAccess.tree);
        p.AddCurveParameter("Beds","B","Bed outlines matching the plate branches.",GH_ParamAccess.tree);
    }

    // An input tree can trigger several solves. Keep the inputs as one set so Bake
    // always corresponds to the entire visible kit.
    protected override void BeforeSolveInstance() { solved = null; }
    protected override void SolveInstance(IGH_DataAccess da)
    {
        if (da.Iteration > 0)
        {
            solved = null;
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error,"Flatten Components and supply one value per setting to bake one kit.");
            return;
        }
        var geometry = new List<IGH_GeometricGoo>();
        double scale=50, height=220, clearance=.2;
        bool fingers=true;
        if (!da.GetDataList(0,geometry) || !da.GetData(1,ref scale) || !da.GetData(2,ref width) ||
            !da.GetData(3,ref depth) || !da.GetData(4,ref height) || !da.GetData(5,ref fingers) || !da.GetData(6,ref clearance)) return;
        useSourceColors = true;
        // Older Grasshopper definitions can load without the new optional input.
        if (Params.Input.Count > 7) da.GetData(7,ref useSourceColors);
        try
        {
            var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Open a Rhino model first.");
            documentId = doc.RuntimeSerialNumber;
            units = doc.ModelUnitSystem;
            sourceBounds = BoundingBox.Empty;
            var sources = new List<SourcePart>();
            foreach (var item in geometry)
            {
                if (item?.ScriptVariable() is not GeometryBase g) throw new ArgumentException("Supply solid geometry.");
                var obj = item.IsReferencedGeometry ? doc.Objects.FindId(item.ReferenceID) : null;
                foreach (var source in SourcePart.FromGeometry(doc,g,obj))
                {
                    sources.Add(source);
                    sourceBounds.Union(source.Geometry.GetBoundingBox(true));
                }
            }
            var layout = PrintLayout.Solve(sources,units,scale,width,depth,height,JointOptions.Defaults with { Fingers = fingers, Clearance = clearance });
            PackedKit.SetPreview(da,layout,width,depth,units);
            solved = layout;
        }
        catch (Exception e) { Go2BedInfo.Report(this,e); }
    }
    public override bool IsBakeCapable => solved != null;
    public override void BakeGeometry(RhinoDoc doc, List<Guid> ids) => BakeGeometry(doc,null,ids);
    public override void BakeGeometry(RhinoDoc doc, ObjectAttributes? attributes, List<Guid> ids)
    {
        if (solved == null) return;
        if (doc.RuntimeSerialNumber!=documentId || doc.ModelUnitSystem!=units)
        { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,"Recompute in the destination document before baking."); return; }
        PrintLayout.Bake(doc,solved,width,depth,sourceBounds,useSourceColors,ids);
    }
}
