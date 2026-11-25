using Rhino;
using Rhino.Geometry;

namespace Go2Bed;

internal static class AssemblyMarks
{
    private const double Tolerance = .001;
    private const double MaxGap = .10; // Includes the clearance left by overlap trimming.
    private const double HalfWidth = .6;
    private const double Depth = .3;
    private static readonly double[] SamplePositions = { .5, .25, .75, .125, .875 };
    private static readonly double[] FootprintSamples = { -HalfWidth - .15, 0, HalfWidth + .15 };

    internal static void Apply(List<Component> components, ISet<(int, int)>? skip = null)
    {
        int count = 0;
        var bounds = components.Select(component => component.Solid.GetBoundingBox(true)).ToArray();
        for (int i = 0; i < components.Count; i++)
        {
            var nearby = bounds[i];
            nearby.Inflate(MaxGap);
            for (int j = i + 1; j < components.Count; j++)
            {
                if (skip?.Contains((i, j)) == true || !BoundingBox.Intersection(nearby, bounds[j]).IsValid ||
                    !TryMarkPair(components[i].Solid, components[j].Solid, out var first, out var second))
                    continue;
                components[i] = components[i] with { Solid = first };
                components[j] = components[j] with { Solid = second };
                count++;
            }
        }
        RhinoApp.WriteLine($"Added {count} pairs of recessed assembly marks.");
    }

    private static IEnumerable<(BrepFace Face, Plane Plane, BoundingBox Bounds)> PlanarFaces(Brep body)
    {
        foreach (var face in body.Faces)
        {
            if (!face.TryGetPlane(out var plane, Tolerance)) continue;
            if (face.OrientationIsReversed) plane = new Plane(plane.Origin, plane.XAxis, -plane.YAxis);
            using var trimmed = face.DuplicateFace(false);
            var bounds = trimmed.GetBoundingBox(plane);
            if (bounds.Diagonal.X >= 2 * HalfWidth && bounds.Diagonal.Y >= 2 * HalfWidth)
                yield return (face, plane, bounds);
        }
    }

    private static bool TryMarkPair(Brep first, Brep second, out Brep markedFirst, out Brep markedSecond)
    {
        markedFirst = first;
        markedSecond = second;
        var secondFaces = PlanarFaces(second).ToArray();
        foreach (var (faceA, planeA, boundsA) in PlanarFaces(first))
        foreach (var (faceB, planeB, _) in secondFaces)
        {
            if (planeA.ZAxis * planeB.ZAxis > -.999999 ||
                Math.Abs(planeA.DistanceTo(planeB.Origin)) > MaxGap)
                continue;

            using var trimmedB = faceB.DuplicateFace(false);
            var boundsB = trimmedB.GetBoundingBox(planeA);
            const double margin = HalfWidth + .15;
            double left = Math.Max(boundsA.Min.X, boundsB.Min.X) + margin;
            double right = Math.Min(boundsA.Max.X, boundsB.Max.X) - margin;
            double bottom = Math.Max(boundsA.Min.Y, boundsB.Min.Y) + margin;
            double top = Math.Min(boundsA.Max.Y, boundsB.Max.Y) - margin;
            if (left > right || bottom > top) continue;

            foreach (double x in SamplePositions)
            foreach (double y in SamplePositions)
            {
                var centerA = planeA.PointAt(left + x * (right - left), bottom + y * (top - bottom));
                var centerB = planeB.ClosestPoint(centerA);
                double gap = planeA.DistanceTo(centerB);
                if (gap < -Tolerance || gap > MaxGap || !Fits(faceA, first, planeA, centerA) ||
                    !Fits(faceB, second, planeB, centerB))
                    continue;
                var stampA = Cut(first, planeA, centerA);
                if (stampA == null) continue;
                var stampB = Cut(second, planeB, centerB);
                if (stampB == null)
                {
                    stampA.Dispose();
                    continue;
                }
                markedFirst = stampA;
                markedSecond = stampB;
                return true;
            }
        }
        return false;
    }

    private static bool Fits(BrepFace face, Brep solid, Plane plane, Point3d center)
    {
        // Check the trimmed face and leave material below the recess.
        foreach (double x in FootprintSamples)
        foreach (double y in FootprintSamples)
        {
            var point = center + x * plane.XAxis + y * plane.YAxis;
            if (!face.ClosestPoint(point, out double u, out double v) ||
                face.IsPointOnFace(u, v) != PointFaceRelation.Interior ||
                !solid.IsPointInside(point - (Depth + .2) * plane.ZAxis, Tolerance, true))
                return false;
        }
        return true;
    }

    private static Brep? Cut(Brep solid, Plane plane, Point3d center)
    {
        plane.Origin = center;
        var side = new Interval(-HalfWidth, HalfWidth);
        using var cutter = new Box(plane, side, side, new Interval(-Depth, .1)).ToBrep();
        var result = Brep.CreateBooleanDifference(solid, cutter, Tolerance);
        double expected = side.Length * side.Length * Depth;
        if (result is { Length: 1 } && result[0].IsValid && result[0].IsSolid &&
            Math.Abs(solid.GetVolume() - result[0].GetVolume() - expected) < .02)
            return result[0];
        if (result != null)
            foreach (var piece in result) piece.Dispose();
        return null;
    }
}
