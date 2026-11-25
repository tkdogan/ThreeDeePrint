using System.Runtime.InteropServices;
using Rhino.Geometry;

namespace Go2Bed;

internal static class SolidCleanup
{
    private const string Library = "Go2BedSolid";
    [DllImport(Library)] private static extern IntPtr td_clean(int count,int[] nv,int[] nf,double[] points,int[] triangles,out int failed);
    [DllImport(Library)] private static extern int td_count(IntPtr result,int component);
    [DllImport(Library)] private static extern void td_mesh(IntPtr result,int component,int piece,out int nv,out int nf,out IntPtr points,out IntPtr triangles);
    [DllImport(Library)] private static extern void td_release(IntPtr result);

    internal static List<Component> Cut(List<Component> source)
    {
        var counts = new int[source.Count];
        var faceCounts = new int[source.Count];
        var points = new List<double>();
        var triangles = new List<int>();
        for (int i=0;i<source.Count;i++)
        {
            using var mesh=Parts.ToMesh(source[i].Solid);
            counts[i]=mesh.Vertices.Count;
            faceCounts[i]=mesh.Faces.Count;
            foreach (var p in mesh.Vertices) { points.Add(p.X); points.Add(p.Y); points.Add(p.Z); }
            foreach (var f in mesh.Faces) { triangles.Add(f.A); triangles.Add(f.B); triangles.Add(f.C); }
        }
        var result=td_clean(source.Count,counts,faceCounts,points.ToArray(),triangles.ToArray(),out int failed);
        if (result==IntPtr.Zero) throw new InvalidOperationException($"Boolean cleanup failed on {source[failed].Name}.");
        try
        {
            var cleaned=new List<Component>();
            for (int i=0;i<source.Count;i++)
            for (int j=0;j<td_count(result,i);j++)
            {
                td_mesh(result,i,j,out int nv,out int nf,out var vertices,out var indices);
                var xyz=new double[nv*3]; Marshal.Copy(vertices,xyz,0,xyz.Length);
                var faces=new long[nf*3]; Marshal.Copy(indices,faces,0,faces.Length);
                // Rounding keeps coplanar faces mergeable. It can also flatten a sliver, so fall back to exact coordinates.
                using var mesh=Rebuild(xyz,faces,true) ?? Rebuild(xyz,faces,false) ??
                    throw new InvalidOperationException($"Boolean cleanup left {source[i].Name} with an open mesh ({nf} faces).");
                var solid=Brep.CreateFromMesh(mesh,true);
                var merged=solid.DuplicateBrep();
                if (merged.MergeCoplanarFaces(.0002,.00001) && merged.IsValid && merged.IsSolid &&
                    Math.Abs(merged.GetVolume()-solid.GetVolume())<.001) solid=merged;
                cleaned.Add(source[i] with {Solid=solid});
            }
            return cleaned;
        }
        finally { td_release(result); }
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
