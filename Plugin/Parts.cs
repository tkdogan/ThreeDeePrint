using Rhino;
using Clipper2Lib;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace ThreeDeePrint;

internal record Part(string Name, Guid Source, Mesh Assembled, Mesh Flat);
internal static class Parts
{
    private const double Tol = .001; // All geometry here is at print scale, in millimetres.
    public static IEnumerable<Part> Prepare(IEnumerable<RhinoObject> objects, double factor, double width, double depth, bool barn)
    {
        var prepared = new List<Part>();
        var cutters = new List<(Brep Solid, BoundingBox Original)>();
        foreach (var obj in objects.OrderBy(o => Priority(o.Attributes.Name ?? "")))
        {
            string name = obj.Attributes.Name ?? "Component";
            try
            {
                var solid = Solid(obj, factor, barn);
                var box = solid.GetBoundingBox(true);
                var pieces = new List<Brep> { solid };
                foreach (var cutter in cutters)
                {
                    var overlap = BoundingBox.Intersection(box, cutter.Original);
                    if (!overlap.IsValid || overlap.Diagonal.X < Tol || overlap.Diagonal.Y < Tol || overlap.Diagonal.Z < Tol) continue;
                    pieces = pieces.SelectMany(b => Subtract(b, cutter.Solid)).ToList();
                }
                var expanded = solid.DuplicateBrep();
                var size = box.Diagonal;
                expanded.Transform(Transform.Scale(new Plane(box.Center, Vector3d.ZAxis),
                    1+.1/size.X, 1+.1/size.Y, 1+.1/size.Z));
                cutters.Add((expanded, box));
                foreach (var piece in pieces)
                    foreach (var part in Flatten(piece, name, obj.Id, width, depth))
                        prepared.Add(part);
            }
            catch (Exception ex) { throw new InvalidOperationException($"{name} ({obj.Id}): {ex.Message}", ex); }
        }
        return prepared;

    }

