#! python 3
# requirements: rhino3dm>=8.35, manifold3d>=3.5, numpy
"""Build a millimetre-scale glue kit from component layers in a Rhino file."""
import argparse
import math
from pathlib import Path
import re
import struct
import tempfile
import xml.etree.ElementTree as ET
from zipfile import ZipFile, ZIP_DEFLATED

import manifold3d as md
import numpy as np
import rhino3dm as rh


def xyz(p):
    return np.array([p.X, p.Y, p.Z])


def unit(v):
    return v / np.linalg.norm(v)


def basis(normal):
    x = unit(np.cross(np.eye(3)[np.argmin(abs(normal))], normal))
    return np.array([x, np.cross(normal, x), normal])


def affine(matrix, offset=(0, 0, 0)):
    return np.column_stack((matrix, offset))


def extrusion(g, scale):
    if not g.IsSolid:
        raise ValueError("Extrusion needs closed ends")
    rings = []
    for i in range(g.ProfileCount):
        profile = g.Profile3d(i, 0.0).TryGetPolyline()
        if profile is None:
            raise ValueError("Mesh curved extrusions before conversion")
        rings.append(np.array([xyz(p) * scale for p in profile][:-1]))
    origin = rings[0][0]
    ring = rings[0] - origin
    normal = unit(np.cross(ring, np.roll(ring, -1, axis=0)).sum(axis=0))
    delta = xyz(g.Profile3d(0, 1.0).PointAtStart) * scale - origin
    if normal @ delta < 0:
        normal *= -1
    axes = basis(normal)
    end = g.Profile3d(0, 1.0).TryGetPolyline()
    if end is None or not np.allclose(
        [xyz(p) * scale for p in end][:-1], rings[0] + delta
    ):
        raise ValueError("Mesh extrusions with nonparallel end cuts first")
    section = md.CrossSection(
        [(r - origin) @ axes[:2].T for r in rings], md.FillRule.EvenOdd
    )
    return section.extrude(1).transform(
        affine(np.column_stack((*axes[:2], delta)), origin)
    )


def triangles(mesh):
    pts = np.array([xyz(p) for p in mesh.Vertices])
    for a, b, c, d in mesh.Faces:
        yield pts[[a, b, c]]
        if c != d:
            yield pts[[a, c, d]]


def mesh_solid(tris):
    vertices, indices = np.unique(
        np.round(tris.reshape(-1, 3), 4), axis=0, return_inverse=True
    )
    faces = indices.reshape(-1, 3).astype(np.uint64)
    keep = (
        (faces[:, 0] != faces[:, 1])
        & (faces[:, 0] != faces[:, 2])
        & (faces[:, 1] != faces[:, 2])
    )
    faces = faces[keep]
    obj = md.Manifold(md.Mesh64(vertices, faces))
    if obj.status() == md.Error.NoError and obj.volume() < 0:
        obj = md.Manifold(md.Mesh64(vertices, faces[:, ::-1].copy()))
    return obj


def component(g, scale, repair):
    if isinstance(g, rh.Extrusion):
        return extrusion(g, scale)
    if isinstance(g, rh.Mesh):
        meshes = [g]
    elif isinstance(g, rh.Brep):
        meshes = [f.GetMesh(rh.MeshType.Render) for f in g.Faces]
        if any(m is None for m in meshes):
            raise ValueError(
                "Save shaded render meshes or convert the part to a mesh in Rhino"
            )
    else:
        raise ValueError("Explode blocks and select component solids")
    tris = np.array([tri for mesh in meshes for tri in triangles(mesh)]) * scale
    obj = mesh_solid(tris)
    if obj.status() == md.Error.NoError and obj.volume() > 0:
        return obj
    if not repair:
        raise ValueError("Repair the open component before generating the kit")
    # Barn preset: the two open exterior walls are axis-aligned, constant-depth panels.
    pts = tris.reshape(-1, 3)
    axis = np.argmin(np.ptp(pts, axis=0))
    axes = basis(np.eye(3)[axis])
    local = tris @ axes.T
    footprints = [md.CrossSection([t[:, :2]], md.FillRule.EvenOdd) for t in local]
    profile = md.CrossSection.batch_boolean(footprints, md.OpType.Add).simplify(0.001)
    low, high = local[:, :, 2].min(), local[:, :, 2].max()
    print(
        "  Reconstructed through-opening silhouette, thickness %.2f mm" % (high - low)
    )
    return profile.extrude(high - low).translate((0, 0, low)).transform(affine(axes.T))


