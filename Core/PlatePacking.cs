using Clipper2Lib;
using Rhino.Geometry;

namespace Go2Bed;

internal record Placement(Part Part, int Plate, Mesh Placed);

internal static class PlatePacking
{
    private const double Step = 2; // mm between candidate positions
    internal const double Margin = 3;
    private const double Clearance = 2;
    private const int Halo = 2;
    private record Span(int Y, int Start, int End);
    private record Shape(Mesh Mesh, Vector3d Size, List<Span> Rows);

    // Conservative occupancy mask of the XY silhouette, including through-openings.
    // Inflating by the cell half-diagonal ensures every touched cell is represented.
    private static Shape Footprint(Mesh mesh)
    {
        var size = mesh.GetBoundingBox(true).Diagonal;
        var triangles = new Paths64();
        const double units = 1000;
        foreach (var f in mesh.Faces)
        {
            var tri = new Path64();
            foreach (int index in new[] { f.A, f.B, f.C })
            {
                var v = mesh.Vertices[index];
                tri.Add(new Point64(v.X * units, v.Y * units));
            }
            var area = Clipper.Area(tri);
            if (area == 0) continue;
            if (area < 0) tri.Reverse();
            triangles.Add(tri);
        }
        var rings = Clipper.InflatePaths(Clipper.Union(triangles, FillRule.NonZero),
            (Clearance / 2 + Step / Math.Sqrt(2) + .02) * units, JoinType.Round, EndType.Polygon);
        var rows = new List<Span>();
        for (int row = -Halo; row < Math.Ceiling(size.Y / Step) + Halo; row++)
        {
            double y = (row + .5) * Step * units;
            var crossings = new List<double>();
            foreach (var ring in rings)
            {
                var a = ring[ring.Count - 1];
                foreach (var b in ring)
                {
                    if ((a.Y > y) != (b.Y > y))
                        crossings.Add(a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y));
                    a = b;
                }
            }
            crossings.Sort();
            for (int i = 0; i < crossings.Count; i += 2)
            {
                int start = (int)Math.Ceiling(crossings[i] / (Step * units) - .5);
                int end = (int)Math.Floor(crossings[i+1] / (Step * units) - .5);
                if (start <= end) rows.Add(new Span(row + Halo, start + Halo, end + Halo));
            }
        }
        return new Shape(mesh, size, rows);
    }

    private sealed class Plate
    {
        private readonly bool[,] occupied;
        private readonly int[,] prefixes;
        public double UsedX, UsedY;
        public Plate(double width, double depth)
        {
            int cols = (int)Math.Ceiling(width / Step) + 2 * Halo;
            int rows = (int)Math.Ceiling(depth / Step) + 2 * Halo;
            occupied = new bool[rows,cols];
            prefixes = new int[rows,cols+1];
        }
        public bool Fits(Shape shape, int x, int y)
        {
            foreach (var span in shape.Rows)
                if (prefixes[y + span.Y, x + span.End + 1] != prefixes[y + span.Y, x + span.Start]) return false;
            return true;
        }
        public void Add(Shape shape, int x, int y)
        {
            foreach (var s in shape.Rows)
                for (int col=x+s.Start; col<=x+s.End; col++) occupied[y+s.Y,col] = true;
            for (int row=0; row<occupied.GetLength(0); row++)
                for (int col=0; col<occupied.GetLength(1); col++)
                    prefixes[row,col+1] = prefixes[row,col] + (occupied[row,col] ? 1 : 0);
            UsedX = Math.Max(UsedX,x*Step+shape.Size.X);
            UsedY = Math.Max(UsedY,y*Step+shape.Size.Y);
        }
    }

    internal static List<Placement> Pack(IEnumerable<Part> parts, double width, double depth, double height)
    {
        var plates = new List<Plate>();
        var result = new List<Placement>();
        double usableX = width - 2*Margin, usableY = depth - 2*Margin;
        foreach (var part in parts.OrderByDescending(p => {
            var size = p.Flat.GetBoundingBox(true).Diagonal;
            return size.X * size.Y;
        }))
        {
            var variants = new List<Shape>();
            for (int turn=0; turn<4; turn++)
            {
                var mesh = part.Flat.DuplicateMesh();
                mesh.Rotate(turn * Math.PI/2, Vector3d.ZAxis, Point3d.Origin);
                mesh.Translate(Point3d.Origin - mesh.GetBoundingBox(true).Min);
                var size = mesh.GetBoundingBox(true).Diagonal;
                if (size.Z > height+.001) throw new ArgumentException(part.Name+" exceeds the build height.");
                if (size.X<=usableX+.001 && size.Y<=usableY+.001) variants.Add(Footprint(mesh));
            }
            if (variants.Count == 0) throw new ArgumentException(part.Name+" exceeds plate dimensions.");
            for (int plateIndex=0;;plateIndex++)
            {
                if (plateIndex==plates.Count) plates.Add(new Plate(usableX,usableY));
                var plate = plates[plateIndex];
                (Shape shape,int x,int y)? best = null;
                var score = (double.PositiveInfinity,double.PositiveInfinity,double.PositiveInfinity);
                foreach (var shape in variants)
                for (int y=0; y*Step+shape.Size.Y<=usableY+.001; y++)
                for (int x=0; x*Step+shape.Size.X<=usableX+.001; x++)
                {
                    double right=x*Step+shape.Size.X, top=y*Step+shape.Size.Y;
                    var next = (Math.Max(plate.UsedX,right)*Math.Max(plate.UsedY,top),top,right);
                    if (next.CompareTo(score)>=0 || !plate.Fits(shape,x,y)) continue;
                    score=next; best=(shape,x,y);
                }
                if (best is not { } choice) continue;
                plate.Add(choice.shape,choice.x,choice.y);
                var placed = choice.shape.Mesh;
                placed.Translate(Margin+choice.x*Step,Margin+choice.y*Step,0);
                result.Add(new Placement(part,plateIndex+1,placed));
                break;
            }
        }
        return result;
    }
}
