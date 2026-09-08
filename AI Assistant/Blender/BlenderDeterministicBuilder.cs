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
            py.AppendLine("from mathutils import Vector");
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
            py.AppendLine("        detail_name = name.lower()");
            py.AppendLine("        if not any(token in detail_name for token in ('eye', 'metal')):");
            py.AppendLine("            noise = m.node_tree.nodes.new('ShaderNodeTexNoise')");
            py.AppendLine("            noise.name = 'AIA_SurfaceDetail'");
            py.AppendLine("            scale = 20.0 if 'skin' in detail_name else (38.0 if 'leather' in detail_name else 85.0)");
            py.AppendLine("            noise.inputs['Scale'].default_value = scale");
            py.AppendLine("            noise.inputs['Detail'].default_value = 3.0");
            py.AppendLine("            noise.inputs['Roughness'].default_value = 0.65");
            py.AppendLine("            bump = m.node_tree.nodes.new('ShaderNodeBump')");
            py.AppendLine("            bump.name = 'AIA_MicroSurface'");
            py.AppendLine("            bump.inputs['Strength'].default_value = 0.08 if 'skin' in detail_name else 0.16");
            py.AppendLine("            bump.inputs['Distance'].default_value = 0.025");
            py.AppendLine("            m.node_tree.links.new(noise.outputs['Fac'], bump.inputs['Height'])");
            py.AppendLine("            m.node_tree.links.new(bump.outputs['Normal'], bsdf.inputs['Normal'])");
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

            AppendHumanoidHelpers(py);

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
                        case "humanoid":
                            py.AppendLine($"{objVar} = aia_humanoid({Q(string.IsNullOrWhiteSpace(part.Name) ? $"Character_{index}" : part.Name)}, {F(Math.Clamp(part.Dimensions.ElementAtOrDefault(0), 0.38f, 0.65f))}, {F(Math.Clamp(part.Dimensions.ElementAtOrDefault(1), 0.22f, 0.40f))}, {F(Math.Clamp(part.Dimensions.ElementAtOrDefault(2), 1.60f, 2.00f))}, {PyMaterialMap(mats)}, {Q(part.Text)}, {Q(quality)})");
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

                    if (type is not "curve" and not "extruded_polygon" and not "mesh" and not "skin" and not "humanoid" and not "text")
                        py.AppendLine($"{objVar} = bpy.context.object");
                    string matVar = !string.IsNullOrWhiteSpace(part.Material) && mats.TryGetValue(part.Material, out string? found) ? found : "None";
                    string parentVar = !string.IsNullOrWhiteSpace(part.ParentPart) && objectVars.TryGetValue(part.ParentPart, out string? pvar) ? pvar : root;
                    float[] pos = Vec(part.Position, new[] { 0f, 0f, 0f });
                    float[] rot = Vec(part.Rotation, new[] { 0f, 0f, 0f });
                    float[] dims = Vec(part.Dimensions, new[] { 1f, 1f, 1f });
                    int bevelSegments = Math.Clamp(Math.Max(part.BevelSegments, qualityBevelSegments), 1, 6);
                    string dimensionsArg = type is "curve" or "extruded_polygon" or "mesh" or "skin" or "humanoid" or "text"
                        ? "None"
                        : $"({F(Math.Max(0.0001f,dims[0]))},{F(Math.Max(0.0001f,dims[1]))},{F(Math.Max(0.0001f,dims[2]))})";
                    py.AppendLine($"aia_finish({objVar}, {Q(string.IsNullOrWhiteSpace(part.Name) ? $"Part_{index}" : part.Name)}, {parentVar}, ({F(pos[0])},{F(pos[1])},{F(pos[2])}), ({F(rot[0])},{F(rot[1])},{F(rot[2])}), {dimensionsArg}, {matVar}, {F(Math.Max(0f, part.Bevel))}, {bevelSegments}, {(part.ShadeSmooth ? "True" : "False")})");
                    if (part.SubdivisionLevels > 0 && type != "skin" && type != "humanoid" && type != "text")
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

        private static void AppendHumanoidHelpers(StringBuilder py)
        {
            py.AppendLine("""
def aia_pick_mat(mats, names, fallback=None):
    lowered = {str(k).lower(): v for k, v in mats.items()}
    for wanted in names:
        for key, value in lowered.items():
            if wanted in key:
                return value
    return fallback or (next(iter(mats.values())) if mats else None)

def aia_assign(obj, mat):
    if mat is not None and hasattr(obj.data, 'materials'):
        obj.data.materials.append(mat)
    if obj.type == 'MESH':
        for poly in obj.data.polygons:
            poly.use_smooth = True
    return obj

def aia_uv(name, location, scale, mat, segments):
    bpy.ops.mesh.primitive_uv_sphere_add(segments=segments, ring_count=max(12, segments // 2), radius=1.0, location=location)
    obj = bpy.context.object
    obj.name = name
    obj.scale = scale
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    return aia_assign(obj, mat)

def aia_segment(name, start, end, radius_a, radius_b, mat, segments):
    a = Vector(start); b = Vector(end); delta = b - a
    length = max(0.001, delta.length)
    bpy.ops.mesh.primitive_cone_add(vertices=segments, radius1=max(0.008, radius_a), radius2=max(0.008, radius_b), depth=length, location=(a + b) * 0.5)
    obj = bpy.context.object
    obj.name = name
    obj.rotation_mode = 'QUATERNION'
    obj.rotation_quaternion = delta.to_track_quat('Z', 'Y')
    return aia_assign(obj, mat)

def aia_box(name, location, scale, mat, bevel=0.015):
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=location)
    obj = bpy.context.object
    obj.name = name
    obj.scale = scale
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=False, rotation=False, scale=True)
    if bevel > 0.0:
        mod = obj.modifiers.new(name='AIA_GarmentBevel', type='BEVEL')
        mod.width = bevel; mod.segments = 3; mod.limit_method = 'ANGLE'
    return aia_assign(obj, mat)

def aia_join(objects, name):
    bpy.ops.object.select_all(action='DESELECT')
    valid = [obj for obj in objects if obj is not None]
    for obj in valid:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = valid[0]
    bpy.ops.object.join()
    valid[0].name = name
    return valid[0]

def aia_parent_details(body, objects):
    for obj in objects:
        if obj is not None:
            world = obj.matrix_world.copy()
            obj.parent = body
            obj.matrix_world = world

def aia_humanoid(name, shoulder_width, body_depth, height, mats, style, quality):
    # Semantic blueprint -> deterministic Blender API character. The model never authors topology.
    q = str(quality).lower()
    segments = 48 if q == 'aa' else (36 if q == 'high' else 28)
    s = height / 1.8
    w = shoulder_width / 0.48
    d = body_depth / 0.30
    skin = aia_pick_mat(mats, ('skin',))
    shirt = aia_pick_mat(mats, ('shirt','jacket','fabric'), skin)
    pants = aia_pick_mat(mats, ('pants','trouser','cargo'), shirt)
    leather = aia_pick_mat(mats, ('leather','boot','strap'), pants)
    hair = aia_pick_mat(mats, ('hair',), leather)
    eyes = aia_pick_mat(mats, ('eye',), shirt)
    metal = aia_pick_mat(mats, ('metal','steel'), leather)

    body_parts = []
    body_parts.append(aia_uv(name + '_PelvisBase', (0, 0, 0.98*s), (0.19*w, 0.135*d, 0.17*s), skin, segments))
    body_parts.append(aia_uv(name + '_AbdomenBase', (0, 0, 1.20*s), (0.175*w, 0.125*d, 0.23*s), skin, segments))
    body_parts.append(aia_uv(name + '_ChestBase', (0, 0, 1.43*s), (0.245*w, 0.145*d, 0.255*s), skin, segments))
    body_parts.append(aia_segment(name + '_NeckBase', (0,0,1.57*s), (0,0,1.66*s), 0.074*w, 0.068*w, skin, segments))
    body_parts.append(aia_uv(name + '_HeadBase', (0, -0.004*d, 1.74*s), (0.108*w, 0.092*d, 0.142*s), skin, segments))

    for side, sign in (('L', -1.0), ('R', 1.0)):
        shoulder = (sign*0.215*w, 0, 1.48*s)
        elbow = (sign*0.405*w, 0.004*d, 1.27*s)
        wrist = (sign*0.505*w, -0.004*d, 1.07*s)
        hip = (sign*0.105*w, 0, 1.01*s)
        knee = (sign*0.105*w, 0.005*d, 0.57*s)
        ankle = (sign*0.105*w, 0.012*d, 0.14*s)
        body_parts.append(aia_uv(name + '_' + side + '_ShoulderBase', shoulder, (0.095*w,0.09*d,0.10*s), skin, segments))
        body_parts.append(aia_segment(name + '_' + side + '_UpperArmBase', shoulder, elbow, 0.078*w, 0.062*w, skin, segments))
        body_parts.append(aia_segment(name + '_' + side + '_ForearmBase', elbow, wrist, 0.064*w, 0.046*w, skin, segments))
        body_parts.append(aia_uv(name + '_' + side + '_HandBase', (sign*0.535*w,-0.016*d,1.015*s), (0.052*w,0.032*d,0.082*s), skin, segments))
        body_parts.append(aia_uv(name + '_' + side + '_HipBase', hip, (0.11*w,0.115*d,0.13*s), skin, segments))
        body_parts.append(aia_segment(name + '_' + side + '_ThighBase', hip, knee, 0.105*w, 0.078*w, skin, segments))
        body_parts.append(aia_segment(name + '_' + side + '_ShinBase', knee, ankle, 0.079*w, 0.052*w, skin, segments))
        body_parts.append(aia_uv(name + '_' + side + '_FootBase', (sign*0.105*w,-0.065*d,0.075*s), (0.075*w,0.15*d,0.06*s), skin, segments))

    body = aia_join(body_parts, name)
    bpy.context.view_layer.objects.active = body
    body.select_set(True)
    # Join keeps the first pelvis object's origin. Bake it before parenting details,
    # otherwise Blender adds the pelvis offset to every garment a second time.
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    try:
        remesh = body.modifiers.new(name='AIA_ConnectedAnatomy', type='REMESH')
        remesh.mode = 'VOXEL'
        remesh.voxel_size = max(0.014, height / (105.0 if q == 'aa' else 82.0))
        remesh.use_smooth_shade = True
        remesh.use_remove_disconnected = False
        bpy.ops.object.modifier_apply(modifier=remesh.name)
    except Exception:
        if body.modifiers.get('AIA_ConnectedAnatomy') is not None:
            body.modifiers.remove(body.modifiers.get('AIA_ConnectedAnatomy'))
        try:
            body.data.remesh_voxel_size = max(0.014, height / (105.0 if q == 'aa' else 82.0))
            body.data.remesh_voxel_adaptivity = 0.0
            bpy.ops.object.voxel_remesh()
        except Exception:
            pass
    smooth = body.modifiers.new(name='AIA_AnatomySmooth', type='SMOOTH')
    smooth.factor = 0.55; smooth.iterations = 3
    try:
        bpy.ops.object.modifier_apply(modifier=smooth.name)
    except Exception:
        pass
    if hasattr(body.data, 'materials'):
        body.data.materials.clear()
        if skin is not None: body.data.materials.append(skin)
    for poly in body.data.polygons:
        poly.use_smooth = True

    details = []
    # Fitted layered upper clothing and sleeves.
    details.append(aia_uv(name + '_ShirtTorso', (0,0.008*d,1.38*s), (0.246*w,0.151*d,0.31*s), shirt, segments))
    for side, sign in (('L',-1.0),('R',1.0)):
        details.append(aia_segment(name + '_' + side + '_Sleeve', (sign*0.205*w,0,1.47*s), (sign*0.395*w,0.004*d,1.28*s), 0.086*w,0.069*w,shirt,segments))
        details.append(aia_uv(name + '_' + side + '_Glove', (sign*0.535*w,-0.016*d,1.015*s), (0.057*w,0.037*d,0.087*s), leather, segments))
        for finger, y in enumerate((-0.030,-0.010,0.010,0.030)):
            details.append(aia_segment(name + '_' + side + '_Finger' + str(finger+1), (sign*0.552*w,y*d,1.005*s), (sign*0.602*w,y*d,(0.985-finger*0.004)*s), 0.010*w,0.008*w,leather,max(16,segments//3)))
        details.append(aia_segment(name + '_' + side + '_Thumb', (sign*0.535*w,-0.038*d,1.035*s), (sign*0.575*w,-0.058*d,1.000*s), 0.012*w,0.009*w,leather,max(16,segments//3)))

    # Cargo pants, reinforced knees and boots.
    details.append(aia_uv(name + '_PantsWaist', (0,0.004*d,0.99*s), (0.205*w,0.145*d,0.175*s), pants, segments))
    for side, sign in (('L',-1.0),('R',1.0)):
        details.append(aia_segment(name + '_' + side + '_TrouserLeg', (sign*0.105*w,0.006*d,0.96*s), (sign*0.105*w,0.012*d,0.22*s), 0.116*w,0.069*w,pants,segments))
        details.append(aia_box(name + '_' + side + '_KneePad', (sign*0.105*w,-0.092*d,0.56*s), (0.074*w,0.025*d,0.085*s), leather, 0.012*s))
        details.append(aia_segment(name + '_' + side + '_BootShaft', (sign*0.105*w,0.012*d,0.27*s), (sign*0.105*w,0.012*d,0.105*s), 0.076*w,0.083*w,leather,segments))
        details.append(aia_uv(name + '_' + side + '_Boot', (sign*0.105*w,-0.075*d,0.068*s), (0.086*w,0.165*d,0.068*s), leather, segments))
        details.append(aia_box(name + '_' + side + '_CargoPocket', (sign*0.172*w,-0.105*d,0.76*s), (0.052*w,0.018*d,0.075*s), pants, 0.008*s))

    # Face, ears and layered hair masses.
    details.append(aia_uv(name + '_Nose', (0,-0.096*d,1.735*s), (0.024*w,0.027*d,0.040*s), skin, max(24,segments//2)))
    for side, sign in (('L',-1.0),('R',1.0)):
        details.append(aia_uv(name + '_' + side + '_Eye', (sign*0.041*w,-0.093*d,1.775*s), (0.016*w,0.011*d,0.012*s), eyes, max(24,segments//2)))
        details.append(aia_uv(name + '_' + side + '_Ear', (sign*0.108*w,-0.001*d,1.745*s), (0.015*w,0.012*d,0.036*s), skin, max(20,segments//2)))
        details.append(aia_segment(name + '_' + side + '_Eyebrow', (sign*0.020*w,-0.096*d,1.805*s), (sign*0.065*w,-0.092*d,1.802*s), 0.006*w,0.005*w,hair,max(14,segments//3)))
    details.append(aia_segment(name + '_Mouth', (-0.034*w,-0.096*d,1.686*s), (0.034*w,-0.096*d,1.686*s), 0.006*w,0.005*w,hair,max(14,segments//3)))
    details.append(aia_box(name + '_ShirtCollarL', (-0.055*w,-0.143*d,1.57*s), (0.050*w,0.010*d,0.085*s), shirt, 0.006*s))
    details.append(aia_box(name + '_ShirtCollarR', (0.055*w,-0.143*d,1.57*s), (0.050*w,0.010*d,0.085*s), shirt, 0.006*s))
    for i, (x,y,z,sx,sy,sz) in enumerate(((-0.065,0.015,1.845,0.065,0.074,0.052),(0,0.025,1.866,0.075,0.078,0.052),(0.065,0.015,1.845,0.065,0.074,0.052),(-0.075,0.045,1.79,0.052,0.070,0.070),(0.075,0.045,1.79,0.052,0.070,0.070))):
        details.append(aia_uv(name + '_Hair_' + str(i+1), (x*w,y*d,z*s), (sx*w,sy*d,sz*s), hair, max(24,segments//2)))

    # Survival construction detail: belt, buckle, straps, pockets and optional pack.
    bpy.ops.mesh.primitive_torus_add(major_segments=segments, minor_segments=max(10,segments//4), location=(0,0,1.02*s), major_radius=0.172*w, minor_radius=0.018*w)
    belt = bpy.context.object; belt.name = name + '_Belt'; details.append(aia_assign(belt, leather))
    details.append(aia_box(name + '_Buckle', (0,-0.142*d,1.02*s), (0.033*w,0.014*d,0.027*s), metal, 0.005*s))
    details.append(aia_box(name + '_ChestPocketL', (-0.10*w,-0.142*d,1.39*s), (0.064*w,0.014*d,0.07*s), shirt, 0.007*s))
    details.append(aia_box(name + '_ChestPocketR', (0.10*w,-0.142*d,1.39*s), (0.064*w,0.014*d,0.07*s), shirt, 0.007*s))
    style_lower = str(style).lower()
    if 'backpack' in style_lower or 'survival' in style_lower or 'rucksack' in style_lower:
        details.append(aia_box(name + '_Backpack', (0,0.19*d,1.30*s), (0.19*w,0.10*d,0.26*s), leather, 0.025*s))
        details.append(aia_segment(name + '_StrapL', (-0.15*w,-0.02*d,1.53*s), (-0.14*w,-0.02*d,1.16*s), 0.018*w,0.018*w,leather,max(16,segments//2)))
        details.append(aia_segment(name + '_StrapR', (0.15*w,-0.02*d,1.53*s), (0.14*w,-0.02*d,1.16*s), 0.018*w,0.018*w,leather,max(16,segments//2)))

    aia_parent_details(body, details)
    body['aia_character_style'] = str(style)
    body['aia_character_height_m'] = float(height)
    body['aia_topology_owner'] = 'host_semantic_humanoid_v1'
    return body
""");
        }

        private static string PyMaterialMap(Dictionary<string, string> materials)
        {
            if (materials.Count == 0) return "{}";
            return "{" + string.Join(",", materials.Select(pair => Q(pair.Key) + ":" + pair.Value)) + "}";
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
