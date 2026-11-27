using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;

namespace Go2Bed;

// Trims every part by a clearance shell around the parts before it in trimming order,
// so touching parts print with a gap instead of fused or overlapping.
internal static class SolidCleanup
{
    // Each part grows by this much along each axis before it cuts later parts,
    // the same as a Minkowski sum with a 0.1 mm cube.
    private const double Clearance = .05;
    private const double Tol = .001;

    internal static List<Component> Cut(List<Component> source)
    {
        var cutters = new List<(Mesh Mesh,BoundingBox Box)>();
        var cutterBreps = new Dictionary<int,Brep?>();
        var cleaned = new List<Component>();
        try
        {
            for (int i=0;i<source.Count;i++)
            {
                using var mesh=Parts.ToMesh(source[i].Solid);
                Check(source,i,mesh);
                var box=mesh.GetBoundingBox(true);
                var overlapping=Enumerable.Range(0,cutters.Count).Where(c => Overlaps(box,cutters[c].Box)).ToList();
                var pieces=overlapping.Count==0 ? new[] { mesh.DuplicateMesh() } : Subtract(source,i,mesh,cutters,overlapping,cutterBreps);
                foreach (var piece in pieces)
                using (piece)
                {
                    piece.Faces.ConvertQuadsToTriangles();
                    CapPlanarHoles(piece);
                    var xyz=new double[piece.Vertices.Count*3];
                    for (int v=0;v<piece.Vertices.Count;v++)
                    {
                        var p=piece.Vertices.Point3dAt(v);
                        xyz[3*v]=p.X; xyz[3*v+1]=p.Y; xyz[3*v+2]=p.Z;
                    }
                    var faces=new long[piece.Faces.Count*3];
                    for (int f=0;f<piece.Faces.Count;f++)
                    {
                        faces[3*f]=piece.Faces[f].A; faces[3*f+1]=piece.Faces[f].B; faces[3*f+2]=piece.Faces[f].C;
                    }
                    // Rounding keeps coplanar faces mergeable. It can also flatten a sliver, so fall back to exact coordinates.
                    using var rebuilt=Rebuild(xyz,faces,true) ?? Rebuild(xyz,faces,false) ??
                        throw new PartException($"Boolean cleanup left {Describe(source,i)} with an open mesh ({piece.Faces.Count} faces) " +
                            "after cutting the clearance gaps to its neighbours. A neighbour probably leaves a sliver or knife edge on it; " +
                            "check where it touches other parts at a shallow angle.",
                            source[i].Objects.Concat(overlapping.SelectMany(c => source[c].Objects)).Distinct().ToList());
                    var solid=Brep.CreateFromMesh(rebuilt,true);
                    var merged=solid.DuplicateBrep();
                    // Merged faces print cleaner, but their edges can drift apart within tolerance so the
                    // part no longer meshes watertight later. Keep the merge only if it still meshes closed.
                    if (merged.MergeCoplanarFaces(.0002,.00001) && merged.IsValid && merged.IsSolid &&
                        Math.Abs(merged.GetVolume()-solid.GetVolume())<.001 && Parts.MeshesWatertight(merged)) solid=merged;
                    cleaned.Add(source[i] with {Solid=solid});
                }
                var grown=Grow(mesh,Clearance);
                if (!grown.IsValid || !grown.IsClosed)
                    throw new PartException($"Boolean cleanup could not grow {Describe(source,i)} by its {Clearance} mm clearance. " +
                        $"{MeshReport(mesh)} Very thin walls or tiny edges cause this. Simplify the part or rebuild it with fewer small faces.",
                        source[i].Objects);
                cutters.Add((grown,grown.GetBoundingBox(true)));
            }
            return cleaned;
        }
        finally
        {
            foreach (var (cutter,_) in cutters) cutter.Dispose();
            foreach (var brep in cutterBreps.Values) brep?.Dispose();
        }
    }

