using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;

namespace Go2Bed;

// Included only in the Debug build. The sample is copied alongside the RHP.
public sealed class FlatPackDEBUG : Command
{
    public override string EnglishName => "FlatPackDEBUG";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        try
        {
            var buildDirectory = Path.GetDirectoryName(typeof(FlatPackDEBUG).Assembly.Location)!;
            var path = Path.Combine(buildDirectory, "Barn_original_with_simplified_interior.3dm");
            var sample = doc.Path == path ? doc : RhinoDoc.Open(path, out _);
            if (sample == null) throw new IOException($"Could not open the debug sample at {path}.");

            var objects = sample.Layers.Where(layer => !layer.IsDeleted &&
                    (layer.FullPath.StartsWith("COMPARE - Simplified full size::", StringComparison.Ordinal) ||
                     layer.FullPath.StartsWith("INTERIOR - Simplified full size::", StringComparison.Ordinal)) &&
                    !layer.FullPath.EndsWith("::Printable union", StringComparison.Ordinal))
                .SelectMany(layer => sample.Objects.FindByLayer(layer) ?? Array.Empty<RhinoObject>())
                .Where(obj => !obj.IsInstanceDefinitionGeometry)
                .ToArray();
            if (objects.Length == 0) throw new InvalidDataException("The sample has no simplified barn components.");

            var kit = Kit.BakeSettings(sample, objects, PreviewSettings.Defaults, barn: true);
            RhinoApp.WriteLine($"Baked the debug barn to {kit.FullPath}.");
            return Result.Success;
        }
        catch (Exception error)
        {
            RhinoApp.WriteLine($"{EnglishName}: {error.Message}");
            return Result.Failure;
        }
    }
}
