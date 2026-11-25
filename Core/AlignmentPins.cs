using Rhino;
using Rhino.Geometry;

namespace Go2Bed;

// Matching blind holes across mating planar faces. A short offcut of 1.75 mm filament
// in each pair of holes registers the two parts while the glue sets. Two pins per
// contact fix both position and rotation. All dimensions are print-scale millimetres.
internal static class AlignmentPins
{
    private const double Tolerance = .001;
    private const double MaxGap = .10; // Includes the clearance left by overlap trimming.
    private const double Filament = 1.75;
    private const double Wall = .6; // Material kept around and behind every hole.
    private const double MinSpacing = 6;
    private static readonly double[] Depths = { 3, 2.2, 1.5, 1.2 };
    private static readonly double[] Along = { 0, 1, .5, .25, .75, .125, .875 };
    private static readonly double[] Across = { .5, .25, .75 };

    private record Site(Plane PlaneA, Plane PlaneB, Point3d CenterA, Point3d CenterB, double DepthA, double DepthB);

    // Hole diameter is 2.0 mm at the default 0.20 mm clearance.
    internal static double Radius(double clearance) => Filament / 2 + clearance / 2 + .025;

    internal static HashSet<(int, int)> Apply(List<Component> components, double clearance, ISet<(int, int)> skip)
    {
        double radius = Radius(clearance);
        var pinned = new HashSet<(int, int)>();
        int holes = 0;
        var bounds = components.Select(component => component.Solid.GetBoundingBox(true)).ToArray();
        for (int i = 0; i < components.Count; i++)
        {
            var nearby = bounds[i];
            nearby.Inflate(MaxGap);
            for (int j = i + 1; j < components.Count; j++)
            {
                if (skip.Contains((i, j)) || !BoundingBox.Intersection(nearby, bounds[j]).IsValid) continue;
                int added = 0;
                foreach (var site in Choose(Sites(components[i].Solid, components[j].Solid, radius)))
                {
                    var first = Cut(components[i].Solid, site.PlaneA, site.CenterA, radius, site.DepthA);
                    if (first == null) continue;
                    var second = Cut(components[j].Solid, site.PlaneB, site.CenterB, radius, site.DepthB);
                    if (second == null) { first.Dispose(); continue; }
                    components[i] = components[i] with { Solid = first };
                    components[j] = components[j] with { Solid = second };
                    added++;
                }
                if (added == 0) continue;
                holes += added;
                pinned.Add((i, j));
            }
        }
        RhinoApp.WriteLine($"Added {holes} filament pin positions across {pinned.Count} contacts " +
            $"({2 * radius:F2} mm holes for 1.75 mm filament).");
        return pinned;
    }

    private static IEnumerable<(BrepFace Face, Plane Plane, BoundingBox Bounds)> PlanarFaces(Brep body, double reach)
    {
        foreach (var face in body.Faces)
        {
            if (!face.TryGetPlane(out var plane, Tolerance)) continue;
            if (face.OrientationIsReversed) plane = new Plane(plane.Origin, plane.XAxis, -plane.YAxis);
            using var trimmed = face.DuplicateFace(false);
            var bounds = trimmed.GetBoundingBox(plane);
            if (bounds.Diagonal.X >= 2 * reach && bounds.Diagonal.Y >= 2 * reach)
                yield return (face, plane, bounds);
        }
    }