    private static int Priority(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("ground floor") || n.Contains("upper floor")) return 0;
        if (n.Contains("wall")) return 1;
        return n.Contains("roof") || n.Contains("slab") || n.Contains("deck") ? 2 : 3;
    }

    private static Brep Solid(RhinoObject obj, double factor, bool barn)
    {
        var b = obj.Geometry switch
        {
            Brep brep => brep.DuplicateBrep(),
            Extrusion e => e.ToBrep(true),
            Mesh m when m.IsClosed => Brep.CreateFromMesh(m, true),
            _ => throw new ArgumentException("Select a solid Brep, extrusion or closed mesh.")
        };
        b.Transform(Transform.Scale(Point3d.Origin, factor));
        if (!b.IsSolid && barn && (obj.Attributes.Name is "Simplified - west wall" or "Simplified - north wall"))
        {
            b = RecoverPanel((Brep)obj.Geometry,factor);
        }
        if (b == null || !b.IsValid || !b.IsSolid) throw new ArgumentException("Component must enclose a valid solid.");
        if (b.SolidOrientation == BrepSolidOrientation.Inward) b.Flip();
        return b;
    }

    private static Brep RecoverPanel(Brep source, double factor)
    {
        // The saved render faces retain the wall openings. Union their projected triangles,
        // including window reveals, and extrude the resulting profile through the panel depth.
        var render = new Mesh();
        foreach (var face in source.Faces)
            render.Append(face.GetMesh(MeshType.Render) ?? throw new ArgumentException("Missing barn render mesh."));
        render.Scale(factor);
        render.Faces.ConvertQuadsToTriangles();
        var box = render.GetBoundingBox(true);
        bool acrossX = box.Diagonal.X < box.Diagonal.Y;
        var paths = new Paths64();
        const double grid = 10000;
        foreach (var f in render.Faces)
        {
            var triangle = new Path64();
            foreach (var i in new[]{f.A,f.B,f.C})
            {
                var p = render.Vertices.Point3dAt(i);
                triangle.Add(new Point64((acrossX?p.Y:p.X)*grid,p.Z*grid));
            }
            if (Clipper.Area(triangle)==0) continue;
            if (!Clipper.IsPositive(triangle)) triangle.Reverse();
            paths.Add(triangle);
        }
        var outline = Clipper.Union(paths,FillRule.NonZero);
        // Close sub-nozzle cracks where source edges meet within a few microns.
        outline = Clipper.InflatePaths(outline,100,JoinType.Miter,EndType.Polygon);
        outline = Clipper.InflatePaths(outline,-100,JoinType.Miter,EndType.Polygon);
        var loops = new List<Curve>();
        foreach (var ring in Clipper.SimplifyPaths(outline,10))
        {
            var points = ring.Select(p => acrossX ? new Point3d(box.Min.X,p.X/grid,p.Y/grid) :
                                                   new Point3d(p.X/grid,box.Min.Y,p.Y/grid)).ToList();
            points.Add(points[0]);
            loops.Add(new PolylineCurve(points));
        }
        var regions = Brep.CreatePlanarBreps(loops,Tol);
        if (regions == null || regions.Length!=1) throw new ArgumentException("Wall silhouette needs repair.");
        var direction = acrossX ? Vector3d.XAxis*box.Diagonal.X : Vector3d.YAxis*box.Diagonal.Y;
        return regions[0].Faces[0].CreateExtrusion(new LineCurve(Point3d.Origin,Point3d.Origin+direction),true);
    }

    private static IEnumerable<Brep> Subtract(Brep body, Brep cutter)
    {
        if (!BoundingBox.Intersection(body.GetBoundingBox(true),cutter.GetBoundingBox(true)).IsValid) return new[]{body};
        foreach (var tolerance in new[] { Tol, Tol/10, Tol*2 })
        {
            var cut = Brep.CreateBooleanDifference(body,cutter,tolerance);
            if (cut != null && cut.All(p => p.IsValid && p.IsSolid)) return cut;
        }
        // Perturb a coincident seam by a few microns, far below the glue allowance.
        var shifted = cutter.DuplicateBrep();
        shifted.Translate(.0023,.0037,.0041);
        var retry = Brep.CreateBooleanDifference(body,shifted,Tol/10);
        if (retry != null && retry.All(p=>p.IsValid && p.IsSolid)) return retry;
        var meshes = Mesh.CreateBooleanDifference(new[]{ToMesh(body)},new[]{ToMesh(cutter)},
            new MeshBooleanOptions {Tolerance=Tol},out var status);
        if (status == Rhino.Commands.Result.Nothing) return new[]{body};
        if (status != Rhino.Commands.Result.Success || meshes == null || meshes.Any(m=>!m.IsValid || !m.IsClosed))
            return PlanarDifference(body,cutter);
        return meshes.Select(m => Brep.CreateFromMesh(m,true));
    }

    // Partition by the cutter's boundary planes when coincident edges defeat a solid boolean.
    // Inside/outside is constant within each resulting cell.
    private static IEnumerable<Brep> PlanarDifference(Brep body, Brep cutter)
    {
        var cells = new List<Brep>{body};
        var box = body.GetBoundingBox(true);
        box.Inflate(1);
        foreach (var face in cutter.Faces)
        {
            if (!face.TryGetPlane(out var plane,Tol)) throw new InvalidOperationException("Curved joint subtraction failed.");
            var surface = PlaneSurface.CreateThroughBox(plane,box);
            if (surface == null) continue;
            var next = new List<Brep>();
            foreach (var cell in cells)
            {
                if (!BoundingBox.Intersection(cell.GetBoundingBox(true),cutter.GetBoundingBox(true)).IsValid) { next.Add(cell); continue; }
                var distances = cell.Vertices.Select(v=>plane.DistanceTo(v.Location)).ToArray();
                if (distances.Min() >= -.02 || distances.Max() <= .02) { next.Add(cell); continue; }
                var fragments = cell.Split(surface.ToBrep(),Tol/10);
                if (fragments.Length < 2) throw new InvalidOperationException($"Cannot split planar joint at {plane.Origin}, normal {plane.Normal}, cell {cell.GetBoundingBox(true)}, cutter {cutter.GetBoundingBox(true)}.");
                foreach (var fragment in fragments)
                {
                    var closed = fragment.CapPlanarHoles(Tol);
                    if (closed == null || !closed.IsSolid) throw new InvalidOperationException("Cannot close planar joint.");
                    next.Add(closed);
                }
            }
            cells = next;
        }
        var outside = cells.Where(cell =>
        {
            // Each half-space of each cutter plane has a constant sign over the cell,
            // so the volume centroid has the same classification even for concave cells.
            using var mass = VolumeMassProperties.Compute(cell);
            return mass != null && !cutter.IsPointInside(mass.Centroid,Tol/10,true);
        }).ToArray();
        if (outside.Length < 2) return outside;
        var joined = Brep.CreateBooleanUnion(outside,Tol);
        return joined != null && joined.All(b=>b.IsValid && b.IsSolid) ? joined : outside;
    }

    private static Mesh ToMesh(Brep b)
    {
        var mesh = new Mesh();
        foreach (var patch in Mesh.CreateFromBrep(b, MeshingParameters.QualityRenderMesh)) mesh.Append(patch);
        mesh.Faces.ConvertQuadsToTriangles();
        mesh.Vertices.CombineIdentical(true,true);
        mesh.Faces.CullDegenerateFaces();
        mesh.UnifyNormals();
        mesh.Compact();
        if (mesh.SolidOrientation() < 0) mesh.Flip(true,true,true);
        if (!mesh.IsValid || !mesh.IsClosed) throw new InvalidOperationException("Part could not be meshed watertight.");
        return mesh;
    }

    private static IEnumerable<Part> Flatten(Brep body, string name, Guid source, double width, double depth)
    {
        if (body.GetVolume() < .001) yield break;
        var mesh = ToMesh(body);
        var candidates = new List<(Transform Frame,double Contact,double Overhang,double Footprint)>();
        foreach (var face in body.Faces)
        {
            if (!face.TryGetPlane(out var plane, Tol)) continue;
            var edge = face.AdjacentEdges().Select(i => body.Edges[i]).Where(e => e.IsLinear(Tol))
                .OrderByDescending(e => e.GetLength()).FirstOrDefault();
            var along = edge == null ? plane.XAxis : edge.PointAtEnd - edge.PointAtStart;
            var n = face.OrientationIsReversed ? -plane.ZAxis : plane.ZAxis;
            var frame = new Plane(plane.Origin, along, Vector3d.CrossProduct(-n,along));
            var transform = Transform.PlaneToPlane(frame, Plane.WorldXY);
            var bounds = mesh.GetBoundingBox(transform);
            if (Math.Abs(bounds.Min.Z) > .01) continue;
            double contact = 0, overhang = 0;
            foreach (var tri in mesh.Faces)
            {
                var a = transform * mesh.Vertices.Point3dAt(tri.A);
                var b = transform * mesh.Vertices.Point3dAt(tri.B);
                var c = transform * mesh.Vertices.Point3dAt(tri.C);
                var cross = Vector3d.CrossProduct(b-a,c-a);
                if (Math.Max(a.Z,Math.Max(b.Z,c.Z)) < bounds.Min.Z+.01) contact += Math.Abs(cross.Z)/2;
                else if (cross.Z < -.7071*cross.Length) overhang -= cross.Z/2;
            }
            if (contact>0) candidates.Add((transform, contact, overhang, bounds.Diagonal.X*bounds.Diagonal.Y));
        }
        if (candidates.Count == 0) throw new InvalidOperationException("No supporting planar face.");
        double minContact = .65*candidates.Max(c => c.Contact);
        var xform = candidates.Where(c => c.Contact >= minContact).OrderBy(c => Math.Round(c.Overhang,1))
            .ThenByDescending(c => Math.Round(c.Contact,1)).ThenBy(c => c.Footprint).First().Frame;
        var flat = body.DuplicateBrep();
        flat.Transform(xform);
        var box = flat.GetBoundingBox(true);
        int Tiles(double w,double h) => (int)Math.Ceiling((w-Tol)/width)*(int)Math.Ceiling((h-Tol)/depth);
        if (Tiles(box.Diagonal.Y,box.Diagonal.X) < Tiles(box.Diagonal.X,box.Diagonal.Y))
        {
            var turn = Transform.Rotation(Math.PI/2,Vector3d.ZAxis,Point3d.Origin);
            xform = turn*xform; flat.Transform(turn); box = flat.GetBoundingBox(true);
        }
        xform.TryGetInverse(out var inverse);
        int nx = Math.Max(1,(int)Math.Ceiling((box.Diagonal.X-Tol)/width));
        int ny = Math.Max(1,(int)Math.Ceiling((box.Diagonal.Y-Tol)/depth));
        if (Math.Min(box.Diagonal.Z, Math.Min(box.Diagonal.X,box.Diagonal.Y)) < .4)
        { RhinoApp.WriteLine($"Omitted sub-0.4 mm sliver of {name}"); yield break; }
        for (int i=0;i<nx;i++)
        for (int j=0;j<ny;j++)
        {
            var low = box.PointAt((double)i/nx,(double)j/ny,0);
            var high = box.PointAt((i+1.0)/nx,(j+1.0)/ny,1);
            var clip = new BoundingBox(low-new Vector3d(i==0?1:0,j==0?1:0,1),
                high+new Vector3d(i==nx-1?1:0,j==ny-1?1:0,1));
            var pieces = nx*ny==1 ? new[]{flat} : Brep.CreateBooleanIntersection(flat,clip.ToBrep(),Tol);
            if (pieces==null) throw new InvalidOperationException("Bed-size split failed.");
            foreach (var piece in pieces.Where(p => p.GetVolume()>.001))
            {
                var printable = ToMesh(piece);
                var assembled = printable.DuplicateMesh(); assembled.Transform(inverse);
                printable.Translate(Point3d.Origin-printable.GetBoundingBox(true).Min);
                yield return new Part(name,source,assembled,printable);
            }
        }
    }
}