    private static void Check(List<Component> source,int i,Mesh mesh)
    {
        bool manifold=mesh.IsManifold(true,out bool oriented,out bool open);
        if (manifold && oriented && !open && mesh.IsClosed && mesh.Volume()>0) return;
        string why=!mesh.IsClosed || open ? "it is not closed" : !manifold ? "some edges are shared by more than two faces"
            : !oriented ? "its faces are not oriented consistently" : "it encloses no volume";
        throw new PartException($"Boolean cleanup rejected {Describe(source,i)}: its mesh is not a valid solid because {why}. {MeshReport(mesh)} " +
            "Typical causes are self-intersections, faces that touch along an edge or at a point, or two solids in one group that only touch. " +
            "Run Check and ShowEdges on it, or BedHeal.",source[i].Objects);
    }

    private static Mesh[] Subtract(List<Component> source,int i,Mesh mesh,List<(Mesh Mesh,BoundingBox Box)> cutters,List<int> overlapping,
        Dictionary<int,Brep?> cutterBreps)
    {
        var all=Difference(mesh,overlapping.Select(c => cutters[c].Mesh),out string log);
        if (all!=null)
        {
            var shells=Shells(all);
            if (shells.All(Closes)) return shells;
            // Rhino 8's mesh boolean can leave holes in the result. Try one neighbour at a time,
            // capping the planar hole each single cut leaves, then the slower Brep boolean.
            var retry=(overlapping.Count>1 ? Stepwise(mesh,overlapping.Select(c => cutters[c])) : null)
                ?? BrepDifference(source[i].Solid,overlapping,cutters,cutterBreps);
            if (retry==null) return shells;
            foreach (var m in shells) m.Dispose();
            return Shells(retry);
        }
        // Replay one cutter at a time to name the neighbour that breaks the cut.
        foreach (int c in overlapping)
        {
            var single=Difference(mesh,new[] { cutters[c].Mesh },out string reason);
            if (single==null)
                throw new PartException($"Boolean cleanup failed while cutting {Describe(source,i)} against {Describe(source,c)}" +
                    $"{Reason(reason)}. Each part is trimmed by a {Clearance} mm clearance shell around the parts before it in trimming order. " +
                    "These two probably meet at a tangent, nearly coincident faces, or a sliver. " +
                    "Move them so they overlap clearly or leave a clear gap, or put them in one group.",
                    source[i].Objects.Concat(source[c].Objects).Distinct().ToList());
            foreach (var m in single) m.Dispose();
        }
        throw new PartException($"Boolean cleanup failed while trimming {Describe(source,i)} against " +
            $"{string.Join(", ",overlapping.Select(c => Describe(source,c)))}{Reason(log)}. Each cut works on its own, " +
            "so the problem shows up only when they are combined. Look for places where several parts meet in one spot.",
            source[i].Objects.Concat(overlapping.SelectMany(c => source[c].Objects)).Distinct().ToList());
    }

    private static Mesh[]? Difference(Mesh mesh,IEnumerable<Mesh> cutters,out string log)
    {
#if NETFRAMEWORK
        // Rhino 7 has no boolean options: no tolerance and no log.
        var result=Mesh.CreateBooleanDifference(new[] { mesh },cutters);
        log="";
        return result;
#else
        using var text=new TextLog();
        var options=new MeshBooleanOptions { Tolerance=Tol, TextLog=text };
        var result=Mesh.CreateBooleanDifference(new[] { mesh },cutters,options,out var status);
        log=text.ToString().Trim();
        return status==Rhino.Commands.Result.Success ? result ?? Array.Empty<Mesh>() : null;
#endif
    }

    private static string Reason(string log) =>
        string.IsNullOrWhiteSpace(log) ? "" : " (Rhino: "+string.Join(" ",log.Split(new[] { '\n' },StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))+")";

    // Splits boolean output into printable pieces. Inward-facing shells are voids and stay with the piece around them.
    private static Mesh[] Shells(Mesh[] results)
    {
        var shells=results.SelectMany(r => { var split=r.SplitDisjointPieces(); r.Dispose(); return split; }).ToList();
        var outer=shells.Where(s => s.SolidOrientation()>=0).ToList();
        foreach (var hole in shells.Where(s => s.SolidOrientation()<0))
        {
            var box=hole.GetBoundingBox(true);
            var host=outer.FirstOrDefault(o => o.GetBoundingBox(true).Contains(box,false));
            host?.Append(hole);
            hole.Dispose();
        }
        var kept=outer.Where(s => s.Volume()>=.001).ToArray();
        foreach (var s in outer.Except(kept)) s.Dispose();
        return kept;
    }

