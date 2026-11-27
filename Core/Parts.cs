using Rhino;
using Clipper2Lib;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Go2Bed;

internal record SourceGroup(Guid Id, string? Name, int Size);

internal record SourcePart(GeometryBase Geometry, string? Name, Guid Id, bool RepairBarnPanel = false,
    string? LayerName = null, double? ModelTolerance = null, double? AngleTolerance = null,
    IReadOnlyList<SourceGroup>? Groups = null)
{
    internal static SourcePart FromObject(RhinoObject obj, bool barn = false)
    {
        var doc = obj.Document;
        var layer = doc.Layers[obj.Attributes.LayerIndex];
        var groups = (obj.Attributes.GetGroupList() ?? Array.Empty<int>())
            .Select(i => doc.Groups.FindIndex(i))
            .Where(g => g != null && !Compat.IsDeleted(g))
            .Select(g => new SourceGroup(g.Id, g.Name, doc.Groups.GroupMembers(g.Index)?.Length ?? 0))
            .ToArray();
        return new SourcePart(obj.Geometry, obj.Attributes.Name, obj.Id,
            barn || layer.FullPath == "COMPARE - Simplified full size::Walls", layer.Name,
            doc.ModelAbsoluteTolerance, doc.ModelAngleToleranceRadians, groups);
    }

    // Like FromObject, but a block instance gives one source per solid inside it.
    internal static IEnumerable<SourcePart> FromObjects(RhinoObject obj, bool barn = false)
    {
        if (obj is not InstanceObject instance) return new[] { FromObject(obj, barn) };
        var groups = FromObject(obj).Groups ?? Array.Empty<SourceGroup>();
        return InputCheck.Expand(obj.Document, instance.InstanceDefinition, instance.InstanceXform, obj.Id,
            obj.Attributes.Name, groups);
    }

    // Grasshopper geometry, with the Rhino object it references when there is one.
    internal static IEnumerable<SourcePart> FromGeometry(RhinoDoc doc, GeometryBase geometry, RhinoObject? referenced)
    {
        if (geometry is InstanceReferenceGeometry block)
        {
            var definition = doc.InstanceDefinitions.FindId(block.ParentIdefId);
            if (definition == null) return new[] { new SourcePart(geometry, referenced?.Attributes.Name, referenced?.Id ?? Guid.Empty) };
            return InputCheck.Expand(doc, definition, block.Xform, referenced?.Id ?? Guid.NewGuid(),
                referenced?.Attributes.Name, referenced == null ? Array.Empty<SourceGroup>() : FromObject(referenced).Groups ?? Array.Empty<SourceGroup>());
        }
        return new[] { referenced == null ? new SourcePart(geometry, null, Guid.Empty) : FromObject(referenced) with { Geometry = geometry } };
    }
}

internal record Part(string Name, Guid Source, Mesh Assembled, Mesh Flat);
// Members lists every Rhino object fused into the component; a single object leaves it null.
internal record Component(string Name, Guid Source, Brep Solid, IReadOnlyList<Guid>? Members = null)
{
    internal IReadOnlyList<Guid> Objects => Members ?? new[] { Source };
}

