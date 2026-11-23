using Rhino.Geometry;

namespace Go2Bed;

internal static class BrepHealing
{
    internal static Brep? Heal(Brep source, double tolerance, double angleTolerance, out bool changed)
    {
        changed = false;
        var candidate = source.DuplicateBrep();
        if (!candidate.IsValid)
        {
            candidate.Repair(tolerance);
            changed = true;
        }

        if (!candidate.IsValid) return null;
        if (!candidate.IsSolid)
        {
            changed |= candidate.JoinNakedEdges(tolerance) > 0;
            if (!candidate.IsSolid)
            {
                var capped = candidate.CapPlanarHoles(tolerance);
                if (capped == null) return null;
                candidate = capped;
                changed = true;
            }
        }

        if (!candidate.IsValid || !candidate.IsSolid) return null;

        var merged = candidate.DuplicateBrep();
        if (merged.MergeCoplanarFaces(tolerance, angleTolerance) && merged.IsValid && merged.IsSolid)
        {
            candidate = merged;
            changed = true;
        }

        if (candidate.SolidOrientation == BrepSolidOrientation.Inward)
        {
            candidate.Flip();
            changed = true;
        }

        return candidate;
    }
}
