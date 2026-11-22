using System.IO.Compression;
using System.Diagnostics;
using System.Xml.Linq;
using Rhino.Geometry;

namespace Go2Bed;

internal sealed class PrusaProject
{
    private static readonly XNamespace Core = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private static readonly XNamespace Prusa = "http://schemas.slic3r.org/3mf/2017/06";
    private readonly XElement resources = new(Core+"resources");
    private readonly XElement build = new(Core+"build");
    private readonly XElement config = new("config");
    private int nextId;

    public void Add(string name, Mesh mesh)
    {
        var bounds = mesh.GetBoundingBox(true);
        if (bounds.Max.X > 250.02 || bounds.Max.Y > 210.02 || bounds.Max.Z > 220.02)
            throw new ArgumentException(name+" exceeds the MK4S build volume.");
        int id = ++nextId;
        var vertices = new XElement(Core+"vertices", mesh.Vertices.Select(v =>
            new XElement(Core+"vertex", new XAttribute("x",v.X), new XAttribute("y",v.Y), new XAttribute("z",v.Z))));
        var triangles = new XElement(Core+"triangles", mesh.Faces.Select(f =>
            new XElement(Core+"triangle", new XAttribute("v1",f.A), new XAttribute("v2",f.B), new XAttribute("v3",f.C))));
        resources.Add(new XElement(Core+"object",new XAttribute("id",id),new XAttribute("type","model"),
            new XAttribute("name",name),new XElement(Core+"mesh",vertices,triangles)));
        build.Add(new XElement(Core+"item",new XAttribute("objectid",id)));
        XElement Name(string type) => new("metadata",new XAttribute("type",type),new XAttribute("key","name"),new XAttribute("value",name));
        config.Add(new XElement("object",new XAttribute("id",id),new XAttribute("instances_count",1),Name("object"),
            new XElement("volume",new XAttribute("firstid",0),new XAttribute("lastid",mesh.Faces.Count-1),Name("volume"))));
    }

    public static void Slice(string project)
    {
        const string binary = "PrusaSlicer.app/Contents/MacOS/PrusaSlicer";
        var locations = new[] { Path.Combine("/Applications",binary),
            Path.Combine("/Applications/Original Prusa Drivers",binary),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Applications",binary) };
        string executable = locations.FirstOrDefault(File.Exists) ??
            throw new FileNotFoundException("Install PrusaSlicer in Applications to export printer-ready BGCode.");
        string output = Path.ChangeExtension(project,".bgcode");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] {"--dont-arrange","--binary-gcode","--export-gcode","--output",output,project})
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Could not launch PrusaSlicer.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string log = stdout.GetAwaiter().GetResult()+"\n"+stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length < 10)
            throw new IOException($"PrusaSlicer failed on {Path.GetFileName(project)}. {log.Trim()}");
        if (log.Contains("warning",StringComparison.OrdinalIgnoreCase)) Rhino.RhinoApp.WriteLine(log.Trim());
    }

    public void Write(string path)
    {
        using var file = File.Open(path,FileMode.CreateNew);
        using var zip = new ZipArchive(file,ZipArchiveMode.Create);
        void Xml(string entry, XElement root)
        {
            using var stream = zip.CreateEntry(entry).Open();
            root.Save(stream,SaveOptions.DisableFormatting);
        }
        XNamespace types = "http://schemas.openxmlformats.org/package/2006/content-types";
        XElement Type(string ext,string mime) => new(types+"Default",new XAttribute("Extension",ext),new XAttribute("ContentType",mime));
        Xml("[Content_Types].xml",new XElement(types+"Types",
            Type("rels","application/vnd.openxmlformats-package.relationships+xml"),
            Type("model","application/vnd.ms-package.3dmanufacturing-3dmodel+xml"),
            Type("config","application/octet-stream")));
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        Xml("_rels/.rels",new XElement(rel+"Relationships",new XElement(rel+"Relationship",
            new XAttribute("Id","r1"),new XAttribute("Target","/3D/3dmodel.model"),
            new XAttribute("Type","http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"))));
        Xml("3D/3dmodel.model",new XElement(Core+"model",new XAttribute("unit","millimeter"),
            new XAttribute(XNamespace.Xmlns+"slic3rpe",Prusa.NamespaceName),
            new XElement(Core+"metadata",new XAttribute("name","slic3rpe:Version3mf"),"1"),resources,build));
        Xml("Metadata/Slic3r_PE_model.config",config);
        using var profile = typeof(PrusaProject).Assembly.GetManifestResourceStream("Go2Bed.MK4S.config")
            ?? throw new InvalidOperationException("Missing embedded MK4S profile.");
        using var output = zip.CreateEntry("Metadata/Slic3r_PE.config").Open();
        profile.CopyTo(output);
    }
}
