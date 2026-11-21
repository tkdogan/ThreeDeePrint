using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.PlugIns;

[assembly: Guid("09D603C7-8651-4969-B62B-C8EA8CCDBE54")]
namespace ThreeDeePrint;

public sealed class ThreeDeePrintPlugin : PlugIn { }

internal static class PreviewDefaults
{
    internal const double Scale = 50, Width = 250, Depth = 210, Height = 220;
}

public sealed class FlatPackBarnPreview : Command
{
    public override string EnglishName => "FlatPackBarnPreview";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            Kit.Bake(doc, Kit.BarnObjects(doc), PreviewDefaults.Scale, PreviewDefaults.Width,
                PreviewDefaults.Depth, PreviewDefaults.Height, true);
            return Result.Success;
        }
        catch (Exception e) { RhinoApp.WriteLine("FlatPackBarnPreview: " + e.Message); return Result.Failure; }
    }
}

public sealed class FlatPackPreview : Command
{
    public override string EnglishName => "FlatPackPreview";
    private PreviewSettings previous = PreviewSettings.Defaults;

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            var barnObjects = Kit.BarnObjects(doc);
            PreviewSettings settings;
            if (mode == RunMode.Interactive)
            {
                using var dialog = new PreviewDialog(barnObjects.Length>0,previous);
                if (!dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow)) return Result.Cancel;
                settings = dialog.Values;
            }
            else
            {
                var scripted = ReadSettings();
                if (scripted==null) return Result.Cancel;
                settings = scripted;
            }
            var inputs = barnObjects;
            if (!settings.Barn)
            {
                var result = RhinoGet.GetMultipleObjects("Select individual printable components",false,
                    ObjectType.Brep | ObjectType.Extrusion | ObjectType.Mesh,out var refs);
                if (result!=Result.Success) return result;
                inputs = refs.Select(r=>r.Object()).ToArray();
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
        using var prompt = new GetOption();
        prompt.SetCommandPrompt("Source components");
        prompt.AddOption("Barn"); prompt.AddOption("Select");
        prompt.SetDefaultString("Barn"); prompt.AcceptNothing(true);
        var answer = prompt.Get();
        if (answer!=GetResult.Option && answer!=GetResult.Nothing) return null;
        bool barn = answer==GetResult.Nothing || prompt.Option().EnglishName=="Barn";
        var defaults = PreviewSettings.Defaults;
        double scale=defaults.Scale,width=defaults.Width,depth=defaults.Depth,height=defaults.Height;
        bool Read(string label, ref double value) =>
            RhinoGet.GetNumber(label,true,ref value,.01,1000000) is Result.Success or Result.Nothing;
        if (!Read("Scale denominator",ref scale) || !Read("Bed width in mm",ref width) ||
            !Read("Bed depth in mm",ref depth) || !Read("Build height in mm",ref height)) return null;
        using var joints = new GetOption();
        joints.SetCommandPrompt("Panel alignment joints");
        joints.AddOption("Plain"); joints.AddOption("FingerJoints");
        joints.SetDefaultString("Plain"); joints.AcceptNothing(true);
        var jointChoice=joints.Get();
        if (jointChoice!=GetResult.Option && jointChoice!=GetResult.Nothing) return null;
        bool fingers=jointChoice==GetResult.Option && joints.Option().EnglishName=="FingerJoints";
        double clearance=defaults.Clearance;
        if (fingers && RhinoGet.GetNumber("Finger clearance per side in mm",true,ref clearance,.05,.60)
            is not (Result.Success or Result.Nothing)) return null;
        return new PreviewSettings(barn,scale,width,depth,height,fingers,clearance);
    }

}

public sealed class FlatPackExportSTL : Command
{
    public override string EnglishName => "FlatPackExportSTL";
    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            var result = RhinoGet.GetOneObject("Pick a baked mesh from the kit to export", false, ObjectType.Mesh, out var part);
            if (result != Result.Success) return result;
            string? kitId = part.Object().Attributes.GetUserString("FlatPack.Kit");
            if (!Guid.TryParse(kitId, out var id)) throw new ArgumentException("Select a mesh from a print plate.");
            using var format = new GetOption();
            format.SetCommandPrompt("STL export layout");
            format.AddOption("Parts"); format.AddOption("GroupedPlates");
            format.SetDefaultString("Parts"); format.AcceptNothing(true);
            var answer = format.Get();
            if (answer != GetResult.Option && answer != GetResult.Nothing) return format.CommandResult();
            bool grouped = answer == GetResult.Option && format.Option().EnglishName == "GroupedPlates";
            string path = "";
            if (mode == RunMode.Scripted)
            {
                if (RhinoGet.GetString("Output parent folder", false, ref path) != Result.Success) return Result.Cancel;
            }
            else
            {
                using var dialog = new Eto.Forms.SelectFolderDialog { Title = "Choose a folder for the STL kit" };
                if (dialog.ShowDialog(Rhino.UI.RhinoEtoApp.MainWindow) != Eto.Forms.DialogResult.Ok) return Result.Cancel;
                path = dialog.Directory;
            }
            RhinoApp.WriteLine("Wrote STL files to " + Kit.ExportStls(doc, id, path, grouped));
            return Result.Success;
        }
        catch (Exception e) { RhinoApp.WriteLine("FlatPackExportSTL: " + e.Message); return Result.Failure; }
    }
}