def arrays(solid):
    mesh = solid.to_mesh64()
    return mesh.vert_properties[:, :3], mesh.tri_verts


def orient(solid):
    vertices, faces = arrays(solid)
    tri = vertices[faces]
    cross = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
    area = np.linalg.norm(cross, axis=1) / 2
    valid = area > 1e-8
    normals = np.unique(np.round(cross[valid] / (2 * area[valid, None]), 6), axis=0)
    options = []
    for n in normals:
        axes = basis(-unit(n))
        local = vertices @ axes.T
        z = local[:, 2]
        contact = area[(z[faces].max(axis=1) - z.min()) < 0.005].sum()
        if contact <= 0:
            continue
        # Align a long contact edge to X for compact rectangular packing.
        base = local[faces[(z[faces].max(axis=1) - z.min()) < 0.005], :2]
        edges = (np.roll(base, 1, axis=1) - base).reshape(-1, 2)
        angles = np.unique(
            np.round(np.arctan2(edges[:, 1], edges[:, 0]) % (math.pi / 2), 5)
        )
        for angle in angles:
            c, s = math.cos(angle), math.sin(angle)
            frame = np.array([[c, s, 0], [-s, c, 0], [0, 0, 1]]) @ axes
            pts = vertices @ frame.T
            w, h, d = np.ptp(pts, axis=0)
            downward = cross @ n
            unsupported = (downward > 1.4142 * area) & (
                z[faces].max(axis=1) - z.min() > 0.005
            )
            overhang = downward[unsupported].sum() / 2
            score = (
                -round(overhang, 1),
                round(contact, 2),
                -round(d, 2),
                -round(w * h, 2),
            )
            options.append((score, frame, pts.min(axis=0), contact))
    if not options:
        raise ValueError("No planar bed contact")
    threshold = max(option[3] for option in options) * 0.65
    best = max(
        (option for option in options if option[3] >= threshold),
        key=lambda item: item[0],
    )
    return best[1], best[2]


def bounds(solid):
    return np.array(solid.bounding_box()).reshape(2, 3)


def priority(item):
    name = item[0].lower()
    if "ground floor" in name or "upper floor" in name:
        return 0
    if "wall" in name:
        return 1
    if "roof" in name or "slab" in name or "deck" in name:
        return 2
    return 3


def tiles(flat, width, depth):
    low, high = bounds(flat)
    size = high - low
    counts = np.maximum(1, np.ceil((size[:2] - 1e-6) / [width, depth]).astype(int))
    for i in range(counts[0]):
        for j in range(counts[1]):
            piece = flat
            for axis, index in enumerate((i, j)):
                n = np.eye(3)[axis]
                step = size[axis] / counts[axis]
                if index:
                    piece = piece.trim_by_plane(n, low[axis] + index * step)
                if index + 1 < counts[axis]:
                    piece = piece.trim_by_plane(-n, -(low[axis] + (index + 1) * step))
            for body in piece.decompose():
                if body.volume() > 1e-6:
                    yield body


def add_mesh(doc, solid, layer, name, source):
    vertices, faces = arrays(solid)
    mesh = rh.Mesh()
    for v in vertices:
        mesh.Vertices.Add(*v)
    for tri in faces:
        mesh.Faces.AddFace(*map(int, tri))
    mesh.Normals.ComputeNormals()
    if not mesh.IsValid or not mesh.IsClosed:
        raise ValueError(
            name
            + " failed Rhino mesh validation "
            + str((mesh.IsValidWithLog, mesh.IsClosed, solid.volume(), bounds(solid)))
        )
    attr = rh.ObjectAttributes()
    attr.Name, attr.LayerIndex = name, layer
    attr.SetUserString("Source object", str(source))
    doc.Objects.AddMesh(mesh, attr)


