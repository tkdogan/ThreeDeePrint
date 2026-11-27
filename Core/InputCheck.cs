using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Go2Bed;

// One reason an input object cannot be printed as it is.
internal record InputProblem(string Name, string Reason, Guid Object);

// Every problem found in the input, reported together so they can all be fixed in one pass.
internal sealed class InputException : PartException
{
    internal InputException(IReadOnlyList<InputProblem> problems)
        : base(Describe(problems), problems.Select(p => p.Object).Where(id => id != Guid.Empty).Distinct().ToArray())
        => Problems = problems;
    internal IReadOnlyList<InputProblem> Problems { get; }

    private const int Listed = 25;
    private static string Describe(IReadOnlyList<InputProblem> problems)
    {
        var lines = problems.Take(Listed).Select(p => $"  - {p.Name}: {p.Reason}");
        string more = problems.Count > Listed ? $"\n  ... and {problems.Count - Listed} more." : "";
        return $"{problems.Count} input problem{(problems.Count == 1 ? "" : "s")} to fix before printing:\n" +
            string.Join("\n", lines) + more;
    }
}

internal static class InputCheck
{
    // Thinnest a part may come out at print scale: about two 0.4 mm nozzle widths.
    internal const double MinThickness = .8;

    // Geometry that carries no volume, such as annotation or curves inside a BIM block, is skipped.
    internal static bool CarriesNoVolume(GeometryBase g) => g is Curve or Rhino.Geometry.Point or PointCloud
        or AnnotationBase or Hatch or TextDot or Light or ClippingPlaneSurface;

    // Why a geometry cannot become a solid, worded for someone fixing the model.
    internal static string NotSolid(GeometryBase g, double factor)
    {
        switch (g)
        {
            case Brep b when !b.IsValid:
                return "the geometry is invalid. Run Check on it, then ExtractBadSrf and rebuild those faces.";
            case Brep b when b.Faces.Count == 1:
                return "it is a single surface with no thickness. Give it thickness, for example with OffsetSrf Solid=Yes.";
            case Brep b:
            {
                var naked = b.DuplicateNakedEdgeCurves(true, false) ?? Array.Empty<Curve>();
                if (naked.Length == 0) return "it does not enclose a solid. Run ShowEdges to look for non-manifold edges.";
                double longest = naked.Max(c => c.GetLength()) * factor;
                return $"it is open: {naked.Length} naked edge{(naked.Length == 1 ? "" : "s")}, the longest {longest:0.##} mm at print scale. " +
                    "Run ShowEdges, then Join or Cap to close it.";
            }
            case Mesh m:
            {
                var holes = m.GetNakedEdges() ?? Array.Empty<Polyline>();
                if (holes.Length > 0)
                    return $"the mesh is open with {holes.Length} hole{(holes.Length == 1 ? "" : "s")}. Run FillMeshHoles, or MeshRepair.";
                return "the mesh is not a closed solid. Run MeshRepair to look for non-manifold or flipped faces.";
            }
            case Extrusion e when !e.IsSolid:
                return "it is an extrusion without end caps. Run Cap on it.";
            case SubD:
                return "SubD objects are not read directly. Convert it with ToNURBS first.";
            case Surface:
                return "it is a single surface with no thickness. Give it thickness, for example with OffsetSrf Solid=Yes.";
            case InstanceReferenceGeometry:
                return "the block definition could not be found in this document.";
            default:
                return $"a {g.ObjectType} is not a solid. Select solid polysurfaces, extrusions, closed meshes or blocks.";
        }
    }

    // A part thinner than a printable wall in every direction of its bounding box.
    internal static string? TooThin(Brep solid)
    {
        var box = solid.GetBoundingBox(true);
        double thinnest = new[] { box.Diagonal.X, box.Diagonal.Y, box.Diagonal.Z }.Min();
        // Diagonal members can look thicker in a world-aligned box; also measure along the largest flat face.
        var face = solid.Faces.Where(f => f.IsPlanar(1e-3)).OrderByDescending(f => AreaMassProperties.Compute(f)?.Area ?? 0).FirstOrDefault();
        if (face != null && face.TryGetPlane(out var plane, 1e-3))
        {
            var local = solid.GetBoundingBox(plane);
            thinnest = Math.Min(thinnest, new[] { local.Diagonal.X, local.Diagonal.Y, local.Diagonal.Z }.Min());
        }
        if (thinnest >= MinThickness) return null;
        return $"it comes out only {thinnest:0.##} mm thick at this scale; printable walls need at least {MinThickness} mm. " +
            "Print at a larger scale, thicken it in the model, or leave it out.";
    }

    // Objects modelled twice in the same place, common in BIM exports. Overlap trimming would
    // silently cut one of them away entirely.
    internal static IEnumerable<InputProblem> Duplicates(IReadOnlyList<(Brep Solid, string Name, Guid Object)> solids)
    {
        const double tolerance = .01; // mm at print scale
        var keyed = solids.Select(s => (s.Solid, s.Name, s.Object, Box: s.Solid.GetBoundingBox(true),
            Volume: Math.Abs(VolumeMassProperties.Compute(s.Solid, true, false, false, false)?.Volume ?? 0))).ToList();
        for (int i = 0; i < keyed.Count; i++)
        for (int j = i + 1; j < keyed.Count; j++)
        {
            var (a, b) = (keyed[i], keyed[j]);
            if (a.Box.Min.DistanceTo(b.Box.Min) > tolerance || a.Box.Max.DistanceTo(b.Box.Max) > tolerance) continue;
            if (Math.Abs(a.Volume - b.Volume) > 1e-3 * Math.Max(a.Volume, 1e-9)) continue;
            yield return new InputProblem(b.Name, $"it is a duplicate of {a.Name} in the same place. Delete one of them (SelDup finds them).", b.Object);
        }
    }

    // The solids inside a block instance, in model coordinates. Nested blocks are expanded; every
    // member shares a group for the instance, so the block prints as one part where its pieces touch.
    internal static IEnumerable<SourcePart> Expand(RhinoDoc doc, InstanceDefinition definition, Transform placement,
        Guid instance, string? instanceName, IReadOnlyList<SourceGroup> groups, int depth = 0)
    {
        if (depth > 16) yield break;
        var members = definition.GetObjects() ?? Array.Empty<RhinoObject>();
        var name = string.IsNullOrWhiteSpace(instanceName) ? definition.Name : instanceName;
        var all = groups.Append(new SourceGroup(instance, name, members.Length)).ToArray();
        foreach (var member in members)
        {
            if (member is InstanceObject nested)
            {
                foreach (var part in Expand(doc, nested.InstanceDefinition, placement * nested.InstanceXform, instance,
                    nested.Attributes.Name ?? nested.InstanceDefinition.Name, all, depth + 1)) yield return part;
                continue;
            }
            if (CarriesNoVolume(member.Geometry)) continue;
            var geometry = member.Geometry.Duplicate();
            if (!geometry.Transform(placement)) continue;
            // Unnamed members are numbered after the block, such as "Window 900 01".
            string? memberName = string.IsNullOrWhiteSpace(member.Attributes.Name) ? (members.Length == 1 ? name : null) : member.Attributes.Name;
            yield return new SourcePart(geometry, memberName, instance, false, name,
                doc.ModelAbsoluteTolerance, doc.ModelAngleToleranceRadians, all);
        }
    }
}
