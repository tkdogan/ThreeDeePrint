using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Go2Bed;

namespace Go2Bed.Grasshopper;

public abstract class ExportKitComponent : GH_Component
{
    private bool buttonDown;
    private string? lastDirectory;

    protected ExportKitComponent(string name, string nickname, string description)
        : base(name,nickname,description,"Go2Bed","Print") { }

    // Inputs between Folder and Write, such as Grouped plates.
    protected virtual void RegisterOptions(GH_InputParamManager p) { }
    private protected abstract ExportFormat Format(IGH_DataAccess da);
    private int WriteIndex => Params.Input.Count-1;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Reviewed kit","K","Reviewed kit from Preview Plates.",GH_ParamAccess.item);
        p.AddTextParameter("Folder","F","Existing parent directory for export.",GH_ParamAccess.item);
        RegisterOptions(p);
        p.AddBooleanParameter("Write","W","Connect a momentary Grasshopper Button. Press once per export.",GH_ParamAccess.item,false);
    }
    protected override void RegisterOutputParams(GH_OutputParamManager p) =>
        p.AddTextParameter("Last export","D","Directory of the last successful export.",GH_ParamAccess.item);
    protected override void SolveInstance(IGH_DataAccess da)
    {
        GH_ObjectWrapper input = new();
        string folder = "";
        bool write = false;
        if (!da.GetData(0,ref input) || !da.GetData(1,ref folder) || !da.GetData(WriteIndex,ref write)) return;
        if (da.Iteration > 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,"Supply one kit and one destination per exporter."); return; }
        bool pressed = write && !buttonDown;
        buttonDown = write;
        if (input.Value is not PackedKit kit) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,"Connect the Reviewed kit output from Preview Plates."); return; }
        var format = Format(da);
        if (format == ExportFormat.Project) Message = kit.Printer ?? "No preset";
        if (pressed)
        {
            try { lastDirectory = PrintFiles.Write(kit.Plates(),folder,format); }
            catch (Exception e) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,e.Message); }
        }
        if (lastDirectory != null) da.SetData(0,lastDirectory);
    }
}

public sealed class ExportStlComponent : ExportKitComponent
{
    public ExportStlComponent() : base("Export STL","STL","Write one STL per part, or one per plate.") { }
    public override Guid ComponentGuid => new("119210ad-83f5-4b50-9408-88078a25787c");
    protected override Bitmap Icon => Go2BedInfo.LoadIcon("Go2Bed.export-stl.png");
    protected override void RegisterOptions(GH_InputParamManager p) =>
        p.AddBooleanParameter("Grouped plates","G","Write one STL per plate instead of one per part.",GH_ParamAccess.item,false);
    private protected override ExportFormat Format(IGH_DataAccess da)
    {
        bool grouped = false;
        da.GetData(2,ref grouped);
        return grouped ? ExportFormat.GroupedStl : ExportFormat.Stl;
    }
}

public sealed class Export3mfComponent : ExportKitComponent
{
    public Export3mfComponent() : base("Export 3MF","3MF",
        "Write one 3MF slicer project per plate, with every piece named and in place. Open it in PrusaSlicer, Bambu Studio, OrcaSlicer, Cura or another slicer and slice there. " +
        "Prusa MK4S projects also carry the MK4S preset.") { }
    public override Guid ComponentGuid => new("4f1d2c7e-8a3b-4e65-9c0d-2b7a6e5f1c93");
    protected override Bitmap Icon => Go2BedInfo.LoadIcon("Go2Bed.export-prusa.png");
    private protected override ExportFormat Format(IGH_DataAccess da) => ExportFormat.Project;
}

// Export for Prusa from 1.1, kept so saved definitions still open. Slicing to G-code was removed.
public sealed class ExportPrusaComponent : ExportKitComponent
{
    public ExportPrusaComponent() : base("Export for Prusa (old)","Prusa","Replaced by Export 3MF. Slicing to G-code is no longer done here.") { }
    public override Guid ComponentGuid => new("30bc1ccc-a6dd-462c-81dc-e879b5ed6522");
    public override GH_Exposure Exposure => GH_Exposure.hidden;
    public override bool Obsolete => true;
    protected override Bitmap Icon => Go2BedInfo.LoadIcon("Go2Bed.export-prusa.png");
    protected override void RegisterOptions(GH_InputParamManager p) =>
        p.AddBooleanParameter("Slice","S","No longer used. Open the project in PrusaSlicer to slice.",GH_ParamAccess.item,false);
    private protected override ExportFormat Format(IGH_DataAccess da)
    {
        bool slice = false;
        if (da.GetData(2,ref slice) && slice)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,"Slicing to G-code was removed. Open the 3MF in PrusaSlicer to slice it.");
        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,"Replace this with Export 3MF.");
        return ExportFormat.Project;
    }
}