// A failure that points at Rhino objects, so commands can select them for the user.
internal class PartException : InvalidOperationException
{
    internal PartException(string message, IReadOnlyList<Guid> objects, Exception? inner = null)
        : base(message, inner) { Objects = objects; }
    internal IReadOnlyList<Guid> Objects { get; }
}
internal static class Parts
{
    private const double Tol = .001; // All geometry here is at print scale, in millimetres.
    internal static List<Component> Clean(IEnumerable<SourcePart> objects, double factor, JointOptions joints)
    {
        var components = new List<Component>();
        // Read every object first and report all problems together, so the model is fixed in one pass.
        var problems = new List<InputProblem>();
        var clusters = new List<(string Name, List<(Brep Solid, string Name, SourcePart Source)> Solids, IReadOnlyList<Guid>? Members, string Label)>();
        int read = 0;
        // Only original names determine trimming priority. A group trims at its highest-priority member.
        foreach (var cluster in Grouped(WithDisplayNames(objects.OrderBy(o => Priority(o.Name)))))
        {
            var solids = new List<(Brep Solid, string Name, SourcePart Source)>();
            foreach (var (obj, name) in cluster.Members)
            {
                if (InputCheck.CarriesNoVolume(obj.Geometry)) continue;
                read++;
                Brep[] shells;
                try { shells = Shells(FuseInternalFaces(Solid(obj,factor))); }
                catch (Exception ex) { problems.Add(new InputProblem(name,ex.Message,obj.Id)); continue; }
                for (int k = 0; k < shells.Length; k++)
                {
                    string shellName = shells.Length == 1 ? name : $"{name} solid {k+1}";
                    if (InputCheck.TooThin(shells[k]) is { } thin) problems.Add(new InputProblem(shellName,thin,obj.Id));
                    solids.Add((shells[k],shellName,obj));
                }
            }
            if (solids.Count == 0) continue;
            var members = cluster.Members.Count == 1 ? null : cluster.Members.Select(m => m.Source.Id).Distinct().ToArray();
            if (members?.Length == 1) members = null;
            var label = cluster.Members.Count == 1 ? cluster.Name : $"Group {cluster.Name}";
            clusters.Add((cluster.Name,solids,members,label));
        }
        if (read == 0) throw new ArgumentException("Nothing to print: select solid polysurfaces, extrusions, closed meshes or blocks that contain them.");
        problems.AddRange(InputCheck.Duplicates(clusters.SelectMany(c => c.Solids).Select(s => (s.Solid,s.Name,s.Source.Id)).ToList()));
        if (problems.Count > 0) throw new InputException(problems);

        foreach (var (name, solids, members, label) in clusters)
        {
            var source = solids[0].Source.Id;
            var breps = solids.Select(s => s.Solid).ToList();
            foreach (var solid in breps.Count == 1 ? breps.ToArray() : Union(label,solids.Select(s => s.Name).ToList(),members?.ToArray() ?? new[] { source },breps))
                components.Add(new Component(name,source,solid,members));
        }
        components=SolidCleanup.Cut(components);
        var jointed = joints.Fingers ? FingerJoints.Apply(components,joints.Clearance) : new HashSet<(int, int)>();
        // Contacts that take pins need no marks. Fingered contacts keep theirs.
        var pinned = joints.Pins ? AlignmentPins.Apply(components,joints.PinHole,jointed) : null;
        if (joints.Marks) AssemblyMarks.Apply(components,pinned);
        return components;
    }

    internal static List<Part> Orient(IEnumerable<Component> components, double width, double depth, double seamClearance = .2)
    {
        var prepared = new List<Part>();
        foreach (var component in components)
        {
            try { prepared.AddRange(Flatten(component.Solid,component.Name,component.Source,width,depth,seamClearance)); }
            catch (PartException) { throw; }
            catch (Exception ex) { throw new PartException($"{component.Name}: {ex.Message}",component.Objects,ex); }
        }
        return prepared;

    }