    private static List<Site> Sites(Brep first, Brep second, double radius)
    {
        double reach = radius + Wall;
        var sites = new List<Site>();
        var secondFaces = PlanarFaces(second, reach).ToArray();
        if (secondFaces.Length == 0) return sites;
        foreach (var (faceA, planeA, boundsA) in PlanarFaces(first, reach))
        foreach (var (faceB, planeB, _) in secondFaces)
        {
            if (planeA.ZAxis * planeB.ZAxis > -.999999 ||
                Math.Abs(planeA.DistanceTo(planeB.Origin)) > MaxGap)
                continue;
            using var trimmedB = faceB.DuplicateFace(false);
            var boundsB = trimmedB.GetBoundingBox(planeA);
            double left = Math.Max(boundsA.Min.X, boundsB.Min.X) + reach;
            double right = Math.Min(boundsA.Max.X, boundsB.Max.X) - reach;
            double bottom = Math.Max(boundsA.Min.Y, boundsB.Min.Y) + reach;
            double top = Math.Min(boundsA.Max.Y, boundsB.Max.Y) - reach;
            if (left > right || bottom > top) continue;
            bool wide = right - left >= top - bottom;
            double span = Math.Max(right - left, top - bottom);
            foreach (double t in Across)
            foreach (double s in Along)
            {
                double x = wide ? s : t, y = wide ? t : s;
                var centerA = planeA.PointAt(left + x * (right - left), bottom + y * (top - bottom));
                var centerB = planeB.ClosestPoint(centerA);
                double gap = planeA.DistanceTo(centerB);
                if (gap < -Tolerance || gap > MaxGap) continue;
                double depthA = FitDepth(faceA, first, planeA, centerA, reach);
                if (depthA == 0) continue;
                double depthB = FitDepth(faceB, second, planeB, centerB, reach);
                if (depthB == 0) continue;
                var site = new Site(planeA, planeB, centerA, centerB, depthA, depthB);
                // Two sites near opposite ends of a contact are enough. Stop searching.
                bool spread = sites.Any(other => other.CenterA.DistanceTo(centerA) >= Math.Max(MinSpacing, .6 * span));
                sites.Add(site);
                if (spread) return sites;
            }
        }
        return sites;
    }

    // The two sites furthest apart resist rotation best. A lone site still fixes position.
    private static IEnumerable<Site> Choose(List<Site> sites)
    {
        if (sites.Count == 0) yield break;
        double best = MinSpacing;
        (Site, Site)? pair = null;
        for (int a = 0; a < sites.Count; a++)
        for (int b = a + 1; b < sites.Count; b++)
        {
            double distance = sites[a].CenterA.DistanceTo(sites[b].CenterA);
            if (distance < best) continue;
            best = distance;
            pair = (sites[a], sites[b]);
        }
        if (pair is not { } chosen) { yield return sites.OrderByDescending(s => s.DepthA + s.DepthB).First(); yield break; }
        yield return chosen.Item1;
        yield return chosen.Item2;
    }

    // Deepest hole that keeps sound material around and behind it, or 0 when none fits.
    private static double FitDepth(BrepFace face, Brep solid, Plane plane, Point3d center, double reach)
    {
        var samples = new List<Point3d> { center };
        for (int k = 0; k < 8; k++)
        {
            double angle = k * Math.PI / 4;
            samples.Add(center + reach * (Math.Cos(angle) * plane.XAxis + Math.Sin(angle) * plane.YAxis));
        }
        foreach (var point in samples)
        {
            if (!face.ClosestPoint(point, out double u, out double v) ||
                face.PointAt(u, v).DistanceTo(point) > MaxGap ||
                face.IsPointOnFace(u, v) != PointFaceRelation.Interior)
                return 0;
        }
        if (!samples.All(point => solid.IsPointInside(point - .05 * plane.ZAxis, Tolerance, true))) return 0;
        foreach (double depth in Depths)
        {
            if (samples.All(point =>
                    solid.IsPointInside(point - depth / 2 * plane.ZAxis, Tolerance, true) &&
                    solid.IsPointInside(point - (depth + Wall) * plane.ZAxis, Tolerance, true)))
                return depth;
        }
        return 0;
    }

    private static Brep? Cut(Brep solid, Plane plane, Point3d center, double radius, double depth)
    {
        var floor = new Plane(center - depth * plane.ZAxis, plane.XAxis, plane.YAxis);
        using var cutter = new Cylinder(new Circle(floor, radius), depth + .1).ToBrep(true, true);
        if (cutter == null) return null;
        if (cutter.SolidOrientation == BrepSolidOrientation.Inward) cutter.Flip();
        var result = Brep.CreateBooleanDifference(solid, cutter, Tolerance);
        double expected = Math.PI * radius * radius * depth;
        if (result is { Length: 1 } && result[0].IsValid && result[0].IsSolid &&
            Math.Abs(solid.GetVolume() - result[0].GetVolume() - expected) < expected * .03 + .01)
            return result[0];
        if (result != null)
            foreach (var piece in result) piece.Dispose();
        return null;
    }
}
