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
    public PreparePartsComponent() : base("Prepare Parts","Prepare",
        "Scale model solids, trim overlaps and cut the alignment joints you choose: finger joints, filament pins and assembly marks.","Go2Bed","Print") { }
    public override Guid ComponentGuid => new("ba996d32-f0e3-495e-8ff4-edf27b405356");
    protected override Bitmap Icon => Go2BedInfo.LoadIcon();

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        var d = JointOptions.Defaults;
        p.AddGeometryParameter("Components","G","Model solids in Rhino document units.",GH_ParamAccess.list);
        p[0].DataMapping = GH_DataMapping.Flatten;
        p.AddNumberParameter(Labels.Scale,"S",Labels.ScaleHelp,GH_ParamAccess.item,50);
        p.AddBooleanParameter(Labels.FingerJoints,"J",Labels.FingerJointsHelp,GH_ParamAccess.item,d.Fingers);
        p.AddNumberParameter(Labels.Clearance,"C",Labels.ClearanceHelp,GH_ParamAccess.item,d.Clearance);
        // Added in 1.2. Definitions saved earlier load without them and keep the defaults.
        p[p.AddBooleanParameter(Labels.FilamentPins,"P",Labels.FilamentPinsHelp,GH_ParamAccess.item,d.Pins)].Optional = true;
        p[p.AddNumberParameter(Labels.PinHole,"PD",Labels.PinHoleHelp,GH_ParamAccess.item,d.PinHole)].Optional = true;
        p[p.AddBooleanParameter(Labels.AssemblyMarks,"M",Labels.AssemblyMarksHelp,GH_ParamAccess.item,d.Marks)].Optional = true;
    }
    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("Prepared kit","K","Connect to Flat Pack.",GH_ParamAccess.item);
        p.AddBrepParameter("Prepared solids","S","Trimmed and jointed assembly at print scale in Rhino document units.",GH_ParamAccess.list);
    }
    protected override void SolveInstance(IGH_DataAccess da)
    {
        var geometry = new List<IGH_GeometricGoo>();
        var d = JointOptions.Defaults;
        double scale = 50, clearance = d.Clearance, pinHole = d.PinHole;
        bool fingers = d.Fingers, pins = d.Pins, marks = d.Marks;
        if (!da.GetDataList(0,geometry) || !da.GetData(1,ref scale) || !da.GetData(2,ref fingers) || !da.GetData(3,ref clearance)) return;
        if (Params.Input.Count > 4) da.GetData(4,ref pins);
        if (Params.Input.Count > 5) da.GetData(5,ref pinHole);
        if (Params.Input.Count > 6) da.GetData(6,ref marks);
        try
        {
            var doc = RhinoDoc.ActiveDoc ?? throw new InvalidOperationException("Open a Rhino document first.");
            var bounds = BoundingBox.Empty;
            var sources = new List<SourcePart>();
            foreach (var item in geometry)
            {
                if (item?.ScriptVariable() is not GeometryBase g) throw new ArgumentException("Supply solid geometry.");
                var obj = item.IsReferencedGeometry ? doc.Objects.FindId(item.ReferenceID) : null;
                foreach (var source in SourcePart.FromGeometry(doc,g,obj))
                {
                    sources.Add(source);
                    bounds.Union(source.Geometry.GetBoundingBox(true));
                }
            }
            var joints = new JointOptions(fingers,pins,marks,clearance,pinHole);
            var parts = PrintLayout.Prepare(sources,doc.ModelUnitSystem,scale,joints);
            da.SetData(0,new GH_ObjectWrapper(new PreparedKit(parts,bounds,doc.ModelUnitSystem,doc.RuntimeSerialNumber,clearance)));
            double units = RhinoMath.UnitScale(UnitSystem.Millimeters,doc.ModelUnitSystem);
            da.SetDataList(1,parts.Select(part =>
            {
                var solid = part.Solid.DuplicateBrep();
                solid.Transform(Transform.Scale(Point3d.Origin,units));
                return solid;
            }));
        }
        catch (Exception e) { Go2BedInfo.Report(this,e); }
    }
}

