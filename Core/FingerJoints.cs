using Rhino;
using Rhino.Geometry;

namespace Go2Bed;

// A row of short fingers indexes a panel into a horizontal plate. All dimensions
// are physical millimetres. Both sides of a joint are committed together.
internal static class FingerJoints
{
    private const double Tolerance = .001;
    private const double ContactTolerance = .30;
    private const double MinimumDepth = .60;
    private const double SocketBacking = .85;
    private const double MinimumFingerThickness = .70;

    internal static HashSet<(int, int)> Apply(List<Component> components, double clearance)
    {
        int count = 0;
        var jointed = new HashSet<(int, int)>();
        var boxes = components.Select(c => c.Solid.GetBoundingBox(true)).ToArray();
        // Only walls take fingers: a tab on a roof or slab would stick out of the face it prints on.
        var walls = components.Select(c => IsWall(c.Solid)).ToArray();
        for (int receiver = 0; receiver < components.Count; receiver++)
        {
            var plate = boxes[receiver];
            double thickness = plate.Diagonal.Z;
            double depth = Math.Min(2, thickness-SocketBacking-clearance);
            if (depth+Tolerance < MinimumDepth || thickness > Math.Min(plate.Diagonal.X,plate.Diagonal.Y)*.2) continue;
            for (int donor = 0; donor < components.Count; donor++)
            {
                var panel = boxes[donor];
                if (donor == receiver || !walls[donor] || panel.Diagonal.Z < 8 ||
                    panel.Min.Z > plate.Max.Z+ContactTolerance || panel.Max.Z < plate.Max.Z-ContactTolerance) continue;
                if (panel.Max.X < plate.Min.X || panel.Min.X > plate.Max.X ||
                    panel.Max.Y < plate.Min.Y || panel.Min.Y > plate.Max.Y) continue;
                var candidates = Locations(components[donor].Solid,plate.Max.Z).ToList();
                foreach (var (frame,halfWidth) in candidates)
                {
                    double floorZ = plate.Max.Z-frame.OriginZ;
                    var tab = new Box(frame,new Interval(-2.5,2.5),new Interval(-halfWidth,halfWidth),
                        new Interval(floorZ-depth,.35));
                    var socket = new Box(frame,new Interval(-2.5-clearance,2.5+clearance),
                        new Interval(-halfWidth-clearance,halfWidth+clearance),new Interval(floorZ-depth-clearance,floorZ+.5));
                    var tabBox = tab.BoundingBox;
                    if (boxes.Where((_,i) => i!=receiver && i!=donor)
                        .Any(b => BoundingBox.Intersection(b,tabBox).IsValid)) continue;
                    var oldPanel = components[donor].Solid;
                    var oldPlate = components[receiver].Solid;
                    // Require sound material at both ends of the proposed joint.
                    if (!ContainedSlice(tab, oldPanel,.10,.30) ||
                        !ContainedSlice(socket,oldPlate,floorZ-depth-clearance,floorZ-.05)) continue;
                    var joined = Brep.CreateBooleanUnion(new[]{oldPanel,tab.ToBrep()},Tolerance);
                    var pocket = Brep.CreateBooleanDifference(oldPlate,socket.ToBrep(),Tolerance);
                    if (!OneSolid(joined) || !OneSolid(pocket)) continue;
                    double removed = oldPlate.GetVolume()-pocket[0].GetVolume();
                    double expected = socket.X.Length*socket.Y.Length*(depth+clearance);
                    if (Math.Abs(removed-expected) > expected*.02 ||
                        Math.Abs(joined[0].GetVolume()-oldPanel.GetVolume()-tab.X.Length*tab.Y.Length*(depth-floorZ)) > .05) continue;
                    components[donor] = components[donor] with {Solid=joined[0]};
                    components[receiver] = components[receiver] with {Solid=pocket[0]};
                    jointed.Add((Math.Min(donor,receiver),Math.Max(donor,receiver)));
                    count++;
                }
            }
        }
        RhinoApp.WriteLine($"Added {count} alignment fingers with matching sockets ({clearance:F2} mm clearance per side).");
        return jointed;
    }

    // A wall's largest planar area faces sideways, so the vertical tabs lie in the plane it prints on.
    // Areas are summed by direction because cleanup can leave one flat side as several faces.
    private static bool IsWall(Brep body)
    {
        var areas = new Dictionary<(long,long,long),(double Area,Vector3d Normal)>();
        foreach (var face in body.Faces)
        {
            if (!face.TryGetPlane(out var plane,Tolerance)) continue;
            var n = plane.ZAxis;
            if (n.X < -1e-6 || (Math.Abs(n.X) <= 1e-6 && (n.Y < -1e-6 || (Math.Abs(n.Y) <= 1e-6 && n.Z < 0)))) n = -n;
            var key = ((long)Math.Round(n.X*100),(long)Math.Round(n.Y*100),(long)Math.Round(n.Z*100));
            var area = AreaMassProperties.Compute(face,true,false,false,false)?.Area ?? 0;
            var sum = areas.GetValueOrDefault(key);
            areas[key] = (sum.Area+area,n);
        }
        if (areas.Count == 0) return false;
        return Math.Abs(areas.Values.MaxBy(a => a.Area).Normal.Z) < Math.Sin(2*Math.PI/180);
    }

    private static bool OneSolid(Brep[] result) =>
        result is {Length:1} && result[0].IsValid && result[0].IsSolid;

    private static bool ContainedSlice(Box box, Brep body, double bottom, double top)
    {
        var slice = new Box(box.Plane,box.X,box.Y,new Interval(bottom,top));
        return slice.GetCorners().All(p => body.IsPointInside(p,Tolerance,true));
    }

    private static IEnumerable<(Plane,double)> Locations(Brep body, double slabTop)
    {
        foreach (var face in body.Faces)
        {
            if (!face.TryGetPlane(out var basePlane,Tolerance) ||
                Math.Abs(basePlane.OriginZ-slabTop)>ContactTolerance) continue;
            var normal = face.OrientationIsReversed ? -basePlane.ZAxis : basePlane.ZAxis;
            if (normal.Z>-.99999) continue;
            var edges = face.AdjacentEdges().Select(i => body.Edges[i]).ToArray();
            var longest = edges.Where(e => e.IsLinear(Tolerance)).OrderByDescending(e=>e.GetLength()).FirstOrDefault();
            if (longest==null) continue;
            var x = longest.PointAtEnd-longest.PointAtStart;
            x.Z=0; if (!x.Unitize()) continue;
            var frame = new Plane(basePlane.Origin,x,Vector3d.CrossProduct(Vector3d.ZAxis,x));
            var box = face.DuplicateFace(false).GetBoundingBox(frame);
            double length = box.Diagonal.X, width = box.Diagonal.Y;
            double halfWidth = Math.Min(width/2-.25,1.6);
            if (length<12 || 2*halfWidth+Tolerance<MinimumFingerThickness || width>length/3 || width>12) continue;
            int n = Math.Clamp((int)Math.Floor(length/20),2,8);
            for (int i=0;i<n;i++)
            {
                var center = frame.PointAt(box.Min.X+length*(i+.5)/n,box.Center.Y);
                yield return (new Plane(center,frame.XAxis,frame.YAxis),halfWidth);
            }
        }
    }
}