def write_stl(path, solid):
    vertices, faces = arrays(solid)
    with path.open("wb") as f:
        f.write(bytes(80) + struct.pack("<I", len(faces)))
        for a, b, c in vertices[faces]:
            n = np.cross(b - a, c - a)
            f.write(struct.pack("<12fH", *unit(n), *a, *b, *c, 0))


def write_plate(path, objects):
    model = ET.Element(
        "model",
        unit="millimeter",
        xmlns="http://schemas.microsoft.com/3dmanufacturing/core/2015/02",
    )
    resources = ET.SubElement(model, "resources")
    build_list = ET.SubElement(model, "build")
    for idx, (name, solid) in enumerate(objects, 1):
        obj = ET.SubElement(resources, "object", id=str(idx), type="model", name=name)
        mesh = ET.SubElement(obj, "mesh")
        vertices = ET.SubElement(mesh, "vertices")
        triangles = ET.SubElement(mesh, "triangles")
        pts, faces = arrays(solid)
        for p in pts:
            ET.SubElement(vertices, "vertex", dict(zip("xyz", map(str, p))))
        for tri in faces:
            ET.SubElement(
                triangles, "triangle", dict(zip(("v1", "v2", "v3"), map(str, tri)))
            )
        ET.SubElement(build_list, "item", objectid=str(idx))
    with ZipFile(path, "w", ZIP_DEFLATED) as archive:
        archive.writestr(
            "3D/3dmodel.model",
            ET.tostring(model, encoding="utf-8", xml_declaration=True),
        )
        archive.writestr(
            "_rels/.rels",
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Target="/3D/3dmodel.model" Id="r1" Type="http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel"/></Relationships>',
        )
        archive.writestr(
            "[Content_Types].xml",
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="model" ContentType="application/vnd.ms-package.3dmanufacturing-3dmodel+xml"/></Types>',
        )


def layer(doc, name, color, visible=True):
    obj = rh.Layer()
    obj.Name, obj.Color, obj.Visible = name, color, visible
    return doc.Layers.Add(obj)


