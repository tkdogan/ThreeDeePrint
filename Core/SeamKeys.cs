using Clipper2Lib;
using Rhino.Geometry;

namespace Go2Bed;

// Splits a part that lies flat on the bed into a grid of pieces. Each seam gets dovetail
// keys where the part is solid, so neighbouring pieces register in the seam plane and lie
// flush on a table while the glue sets. Keys run the full height, so they print without
// overhangs. All dimensions are print-scale millimetres.
internal static class SeamKeys
{
    internal const double Length = 4; // How far a key reaches past its seam.
    private const double Neck = 4, Head = 6, Root = .5;
    private const double Guard = 1.5; // Solid material required around a key.
    private const double EndMargin = Head / 2 + Guard + Length;
    private const double Spacing = Head + 4 * Guard;
    private const double Tol = .001;
    private const double Units = 1000;
    private static readonly double[] Positions = { .2, .8, .5, .35, .65, .05, .95 };

    internal static List<Brep>? Split(Brep flat, BoundingBox box, int nx, int ny, double clearance, out int keys)
    {
        keys = 0;
        double dx = box.Diagonal.X / nx, dy = box.Diagonal.Y / ny;
        var own = new Dictionary<(int, int), Paths64>();
        var foreign = new Dictionary<(int, int), Paths64>();
        void Add(Dictionary<(int, int), Paths64> map, (int, int) cell, Path64 tab)
        {
            if (!map.TryGetValue(cell, out var list)) map[cell] = list = new Paths64();
            list.Add(tab);
        }
        void Seam(Point3d start, Vector3d along, Vector3d across, double length, (int, int) owner, (int, int) neighbour, ref int count)
        {
            double usable = length - 2 * EndMargin;
            if (usable < 0) return;
            var placed = new List<double>();
            foreach (double fraction in Positions)
            {
                double at = EndMargin + fraction * usable;
                if (placed.Count == 2 || placed.Any(p => Math.Abs(p - at) < Spacing)) continue;
                var center = start + at * along;
                if (!Solid(flat, box, center, along, across)) continue;
                placed.Add(at);
                var tab = Tab(center, along, across);
                Add(own, owner, tab);
                Add(foreign, neighbour, tab);
                count++;
            }
        }
        for (int i = 0; i < nx; i++)
        for (int j = 0; j < ny; j++)
        {
            var corner = new Point3d(box.Min.X + (i + 1) * dx, box.Min.Y + (j + 1) * dy, box.Min.Z);
            if (i < nx - 1) Seam(corner - dy * Vector3d.YAxis, Vector3d.YAxis, Vector3d.XAxis, dy, (i, j), (i + 1, j), ref keys);
            if (j < ny - 1) Seam(corner - dx * Vector3d.XAxis, Vector3d.XAxis, Vector3d.YAxis, dx, (i, j), (i, j + 1), ref keys);
        }

        var pieces = new List<Brep>();
        var lift = new LineCurve(Point3d.Origin, new Point3d(0, 0, box.Diagonal.Z + 2));
        for (int i = 0; i < nx; i++)
        for (int j = 0; j < ny; j++)
        {
            // Outer cell edges overshoot so only seams cut the part.
            double x0 = box.Min.X + i * dx - (i == 0 ? 1 : 0), x1 = box.Min.X + (i + 1) * dx + (i == nx - 1 ? 1 : 0);
            double y0 = box.Min.Y + j * dy - (j == 0 ? 1 : 0), y1 = box.Min.Y + (j + 1) * dy + (j == ny - 1 ? 1 : 0);
            var outline = new Paths64 { Ring(new Point3d(x0, y0, 0), new Point3d(x1, y0, 0), new Point3d(x1, y1, 0), new Point3d(x0, y1, 0)) };
            if (own.TryGetValue((i, j), out var tabs)) outline = Clipper.Union(outline, tabs, FillRule.NonZero);
            if (foreign.TryGetValue((i, j), out var sockets))
                outline = Clipper.Difference(outline,
                    Clipper.InflatePaths(sockets, clearance * Units, JoinType.Miter, EndType.Polygon), FillRule.NonZero);
            var loops = new List<Curve>();
            foreach (var ring in outline)
            {
                var points = ring.Select(p => new Point3d(p.X / Units, p.Y / Units, box.Min.Z - 1)).ToList();
                points.Add(points[0]);
                loops.Add(new PolylineCurve(points));
            }
            var regions = Brep.CreatePlanarBreps(loops, Tol);
            if (regions == null || regions.Length == 0) return null;
            foreach (var region in regions)
            {
                var cutter = region.Faces[0].CreateExtrusion(lift, true);
                if (cutter == null || !cutter.IsSolid) return null;
                if (cutter.SolidOrientation == BrepSolidOrientation.Inward) cutter.Flip();
                var cut = Brep.CreateBooleanIntersection(flat, cutter, Tol);
                if (cut == null) return null;
                pieces.AddRange(cut.Where(p => p.GetVolume() > .001));
            }
        }
        if (pieces.Any(p => !p.IsValid || !p.IsSolid)) return null;
        // Only the socket clearance may go missing.
        double volume = flat.GetVolume(), kept = pieces.Sum(p => p.GetVolume());
        double allowance = keys * (2 * Length + Head + Neck + 4) * clearance * box.Diagonal.Z * 1.5 + .02;
        return kept > volume + .02 || kept < volume - allowance ? null : pieces;
    }

    // A key needs sound material on both sides of the seam, from the bed up.
    private static bool Solid(Brep flat, BoundingBox box, Point3d center, Vector3d along, Vector3d across)
    {
        double reachAlong = Head / 2 + Guard, reachAcross = Length + Guard;
        double low = .15, high = Math.Min(box.Diagonal.Z - .15, 1);
        for (int a = -2; a <= 2; a++)
        for (int b = -2; b <= 2; b++)
        {
            var point = center + a / 2.0 * reachAlong * along + b / 2.0 * reachAcross * across;
            if (!flat.IsPointInside(point + low * Vector3d.ZAxis, Tol, true) ||
                !flat.IsPointInside(point + high * Vector3d.ZAxis, Tol, true)) return false;
        }
        return true;
    }

    private static Path64 Tab(Point3d center, Vector3d along, Vector3d across) => Ring(
        center - Root * across - Neck / 2 * along,
        center - Neck / 2 * along,
        center + Length * across - Head / 2 * along,
        center + Length * across + Head / 2 * along,
        center + Neck / 2 * along,
        center - Root * across + Neck / 2 * along);

    private static Path64 Ring(params Point3d[] points)
    {
        var ring = new Path64(points.Select(p => new Point64(p.X * Units, p.Y * Units)));
        if (!Clipper.IsPositive(ring)) ring.Reverse();
        return ring;
    }
}