    internal static IEnumerable<(SourcePart Source, string Name)> WithDisplayNames(IEnumerable<SourcePart> objects)
    {
        var sources = objects.ToList();
        var used = new HashSet<string>(sources.Select(o => o.Name?.Trim() ?? ""), StringComparer.OrdinalIgnoreCase);
        var nextByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            if (!string.IsNullOrWhiteSpace(source.Name))
            {
                yield return (source, source.Name!);
                continue;
            }

            string prefix = string.IsNullOrWhiteSpace(source.LayerName) ? "Part" : source.LayerName!.Trim();
            int number = nextByLayer.GetValueOrDefault(prefix, 1);
            string name;
            do { name = $"{prefix} {number++:00}"; } while (!used.Add(name));
            nextByLayer[prefix] = number;
            yield return (source, name);
        }
    }

    // Fuses a group's solids so the rest of the pipeline sees one part. Members that do not
    // touch stay separate solids and print as separate pieces.
    private static Brep[] Union(string name, List<string> names, Guid[] ids, List<Brep> solids)
    {
        var fused = Brep.CreateBooleanUnion(solids,Tol);
        if (fused == null || fused.Length == 0 || fused.Any(b => !b.IsValid || !b.IsSolid))
            throw new PartException($"{name}: its {solids.Count} solids ({string.Join(", ",names)}) could not be unioned " +
                $"({(fused == null || fused.Length == 0 ? "no result" : $"{fused.Count(b => !b.IsValid || !b.IsSolid)} of {fused.Length} results open or invalid")}). " +
                "Union them in Rhino and retry; BooleanUnion there shows where it fails.",ids);
        foreach (var brep in fused)
        {
            brep.MergeCoplanarFaces(Tol,RhinoMath.ToRadians(.1));
            if (brep.SolidOrientation == BrepSolidOrientation.Inward) brep.Flip();
        }
        if (fused.Length > 1)
        {
            // Name which members ended up in which piece, so the gap is easy to find. A member lies
            // wholly inside one piece, so the piece nearest all its vertices is the one it joined.
            var owner = solids.Select(solid => Enumerable.Range(0,fused.Length).OrderBy(k =>
                solid.Vertices.Sum(v => v.Location.DistanceTo(fused[k].ClosestPoint(v.Location)))).First()).ToList();
            var pieces = fused.Select((_,k) =>
                $"piece {k+1}: {string.Join(", ",names.Where((_,m) => owner[m] == k))}");
            RhinoApp.WriteLine($"{name} prints as {fused.Length} separate pieces because its solids do not all touch ({string.Join("; ",pieces)}).");
        }
        return fused;
    }

    // A polysurface can be closed and still hold faces that lie back to back, such as a post cap
    // joined against a beam's underside without a boolean. Rhino accepts it as a solid, but welded
    // into a mesh those faces give edges with four faces, which no mesh boolean accepts.
    // Cutting the shared area out of both faces and rejoining fuses the two sides.
    internal static Brep FuseInternalFaces(Brep brep)
    {
        if (FourFaceEdges(brep) == 0) return brep;
        var faces = Enumerable.Range(0, brep.Faces.Count).Select(i => brep.Faces[i].DuplicateFace(false)).ToList();
        bool changed = false;
        for (int guard = 0; guard < 4 * brep.Faces.Count; guard++)
        {
            bool found = false;
            for (int i = 0; i < faces.Count && !found; i++)
            for (int j = i + 1; j < faces.Count && !found; j++)
            {
                if (!Opposed(faces[i], faces[j])) continue;
                var a = Outside(faces[i], faces[j]);
                var b = Outside(faces[j], faces[i]);
                if (a == null || b == null) continue;
                faces.RemoveAt(j); faces.RemoveAt(i);
                faces.AddRange(a); faces.AddRange(b);
                found = changed = true;
            }
            if (!found) break;
        }
        if (!changed) return brep;
        var joined = Brep.JoinBreps(faces, Tol);
        if (joined == null || joined.Length != 1 || !joined[0].IsSolid) return brep;
        var fused = joined[0];
        // Back-to-back faces enclose nothing, so fusing must keep the volume.
        double before = brep.GetVolume(), after = Math.Abs(fused.GetVolume());
        if (Math.Abs(after - Math.Abs(before)) > 1e-4 * Math.Abs(before) + Tol) return brep;
        fused.MergeCoplanarFaces(Tol, RhinoMath.ToRadians(.1));
        if (fused.SolidOrientation == BrepSolidOrientation.Inward) fused.Flip();
        return FourFaceEdges(fused) < FourFaceEdges(brep) ? fused : brep;
    }

    private static int FourFaceEdges(Brep brep)
    {
        var mesh = new Mesh();
        foreach (var patch in Mesh.CreateFromBrep(brep, MeshingParameters.QualityRenderMesh) ?? Array.Empty<Mesh>()) mesh.Append(patch);
        mesh.Vertices.CombineIdentical(true, true);
        int count = 0;
        for (int e = 0; e < mesh.TopologyEdges.Count; e++)
            if (mesh.TopologyEdges.GetConnectedFaces(e).Length > 2) count++;
        return count;
    }

    // A point strictly inside a single-face brep, and the face's outward normal there.
    private static (Point3d Point, Vector3d Normal)? Sample(Brep face)
    {
        var mesh = Mesh.CreateFromBrep(face, MeshingParameters.QualityRenderMesh)?.FirstOrDefault();
        if (mesh == null || mesh.Faces.Count == 0) return null;
        mesh.Faces.ConvertQuadsToTriangles();
        mesh.FaceNormals.ComputeFaceNormals();
        // The largest triangle keeps the sample clear of the face's edges.
        int best = Enumerable.Range(0, mesh.Faces.Count).OrderByDescending(f =>
        {
            var t = mesh.Faces[f];
            return Vector3d.CrossProduct(mesh.Vertices[t.B] - mesh.Vertices[t.A], mesh.Vertices[t.C] - mesh.Vertices[t.A]).Length;
        }).First();
        var tri = mesh.Faces[best];
        var p = (new Point3d(mesh.Vertices[tri.A]) + new Point3d(mesh.Vertices[tri.B]) + new Point3d(mesh.Vertices[tri.C])) / 3;
        return (p, mesh.FaceNormals[best]);
    }

    private static bool Opposed(Brep a, Brep b)
    {
        if (!a.Faces[0].IsPlanar(Tol) || !b.Faces[0].IsPlanar(Tol)) return false;
        if (!BoundingBox.Intersection(a.GetBoundingBox(true), b.GetBoundingBox(true)).IsValid) return false;
        var sa = Sample(a); var sb = Sample(b);
        if (sa == null || sb == null || sa.Value.Normal * sb.Value.Normal > -.9999) return false;
        return Math.Abs((sb.Value.Point - sa.Value.Point) * sa.Value.Normal) < Tol * 10;
    }

    // The pieces of face outside other, or null when the two do not overlap.
    private static List<Brep>? Outside(Brep face, Brep other)
    {
        var split = face.Faces[0].Split(other.DuplicateEdgeCurves(), Tol) ?? face;
        var keep = new List<Brep>();
        bool overlap = false;
        foreach (var piece in split.Faces.Select(f => f.DuplicateFace(false)))
        {
            var sample = Sample(piece);
            if (sample == null) continue;
            var target = other.Faces[0];
            if (target.ClosestPoint(sample.Value.Point, out double u, out double v) &&
                target.PointAt(u, v).DistanceTo(sample.Value.Point) < Tol * 10 &&
                target.IsPointOnFace(u, v) == PointFaceRelation.Interior) overlap = true;
            else keep.Add(piece);
        }
        return overlap ? keep : null;
    }

    // A polysurface can be closed and still be several solids that only touch, such as posts joined
    // to a beam without a boolean: the post caps lie on the beam's underside. Welded into one mesh,
    // those faces give edges with four faces, which no mesh boolean accepts. This splits such an
    // object into its closed solids so they can be unioned like the members of a group.
    internal static Brep[] Shells(Brep brep)
    {
        var patches = Mesh.CreateFromBrep(brep, MeshingParameters.QualityRenderMesh);
        if (patches == null || patches.Length != brep.Faces.Count) return new[] { brep };
        var mesh = new Mesh();
        var faceOf = new List<int>();
        for (int i = 0; i < patches.Length; i++)
        {
            patches[i].Faces.ConvertQuadsToTriangles();
            mesh.Append(patches[i]);
            faceOf.AddRange(Enumerable.Repeat(i, patches[i].Faces.Count));
        }
        // Weld as ToMesh does; this keeps every triangle, so faceOf stays aligned.
        mesh.Vertices.CombineIdentical(true, true);
        if (mesh.Faces.Count != faceOf.Count) return new[] { brep };
        mesh.FaceNormals.ComputeFaceNormals();
        var parent = Enumerable.Range(0, brep.Faces.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        void Join(int a, int b) => parent[Find(faceOf[a])] = Find(faceOf[b]);
        var edges = mesh.TopologyEdges;
        var vertices = mesh.TopologyVertices;
        bool split = false;
        for (int e = 0; e < edges.Count; e++)
        {
            var faces = edges.GetConnectedFaces(e);
            if (faces.Length == 2) { Join(faces[0], faces[1]); continue; }
            if (faces.Length < 3) continue;
            split = true;
            var ends = edges.GetTopologyVertices(e);
            Point3d u = vertices[ends.I], v = vertices[ends.J];
            var axis = v - u; axis.Unitize();
            // Whether a triangle runs along the edge from I to J, and the direction it leaves the edge in.
            var forward = new bool[faces.Length];
            var away = new Vector3d[faces.Length];
            for (int k = 0; k < faces.Length; k++)
            {
                var f = mesh.Faces[faces[k]];
                int[] t = { vertices.TopologyVertexIndex(f.A), vertices.TopologyVertexIndex(f.B), vertices.TopologyVertexIndex(f.C) };
                int at = Array.IndexOf(t, ends.I);
                forward[k] = t[(at + 1) % 3] == ends.J;
                var w = vertices[t.First(x => x != ends.I && x != ends.J)] - u;
                away[k] = w - (w * axis) * axis; away[k].Unitize();
            }
            // Turning from a triangle into the solid behind it, the first oppositely oriented
            // triangle closes the same solid. A coincident face at zero turn belongs to the other side.
            for (int k = 0; k < faces.Length; k++)
            {
                var inward = -(Vector3d)mesh.FaceNormals[faces[k]];
                int best = -1; double turn = double.MaxValue;
                for (int s = 0; s < faces.Length; s++)
                {
                    if (s == k || forward[s] == forward[k]) continue;
                    double a = Math.Atan2(away[s] * inward, away[s] * away[k]);
                    if (a < 1e-6) a += 2 * Math.PI;
                    if (a < turn) { turn = a; best = s; }
                }
                if (best >= 0) Join(faces[k], faces[best]);
            }
        }
        if (!split) return new[] { brep };
        var groups = Enumerable.Range(0, brep.Faces.Count).GroupBy(Find).ToList();
        if (groups.Count < 2) return new[] { brep };
        var shells = new List<Brep>();
        foreach (var group in groups)
        {
            var joined = Brep.JoinBreps(group.Select(i => brep.Faces[i].DuplicateFace(false)), Tol);
            if (joined == null || joined.Length != 1 || !joined[0].IsSolid) return new[] { brep };
            if (joined[0].SolidOrientation == BrepSolidOrientation.Inward) joined[0].Flip();
            shells.Add(joined[0]);
        }
        return shells.ToArray();
    }

    // Objects sharing any Rhino group fuse into one part, so nested and overlapping groups
    // collapse into a single cluster. Clusters keep the order of their first member.
    internal static List<(string Name, List<(SourcePart Source, string Name)> Members)> Grouped(
        IEnumerable<(SourcePart Source, string Name)> named)
    {
        var items = named.ToList();
        var parent = Enumerable.Range(0,items.Count).ToArray();
        int Find(int i) { while (parent[i]!=i) i = parent[i] = parent[parent[i]]; return i; }
        var firstWithGroup = new Dictionary<Guid,int>();
        for (int i=0;i<items.Count;i++)
        foreach (var group in items[i].Source.Groups ?? Array.Empty<SourceGroup>())
        {
            if (firstWithGroup.TryGetValue(group.Id,out int other)) parent[Find(i)] = Find(other);
            else firstWithGroup.Add(group.Id,i);
        }
        var clusters = new List<(string Name, List<(SourcePart Source, string Name)> Members)>();
        var byRoot = new Dictionary<int,int>();
        for (int i=0;i<items.Count;i++)
        {
            int root = Find(i);
            if (!byRoot.TryGetValue(root,out int index))
            {
                index = clusters.Count; byRoot.Add(root,index);
                clusters.Add(("",new List<(SourcePart Source, string Name)>()));
            }
            clusters[index].Members.Add(items[i]);
        }
        for (int c=0;c<clusters.Count;c++)
        {
            var members = clusters[c].Members;
            // The outermost named group names the part; otherwise its first member does.
            var groupName = members.Count == 1 ? null : members
                .SelectMany(m => m.Source.Groups ?? Array.Empty<SourceGroup>())
                .Where(g => !string.IsNullOrWhiteSpace(g.Name))
                .OrderByDescending(g => g.Size).Select(g => g.Name!.Trim()).FirstOrDefault();
            clusters[c] = (groupName ?? members[0].Name, members);
        }
        return clusters;
    }

    private static int Priority(string? name)
    {
        var n = name?.ToLowerInvariant() ?? "";
        if (n.Contains("ground floor") || n.Contains("upper floor")) return 0;
        if (n.Contains("wall")) return 1;
        return n.Contains("roof") || n.Contains("slab") || n.Contains("deck") ? 2 : 3;
    }

    private static Brep Solid(SourcePart obj, double factor)
    {
        var b = obj.Geometry switch
        {
            Brep brep => BrepHealing.Heal(brep, obj.ModelTolerance ?? Tol / factor,
                obj.AngleTolerance ?? RhinoMath.DefaultAngleTolerance, out _),
            Extrusion e => e.ToBrep(true),
            Mesh m when m.IsClosed => Brep.CreateFromMesh(m, true),
            _ => throw new ArgumentException(InputCheck.NotSolid(obj.Geometry, factor))
        };
        if (b == null && obj.Geometry is Brep panel &&
            obj.Name is ("Simplified - west wall" or "Simplified - north wall") &&
            obj.RepairBarnPanel)
        {
            b = RecoverPanel(panel,factor);
        }
        else if (b != null && !b.Transform(Transform.Scale(Point3d.Origin, factor)))
        {
            throw new ArgumentException("Component could not be scaled.");
        }
        if (b == null || !b.IsValid || !b.IsSolid) throw new ArgumentException(InputCheck.NotSolid(obj.Geometry, factor));
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

    internal static bool MeshesWatertight(Brep b)
    {
        try { using var mesh = ToMesh(b); return true; }
        catch (InvalidOperationException) { return false; }
    }

    internal static Mesh ToMesh(Brep b)
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

    // ToMesh welds every vertex, which averages normals across sharp plate edges and smears the shading.
    // Split vertices at creases for display; export re-welds before writing.
    internal static Mesh Shaded(Mesh mesh)
    {
        // Baked meshes are scaled to model units and placed far from the origin; single-precision
        // vertices there can merge thin triangles and open the mesh.
        mesh.Vertices.UseDoublePrecisionVertices = true;
        mesh.Unweld(RhinoMath.ToRadians(30),true);
        mesh.Normals.ComputeNormals();
        mesh.Compact();
        return mesh;
    }

    // Cuts off whatever lies below the bed plane z = 0, so the part rests on its whole base.
    // Returns null if the cut does not leave one clean solid.
    private static Brep? FlattenBase(Brep placed)
    {
        var bb = placed.GetBoundingBox(true);
        var below = new BoundingBox(bb.Min - new Vector3d(1,1,1), new Point3d(bb.Max.X+1,bb.Max.Y+1,0)).ToBrep();
        var cut = Brep.CreateBooleanDifference(placed, below, Tol);
        if (cut is not { Length: 1 } || !cut[0].IsValid || !cut[0].IsSolid) return null;
        var merged = cut[0].DuplicateBrep();
        if (merged.MergeCoplanarFaces(Tol, RhinoMath.ToRadians(.1)) && merged.IsSolid && MeshesWatertight(merged)) return merged;
        return MeshesWatertight(cut[0]) ? cut[0] : null;
    }

    private static IEnumerable<Part> Flatten(Brep body, string name, Guid source, double width, double depth, double seamClearance)
    {
        if (body.GetVolume() < .001) yield break;
        const double contactBand = .05; // Millimetres above the lowest point at print scale.
        // Modelled faces that should be coplanar are often stepped by a few tenths of a millimetre,
        // and trimming can leave slivers below them. A face still counts as a base when what lies
        // below it is at most this deep and small in volume; that material is cut off.
        const double flatBand = .5;
        const double maxCutShare = .02;
        double contactNormal = Math.Cos(2 * Math.PI / 180);
        double volume = body.GetVolume();
        var mesh = ToMesh(body);
        var candidates = new List<(Transform Frame,double Contact,bool Cut,double Overhang,double Footprint)>();
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
            if (bounds.Min.Z < -flatBand) continue;
            double contact = 0, overhang = 0, below = 0;
            foreach (var tri in mesh.Faces)
            {
                var a = transform * mesh.Vertices.Point3dAt(tri.A);
                var b = transform * mesh.Vertices.Point3dAt(tri.B);
                var c = transform * mesh.Vertices.Point3dAt(tri.C);
                var cross = Vector3d.CrossProduct(b-a,c-a);
                // Volume under the face's plane, from each triangle's projected area and depth.
                below -= cross.Z/2 * Math.Max(0, -(a.Z+b.Z+c.Z)/3);
                // Sum the projected area of all near-horizontal underside triangles at the bed.
                if (Math.Max(a.Z,Math.Max(b.Z,c.Z)) <= contactBand && cross.Z < -contactNormal * cross.Length)
                    contact -= cross.Z/2;
                else if (cross.Z < -.7071*cross.Length) overhang -= cross.Z/2;
            }
            if (contact <= 0 || below > maxCutShare*volume) continue;
            candidates.Add((transform, contact, bounds.Min.Z < -contactBand, overhang, bounds.Diagonal.X*bounds.Diagonal.Y));
        }
        if (candidates.Count == 0) throw new InvalidOperationException("No supporting planar face.");
        // A part is only cut when that gives it at least 10% more bed contact than any uncut base.
        Transform xform = Transform.Unset;
        Brep? flat = null;
        foreach (var candidate in candidates.OrderByDescending(c => Math.Round(c.Cut ? c.Contact/1.1 : c.Contact))
            .ThenBy(c => c.Cut).ThenBy(c => Math.Round(c.Overhang,1)).ThenBy(c => c.Footprint))
        {
            var placed = body.DuplicateBrep();
            placed.Transform(candidate.Frame);
            flat = candidate.Cut ? FlattenBase(placed) : placed;
            if (flat == null) continue;
            xform = candidate.Frame;
            break;
        }
        if (flat == null) throw new InvalidOperationException("No supporting planar face could be cut flat.");
        var box = flat.GetBoundingBox(true);
        int Tiles(double w,double h) => (int)Math.Ceiling((w-Tol)/width)*(int)Math.Ceiling((h-Tol)/depth);
        if (Tiles(box.Diagonal.Y,box.Diagonal.X) < Tiles(box.Diagonal.X,box.Diagonal.Y))
        {
            var turn = Transform.Rotation(Math.PI/2,Vector3d.ZAxis,Point3d.Origin);
            xform = turn*xform; flat.Transform(turn); box = flat.GetBoundingBox(true);
        }
        xform.TryGetInverse(out var inverse);
        // A keyed piece overhangs its cell by the key length, so cells of split parts are shorter.
        int Count(double size,double limit) => size-Tol<=limit ? 1 :
            (int)Math.Ceiling((size-Tol)/Math.Max(limit-SeamKeys.Length,limit/2));
        int nx = Count(box.Diagonal.X,width), ny = Count(box.Diagonal.Y,depth);
        if (Math.Min(box.Diagonal.Z, Math.Min(box.Diagonal.X,box.Diagonal.Y)) < .4)
        { RhinoApp.WriteLine($"Omitted sub-0.4 mm sliver of {name}"); yield break; }
        var pieces = new List<Brep>();
        if (nx*ny==1) pieces.Add(flat);
        else if (SeamKeys.Split(flat,box,nx,ny,seamClearance,out int keys) is { } keyed)
        {
            pieces = keyed;
            RhinoApp.WriteLine($"Split {name} into {keyed.Count} pieces with {keys} seam keys.");
        }
        else
        {
            for (int i=0;i<nx;i++)
            for (int j=0;j<ny;j++)
            {
                var low = box.PointAt((double)i/nx,(double)j/ny,0);
                var high = box.PointAt((i+1.0)/nx,(j+1.0)/ny,1);
                var clip = new BoundingBox(low-new Vector3d(i==0?1:0,j==0?1:0,1),
                    high+new Vector3d(i==nx-1?1:0,j==ny-1?1:0,1));
                var cut = Brep.CreateBooleanIntersection(flat,clip.ToBrep(),Tol);
                if (cut==null) throw new InvalidOperationException("Bed-size split failed.");
                pieces.AddRange(cut.Where(p => p.GetVolume()>.001));
            }
            RhinoApp.WriteLine($"Split {name} into {pieces.Count} pieces with plain seams. Keys did not fit.");
        }
        for (int index=0;index<pieces.Count;index++)
        {
            var printable = ToMesh(pieces[index]);
            var assembled = printable.DuplicateMesh(); assembled.Transform(inverse);
            printable.Translate(Point3d.Origin-printable.GetBoundingBox(true).Min);
            // Pieces of one part share its name and take a letter each.
            string label = pieces.Count==1 ? name : index<26 ? $"{name} {(char)('a'+index)}" : $"{name} {index+1}";
            yield return new Part(label,source,assembled,printable);
        }
    }
}
