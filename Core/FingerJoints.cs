using Rhino;
using Rhino.Geometry;

namespace Go2Bed;

// A row of short fingers indexes a panel into a horizontal plate. All dimensions
// are physical millimetres. Both sides of a joint are committed together.
internal static class FingerJoints
{
    private const double Tolerance = .001;

    internal static void Apply(List<Component> components, double clearance)
    {
        int count = 0;
        var boxes = components.Select(c => c.Solid.GetBoundingBox(true)).ToArray();
        for (int receiver = 0; receiver < components.Count; receiver++)
        {
            var plate = boxes[receiver];
            double thickness = plate.Diagonal.Z;
            if (thickness < 2 || thickness > Math.Min(plate.Diagonal.X,plate.Diagonal.Y)*.2) continue;
            for (int donor = 0; donor < components.Count; donor++)
            {
                var panel = boxes[donor];
                if (donor == receiver || panel.Diagonal.Z < 8 || Math.Abs(panel.Min.Z-plate.Max.Z) > .30) continue;
                if (panel.Max.X < plate.Min.X || panel.Min.X > plate.Max.X ||
                    panel.Max.Y < plate.Min.Y || panel.Min.Y > plate.Max.Y) continue;
                var candidates = Locations(components[donor].Solid,panel.Min.Z).ToList();
                foreach (var (frame,halfWidth) in candidates)
                {
                    double depth = Math.Min(2, thickness-.85-clearance);
                    double floorZ = plate.Max.Z-frame.OriginZ;
                    if (depth<1) continue;
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
                    count++;
                }
            }
        }
        RhinoApp.WriteLine($"Added {count} alignment fingers with matching sockets ({clearance:F2} mm clearance per side).");
    }

    private static bool OneSolid(Brep[] result) =>
        result is {Length:1} && result[0].IsValid && result[0].IsSolid;

    private static bool ContainedSlice(Box box, Brep body, double bottom, double top)
    {
        var slice = new Box(box.Plane,box.X,box.Y,new Interval(bottom,top));
        return slice.GetCorners().All(p => body.IsPointInside(p,Tolerance,true));
    }

    private static IEnumerable<(Plane,double)> Locations(Brep body, double baseZ)
    {
        foreach (var face in body.Faces)
        {
            if (!face.TryGetPlane(out var basePlane,Tolerance) || Math.Abs(basePlane.ZAxis.Z)<.99999 ||
                Math.Abs(basePlane.OriginZ-baseZ)>.02) continue;
            var edges = face.AdjacentEdges().Select(i => body.Edges[i]).ToArray();
            var longest = edges.Where(e => e.IsLinear(Tolerance)).OrderByDescending(e=>e.GetLength()).FirstOrDefault();
            if (longest==null) continue;
            var x = longest.PointAtEnd-longest.PointAtStart;
            x.Z=0; if (!x.Unitize()) continue;
            var frame = new Plane(new Point3d(basePlane.OriginX,basePlane.OriginY,baseZ),x,Vector3d.CrossProduct(Vector3d.ZAxis,x));
            var box = face.DuplicateFace(false).GetBoundingBox(frame);
            double length = box.Diagonal.X, width = box.Diagonal.Y;
            if (length<12 || width<1.8 || width>length/3 || width>12) continue;
            int n = Math.Clamp((int)Math.Floor(length/20),2,8);
            for (int i=0;i<n;i++)
            {
                var center = frame.PointAt(box.Min.X+length*(i+.5)/n,box.Center.Y);
                yield return (new Plane(center,frame.XAxis,frame.YAxis),Math.Min(width/2-.25,1.6));
            }
        }
    }
}