def build(source, out, scale, bed, pattern):
    if min(scale, *bed) <= 0 or min(bed[:2]) <= 6:
        raise ValueError(
            "Use a positive scale and a bed larger than the two 3 mm margins"
        )
    model = rh.File3dm.Read(str(source))
    if model is None:
        raise ValueError("Cannot read " + str(source))
    if model.Settings.ModelUnitSystem in (
        getattr(rh.UnitSystem, "None"),
        rh.UnitSystem.CustomUnits,
    ):
        raise ValueError("Set known physical units in the source document")
    factor = (
        rh.UnitSystem.UnitScale(
            model.Settings.ModelUnitSystem, rh.UnitSystem.Millimeters
        )
        / scale
    )
    items = []
    for o in model.Objects:
        attr = o.Attributes
        path = model.Layers.FindIndex(attr.LayerIndex).FullPath
        if attr.IsInstanceDefinitionObject:
            continue
        if pattern:
            if not re.search(pattern, path):
                continue
        elif not (
            path.startswith(
                (
                    "COMPARE - Simplified full size::",
                    "INTERIOR - Simplified full size::",
                )
            )
            and not path.endswith("::Printable union")
        ):
            continue
        name = (attr.Name or path.split("::")[-1]).removeprefix("Simplified - ")
        try:
            body = component(
                o.Geometry, factor, not pattern and name in ("west wall", "north wall")
            )
        except Exception as exc:
            raise ValueError(f"{path} / {name}: {exc}") from exc
        if body.status() != md.Error.NoError or body.volume() <= 0:
            raise ValueError("Invalid solid: " + name)
        items.append((name, body, attr.Id))
    if not items:
        raise ValueError(
            "No matching parts. Supply --layers with a component layer expression."
        )
    prior, parts, notes = [], [], []
    for name, original, source_id in sorted(items, key=priority):
        body = original
        lo, hi = bounds(original)
        for other, low, high in prior:
            if np.all(hi > low) and np.all(high > lo):
                body = body - other
        # A 0.05 mm allowance on intersecting joints also removes coincident-face slivers.
        cutter = original.minkowski_sum(md.Manifold.cube((0.1, 0.1, 0.1), True))
        prior.append((cutter, lo, hi))
        if body.status() != md.Error.NoError:
            raise ValueError("Boolean failed: " + name)
        if body.volume() < 0.001:
            notes.append(name + " fully overlapped or below 0.001 cubic mm")
            continue
        for component_body in body.simplify(0.005).decompose():
            if component_body.volume() < 0.001:
                continue
            v, f = arrays(component_body)
            cleaned = mesh_solid(v[f])
            if cleaned.status() == md.Error.NoError:
                component_body = cleaned.simplify(0.001)
            frame, origin = orient(component_body)
            flat = component_body.transform(affine(frame, -origin))
            dims = np.ptp(bounds(flat), axis=0)
            if min(dims) < 0.4:
                notes.append(
                    f"{name}: omitted a {min(dims):.2f} mm sliver (below 0.4 mm)."
                )
                continue
            usable = np.array(bed[:2]) - 6
            if np.prod(np.ceil((dims[:2][::-1] - 1e-6) / usable)) < np.prod(
                np.ceil((dims[:2] - 1e-6) / usable)
            ):
                rot = np.array([[0, -1, 0], [1, 0, 0], [0, 0, 1]])
                frame, origin = rot @ frame, rot @ origin
                flat = component_body.transform(affine(frame, -origin))
            for tile in tiles(flat, *usable):
                assembled = tile.transform(affine(frame.T, frame.T @ origin))
                low, high = bounds(tile)
                if high[2] - low[2] > bed[2]:
                    raise ValueError(name + " exceeds build height")
                parts.append(
                    (name, source_id, assembled, tile.translate(-low), high - low)
                )
    doc = rh.File3dm()
    doc.Settings.ModelUnitSystem = rh.UnitSystem.Millimeters
    doc.Settings.ModelAbsoluteTolerance = 0.001
    assembly = layer(doc, "Assembly reference", (100, 140, 170, 255), False)
    rows = ["| ID | Part | Plate |", "|---|---|---|"]
    plates = []
    for index, (name, source_id, assembled, flat, size) in enumerate(
        sorted(parts, key=lambda p: -max(p[4][:2])), 1
    ):
        choice = None
        for number, (_, rectangles, _) in enumerate(plates):
            for x in sorted({3} | {r[2] + 3 for r in rectangles}):
                for y in sorted({3} | {r[3] + 3 for r in rectangles}):
                    for turn, (w, h) in enumerate((size[:2], size[:2][::-1])):
                        if x + w > bed[0] - 3 + 1e-5 or y + h > bed[1] - 3 + 1e-5:
                            continue
                        if any(
                            x < r[2] + 2.999
                            and x + w + 2.999 > r[0]
                            and y < r[3] + 2.999
                            and y + h + 2.999 > r[1]
                            for r in rectangles
                        ):
                            continue
                        key = (number, y + h, x + w, turn)
                        if choice is None or key < choice[0]:
                            choice = key, number, x, y, w, h, turn
            if choice:
                break
        if choice:
            _, number, x, y, w, h, turn = choice
        else:
            number = len(plates)
            plates.append(
                (layer(doc, f"Plate {number+1:02}", (36, 127, 125, 255)), [], [])
            )
            (out / f"Plate-{number+1:02}").mkdir()
            x, y, w, h, turn = 3, 3, *size[:2], 0
        if turn:
            flat = flat.rotate((0, 0, 90)).translate((size[1], 0, 0))
        plate_layer, rectangles, objects = plates[number]
        rectangles.append((x, y, x + w, y + h))
        plate = number + 1
        folder = out / f"Plate-{plate:02}"
        pid = f"P{index:02}"
        label = pid + " " + name
        add_mesh(doc, assembled, assembly, label, source_id)
        placed = flat.translate((x, y, 0))
        add_mesh(
            doc,
            placed.translate(((plate - 1) * (bed[0] + 15), 0, 0)),
            plate_layer,
            label,
            source_id,
        )
        write_stl(folder / (pid + ".stl"), placed)
        rows.append(f"| {pid} | {name} | {plate:02} |")
        objects.append((label, placed))
    for number, (_, _, objects) in enumerate(plates, 1):
        write_plate(out / f"Plate-{number:02}.3mf", objects)
    if not parts or not doc.Write(str(out / "Flatpack.3dm"), 8):
        raise ValueError("No kit written")
    guide = f"""# Flat-pack assembly

Scale 1:{scale:g}. Plate size {bed[0]:g} × {bed[1]:g} mm. Open a plate 3MF in the slicer to keep its layout. The STL folders contain individual spares. Keep each piece with its part ID after removing it from the bed.

In Flatpack.3dm, hide the Plate layers and show Assembly reference. Use Zoom Extents. Select a named mesh to match it to the table. Dry-fit the ground floor, porch decks and exterior walls first. Fit landings, stairs and partitions while the interior is accessible. Check the upper floor and stair opening before gluing. Fit porch posts, dormer and roof panels last. Where a part is tiled, join its straight cut edges on a flat surface first.

The joints follow the trimmed component surfaces with a nominal 0.05 mm allowance along each coordinate axis at overlapping joints. Check your slicer preview for small ledges, thin sections and first-layer expansion.

"""
    (out / "Assembly.md").write_text(
        guide + "\n".join(rows) + "\n" + "\n".join(notes) + "\n"
    )
    print(
        f"{len(items)} source elements -> {len(parts)} pieces on {len(plates)} plates"
    )


