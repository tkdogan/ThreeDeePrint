using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input.Custom;

namespace Go2Bed;

public sealed class BedHeal : Command
{
    public override string EnglishName => "BedHeal";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        using var picker = new GetObject();
        picker.SetCommandPrompt("Select Breps to heal, then press Enter");
        picker.GeometryFilter = ObjectType.Brep | ObjectType.Surface;
        picker.SetCustomGeometryFilter((obj, _, _) => obj.Geometry is Brep);
        picker.SubObjectSelect = false;
        picker.EnablePreSelect(true, true);
        picker.GetMultiple(1, 0);
        if (picker.CommandResult() != Result.Success) return picker.CommandResult();

        int healed = 0, alreadySolid = 0, failed = 0;
        foreach (var reference in picker.Objects())
        {
            var obj = reference.Object();
            string label = string.IsNullOrWhiteSpace(obj.Name) ? obj.Id.ToString() : $"{obj.Name} ({obj.Id})";
            try
            {
                var result = BrepHealing.Heal((Brep)obj.Geometry, doc.ModelAbsoluteTolerance,
                    doc.ModelAngleToleranceRadians, out bool changed);
                if (result == null)
                    throw new InvalidOperationException("Could not enclose a valid solid.");

                if (!changed)
                {
                    alreadySolid++;
                    continue;
                }

                if (!doc.Objects.Replace(obj.Id, result))
                    throw new InvalidOperationException("Could not replace the document object.");

                healed++;
            }
            catch (Exception ex)
            {
                failed++;
                RhinoApp.WriteLine($"BedHeal: {label}: {ex.Message}");
            }
        }

        if (healed > 0) doc.Views.Redraw();
        RhinoApp.WriteLine($"BedHeal: {healed} updated, {alreadySolid} already clean, {failed} failed.");
        return healed + alreadySolid > 0 ? Result.Success : Result.Failure;
    }
}
