using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AI_Assistant.Blender
{
    internal sealed class BlenderBuilderAsset
    {
        public string RootObject { get; set; } = "";
        public List<BlenderBuilderMaterial> Materials { get; set; } = new();
        public List<BlenderBuilderPart> Parts { get; set; } = new();
    }

    internal sealed class BlenderBuilderMaterial
    {
        public string Name { get; set; } = "";
        public float[] Color { get; set; } = new[] { 0.5f, 0.5f, 0.5f, 1f };
        public float Metallic { get; set; }
        public float Roughness { get; set; } = 0.6f;
    }

    internal sealed class BlenderBuilderPart
    {
        public string Type { get; set; } = "cube";
        public string Name { get; set; } = "Part";
        public string ParentPart { get; set; } = "";
        public string Material { get; set; } = "";
        public float[] Position { get; set; } = new[] { 0f, 0f, 0f };
        public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f };
        public float[] Dimensions { get; set; } = new[] { 1f, 1f, 1f };
        public float Radius { get; set; } = 0.5f;
        public float Radius2 { get; set; } = 0.25f;
        public float Depth { get; set; } = 1f;
        public int Vertices { get; set; } = 24;
        public int MajorSegments { get; set; } = 32;
        public int MinorSegments { get; set; } = 12;
        public float Bevel { get; set; }
        public int BevelSegments { get; set; } = 2;
        public bool ShadeSmooth { get; set; }
        public int SubdivisionLevels { get; set; }
        public string Text { get; set; } = "";
        public float Extrude { get; set; } = 0.08f;
        public List<float[]> Points { get; set; } = new();
        public List<int[]> Edges { get; set; } = new();
        public List<int[]> Faces { get; set; } = new();
        public List<float> Radii { get; set; } = new();
    }

    internal static class BlenderDeterministicBuilder
    {
        public static string BuildPython(IEnumerable<BlenderBuilderAsset> assets, string qualityProfile)
        {
            string quality = string.IsNullOrWhiteSpace(qualityProfile) ? "Medium" : qualityProfile;
            int qualitySegments = quality.Equals("AA", StringComparison.OrdinalIgnoreCase) ? 64
                : quality.Equals("High", StringComparison.OrdinalIgnoreCase) ? 48
                : quality.Equals("Low", StringComparison.OrdinalIgnoreCase) ? 12
                : 32;
            int qualityBevelSegments = quality.Equals("AA", StringComparison.OrdinalIgnoreCase) ? 4
                : quality.Equals("High", StringComparison.OrdinalIgnoreCase) ? 3
                : quality.Equals("Low", StringComparison.OrdinalIgnoreCase) ? 1
                : 2;

            StringBuilder py = new StringBuilder();
            py.AppendLine("# Deterministic host-generated Blender 3.6 asset build");
            py.AppendLine("import bpy");
            py.AppendLine("import math");
            py.AppendLine();
            py.AppendLine("def aia_mat(name, color, metallic=0.0, roughness=0.6):");
            py.AppendLine("    m = bpy.data.materials.get(name) or bpy.data.materials.new(name=name)");
            py.AppendLine("    m.diffuse_color = color");
            py.AppendLine("    m.use_nodes = True");
            py.AppendLine("    bsdf = m.node_tree.nodes.get('Principled BSDF') if m.node_tree else None");
            py.AppendLine("    if bsdf is not None:");
            py.AppendLine("        base = bsdf.inputs.get('Base Color')");
            py.AppendLine("        if base is not None: base.default_value = color");
            py.AppendLine("        metal = bsdf.inputs.get('Metallic')");
            py.AppendLine("        if metal is not None: metal.default_value = metallic");
            py.AppendLine("        rough = bsdf.inputs.get('Roughness')");
            py.AppendLine("        if rough is not None: rough.default_value = roughness");
            py.AppendLine("    return m");
            py.AppendLine();
            py.AppendLine("def aia_finish(obj, name, parent, pos, rot, dims, mat, bevel, bevel_segments, smooth):");
            py.AppendLine("    obj.name = name");
            py.AppendLine("    obj.parent = parent");
            py.AppendLine("    obj.location = pos");
            py.AppendLine("    obj.rotation_euler = tuple(math.radians(v) for v in rot)");
            py.AppendLine("    if dims is not None:");
            py.AppendLine("        obj.dimensions = dims");
            py.AppendLine("        bpy.context.view_layer.objects.active = obj");
            py.AppendLine("        bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)");
            py.AppendLine("    if mat is not None and hasattr(obj.data, 'materials'):");
            py.AppendLine("        obj.data.materials.append(mat)");
            py.AppendLine("    if bevel > 0.00001:");
            py.AppendLine("        mod = obj.modifiers.new(name='AIA_Bevel', type='BEVEL')");
            py.AppendLine("        mod.width = bevel");
            py.AppendLine("        mod.segments = max(1, int(bevel_segments))");
            py.AppendLine("        mod.limit_method = 'ANGLE'");
            py.AppendLine("    if smooth and obj.type == 'MESH':");
            py.AppendLine("        for p in obj.data.polygons: p.use_smooth = True");
            py.AppendLine("    return obj");
            py.AppendLine();
            py.AppendLine("def aia_mesh(name, verts, faces):");
            py.AppendLine("    mesh = bpy.data.meshes.new(name + '_Mesh')");
            py.AppendLine("    mesh.from_pydata(verts, [], faces)");
            py.AppendLine("    mesh.update(calc_edges=True)");
            py.AppendLine("    obj = bpy.data.objects.new(name, mesh)");
            py.AppendLine("    bpy.context.scene.collection.objects.link(obj)");
            py.AppendLine("    return obj");
            py.AppendLine();
            py.AppendLine("def aia_extruded_polygon(name, points, depth):");
            py.AppendLine("    n = len(points)");
            py.AppendLine("    half = max(0.0001, depth) * 0.5");
            py.AppendLine("    verts = [(p[0], p[1], -half) for p in points] + [(p[0], p[1], half) for p in points]");
            py.AppendLine("    faces = [tuple(range(n - 1, -1, -1)), tuple(range(n, n * 2))]");
            py.AppendLine("    for i in range(n):");
            py.AppendLine("        j = (i + 1) % n");
            py.AppendLine("        faces.append((i, j, n + j, n + i))");
            py.AppendLine("    return aia_mesh(name, verts, faces)");
            py.AppendLine();
            py.AppendLine("def aia_curve(name, points, radius, resolution):");
            py.AppendLine("    curve = bpy.data.curves.new(name + '_Curve', type='CURVE')");
            py.AppendLine("    curve.dimensions = '3D'");
            py.AppendLine("    curve.resolution_u = max(2, int(resolution))");
            py.AppendLine("    curve.bevel_depth = max(0.0001, radius)");
            py.AppendLine("    curve.bevel_resolution = max(2, int(resolution // 2))");
            py.AppendLine("    curve.use_fill_caps = True");
            py.AppendLine("    spline = curve.splines.new('BEZIER')");
            py.AppendLine("    spline.bezier_points.add(len(points) - 1)");
            py.AppendLine("    for bp, co in zip(spline.bezier_points, points):");
            py.AppendLine("        bp.co = co; bp.handle_left_type = 'AUTO'; bp.handle_right_type = 'AUTO'");
            py.AppendLine("    obj = bpy.data.objects.new(name, curve)");
            py.AppendLine("    bpy.context.scene.collection.objects.link(obj)");
            py.AppendLine("    return obj");
            py.AppendLine();
            py.AppendLine("def aia_skin(name, points, edges, radii, subdivision):");
            py.AppendLine("    mesh = bpy.data.meshes.new(name + '_SkinMesh')");
            py.AppendLine("    mesh.from_pydata(points, edges, [])");
            py.AppendLine("    mesh.update(calc_edges=True)");
            py.AppendLine("    obj = bpy.data.objects.new(name, mesh)");
            py.AppendLine("    bpy.context.scene.collection.objects.link(obj)");
            py.AppendLine("    bpy.context.view_layer.objects.active = obj; obj.select_set(True)");
            py.AppendLine("    skin = obj.modifiers.new(name='AIA_Skin', type='SKIN')");
            py.AppendLine("    bpy.context.view_layer.objects.active = obj");
            py.AppendLine("    bpy.context.view_layer.update()");
            py.AppendLine("    if mesh.skin_vertices:");
            py.AppendLine("        layer = mesh.skin_vertices[0].data");
            py.AppendLine("        for i in range(min(len(layer), len(radii))):");
            py.AppendLine("            r = max(0.015, float(radii[i])); layer[i].radius = (r, r)");
            py.AppendLine("    if subdivision > 0:");
            py.AppendLine("        sub = obj.modifiers.new(name='AIA_Subdivision', type='SUBSURF')");
            py.AppendLine("        sub.levels = min(3, max(1, int(subdivision))); sub.render_levels = sub.levels");
            py.AppendLine("    return obj");
            py.AppendLine();

            foreach (BlenderBuilderAsset asset in assets)
            {
                string root = SafeIdentifier(asset.RootObject);
                py.AppendLine($"{root} = bpy.data.objects.new({Q(asset.RootObject)}, None)");
                py.AppendLine($"bpy.context.scene.collection.objects.link({root})");
                py.AppendLine($"{root}.location = (0.0, 0.0, 0.0)");
                py.AppendLine($"{root}.rotation_euler = (0.0, 0.0, 0.0)");
                py.AppendLine($"{root}.scale = (1.0, 1.0, 1.0)");

                Dictionary<string, string> mats = new(StringComparer.OrdinalIgnoreCase);
                foreach (BlenderBuilderMaterial material in asset.Materials)
                {
                    string varName = "mat_" + SafeIdentifier(asset.RootObject) + "_" + SafeIdentifier(material.Name);
                    float[] c = NormalizeColor(material.Color);
                    py.AppendLine($"{varName} = aia_mat({Q(asset.RootObject + "__" + material.Name)}, ({F(c[0])}, {F(c[1])}, {F(c[2])}, {F(c[3])}), {F(Clamp01(material.Metallic))}, {F(Clamp01(material.Roughness))})");
                    mats[material.Name] = varName;
                }

                List<BlenderBuilderPart> ordered = OrderParts(asset.Parts);
                Dictionary<string, string> objectVars = new(StringComparer.OrdinalIgnoreCase);
                int index = 0;
                foreach (BlenderBuilderPart part in ordered)
                {
                    index++;
                    string objVar = "obj_" + SafeIdentifier(asset.RootObject) + "_" + index;
                    string type = (part.Type ?? "cube").Trim().ToLowerInvariant();
                    int vertices = Math.Clamp(part.Vertices <= 0 ? qualitySegments : Math.Max(part.Vertices, qualitySegments / 2), 3, 128);
                    int major = Math.Clamp(part.MajorSegments <= 0 ? qualitySegments : Math.Max(part.MajorSegments, qualitySegments / 2), 3, 128);
                    int minor = Math.Clamp(part.MinorSegments <= 0 ? qualitySegments / 3 : Math.Max(part.MinorSegments, qualitySegments / 4), 3, 64);

                    switch (type)
                    {
                        case "cylinder":
                            py.AppendLine($"bpy.ops.mesh.primitive_cylinder_add(vertices={vertices}, radius={F(Math.Max(0.0001f, part.Radius))}, depth={F(Math.Max(0.0001f, part.Depth))}, location=(0,0,0))");
                            break;
                        case "cone":
                            py.AppendLine($"bpy.ops.mesh.primitive_cone_add(vertices={vertices}, radius1={F(Math.Max(0.0001f, part.Radius))}, radius2={F(Math.Max(0f, part.Radius2))}, depth={F(Math.Max(0.0001f, part.Depth))}, location=(0,0,0))");
                            break;
                        case "uv_sphere":
                        case "sphere":
                            py.AppendLine($"bpy.ops.mesh.primitive_uv_sphere_add(segments={vertices}, ring_count={Math.Clamp(vertices / 2, 4, 64)}, radius={F(Math.Max(0.0001f, part.Radius))}, location=(0,0,0))");
                            break;
                        case "torus":
                            py.AppendLine($"bpy.ops.mesh.primitive_torus_add(major_segments={major}, minor_segments={minor}, location=(0,0,0), major_radius={F(Math.Max(0.0001f, part.Radius))}, minor_radius={F(Math.Max(0.0001f, part.Radius2))})");
                            break;
                        case "curve":
                            py.AppendLine($"{objVar} = aia_curve({Q(string.IsNullOrWhiteSpace(part.Name) ? $"Part_{index}" : part.Name)}, {PyPoints(part.Points)}, {F(Math.Max(0.001f, part.Radius))}, {Math.Clamp(part.Vertices / 8, 3, 12)})");
                            break;
                        case "extruded_polygon":
                            py.AppendLine($"{objVar} = aia_extruded_polygon({Q(string.IsNullOrWhiteSpace(part.Name) ? $"Part_{index}" : part.Name)}, {PyPoints2(part.Points)}, {F(Math.Max(0.001f, part.Extrude))})");
                            break;
                        case "mesh":
                            py.AppendLine($"{objVar} = aia_mesh({Q(string.IsNullOrWhiteSpace(part.Name) ? $"Part_{index}" : part.Name)}, {PyPoints(part.Points)}, {PyIndexLists(part.Faces)})");
                            break;
                        case "skin":
                            py.AppendLine($"{objVar} = aia_skin({Q(string.IsNullOrWhiteSpace(part.Name) ? $"Part_{index}" : part.Name)}, {PyPoints(part.Points)}, {PyIndexLists(part.Edges)}, {PyFloats(part.Radii)}, {Math.Clamp(part.SubdivisionLevels, 0, 3)})");
                            break;
                        case "text":
                            py.AppendLine("bpy.ops.object.text_add(location=(0,0,0))");
                            py.AppendLine($"{objVar} = bpy.context.object");
                            py.AppendLine($"{objVar}.data.body = {Q(part.Text)}");
                            py.AppendLine($"{objVar}.data.extrude = {F(Math.Max(0.001f, part.Extrude))}");
                            py.AppendLine($"{objVar}.data.bevel_depth = {F(Math.Max(0f, part.Bevel))}");
                            break;
                        case "plane":
                            py.AppendLine("bpy.ops.mesh.primitive_plane_add(size=1.0, location=(0,0,0))");
                            break;
                        default:
                            py.AppendLine("bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0,0,0))");
                            break;
                    }

                    if (type is not "curve" and not "extruded_polygon" and not "mesh" and not "skin" and not "text")
                        py.AppendLine($"{objVar} = bpy.context.object");
                    string matVar = !string.IsNullOrWhiteSpace(part.Material) && mats.TryGetValue(part.Material, out string? found) ? found : "None";
                    string parentVar = !string.IsNullOrWhiteSpace(part.ParentPart) && objectVars.TryGetValue(part.ParentPart, out string? pvar) ? pvar : root;
                    float[] pos = Vec(part.Position, new[] { 0f, 0f, 0f });
                    float[] rot = Vec(part.Rotation, new[] { 0f, 0f, 0f });
                    float[] dims = Vec(part.Dimensions, new[] { 1f, 1f, 1f });
                    int bevelSegments = Math.Clamp(Math.Max(part.BevelSegments, qualityBevelSegments), 1, 6);
                    string dimensionsArg = type is "curve" or "extruded_polygon" or "mesh" or "skin" or "text"
                        ? "None"
                        : $"({F(Math.Max(0.0001f,dims[0]))},{F(Math.Max(0.0001f,dims[1]))},{F(Math.Max(0.0001f,dims[2]))})";
                    py.AppendLine($"aia_finish({objVar}, {Q(string.IsNullOrWhiteSpace(part.Name) ? $"Part_{index}" : part.Name)}, {parentVar}, ({F(pos[0])},{F(pos[1])},{F(pos[2])}), ({F(rot[0])},{F(rot[1])},{F(rot[2])}), {dimensionsArg}, {matVar}, {F(Math.Max(0f, part.Bevel))}, {bevelSegments}, {(part.ShadeSmooth ? "True" : "False")})");
                    if (part.SubdivisionLevels > 0 && type != "skin" && type != "text")
                    {
                        py.AppendLine($"sub = {objVar}.modifiers.new(name='AIA_Subdivision', type='SUBSURF')");
                        py.AppendLine($"sub.levels = {Math.Clamp(part.SubdivisionLevels, 1, 3)}; sub.render_levels = sub.levels");
                    }
                    if (!string.IsNullOrWhiteSpace(part.Name)) objectVars[part.Name] = objVar;
                }
                py.AppendLine();
            }

            return py.ToString();
        }

        private static List<BlenderBuilderPart> OrderParts(IEnumerable<BlenderBuilderPart> parts)
        {
            List<BlenderBuilderPart> remaining = parts.ToList();
            List<BlenderBuilderPart> ordered = new();
            HashSet<string> emitted = new(StringComparer.OrdinalIgnoreCase);
            int guard = 0;
            while (remaining.Count > 0 && guard++ < 512)
            {
                int before = remaining.Count;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    BlenderBuilderPart part = remaining[i];
                    if (string.IsNullOrWhiteSpace(part.ParentPart) || emitted.Contains(part.ParentPart))
                    {
                        ordered.Add(part);
                        if (!string.IsNullOrWhiteSpace(part.Name)) emitted.Add(part.Name);
                        remaining.RemoveAt(i);
                    }
                }
                if (remaining.Count == before)
                {
                    foreach (BlenderBuilderPart part in remaining)
                    {
                        part.ParentPart = "";
                        ordered.Add(part);
                    }
                    break;
                }
            }
            return ordered;
        }

        private static float[] Vec(float[]? value, float[] fallback) => value != null && value.Length >= 3 ? value : fallback;
        private static float[] NormalizeColor(float[]? c)
        {
            float[] r = new[] { 0.5f, 0.5f, 0.5f, 1f };
            if (c != null) for (int i = 0; i < Math.Min(4, c.Length); i++) r[i] = Clamp01(c[i]);
            return r;
        }
        private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);
        private static string PyPoints(IEnumerable<float[]>? points) => "[" + string.Join(",", (points ?? Array.Empty<float[]>()).Where(p => p != null && p.Length >= 3).Select(p => $"({F(p[0])},{F(p[1])},{F(p[2])})")) + "]";
        private static string PyPoints2(IEnumerable<float[]>? points) => "[" + string.Join(",", (points ?? Array.Empty<float[]>()).Where(p => p != null && p.Length >= 2).Select(p => $"({F(p[0])},{F(p[1])})")) + "]";
        private static string PyIndexLists(IEnumerable<int[]>? values) => "[" + string.Join(",", (values ?? Array.Empty<int[]>()).Where(v => v != null && v.Length >= 2).Select(v => "(" + string.Join(",", v.Select(i => Math.Max(0, i))) + ")")) + "]";
        private static string PyFloats(IEnumerable<float>? values) => "[" + string.Join(",", (values ?? Array.Empty<float>()).Select(v => F(Math.Max(0.001f, v)))) + "]";
        private static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        private static string Q(string value) => "'" + (value ?? "").Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        private static string SafeIdentifier(string value)
        {
            string raw = string.IsNullOrWhiteSpace(value) ? "AIA_Root" : value;
            StringBuilder b = new StringBuilder();
            foreach (char c in raw) b.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            if (b.Length == 0 || char.IsDigit(b[0])) b.Insert(0, '_');
            return b.ToString();
        }
    }
}