def generate(source, destination, scale=50, bed=(220, 220, 250), pattern=None):
    destination = Path(destination).resolve()
    if destination.exists():
        raise ValueError("Choose a new output folder")
    with tempfile.TemporaryDirectory(dir=destination.parent) as staging:
        folder = Path(staging) / "kit"
        folder.mkdir()
        build(source, folder, scale, bed, pattern)
        folder.rename(destination)


if __name__ == "__main__":
    try:
        import rhinoscriptsyntax as rs
    except ImportError:
        rs = None
    if rs:
        src = rs.OpenFileName("Choose component model", "Rhino models (*.3dm)|*.3dm||")
        if src:
            dest = rs.SaveFileName("Name a new kit folder")
            if dest:
                expr = rs.GetString(
                    "Component layer regex (Barn uses the simplified layers)", "Barn"
                )
                if expr:
                    scale = rs.GetReal("Scale denominator (50 means 1:50)", 50, 0.01)
                    if scale is not None:
                        bed = rs.GetString(
                            "Bed width depth and height in mm", "220 220 250"
                        )
                        if bed:
                            dimensions = tuple(map(float, bed.split()))
                            if len(dimensions) != 3:
                                raise ValueError("Enter three bed dimensions")
                            generate(
                                src,
                                dest,
                                scale,
                                dimensions,
                                None if expr == "Barn" else expr,
                            )
    else:
        parser = argparse.ArgumentParser(description=__doc__)
        parser.add_argument("source", type=Path)
        parser.add_argument("output", type=Path)
        parser.add_argument("--scale", type=float, default=50)
        parser.add_argument(
            "--bed",
            type=float,
            nargs=3,
            default=(220, 220, 250),
            metavar=("X", "Y", "Z"),
        )
        parser.add_argument(
            "--layers", help="Regex matching full component layer paths"
        )
        args = parser.parse_args()
        generate(args.source, args.output, args.scale, args.bed, args.layers)
