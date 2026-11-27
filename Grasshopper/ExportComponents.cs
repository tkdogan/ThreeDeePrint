using System.Drawing;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Go2Bed;

namespace Go2Bed.Grasshopper;

public abstract class ExportKitComponent : GH_Component
{
    private readonly bool prusa;
    private bool buttonDown;
    private string? lastDirectory;

    protected ExportKitComponent(bool prusa) : base(prusa ? "Export for Prusa" : "Export STL",
        prusa ? "Prusa" : "STL",prusa ? "Write a PrusaSlicer project for each plate, using the Prusa MK4S preset." : "Write one STL per part, or one per plate.",
        "Go2Bed","Print") => this.prusa = prusa;

    protected override Bitmap Icon => Go2BedInfo.LoadIcon(prusa ? "Go2Bed.export-prusa.png" : "Go2Bed.export-stl.png");
    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        // GH_Component registers its parameters during base construction.
        bool isPrusa = this is ExportPrusaComponent;
        p.AddGenericParameter("Reviewed kit","K","Reviewed kit from Preview Plates.",GH_ParamAccess.item);
        p.AddTextParameter("Folder","F","Existing parent directory for export.",GH_ParamAccess.item);
        p.AddBooleanParameter(isPrusa ? "Slice" : "Grouped plates",isPrusa ? "S" : "G",
            isPrusa ? "Also generate BGCode using the installed PrusaSlicer." : "Write one STL per plate instead of one per part.",
            GH_ParamAccess.item,false);
        p.AddBooleanParameter("Write","W","Connect a momentary Grasshopper Button. Press once per export.",GH_ParamAccess.item,false);
    }
    protected override void RegisterOutputParams(GH_OutputParamManager p) =>
        p.AddTextParameter("Last export","D","Directory of the last successful export.",GH_ParamAccess.item);
    protected override void SolveInstance(IGH_DataAccess da)
    {
        GH_ObjectWrapper input = new();
        string folder = "";
        bool option = false, write = false;
        if (!da.GetData(0,ref input) || !da.GetData(1,ref folder) || !da.GetData(2,ref option) || !da.GetData(3,ref write)) return;
        if (da.Iteration > 0) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,"Supply one kit and one destination per exporter."); return; }
        bool pressed = write && !buttonDown;
        buttonDown = write;
        if (input.Value is not PackedKit kit) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,"Connect the Reviewed kit output from Preview Plates."); return; }
        if (pressed)
        {
            try { lastDirectory = PrintFiles.Write(kit.Plates(),folder,!prusa && option,prusa,prusa && option); }
            catch (Exception e) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error,e.Message); }
        }
        if (lastDirectory != null) da.SetData(0,lastDirectory);
    }
}

public sealed class ExportStlComponent : ExportKitComponent
{
    public ExportStlComponent() : base(false) { }
    public override Guid ComponentGuid => new("119210ad-83f5-4b50-9408-88078a25787c");
}

public sealed class ExportPrusaComponent : ExportKitComponent
{
    public ExportPrusaComponent() : base(true) { }
    public override Guid ComponentGuid => new("30bc1ccc-a6dd-462c-81dc-e879b5ed6522");
}
