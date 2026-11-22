using System.Text;
using Rhino.Geometry;

namespace Go2Bed;

internal record ExportPiece(string Name, Mesh Mesh);
internal record ExportPlate(string RelativeName, double Width, double Depth, IEnumerable<ExportPiece> Pieces,
    Point3d Origin, double MillimetersPerUnit);

internal static class PrintFiles
{
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
                    mesh.Faces.ConvertQuadsToTriangles();
                    if (!mesh.IsValid || !mesh.IsClosed || mesh.SolidOrientation()==0) throw new ArgumentException(obj.Name+" must be a closed oriented solid.");
                    if (mesh.SolidOrientation()<0) mesh.Flip(true,true,true);
                    var bounds = mesh.GetBoundingBox(true);
                    if (Math.Abs(bounds.Min.Z)>.02 || bounds.Min.X<-.02 || bounds.Min.Y<-.02 ||
                        bounds.Max.X>plate.Width+.02 || bounds.Max.Y>plate.Depth+.02)
                        throw new ArgumentException(obj.Name+" is off the bed on "+plate.RelativeName);
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
