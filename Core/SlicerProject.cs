using System.IO.Compression;
using System.Xml.Linq;
using Rhino.Geometry;

namespace Go2Bed;

// One 3MF project per plate, with every piece as a named object at its place on the bed. Slicing
// is left to the slicer: PrusaSlicer, Bambu Studio, OrcaSlicer, Cura and others all open it.
// PrusaSlicer also reads the object names from Slic3r_PE_model.config, and for printers with a
// bundled profile the file carries that printer's preset so it opens ready to slice.
internal sealed class SlicerProject
{
    private static readonly XNamespace Core = "http://schemas.microsoft.com/3dmanufacturing/core/2015/02";
    private static readonly XNamespace Prusa = "http://schemas.slic3r.org/3mf/2017/06";
    private readonly XElement resources = new(Core+"resources");
    private readonly XElement build = new(Core+"build");
    private readonly XElement config = new("config");
    private readonly string title;
    private readonly string? profile;
    private int nextId;

    // Printers whose slicer preset ships with Go2Bed, by library name.
    private static readonly Dictionary<string,string> Profiles = new() { ["Prusa MK4S"] = "Go2Bed.MK4S.config" };

    internal SlicerProject(string title, string? printer)
    {
        this.title = title;
        profile = printer != null && Profiles.TryGetValue(printer,out var resource) ? resource : null;
    }

    internal static bool HasProfile(string? printer) => printer != null && Profiles.ContainsKey(printer);

    public void Add(string name, Mesh mesh)
    {
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
        XElement Meta(string name, string value) => new(Core+"metadata",new XAttribute("name",name),value);
        Xml("3D/3dmodel.model",new XElement(Core+"model",new XAttribute("unit","millimeter"),
            new XAttribute(XNamespace.Xmlns+"slic3rpe",Prusa.NamespaceName),
            Meta("slic3rpe:Version3mf","1"),Meta("Title",title),Meta("Application","Go2Bed"),resources,build));
        Xml("Metadata/Slic3r_PE_model.config",config);
        if (profile == null) return;
        using var preset = typeof(SlicerProject).Assembly.GetManifestResourceStream(profile)
            ?? throw new InvalidOperationException("Missing embedded printer profile "+profile+".");
        using var output = zip.CreateEntry("Metadata/Slic3r_PE.config").Open();
        preset.CopyTo(output);
    }
}
