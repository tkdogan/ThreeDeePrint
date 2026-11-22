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
                using var mesh=new Mesh();
                mesh.Vertices.UseDoublePrecisionVertices=true;
                for (int v=0;v<xyz.Length;v+=3) mesh.Vertices.Add(Math.Round(xyz[v],4),Math.Round(xyz[v+1],4),Math.Round(xyz[v+2],4));
                for (int f=0;f<faces.Length;f+=3) mesh.Faces.AddFace((int)faces[f],(int)faces[f+1],(int)faces[f+2]);
                mesh.Vertices.CombineIdentical(true,true);
                mesh.Faces.CullDegenerateFaces();
                if (!mesh.IsValidWithLog(out var detail) || !mesh.IsClosed) throw new InvalidOperationException($"Invalid boolean mesh for {source[i].Name}: {detail} (closed={mesh.IsClosed}, faces={nf}).");
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
}
