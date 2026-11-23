using System.Drawing;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Go2Bed;

public static class Kit
{
    public static Layer Bake(RhinoDoc doc, RhinoObject[] objects, double scale, double width, double depth, double height, bool barn)
        => BakeWithJoints(doc, objects, scale, width, depth, height, barn, PreviewSettings.Defaults.Fingers);

    public static Layer BakeWithJoints(RhinoDoc doc, RhinoObject[] objects, double scale, double width, double depth, double height, bool barn, bool fingers)
        => BakeSettings(doc,objects,new PreviewSettings(scale,width,depth,height,fingers,.20),barn);

    internal static Layer BakeSettings(RhinoDoc doc, RhinoObject[] objects, PreviewSettings settings, bool barn = false)
    {
        var (scale,width,depth,height,fingers,clearance,useSourceColors) = settings;
        var sources = objects.Select(o => SourcePart.FromObject(o,barn));
        var layout = PrintLayout.Solve(sources,doc.ModelUnitSystem,scale,width,depth,height,fingers,clearance);
        var bounds = BoundingBox.Empty;
        foreach (var obj in objects) bounds.Union(obj.Geometry.GetBoundingBox(true));
        return PrintLayout.Bake(doc,layout,width,depth,bounds,useSourceColors);
    }

    public static string Export(RhinoDoc doc, Guid kitId, string parent)
        => ExportStls(doc,kitId,parent,false);

    public static string ExportStls(RhinoDoc doc, Guid kitId, string parent, bool groupByPlate)
    {
        var root = doc.Layers.FindId(kitId) ?? throw new ArgumentException("Kit layer no longer exists.");
        var parts = doc.Layers.Where(l => !l.IsDeleted && l.ParentLayerId == root.Id)
            .SelectMany(l => doc.Objects.FindByLayer(l) ?? Array.Empty<RhinoObject>())
            .Where(o => IsPrintPart(o) && o.Attributes.GetUserString("FlatPack.Kit") == kitId.ToString());
        return ExportSelection(doc, parts, parent, groupByPlate);
    }

    internal static bool IsPrintPart(RhinoObject obj) => obj.Geometry is Mesh &&
        obj.Attributes.GetUserString("FlatPack.Kind") == "Print" &&
        Guid.TryParse(obj.Attributes.GetUserString("FlatPack.Kit"), out _);

    public static string ExportSelection(RhinoDoc doc, IEnumerable<RhinoObject> selection, string parent, bool groupByPlate)
        => ExportFiles(doc,selection,parent,groupByPlate,false);

    public static string ExportPrusa(RhinoDoc doc, IEnumerable<RhinoObject> selection, string parent, bool slice = false)
        => ExportFiles(doc,selection,parent,false,true,slice);

