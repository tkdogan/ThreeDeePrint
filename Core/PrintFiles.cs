using System.Text;
using Rhino.Geometry;

namespace Go2Bed;

internal record ExportPiece(string Name, Mesh Mesh);
internal record ExportPlate(string RelativeName, double Width, double Depth, IEnumerable<ExportPiece> Pieces,
    Point3d Origin, double MillimetersPerUnit);

internal static class PrintFiles
{
    private const double GapTolerance = .005; // Millimetres; hairline gaps closed before export.
    private const double MaxHoleLength = 2;  // Millimetres of naked edge per hole that may be filled.

    // Welds a baked mesh back into one closed solid. Meshes baked far from the origin in single
    // precision can open hairline gaps; those are closed here. Anything larger is reported.
    private static void Close(Mesh mesh, string name)
    {
        mesh.Faces.ConvertQuadsToTriangles();
        mesh.Vertices.CombineIdentical(true,true); // Baked meshes are unwelded at creases for shading.
        mesh.Compact();
        if (IsSolid(mesh)) return;
        mesh.Vertices.Align(GapTolerance);
        mesh.Vertices.CombineIdentical(true,true);
        mesh.Faces.CullDegenerateFaces();
        mesh.Vertices.CullUnused();
        mesh.Compact();
        var holes = mesh.GetNakedEdges() ?? Array.Empty<Polyline>();
        if (holes.Length > 0 && holes.All(h => h.Length <= MaxHoleLength)) mesh.FillHoles();
        mesh.UnifyNormals();
        mesh.Compact();
        if (IsSolid(mesh)) return;
        holes = mesh.GetNakedEdges() ?? Array.Empty<Polyline>();
        int nonManifold = Enumerable.Range(0,mesh.TopologyEdges.Count).Count(i => mesh.TopologyEdges.GetConnectedFaces(i).Length > 2);
        string why = holes.Length > 0
            ? $"it has {holes.Length} open hole{(holes.Length==1?"":"s")}, the largest with {holes.Max(h => h.Length):0.##} mm of open edge"
            : nonManifold > 0 ? $"{nonManifold} of its edges are shared by more than two faces"
            : !mesh.IsValid ? "Rhino reports it as an invalid mesh" : "its faces are not oriented consistently";
        throw new ArgumentException($"{name} is not a closed solid mesh: {why}. " +
            "Bake the kit again with BedLayout, or run ShowEdges on the piece to see the gaps.");
    }

    private static bool IsSolid(Mesh mesh) => mesh.IsValid && mesh.IsClosed && mesh.SolidOrientation() != 0;

    internal static string Write(IEnumerable<ExportPlate> plates,string parent,bool groupByPlate,bool prusa,bool slice)
    {
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        string staging = Path.Combine(parent, ".flatpack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            int count = 0;
            foreach (var plate in plates)
            {
                string folder = Path.Combine(staging,plate.RelativeName);
                Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
                using var grouped = groupByPlate ? new Mesh() : null;
                var project = prusa ? new PrusaProject() : null;
                if (!groupByPlate && !prusa) Directory.CreateDirectory(folder);
                foreach (var obj in plate.Pieces)
                {
                    var mesh = obj.Mesh.DuplicateMesh();
                    mesh.Translate(Point3d.Origin-plate.Origin);
                    mesh.Scale(plate.MillimetersPerUnit);
                    Close(mesh,obj.Name);
                    if (mesh.SolidOrientation()<0) mesh.Flip(true,true,true);
                    var bounds = mesh.GetBoundingBox(true);
                    const double bedTolerance = .02;
                    if (Math.Abs(bounds.Min.Z)>bedTolerance)
                    {
                        string direction = bounds.Min.Z < 0 ? "below" : "above";
                        throw new ArgumentException(
                            $"{obj.Name} is {Math.Abs(bounds.Min.Z):0.###} mm {direction} bed height on {plate.RelativeName}.");
                    }
                    double overflowX = Math.Max(-bounds.Min.X,bounds.Max.X-plate.Width);
                    double overflowY = Math.Max(-bounds.Min.Y,bounds.Max.Y-plate.Depth);
                    if (Math.Max(overflowX,overflowY)>bedTolerance)
                        throw new ArgumentException(
                            $"{obj.Name} extends {Math.Max(overflowX,overflowY):0.###} mm past the bed outline on {plate.RelativeName}.");
                    var label = new string(obj.Name.Select(c=>char.IsLetterOrDigit(c)||c=='-'?c:'_').ToArray());
                    count++;
                    if (project != null) project.Add(obj.Name,mesh);
                    else if (grouped != null) grouped.Append(mesh);
                    else WriteStl(Path.Combine(folder,$"{count:00}-{label}.stl"),mesh);
                }
                if (project != null)
                {
                    string projectPath = folder+".3mf";
                    project.Write(projectPath);
                    if (slice) PrusaProject.Slice(projectPath);
                }
                if (grouped != null) WriteStl(folder+".stl",grouped);
            }
            string baseName = prusa ? "FlatPack-Prusa" : "FlatPack-STL";
            string dest = Path.Combine(parent,baseName);
            for (int i=2;Directory.Exists(dest)||File.Exists(dest);i++) dest=Path.Combine(parent,$"{baseName}-{i}");
            Directory.Move(staging,dest);
            return dest;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging,true); }
    }

    private static void WriteStl(string path,Mesh mesh)
    {
        using var writer = new BinaryWriter(File.Open(path,FileMode.CreateNew),Encoding.ASCII);
        writer.Write(new byte[80]); writer.Write((uint)mesh.Faces.Count);
        foreach (var f in mesh.Faces)
        {
            var a = mesh.Vertices[f.A]; var b = mesh.Vertices[f.B]; var c = mesh.Vertices[f.C];
            var n = Vector3d.CrossProduct((Vector3d)(b-a),(Vector3d)(c-a));
            if (!n.Unitize()) throw new ArgumentException("Degenerate STL triangle in "+Path.GetFileName(path));
            writer.Write((float)n.X); writer.Write((float)n.Y); writer.Write((float)n.Z);
            foreach (var p in new[]{a,b,c}) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
            writer.Write((ushort)0);
        }
    }
}