    private static bool Overlaps(BoundingBox a,BoundingBox b) =>
        a.Min.X<=b.Max.X && b.Min.X<=a.Max.X && a.Min.Y<=b.Max.Y && b.Min.Y<=a.Max.Y && a.Min.Z<=b.Max.Z && b.Min.Z<=a.Max.Z;

    // Moves every vertex so each adjacent face plane shifts outward by the cube's support distance
    // along its normal, which matches a Minkowski sum with a cube on planar faces.
    // The same cut with Brep booleans, using the cutters rebuilt as Breps. Null if it fails.
    private static Mesh[]? BrepDifference(Brep part,List<int> overlapping,List<(Mesh Mesh,BoundingBox Box)> cutters,Dictionary<int,Brep?> cache)
    {
        var breps=new List<Brep>();
        foreach (int c in overlapping)
        {
            if (!cache.TryGetValue(c,out var brep))
            {
                brep=Brep.CreateFromMesh(cutters[c].Mesh,true);
                if (brep!=null)
                {
                    brep.MergeCoplanarFaces(Tol,RhinoMath.ToRadians(.01));
                    if (!brep.IsValid || !brep.IsSolid) { brep.Dispose(); brep=null; }
                }
                cache[c]=brep;
            }
            if (brep==null) return null;
            breps.Add(brep);
        }
        var result=Brep.CreateBooleanDifference(new[] { part },breps,Tol);
        if (result==null) return null;
        var meshes=new List<Mesh>();
        foreach (var piece in result)
        {
            if (!piece.IsValid || !piece.IsSolid) { foreach (var m in meshes) m.Dispose(); return null; }
            if (piece.SolidOrientation==BrepSolidOrientation.Inward) piece.Flip();
            try { meshes.Add(Parts.ToMesh(piece)); }
            catch (InvalidOperationException) { foreach (var m in meshes) m.Dispose(); return null; }
        }
        return meshes.ToArray();
    }

    private static bool Closes(Mesh piece)
    {
        using var probe=piece.DuplicateMesh();
        probe.Faces.ConvertQuadsToTriangles();
        CapPlanarHoles(probe);
        probe.Vertices.CombineIdentical(true,true);
        return probe.IsClosed;
    }

    // Cuts with each cutter in turn, capping planar holes after every cut. Null if any step fails or stays open.
    private static Mesh[]? Stepwise(Mesh mesh,IEnumerable<(Mesh Mesh,BoundingBox Box)> cutters)
    {
        var current=new List<Mesh> { mesh.DuplicateMesh() };
        foreach (var (cutter,box) in cutters)
        {
            var next=new List<Mesh>();
            foreach (var part in current)
            {
                if (!Overlaps(part.GetBoundingBox(true),box)) { next.Add(part); continue; }
                var result=Difference(part,new[] { cutter },out _);
                part.Dispose();
                if (result==null) { foreach (var m in next.Concat(current)) m.Dispose(); return null; }
                foreach (var piece in result)
                {
                    piece.Faces.ConvertQuadsToTriangles();
                    CapPlanarHoles(piece);
                    piece.Vertices.CombineIdentical(true,true);
                    piece.Compact();
                    if (!piece.IsClosed) { foreach (var m in next.Concat(result)) m.Dispose(); return null; }
                    next.Add(piece);
                }
            }
            current=next;
        }
        return current.ToArray();
    }