public sealed class PrinterBedComponent : GH_Component
{
    public PrinterBedComponent() : base("Printer Bed","Printer",
        "Build volume of a common printer, for Flat Pack. Connect a Value List of the Printers output, or type a name.","Go2Bed","Print") { }
    public override Guid ComponentGuid => new("5d0b8c52-2f6e-4f0a-9a36-0d7f1f3c8e41");
    protected override Bitmap Icon => Go2BedInfo.LoadIcon("Go2Bed.FlatPack.png");
    protected override void RegisterInputParams(GH_InputParamManager p) =>
        p.AddTextParameter(Labels.Printer,"P","Printer name, such as \"" + PrinterBeds.Default.Name + "\", or its index in Printers.",
            GH_ParamAccess.item,PrinterBeds.Default.Name);
    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddNumberParameter(Labels.BedWidth,"W",Labels.BedWidthHelp,GH_ParamAccess.item);
        p.AddNumberParameter(Labels.BedDepth,"D",Labels.BedDepthHelp,GH_ParamAccess.item);
        p.AddNumberParameter(Labels.BuildHeight,"H",Labels.BuildHeightHelp,GH_ParamAccess.item);
        p.AddTextParameter("Printers","N","Every printer in the library.",GH_ParamAccess.list);
    }
    protected override void SolveInstance(IGH_DataAccess da)
    {
        string name = PrinterBeds.Default.Name;
        da.SetDataList(3,PrinterBeds.All.Select(bed => bed.Name));
        if (!da.GetData(0,ref name)) return;
        var bed = int.TryParse(name,out int index) && index >= 0 && index < PrinterBeds.All.Length
            ? PrinterBeds.All[index] : PrinterBeds.Find(name);
        if (bed == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error,$"Unknown printer \"{name}\". Choose one of the Printers output, or wire your own bed size into Flat Pack.");
            return;
        }
        Message = bed.Name;
        da.SetData(0,bed.Width); da.SetData(1,bed.Depth); da.SetData(2,bed.Height);
    }
}

public sealed class PackBedsComponent : GH_Component
{
    public PackBedsComponent() : base("Flat Pack","Pack","Orient prepared pieces, split oversized parts and nest them onto beds.","Go2Bed","Print") { }
    public override Guid ComponentGuid => new("21981cba-70c0-467b-a577-0bf27ef501b2");
    protected override Bitmap Icon => Go2BedInfo.LoadIcon("Go2Bed.FlatPack.png");
    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        var bed = PrinterBeds.Default;
        p.AddGenericParameter("Prepared kit","K","Prepared kit from Prepare Parts.",GH_ParamAccess.item);
        p.AddNumberParameter(Labels.BedWidth,"W",Labels.BedWidthHelp+" Use Printer Bed for common printers.",GH_ParamAccess.item,bed.Width);
        p.AddNumberParameter(Labels.BedDepth,"D",Labels.BedDepthHelp,GH_ParamAccess.item,bed.Depth);
        p.AddNumberParameter(Labels.BuildHeight,"H",Labels.BuildHeightHelp,GH_ParamAccess.item,bed.Height);
    }
    protected override void RegisterOutputParams(GH_OutputParamManager p) =>
        p.AddGenericParameter("Packed kit","K","Connect to Preview Plates.",GH_ParamAccess.item);
    protected override void SolveInstance(IGH_DataAccess da)
    {
        GH_ObjectWrapper input = new();
        var bed = PrinterBeds.Default;
        double width = bed.Width, depth = bed.Depth, height = bed.Height;
        if (!da.GetData(0,ref input) || !da.GetData(1,ref width) || !da.GetData(2,ref depth) || !da.GetData(3,ref height)) return;
        try
        {
            var source = input.Value as PreparedKit ?? throw new ArgumentException("Connect the Prepared kit output from Prepare Parts.");
            var layout = PrintLayout.Pack(source.Parts,width,depth,height,source.Clearance);
            Message = PrinterBeds.Matching(width,depth,height)?.Name ?? $"{width:0} × {depth:0} × {height:0} mm";
            da.SetData(0,new GH_ObjectWrapper(new PackedKit(source,layout,width,depth,height)));
        }
        catch (Exception e) { Go2BedInfo.Report(this,e); }
    }
}

public sealed class PreviewPlatesComponent : GH_Component
{
    private readonly List<(PackedKit Kit,bool Colors)> kits = new();
    public PreviewPlatesComponent() : base("Preview Plates","Preview","Review packed plates. Right-click and choose Bake to create a numbered Rhino kit.","Go2Bed","Print") { }
    public override Guid ComponentGuid => new("b168be01-74a7-4c8f-8354-df09eca9882c");
    protected override Bitmap Icon => Go2BedInfo.LoadIcon("Go2Bed.preview.png");
    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Packed kit","K","Packed kit from Flat Pack.",GH_ParamAccess.item);
        p.AddBooleanParameter(Labels.UseSourceColors,"SC",Labels.UseSourceColorsHelp,GH_ParamAccess.item,true);
    }
    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddMeshParameter("Packed parts","P","Print meshes by plate.",GH_ParamAccess.tree);
        p.AddTextParameter("Part names","N","Matching IDs and names.",GH_ParamAccess.tree);
        p.AddCurveParameter("Bed outlines","B","Build plate outlines matching the plate branches.",GH_ParamAccess.tree);
        p.AddGenericParameter("Reviewed kit","K","Connect to Export STL or Export 3MF.",GH_ParamAccess.item);
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
            PrintLayout.Bake(doc,kit.Layout,kit.Width,kit.Depth,kit.Source.SourceBounds,colors,ids,kit.Printer);
        }
    }
}