    private static string ExportFiles(RhinoDoc doc, IEnumerable<RhinoObject> selection, string parent, bool groupByPlate, bool prusa, bool slice = false)
    {
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        var parts = selection.Where(IsPrintPart).DistinctBy(o => o.Id).ToArray();
        if (parts.Length == 0) throw new ArgumentException("No baked print meshes selected.");
        double mm = RhinoMath.UnitScale(doc.ModelUnitSystem,UnitSystem.Millimeters);
        var beds = doc.Objects.GetObjectList(ObjectType.Curve)
            .Where(o => o.Attributes.GetUserString("FlatPack.Kind") == "Bed")
            .Select(o => (Object: o, Layer: doc.Layers[o.Attributes.LayerIndex]))
            .Where(b => !b.Layer.IsDeleted &&
                Guid.TryParse(b.Object.Attributes.GetUserString("FlatPack.Kit"), out var kit) &&
                kit == b.Layer.ParentLayerId)
            .Select(b => new PrintBed(b.Layer, b.Object.Geometry.GetBoundingBox(true)))
            .ToArray();

        const double correctionLimitMm = .25;
        double limit = correctionLimitMm/mm;
        var placements = parts.Select(part =>
        {
            var bounds = part.Geometry.GetBoundingBox(true);
            var matches = beds.Select(bed => (Bed: bed, Offset: BedOffset(bounds,bed.Bounds)))
                .Where(match => match.Offset.IsValid && match.Offset.Length <= limit)
                .OrderBy(match => match.Offset.Length)
                .ToArray();
            if (matches.Length > 1)
            {
                // Keep the current assignment if its bed already contains the piece.
                var current = matches.FirstOrDefault(m => m.Bed.Layer.Index == part.Attributes.LayerIndex &&
                    m.Offset.X == 0 && m.Offset.Y == 0);
                if (current.Bed != null) return (Part: part, current.Bed, current.Offset);
                if (matches[1].Offset.Length-matches[0].Offset.Length < .001/mm)
                    throw new ArgumentException($"{part.Name} matches multiple beds. Move it fully onto one bed and retry.");
            }
            if (matches.Length > 0) return (Part: part, matches[0].Bed, matches[0].Offset);

            // Let the export validator report the measured deviation on the original bed.
            var original = beds.SingleOrDefault(bed => bed.Layer.Index == part.Attributes.LayerIndex)
                ?? throw new ArgumentException($"{part.Name} is outside the correction range of every print bed.");
            return (Part: part, Bed: original, Offset: Vector3d.Zero);
        }).ToArray();

        bool multipleKits = placements.Select(p => p.Bed.Layer.ParentLayerId).Distinct().Count() > 1;
        var exportMeshes = new List<Mesh>();
        var layerChanges = new List<(Guid Id, ObjectAttributes Original)>();
        try
        {
            var plates = placements.GroupBy(p => p.Bed).OrderBy(g => g.Key.Layer.Index).Select(group =>
            {
                var (plate, box) = group.Key;
                string directory = multipleKits ? $"Kit-{doc.Layers.FindId(plate.ParentLayerId).Index}" : "";
                string plateName = plate.Name.StartsWith("Plate ", StringComparison.Ordinal) &&
                    int.TryParse(plate.Name.Substring(6), out int number) && number > 0
                    ? $"Plate-{number:00}" : $"Layer-{plate.Index}";
                var pieces = group.OrderBy(p => p.Part.Name).ThenBy(p => p.Part.Id).Select(p =>
                {
                    var mesh = (Mesh)p.Part.Geometry;
                    if (p.Offset.Length > RhinoMath.ZeroTolerance)
                    {
                        mesh = mesh.DuplicateMesh();
                        exportMeshes.Add(mesh);
                        mesh.Vertices.UseDoublePrecisionVertices = true;
                        mesh.Translate(p.Offset);
                    }
                    return new ExportPiece(p.Part.Name ?? "Part", mesh);
                }).ToArray();
                return new ExportPlate(Path.Combine(directory,plateName), box.Diagonal.X*mm, box.Diagonal.Y*mm,
                    pieces, box.Min, mm);
            }).ToArray();

            foreach (var (part, bed, _) in placements)
            {
                if (part.Attributes.LayerIndex == bed.Layer.Index &&
                    Guid.Parse(part.Attributes.GetUserString("FlatPack.Kit")) == bed.Layer.ParentLayerId) continue;
                var original = part.Attributes.Duplicate();
                var updated = original.Duplicate();
                updated.LayerIndex = bed.Layer.Index;
                updated.SetUserString("FlatPack.Kit",bed.Layer.ParentLayerId.ToString());
                if (!doc.Objects.ModifyAttributes(part.Id,updated,true))
                    throw new InvalidOperationException($"Could not move {part.Name} to {bed.Layer.FullPath}.");
                layerChanges.Add((part.Id,original));
            }

            var destination = PrintFiles.Write(plates,parent,groupByPlate,prusa,slice);
            int aligned = placements.Count(p => p.Offset.Length > .001/mm);
            RhinoApp.WriteLine($"Exported {parts.Length} printable pieces on {plates.Length} plates. " +
                $"Aligned {aligned} for export and updated {layerChanges.Count} plate assignments.");
            if (layerChanges.Count > 0) doc.Views.Redraw();
            return destination;
        }
        catch
        {
            foreach (var (id, original) in layerChanges)
                if (!doc.Objects.ModifyAttributes(id,original,true))
                    RhinoApp.WriteLine($"Could not restore the original plate assignment for {id}.");
            throw;
        }
        finally
        {
            foreach (var mesh in exportMeshes) mesh.Dispose();
            foreach (var (_, original) in layerChanges) original.Dispose();
        }
    }

    private sealed record PrintBed(Layer Layer, BoundingBox Bounds);

    private static Vector3d BedOffset(BoundingBox piece, BoundingBox bed)
    {
        if (piece.Diagonal.X > bed.Diagonal.X || piece.Diagonal.Y > bed.Diagonal.Y)
            return Vector3d.Unset;

        return new Vector3d(
            Math.Clamp(0,bed.Min.X-piece.Min.X,bed.Max.X-piece.Max.X),
            Math.Clamp(0,bed.Min.Y-piece.Min.Y,bed.Max.Y-piece.Max.Y),
            bed.Min.Z-piece.Min.Z);
    }
}