    // Rhino 8's mesh boolean can leave out the flat face where a cutter slices clean through a part,
    // such as the bottom of a wall standing on a slab, leaving one planar hole (Rhino 9 does not).
    // Holes whose edges all lie in one plane are filled; anything else is left for the checks below.
    internal static void CapPlanarHoles(Mesh piece)
    {
        var probe=piece.DuplicateMesh();
        probe.Vertices.CombineIdentical(true,true);
        if (probe.IsClosed) { probe.Dispose(); return; }
        var holes=probe.GetNakedEdges() ?? Array.Empty<Polyline>();
        bool planar=holes.Length>0 && holes.All(h => h.IsClosed && h.Count>=4 &&
            Plane.FitPlaneToPoints(h,out var plane)==PlaneFitResult.Success && h.All(p => Math.Abs(plane.DistanceTo(p))<Tol));
        if (planar && probe.FillHoles())
        {
            probe.Vertices.CombineIdentical(true,true);
            probe.UnifyNormals();
            probe.Compact();
            if (probe.IsClosed && probe.IsValid && probe.SolidOrientation()>0)
            {
                piece.CopyFrom(probe);
                probe.Dispose();
                return;
            }
        }
        probe.Dispose();
    }

    private static Mesh Grow(Mesh source,double half)
    {
        var mesh=source.DuplicateMesh();
        mesh.FaceNormals.ComputeFaceNormals();
        var moved=new Point3d[mesh.Vertices.Count];
        for (int v=0;v<mesh.Vertices.Count;v++)
        {
            var planes=new List<Vector3d>();
            foreach (int f in mesh.Vertices.GetVertexFaces(v))
            {
                Vector3d n=mesh.FaceNormals[f];
                if (n.IsValid && n.Length>0 && planes.All(p => p*n<.99999)) planes.Add(n);
            }
            // Least squares for n·d = h over the distinct planes; the small ridge keeps flat and edge vertices solvable.
            double a00=1e-9,a01=0,a02=0,a11=1e-9,a12=0,a22=1e-9,b0=0,b1=0,b2=0;
            foreach (var n in planes)
            {
                double h=half*(Math.Abs(n.X)+Math.Abs(n.Y)+Math.Abs(n.Z));
                a00+=n.X*n.X; a01+=n.X*n.Y; a02+=n.X*n.Z; a11+=n.Y*n.Y; a12+=n.Y*n.Z; a22+=n.Z*n.Z;
                b0+=h*n.X; b1+=h*n.Y; b2+=h*n.Z;
            }
            double det=a00*(a11*a22-a12*a12)-a01*(a01*a22-a12*a02)+a02*(a01*a12-a11*a02);
            var d=new Vector3d(
                (b0*(a11*a22-a12*a12)-a01*(b1*a22-a12*b2)+a02*(b1*a12-a11*b2))/det,
                (a00*(b1*a22-a12*b2)-b0*(a01*a22-a12*a02)+a02*(a01*b2-b1*a02))/det,
                (a00*(a11*b2-b1*a12)-a01*(a01*b2-b1*a02)+b0*(a01*a12-a11*a02))/det);
            // A sharp spike would push its tip far out; cap it so the cutter stays local.
            if (!d.IsValid) d=Vector3d.Zero;
            if (d.Length>4*half) { d.Unitize(); d*=4*half; }
            moved[v]=mesh.Vertices.Point3dAt(v)+d;
        }
        for (int v=0;v<moved.Length;v++) mesh.Vertices.SetVertex(v,moved[v]);
        mesh.Normals.ComputeNormals();
        mesh.FaceNormals.ComputeFaceNormals();
        return mesh;
    }

    // Names a component well enough to find it: a split group yields several components with one name.
    internal static string Describe(List<Component> source,int i)
    {
        var name=source[i].Name;
        var same=source.Select((c,k) => (c,k)).Where(x => x.c.Name==name).Select(x => x.k).ToList();
        var piece=same.Count>1 ? $", piece {same.IndexOf(i)+1} of {same.Count}" : "";
        return $"'{name}' (part {i+1} of {source.Count}{piece})";
    }

