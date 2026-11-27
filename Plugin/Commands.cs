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
    internal const double Scale = 50;
}

public sealed class BedLayout : Command
{
    public override string EnglishName => "BedLayout";
    private PreviewSettings previous = PreviewSettings.Defaults;

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            using var picker = new GetObject();
            picker.SetCommandPrompt("Select solids or blocks to print, then press Enter");
            picker.GeometryFilter = ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh | ObjectType.InstanceReference;
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
            RhinoApp.WriteLine($"Baked {root.FullPath}. Inspect the plate layers, then run Bed3MF or BedSTL.");
            return Result.Success;
        }
        catch (Exception e)
        {
            RhinoApp.WriteLine("BedLayout: "+e.Message);
            SelectCulprits(doc,e);
            return Result.Failure;
        }
    }

    // Selects the objects a failure names, so the user can find them in the model.
    internal static void SelectCulprits(RhinoDoc doc,Exception e)
    {
        var part=e as PartException ?? e.InnerException as PartException;
        if (part==null) return;
        doc.Objects.UnselectAll();
        int found=part.Objects.Count(id => doc.Objects.Select(id,true,true));
        if (found==0) return;
        doc.Views.Redraw();
        RhinoApp.WriteLine($"Selected the {(found==1 ? "object" : $"{found} objects")} involved. Run Zoom Selected to find {(found==1 ? "it" : "them")}.");
    }

    // Scripted -BedLayout: every setting is a command-line option, named after the dialog label.
    // Example: -BedLayout Printer=PrusaMINI FilamentPins=Off Scale=100 _Enter
    private PreviewSettings? ReadSettings()
    {
        var current = previous;
        var j = current.Joints;
        var scale = new OptionDouble(current.Scale,.01,1000000);
        var width = new OptionDouble(current.Width,7,10000);
        var depth = new OptionDouble(current.Depth,7,10000);
        var height = new OptionDouble(current.Height,1,10000);
        var clearance = new OptionDouble(j.Clearance,JointOptions.MinClearance,JointOptions.MaxClearance);
        var pinHole = new OptionDouble(j.PinHole,JointOptions.MinPinHole,JointOptions.MaxPinHole);
        var fingers = new OptionToggle(j.Fingers,"Off","On");
        var pins = new OptionToggle(j.Pins,"Off","On");
        var marks = new OptionToggle(j.Marks,"Off","On");
        var colors = new OptionToggle(current.UseSourceColors,"Off","On");
        var printers = PrinterBeds.All.Select(p => p.OptionName).Append(PrinterBeds.Custom).ToArray();
        while (true)
        {
            var bed = PrinterBeds.Matching(width.CurrentValue,depth.CurrentValue,height.CurrentValue);
            int printerIndex = bed == null ? printers.Length-1 : Array.IndexOf(PrinterBeds.All,bed);
            using var get = new GetOption();
            get.SetCommandPrompt("Lay out print plates. Press Enter to bake");
            get.AcceptNothing(true);
            get.AddOptionDouble("Scale",ref scale,Labels.ScaleHelp);
            int printerOption = get.AddOptionList(Labels.Printer,printers,printerIndex);
            get.AddOptionDouble("BedWidth",ref width,Labels.BedWidthHelp);
            get.AddOptionDouble("BedDepth",ref depth,Labels.BedDepthHelp);
            get.AddOptionDouble("BuildHeight",ref height,Labels.BuildHeightHelp);
            get.AddOptionToggle("FingerJoints",ref fingers);
            get.AddOptionToggle("FilamentPins",ref pins);
            get.AddOptionDouble("PinHole",ref pinHole,Labels.PinHoleHelp);
            get.AddOptionToggle("AssemblyMarks",ref marks);
            get.AddOptionDouble("Clearance",ref clearance,Labels.ClearanceHelp);
            get.AddOptionToggle("SourceColors",ref colors);
            var result = get.Get();
            if (result == GetResult.Nothing) break;
            if (result != GetResult.Option) return null;
            if (get.OptionIndex() == printerOption && PrinterBeds.Find(printers[get.Option().CurrentListOptionIndex]) is { } chosen)
                (width.CurrentValue,depth.CurrentValue,height.CurrentValue) = (chosen.Width,chosen.Depth,chosen.Height);
        }
        return new PreviewSettings(scale.CurrentValue,width.CurrentValue,depth.CurrentValue,height.CurrentValue,
            new JointOptions(fingers.CurrentValue,pins.CurrentValue,marks.CurrentValue,clearance.CurrentValue,pinHole.CurrentValue),
            colors.CurrentValue);
    }

}

public sealed class BedSTL : Command
{
    public override string EnglishName => "BedSTL";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => ExportCommand.Run(doc,mode,false);
}

public sealed class Bed3MF : Command
{
    public override string EnglishName => "Bed3MF";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => ExportCommand.Run(doc,mode,true);
}

// The 1.1 name of Bed3MF, kept so existing toolbars and scripts still work.
[CommandStyle(Style.Hidden)]
public sealed class BedPrusa : Command
{
    public override string EnglishName => "BedPrusa";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode) => ExportCommand.Run(doc,mode,true);
}

internal static class ExportCommand
{
    internal static Result Run(RhinoDoc doc, RunMode mode, bool projects)
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
            bool grouped = false;
            if (!projects)
            {
                using var format = new GetOption();
                format.SetCommandPrompt("STL export layout");
                format.AddOption("Parts"); format.AddOption("GroupedPlates");
                format.SetDefaultString("Parts"); format.AcceptNothing(true);
                var answer = format.Get();
                if (answer != GetResult.Option && answer != GetResult.Nothing) return format.CommandResult();
                grouped = answer == GetResult.Option && format.Option().EnglishName == "GroupedPlates";
            }
            string path = "";
            if (mode == RunMode.Scripted)
            {
                if (RhinoGet.GetString("Output parent folder", false, ref path) != Result.Success) return Result.Cancel;
            }
            else
            {
                using var dialog = new Eto.Forms.SelectFolderDialog { Title = projects ? "Choose a folder for the 3MF projects" : "Choose a folder for the STL kit" };
                if (dialog.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) != Eto.Forms.DialogResult.Ok) return Result.Cancel;
                path = dialog.Directory;
            }
            var destination = projects ? Kit.ExportProjects(doc, parts, path) : Kit.ExportSelection(doc, parts, path, grouped);
            RhinoApp.WriteLine("Exported to " + destination + (projects ? ". Open each plate's 3MF in your slicer, check the preset and slice." : ""));
            return Result.Success;
        }
        catch (Exception e) { RhinoApp.WriteLine(e.Message); return Result.Failure; }
    }
}
