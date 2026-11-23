using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.PlugIns;

[assembly: PlugInDescription(DescriptionType.Icon, "Go2Bed.Icon.ico")]
[assembly: Guid("09D603C7-8651-4969-B62B-C8EA8CCDBE54")]
namespace Go2Bed;

public sealed class Go2BedPlugin : PlugIn { }

internal static class PreviewDefaults
{
    internal const double Scale = 50, Width = 250, Depth = 210, Height = 220;
}

public sealed class FlatPackPreview : Command
{
    public override string EnglishName => "FlatPackPreview";
    private PreviewSettings previous = PreviewSettings.Defaults;

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            using var picker = new GetObject();
            picker.SetCommandPrompt("Select components to print, then press Enter");
            picker.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh;
            picker.SubObjectSelect = false;
            picker.EnablePreSelect(true, true);
            picker.GetMultiple(1, 0);
            if (picker.CommandResult() != Result.Success) return picker.CommandResult();
            var inputs = picker.Objects().Select(r => r.Object()).ToArray();

            PreviewSettings settings;
            if (mode == RunMode.Interactive)
            {
                using var dialog = new PreviewDialog(previous);
                if (!dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow)) return Result.Cancel;
                settings = dialog.Values;
            }
            else
            {
                var scripted = ReadSettings();
                if (scripted==null) return Result.Cancel;
                settings = scripted;
            }
            var root = Kit.BakeSettings(doc,inputs,settings);
            previous = settings;
            RhinoApp.WriteLine($"Baked {root.FullPath}. Inspect the plate layers, then run FlatPackExportSTL.");
            return Result.Success;
        }
        catch (Exception e) { RhinoApp.WriteLine("FlatPackPreview: "+e.Message); return Result.Failure; }
    }

    private static PreviewSettings? ReadSettings()
    {
        var defaults = PreviewSettings.Defaults;
        double scale=defaults.Scale,width=defaults.Width,depth=defaults.Depth,height=defaults.Height;
        bool Read(string label, ref double value) =>
            RhinoGet.GetNumber(label,true,ref value,.01,1000000) is Result.Success or Result.Nothing;
        if (!Read("Scale denominator",ref scale) || !Read("Bed width in mm",ref width) ||
            !Read("Bed depth in mm",ref depth) || !Read("Build height in mm",ref height)) return null;
        using var joints = new GetOption();
        joints.SetCommandPrompt("Panel alignment joints");
        joints.AddOption("Plain"); joints.AddOption("FingerJoints");
        var sourceColors = new OptionToggle(defaults.UseSourceColors,"Off","On");
        int colorOption = joints.AddOptionToggle("SourceColors",ref sourceColors);
        joints.SetDefaultString("FingerJoints"); joints.AcceptNothing(true);
        GetResult jointChoice;
        do { jointChoice = joints.Get(); }
        while (jointChoice == GetResult.Option && joints.OptionIndex() == colorOption);
        if (jointChoice!=GetResult.Option && jointChoice!=GetResult.Nothing) return null;
        bool fingers=jointChoice==GetResult.Nothing || joints.Option().EnglishName=="FingerJoints";
        double clearance=defaults.Clearance;
        if (fingers && RhinoGet.GetNumber("Finger clearance per side in mm",true,ref clearance,.05,.60)
            is not (Result.Success or Result.Nothing)) return null;
        return new PreviewSettings(scale,width,depth,height,fingers,clearance,sourceColors.CurrentValue);
    }

}

public sealed class FlatPackExportSTL : Command
{
    public override string EnglishName => "FlatPackExportSTL";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => ExportCommand.Run(doc,mode,false);
}

public sealed class FlatPackExportPrusa : Command
{
    public override string EnglishName => "FlatPackExportPrusa";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => ExportCommand.Run(doc,mode,true);
}

internal static class ExportCommand
{
    internal static Result Run(RhinoDoc doc, RunMode mode, bool prusa)
    {
        try
        {
            using var picker = new GetObject();
            picker.SetCommandPrompt("Select baked parts to print, then press Enter");
            picker.GeometryFilter = ObjectType.Mesh;
            picker.SubObjectSelect = false;
            picker.SetCustomGeometryFilter((obj, _, _) => Kit.IsPrintPart(obj));
            picker.EnablePreSelect(true, true);
            picker.GetMultiple(1, 0);
            if (picker.CommandResult() != Result.Success) return picker.CommandResult();
            var parts = picker.Objects().Select(r => r.Object()).Where(Kit.IsPrintPart).ToArray();
            if (parts.Length == 0) throw new ArgumentException("Select printable meshes on the baked Plate layers.");
            RhinoApp.WriteLine($"Selected {parts.Length} printable pieces.");
            bool grouped = false, slice = false;
            if (!prusa)
            {
                using var format = new GetOption();
                format.SetCommandPrompt("STL export layout");
                format.AddOption("Parts"); format.AddOption("GroupedPlates");
                format.SetDefaultString("Parts"); format.AcceptNothing(true);
                var answer = format.Get();
                if (answer != GetResult.Option && answer != GetResult.Nothing) return format.CommandResult();
                grouped = answer == GetResult.Option && format.Option().EnglishName == "GroupedPlates";
            }
            else
            {
                using var output = new GetOption();
                output.SetCommandPrompt("MK4S HF 0.4 mm nozzle, Generic PLA, 0.20 mm Structural — export");
                output.AddOption("Project"); output.AddOption("ProjectAndBGCode");
                output.SetDefaultString("Project"); output.AcceptNothing(true);
                var answer = output.Get();
                if (answer != GetResult.Option && answer != GetResult.Nothing) return output.CommandResult();
                slice = answer == GetResult.Option && output.Option().EnglishName == "ProjectAndBGCode";
            }
            string path = "";
            if (mode == RunMode.Scripted)
            {
                if (RhinoGet.GetString("Output parent folder", false, ref path) != Result.Success) return Result.Cancel;
            }
            else
            {
                using var dialog = new Eto.Forms.SelectFolderDialog { Title = prusa ? "Choose a folder for the PrusaSlicer projects" : "Choose a folder for the STL kit" };
                if (dialog.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) != Eto.Forms.DialogResult.Ok) return Result.Cancel;
                path = dialog.Directory;
            }
            var destination = prusa ? Kit.ExportPrusa(doc, parts, path, slice) : Kit.ExportSelection(doc, parts, path, grouped);
            RhinoApp.WriteLine("Exported to " + destination);
            return Result.Success;
        }
        catch (Exception e) { RhinoApp.WriteLine(e.Message); return Result.Failure; }
    }
}