    private static string MeshReport(Mesh mesh)
    {
        int naked=mesh.GetNakedEdges()?.Length ?? 0;
        int nonManifold=0;
        for (int e=0;e<mesh.TopologyEdges.Count;e++)
            if (mesh.TopologyEdges.GetConnectedFaces(e).Length>2) nonManifold++;
        var size=mesh.GetBoundingBox(true).Diagonal;
        return $"Its print-scale mesh has {mesh.Faces.Count} triangles, {naked} open edge loops, {nonManifold} edges shared by more than two faces, " +
            $"and measures {size.X:0.##} x {size.Y:0.##} x {size.Z:0.##} mm.";
    }

    // Welds the boolean output into a closed Rhino mesh, or returns null if it stays open.
    private static Mesh? Rebuild(double[] xyz,long[] faces,bool round)
    {
        var lookup=new Dictionary<(double,double,double),int>();
        var points=new List<Point3d>();
        var map=new int[xyz.Length/3];
        for (int v=0;v<map.Length;v++)
        {
            // Adding zero turns -0 into 0 so both hash alike.
            double X(int k) => (round ? Math.Round(xyz[3*v+k],4) : xyz[3*v+k])+0.0;
            var key=(X(0),X(1),X(2));
            if (!lookup.TryGetValue(key,out int index))
            {
                index=points.Count; lookup.Add(key,index);
                points.Add(new Point3d(key.Item1,key.Item2,key.Item3));
            }
            map[v]=index;
        }
        var triangles=new List<(int A,int B,int C)>();
        for (int f=0;f<faces.Length;f+=3)
        {
            int a=map[faces[f]],b=map[faces[f+1]],c=map[faces[f+2]];
            if (a!=b && b!=c && a!=c) triangles.Add((a,b,c));
        }
        RemoveSlivers(points,triangles);
        var mesh=new Mesh();
        mesh.Vertices.UseDoublePrecisionVertices=true;
        foreach (var p in points) mesh.Vertices.Add(p.X,p.Y,p.Z);
        foreach (var (a,b,c) in triangles) mesh.Faces.AddFace(a,b,c);
        mesh.Vertices.CombineIdentical(true,true);
        mesh.Faces.CullDegenerateFaces();
        mesh.Compact();
        if (mesh.Faces.Count>0 && mesh.IsValid && mesh.IsClosed) return mesh;
        mesh.Dispose();
        return null;
    }

    // A zero-area triangle has one vertex on its own long edge. Deleting it alone would leave
    // that vertex on the neighbour's edge without a shared edge, which opens the mesh.
    // Splitting the neighbour at that vertex removes the sliver and keeps the mesh closed.
    private static void RemoveSlivers(List<Point3d> points,List<(int A,int B,int C)> triangles)
    {
        for (int guard=4*triangles.Count+16;guard>0;guard--)
        {
            int sliver=-1;
            (int M,int P,int Q) turned=default;
            for (int t=0;t<triangles.Count && sliver<0;t++)
            {
                var (a,b,c)=triangles[t];
                double ab=points[a].DistanceTo(points[b]),bc=points[b].DistanceTo(points[c]),ca=points[c].DistanceTo(points[a]);
                double longest=Math.Max(ab,Math.Max(bc,ca));
                double twiceArea=Vector3d.CrossProduct(points[b]-points[a],points[c]-points[a]).Length;
                if (twiceArea>1e-7*longest) continue;
                sliver=t;
                // Rotate so the long edge runs from P to Q and M lies on it.
                turned = longest==ab ? (c,a,b) : longest==bc ? (a,b,c) : (b,c,a);
            }
            if (sliver<0) return;
            var (m,p,q)=turned;
            int neighbour=triangles.FindIndex(t => (t.A==q && t.B==p) || (t.B==q && t.C==p) || (t.C==q && t.A==p));
            if (neighbour<0) { triangles.RemoveAt(sliver); continue; }
            var n=triangles[neighbour];
            int d = n.A!=p && n.A!=q ? n.A : n.B!=p && n.B!=q ? n.B : n.C;
            if (d==m)
            {
                // Two faces folded onto each other.
                triangles.RemoveAt(Math.Max(sliver,neighbour));
                triangles.RemoveAt(Math.Min(sliver,neighbour));
                continue;
            }
            triangles[neighbour]=(q,m,d);
            triangles[sliver]=(m,p,d);
        }
    }
}
