using AI_Assistant.AgentV2;
using AI_Assistant.Runtime;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Assistant.Blender
{
    public sealed class BlenderAgentV3
    {
        private const int MaxAssets = 12;
        private const int MaxInstances = 48;
        private const int TimeoutSeconds = 300;

        private readonly RuntimeSettings settings;
        private readonly Action<string> activity;
        private readonly ProviderRouterV2 providers;
        private readonly IAIProviderV2 primary;

        public BlenderAgentV3(RuntimeSettings settings, Action<string> activity)
        {
            this.settings = settings;
            this.activity = activity;
            providers = new ProviderRouterV2(activity);
            primary = new OpenAiCompatibleProviderV2(
                "Blender-InclusionAI",
                "https://openrouter.ai/api/v1/chat/completions",
                Environment.GetEnvironmentVariable("BLENDER_OPENROUTER_MODEL")
                    ?? "inclusionai/ling-3.0-flash-fin:free",
                "OPENROUTER_API_KEY",
                180
            );
        }

        public bool ShouldHandle(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            return p.StartsWith("/blender ") || p.Contains(" blender ") || p.StartsWith("blender ")
                || p.Contains("napravi model") || p.Contains("3d model") || p.Contains("napravi scenu")
                || p.Contains("build a scene") || p.Contains("benzinsk") || p.Contains("gas station");
        }

        public async Task<string> HandleAsync(string prompt)
        {
            CancellationToken token = AgentCancellationHub.Token;
            string goal = CleanGoal(prompt);
            if (string.IsNullOrWhiteSpace(goal)) return "Blender Agent: napiši šta želiš da napravim.";

            string blenderExe = settings.ResolveBlenderExecutable();
            if (string.IsNullOrWhiteSpace(blenderExe) || !File.Exists(blenderExe))
                return "Blender Agent nije spreman: Blender executable nije pronađen.";

            string version = await ProbeAsync(blenderExe, token);
            if (token.IsCancellationRequested) return "Blender task cancelled by user.";

            string quality = DetectQuality(goal);
            activity("[BLENDER V3] builder-first · " + version);
            activity("[BLENDER V3 QUALITY] " + quality);

            string runRoot = Path.Combine(
                settings.BlenderWorkspace,
                "AI_Runs",
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
            );
            Directory.CreateDirectory(runRoot);

            AgentTaskStateV2 task = new AgentTaskStateV2
            {
                Goal = goal,
                Phase = AgentTaskPhaseV2.Designing
            };

            ProviderReplyV2 reply = await CompleteAsync(
                task,
                BuildSystemPrompt(version),
                BuildUserPrompt(goal),
                token
            );
            if (!reply.Success) return "Blender Agent model failure: " + reply.Error;

            if (!TryParsePlan(reply.Content, out BuilderScenePlan plan, out string error))
            {
                activity("[BLENDER V3 SCHEMA] repairing malformed structured plan");
                ProviderReplyV2 retry = await CompleteAsync(
                    task,
                    BuildSystemPrompt(version),
                    BuildUserPrompt(goal)
                        + "\nPrevious output was rejected: " + error
                        + "\nReturn the COMPLETE strict builder JSON schema with non-empty assets, meaningful parts, parent relationships where parts attach, and instances.",
                    token
                );
                if (!retry.Success || !TryParsePlan(retry.Content, out plan, out error))
                    return "Blender Agent received invalid builder plan after recovery: " + error;
                reply = retry;
            }

            NormalizePlan(plan, quality);

            List<string> spatialWarnings = InspectSpatialIntegrity(plan);
            if (spatialWarnings.Count > 0 && !token.IsCancellationRequested)
            {
                activity("[BLENDER V3 SPATIAL] asset attachment issues detected; requesting one structural repair");
                task.Phase = AgentTaskPhaseV2.Correcting;
                ProviderReplyV2 repair = await CompleteAsync(
                    task,
                    BuildSystemPrompt(version),
                    BuildSpatialRepairPrompt(goal, plan, spatialWarnings),
                    token
                );
                if (repair.Success && TryParsePlan(repair.Content, out BuilderScenePlan repaired, out _))
                {
                    plan = repaired;
                    NormalizePlan(plan, quality);
                    reply = repair;
                    spatialWarnings = InspectSpatialIntegrity(plan);
                }
            }

            if (token.IsCancellationRequested) return "Blender task cancelled by user.";

            BuildOutcome first = await ExecutePlanAsync(
                plan,
                quality,
                runRoot,
                blenderExe,
                "builder_scene.py",
                "blender.log",
                token
            );

            if (first.Cancelled || token.IsCancellationRequested) return "Blender task cancelled by user.";

            if (!first.Success && first.ExecutionHealthy && !token.IsCancellationRequested)
            {
                activity("[BLENDER V3 QUALITY] build executed but fidelity gate failed; requesting one quality repair");
                task.Phase = AgentTaskPhaseV2.Correcting;
                ProviderReplyV2 qualityReply = await CompleteAsync(
                    task,
                    BuildSystemPrompt(version),
                    BuildQualityRepairPrompt(goal, plan, first.Topology, quality),
                    token
                );
                if (qualityReply.Success && TryParsePlan(qualityReply.Content, out BuilderScenePlan qualityPlan, out _))
                {
                    NormalizePlan(qualityPlan, quality);
                    List<string> qualitySpatial = InspectSpatialIntegrity(qualityPlan);
                    if (qualitySpatial.Count == 0)
                    {
                        BuildOutcome second = await ExecutePlanAsync(
                            qualityPlan,
                            quality,
                            runRoot,
                            blenderExe,
                            "builder_scene_quality_retry.py",
                            "blender_quality_retry.log",
                            token
                        );
                        if (second.Success)
                        {
                            plan = qualityPlan;
                            first = second;
                            reply = qualityReply;
                        }
                    }
                }
            }

            if (!first.Success)
            {
                return "Blender V3 build failed final verification.\nLog: " + first.LogPath
                    + "\nexit=" + first.ExitCode
                    + ", blend=" + first.BlendExists
                    + ", prefabBundle=" + first.SceneBundleExists
                    + ", exports=" + first.ExportsOk
                    + ", topology=" + first.TopologyOk
                    + ", quality=" + first.QualityOk
                    + "\n" + Compact(first.Output, 1800);
            }

            string? manifest = Handoff(plan, first.RuntimeAssets, first.Topology, first.SceneBundlePath);
            int totalTris = first.Topology.Sum(t => t.Triangles);
            int minScore = first.Topology.Count == 0 ? 0 : first.Topology.Min(t => t.Score);

            activity("[BLENDER V3 VERIFY] full Blender-authored prefab bundle passed · " + totalTris + " tris · min " + minScore + "/100");

            StringBuilder result = new StringBuilder();
            result.AppendLine(string.IsNullOrWhiteSpace(plan.Summary) ? "Blender-authored scene prefab created." : plan.Summary);
            result.AppendLine("Engine: Blender V3 deterministic builder + Blender-authored final layout");
            result.AppendLine("Quality: " + quality);
            result.AppendLine("Assets: " + plan.Assets.Count + " reusable model(s), " + plan.Instances.Count + " assembled instance(s).");
            result.AppendLine("Topology: " + totalTris + " triangles total, minimum score " + minScore + "/100.");
            result.AppendLine("Blend: " + first.BlendPath);
            result.AppendLine("Final prefab FBX: " + first.SceneBundlePath);
            if (!string.IsNullOrWhiteSpace(manifest)) result.AppendLine("Unity prefab handoff: " + manifest);
            result.AppendLine("Provider: " + reply.Provider + " / " + reply.Model);
            return result.ToString().Trim();
        }

        private async Task<BuildOutcome> ExecutePlanAsync(
            BuilderScenePlan plan,
            string quality,
            string runRoot,
            string blenderExe,
            string scriptName,
            string logName,
            CancellationToken token
        )
        {
            string safeScene = Safe(plan.SceneName);
            string blendPath = Path.Combine(runRoot, safeScene + ".blend");
            string sceneBundlePath = Path.Combine(runRoot, safeScene + "_Prefab.fbx");
            string scriptPath = Path.Combine(runRoot, scriptName);
            string logPath = Path.Combine(runRoot, logName);

            List<RuntimeAsset> runtimeAssets = plan.Assets.Select(a => new RuntimeAsset
            {
                Plan = a,
                ExportPath = Path.Combine(runRoot, Safe(a.AssetName) + ".fbx")
            }).ToList();

            string generated = BlenderDeterministicBuilder.BuildPython(
                plan.Assets.Select(a => a.Builder),
                quality
            );

            File.WriteAllText(
                scriptPath,
                BuildExecutableScript(generated, blendPath, sceneBundlePath, runtimeAssets, plan),
                new UTF8Encoding(false)
            );

            activity("[BLENDER V3] building assets + final Blender scene hierarchy");
            ProcessResult execution = await RunAsync(blenderExe, scriptPath, logPath, token);

            if (execution.Cancelled || token.IsCancellationRequested)
            {
                return new BuildOutcome { Cancelled = true, LogPath = logPath, Output = execution.Output };
            }

            List<Topology> topology = ParseTopology(execution.Output);
            bool exportsOk = runtimeAssets.All(a => File.Exists(a.ExportPath));
            bool topologyOk = topology.Count == runtimeAssets.Count && topology.All(t => t.Triangles > 0 && t.Score >= 60);
            bool qualityOk = PassQualityGate(plan, topology, quality);
            bool blendExists = File.Exists(blendPath);
            bool sceneBundleExists = File.Exists(sceneBundlePath);
            bool executionHealthy = execution.ExitCode == 0 && !ContainsPythonFailure(execution.Output) && blendExists && exportsOk && sceneBundleExists;
            bool success = executionHealthy && topologyOk && qualityOk;

            return new BuildOutcome
            {
                Success = success,
                ExecutionHealthy = executionHealthy,
                ExitCode = execution.ExitCode,
                Output = execution.Output,
                LogPath = logPath,
                BlendPath = blendPath,
                BlendExists = blendExists,
                SceneBundlePath = sceneBundlePath,
                SceneBundleExists = sceneBundleExists,
                ExportsOk = exportsOk,
                TopologyOk = topologyOk,
                QualityOk = qualityOk,
                RuntimeAssets = runtimeAssets,
                Topology = topology
            };
        }

        private async Task<ProviderReplyV2> CompleteAsync(
            AgentTaskStateV2 task,
            string system,
            string user,
            CancellationToken token
        )
        {
            if (primary.IsConfigured)
            {
                task.ActiveProvider = primary.Name;
                task.ModelCalls++;
                activity("[V2 MODEL] " + primary.Name + " / " + primary.ModelName + " call " + task.ModelCalls);
                ProviderReplyV2 r = await primary.CompleteAsync(system, user, token);
                if (r.Success || r.StatusCode == 499) return r;
                activity("[V2 PROVIDER] Blender InclusionAI unavailable; using free fallback chain");
            }
            return await providers.CompleteAsync(task, system, user, token);
        }

        private static string BuildSystemPrompt(string version)
        {
            return "You are a production 3D asset architect. You DO NOT write Python or bpy. Target runtime is " + version + ". Return strict JSON only. "
                + "Schema: {\"scene_name\":\"GasStation\",\"summary\":\"short\",\"assets\":[{\"asset_name\":\"LightPole\",\"root_object\":\"AIA_LightPole\",\"target_triangles\":3500,\"materials\":[{\"name\":\"Metal\",\"color\":[0.15,0.15,0.15,1],\"metallic\":0.7,\"roughness\":0.35}],\"parts\":[{\"type\":\"cylinder\",\"name\":\"Pole\",\"parent\":\"\",\"position\":[0,0,2.5],\"rotation\":[0,0,0],\"dimensions\":[0.22,0.22,5],\"material\":\"Metal\",\"vertices\":48,\"bevel\":0.02,\"bevel_segments\":3,\"shade_smooth\":true},{\"type\":\"cube\",\"name\":\"Arm\",\"parent\":\"Pole\",\"position\":[0,0,2.35],\"rotation\":[0,0,0],\"dimensions\":[1.2,0.12,0.12],\"material\":\"Metal\",\"bevel\":0.03,\"bevel_segments\":3,\"shade_smooth\":false}]}],\"instances\":[{\"asset_name\":\"LightPole\",\"name\":\"LightPole_01\",\"position\":[4,0,8],\"rotation\":[0,0,0],\"scale\":[1,1,1]}]}. "
                + "Allowed part types only: cube, plane, cylinder, cone, sphere, uv_sphere, torus. Allowed fields: name, parent, position, rotation, dimensions, material, radius, radius2, depth, vertices, major_segments, minor_segments, bevel, bevel_segments, shade_smooth. "
                + "PART COORDINATES are Blender-local Z-up. If parent is non-empty, position/rotation are LOCAL TO THAT PARENT. Use parent relationships for attached structures: lamp housing -> arm -> pole, nozzle -> pump body, canopy fascia -> canopy roof, handles -> doors, etc. Attached parts must physically touch or overlap their parent enough to look constructed, never float meters away. "
                + "INSTANCE positions are Unity world coordinates [x,y,z]. The host converts them into Blender coordinates and exports the entire assembled hierarchy as one final prefab FBX, so you must design the COMPLETE scene composition here. "
                + "INSTANCE scale should ALWAYS be [1,1,1]. If an object needs a different physical size, make a correctly sized unique asset; never use scene-instance downscaling as a layout shortcut. "
                + "Create up to 12 reusable assets and 48 instances. Reuse identical assets through instances. Every major environment request should include enough reusable architecture and props to read clearly as the requested place. "
                + "For AA quality, target production-ready medium-high detail: important props commonly 2k-8k triangles, hero architecture commonly 8k-25k triangles, and a complete multi-asset environment should normally exceed 15k triangles before instancing. Spend geometry on silhouette, bevels, curved forms, frames, trim, panels, handles, housings, supports, seams and era-specific details. Do not fake AA with a few primitive boxes and do not inflate invisible geometry. "
                + "Every asset root stays at local origin. Do not output code, nodes, world settings, lights, cameras, file paths, save/export calls or unsupported operations. No markdown and no prose outside JSON.";
        }

        private static string BuildUserPrompt(string goal)
        {
            return "Turn this ONE instruction into a complete reusable asset kit AND a finished scene composition. The host will build every asset deterministically in Blender, spatially verify it, assemble all instances in Blender, export the complete hierarchy as one prefab FBX, and Unity will import that prefab without rebuilding the layout.\nUSER GOAL:\n" + goal;
        }

        private static string BuildSpatialRepairPrompt(
            string goal,
            BuilderScenePlan plan,
            List<string> warnings
        )
        {
            return "Repair ONLY structural/spatial awareness problems in this complete builder plan and return the full strict JSON again. Preserve style, asset set and intended scene composition. Use parent relationships so attached sub-parts use sensible local offsets and physically connect. Do not solve attachment problems by shrinking whole instances. All instance scales must remain [1,1,1].\nUSER GOAL:\n"
                + goal
                + "\nSTRUCTURAL WARNINGS:\n- " + string.Join("\n- ", warnings.Take(24))
                + "\nCURRENT PLAN:\n" + Compact(JsonSerializer.Serialize(plan), 16000);
        }

        private static string BuildQualityRepairPrompt(
            string goal,
            BuilderScenePlan plan,
            List<Topology> topology,
            string quality
        )
        {
            int total = topology.Sum(t => t.Triangles);
            return "The deterministic build is technically valid but did not meet the requested " + quality + " visual-fidelity geometry gate. Return the COMPLETE builder JSON again with substantially richer PURPOSEFUL geometry while preserving layout and [1,1,1] instance scales. Add silhouette detail, bevel-supporting forms, trim, frames, panels, supports, housings, handles, seams and smoother curved components where visually meaningful. Use parent relationships for attached sub-parts. Do not add hidden/random geometry.\nUSER GOAL:\n"
                + goal
                + "\nCURRENT TOTAL TRIANGLES: " + total
                + "\nTOPOLOGY: " + JsonSerializer.Serialize(topology)
                + "\nCURRENT PLAN:\n" + Compact(JsonSerializer.Serialize(plan), 16000);
        }

        private static void NormalizePlan(BuilderScenePlan plan, string quality)
        {
            foreach (BuilderInstance instance in plan.Instances)
            {
                instance.Scale = new[] { 1f, 1f, 1f };
            }

            foreach (BuilderAssetPlan asset in plan.Assets)
            {
                int minimumTarget = quality.Equals("AA", StringComparison.OrdinalIgnoreCase) ? 2200
                    : quality.Equals("High", StringComparison.OrdinalIgnoreCase) ? 1200
                    : quality.Equals("Low", StringComparison.OrdinalIgnoreCase) ? 120
                    : 500;
                asset.TargetTriangles = Math.Max(asset.TargetTriangles, minimumTarget);

                foreach (BlenderBuilderPart part in asset.Builder.Parts)
                {
                    if (quality.Equals("AA", StringComparison.OrdinalIgnoreCase))
                    {
                        if (part.Type is "cylinder" or "cone" or "sphere" or "uv_sphere")
                            part.Vertices = Math.Max(part.Vertices, 48);
                        if (part.Type == "torus")
                        {
                            part.MajorSegments = Math.Max(part.MajorSegments, 48);
                            part.MinorSegments = Math.Max(part.MinorSegments, 16);
                        }
                        if (part.Bevel > 0f) part.BevelSegments = Math.Max(part.BevelSegments, 3);
                    }
                }
            }
        }

        private static List<string> InspectSpatialIntegrity(BuilderScenePlan plan)
        {
            List<string> warnings = new();
            foreach (BuilderAssetPlan asset in plan.Assets)
            {
                Dictionary<string, BlenderBuilderPart> byName = asset.Builder.Parts
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                    .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

                foreach (BlenderBuilderPart part in asset.Builder.Parts)
                {
                    float ownMax = MaxDim(part.Dimensions);
                    float distance = Length(part.Position);

                    if (!string.IsNullOrWhiteSpace(part.ParentPart))
                    {
                        if (!byName.TryGetValue(part.ParentPart, out BlenderBuilderPart? parent))
                        {
                            warnings.Add(asset.AssetName + ": part '" + part.Name + "' references missing parent '" + part.ParentPart + "'.");
                            continue;
                        }
                        float parentMax = Math.Max(0.1f, MaxDim(parent.Dimensions));
                        float allowed = Math.Max(1.25f, parentMax * 1.35f + ownMax * 0.75f);
                        if (distance > allowed)
                            warnings.Add(asset.AssetName + ": attached part '" + part.Name + "' is " + distance.ToString("0.##", CultureInfo.InvariantCulture) + "m from parent '" + part.ParentPart + "' in local space; likely floating.");
                    }
                }

                List<BlenderBuilderPart> rootParts = asset.Builder.Parts.Where(p => string.IsNullOrWhiteSpace(p.ParentPart)).ToList();
                if (rootParts.Count > 1)
                {
                    BlenderBuilderPart anchor = rootParts.OrderByDescending(p => Volume(p.Dimensions)).First();
                    float anchorMax = Math.Max(0.25f, MaxDim(anchor.Dimensions));
                    foreach (BlenderBuilderPart part in rootParts)
                    {
                        if (ReferenceEquals(part, anchor)) continue;
                        float d = Distance(part.Position, anchor.Position);
                        float allowed = Math.Max(2.0f, anchorMax * 1.8f + MaxDim(part.Dimensions));
                        if (d > allowed)
                            warnings.Add(asset.AssetName + ": root-level part '" + part.Name + "' sits " + d.ToString("0.##", CultureInfo.InvariantCulture) + "m away from main body '" + anchor.Name + "'; attach/reposition it if it belongs to the same object.");
                    }
                }
            }
            return warnings;
        }

        private static bool PassQualityGate(BuilderScenePlan plan, List<Topology> topology, string quality)
        {
            if (topology.Count == 0) return false;
            int total = topology.Sum(t => t.Triangles);
            int floor;
            if (quality.Equals("AA", StringComparison.OrdinalIgnoreCase))
                floor = plan.Assets.Count >= 4 ? 15000 : plan.Assets.Count >= 2 ? 7000 : 1800;
            else if (quality.Equals("High", StringComparison.OrdinalIgnoreCase))
                floor = plan.Assets.Count >= 4 ? 7000 : plan.Assets.Count >= 2 ? 3000 : 900;
            else if (quality.Equals("Low", StringComparison.OrdinalIgnoreCase))
                floor = 1;
            else
                floor = plan.Assets.Count >= 4 ? 2500 : 500;

            if (total < floor) return false;

            Dictionary<string, BuilderAssetPlan> assets = plan.Assets.ToDictionary(a => a.AssetName, StringComparer.OrdinalIgnoreCase);
            foreach (Topology item in topology)
            {
                if (!assets.TryGetValue(item.AssetName, out BuilderAssetPlan? asset)) continue;
                int target = Math.Max(1, asset.TargetTriangles);
                if (quality.Equals("AA", StringComparison.OrdinalIgnoreCase) && item.Triangles < target * 0.30f)
                    return false;
            }
            return true;
        }

        private static bool TryParsePlan(string text, out BuilderScenePlan plan, out string error)
        {
            plan = new BuilderScenePlan();
            error = "";
            try
            {
                string json = AgentJsonV2.ExtractObject(text);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                plan.SceneName = Str(root, "scene_name");
                plan.Summary = Str(root, "summary");

                if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement a in assets.EnumerateArray())
                    {
                        if (plan.Assets.Count >= MaxAssets) break;
                        BuilderAssetPlan asset = new BuilderAssetPlan
                        {
                            AssetName = Str(a, "asset_name"),
                            RootObject = Str(a, "root_object"),
                            TargetTriangles = Int(a, "target_triangles")
                        };
                        if (string.IsNullOrWhiteSpace(asset.AssetName) || string.IsNullOrWhiteSpace(asset.RootObject)) continue;
                        asset.Builder.RootObject = asset.RootObject;

                        if (a.TryGetProperty("materials", out JsonElement mats) && mats.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement m in mats.EnumerateArray().Take(32))
                            {
                                string name = Str(m, "name");
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                asset.Builder.Materials.Add(new BlenderBuilderMaterial
                                {
                                    Name = name,
                                    Color = Vec4(m, "color", new[] { 0.5f, 0.5f, 0.5f, 1f }),
                                    Metallic = Num(m, "metallic", 0f),
                                    Roughness = Num(m, "roughness", 0.6f)
                                });
                            }
                        }

                        if (a.TryGetProperty("parts", out JsonElement parts) && parts.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement p in parts.EnumerateArray().Take(240))
                            {
                                string type = Str(p, "type").ToLowerInvariant();
                                if (!AllowedType(type)) continue;
                                asset.Builder.Parts.Add(new BlenderBuilderPart
                                {
                                    Type = type,
                                    Name = Str(p, "name"),
                                    ParentPart = Str(p, "parent"),
                                    Material = Str(p, "material"),
                                    Position = Vec3(p, "position", new[] { 0f, 0f, 0f }),
                                    Rotation = Vec3(p, "rotation", new[] { 0f, 0f, 0f }),
                                    Dimensions = Vec3(p, "dimensions", new[] { 1f, 1f, 1f }),
                                    Radius = Num(p, "radius", 0.5f),
                                    Radius2 = Num(p, "radius2", 0.25f),
                                    Depth = Num(p, "depth", 1f),
                                    Vertices = Int(p, "vertices", 24),
                                    MajorSegments = Int(p, "major_segments", 32),
                                    MinorSegments = Int(p, "minor_segments", 12),
                                    Bevel = Num(p, "bevel", 0f),
                                    BevelSegments = Int(p, "bevel_segments", 2),
                                    ShadeSmooth = Bool(p, "shade_smooth")
                                });
                            }
                        }

                        if (asset.Builder.Parts.Count > 0) plan.Assets.Add(asset);
                    }
                }

                if (plan.Assets.Count == 0)
                {
                    error = "assets missing or no valid builder parts";
                    return false;
                }

                HashSet<string> known = new(plan.Assets.Select(a => a.AssetName), StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("instances", out JsonElement instances) && instances.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement i in instances.EnumerateArray())
                    {
                        if (plan.Instances.Count >= MaxInstances) break;
                        string assetName = Str(i, "asset_name");
                        if (!known.Contains(assetName)) continue;
                        plan.Instances.Add(new BuilderInstance
                        {
                            AssetName = assetName,
                            Name = Str(i, "name"),
                            Position = Vec3(i, "position", new[] { 0f, 0f, 0f }),
                            Rotation = Vec3(i, "rotation", new[] { 0f, 0f, 0f }),
                            Scale = new[] { 1f, 1f, 1f }
                        });
                    }
                }

                if (plan.Instances.Count == 0)
                {
                    foreach (BuilderAssetPlan a in plan.Assets)
                        plan.Instances.Add(new BuilderInstance
                        {
                            AssetName = a.AssetName,
                            Name = a.AssetName,
                            Position = new[] { 0f, 0f, 0f },
                            Rotation = new[] { 0f, 0f, 0f },
                            Scale = new[] { 1f, 1f, 1f }
                        });
                }

                if (string.IsNullOrWhiteSpace(plan.SceneName)) plan.SceneName = "AI_Scene";
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string BuildExecutableScript(
            string generated,
            string blendPath,
            string sceneBundlePath,
            List<RuntimeAsset> assets,
            BuilderScenePlan plan
        )
        {
            StringBuilder s = new StringBuilder();
            s.AppendLine("import bpy, json, math, traceback");
            s.AppendLine("try:");
            s.AppendLine("    bpy.ops.object.select_all(action='SELECT')");
            s.AppendLine("    bpy.ops.object.delete(use_global=False)");
            foreach (string line in generated.Replace("\r\n", "\n").Split('\n')) s.AppendLine("    " + line);

            s.AppendLine("    depsgraph = bpy.context.evaluated_depsgraph_get()");
            s.AppendLine("    specs = [");
            foreach (RuntimeAsset a in assets)
                s.AppendLine("        {'name':" + Py(a.Plan.AssetName) + ",'root':" + Py(a.Plan.RootObject) + ",'path':" + Py(a.ExportPath) + ",'target':" + Math.Max(0, a.Plan.TargetTriangles) + "},");
            s.AppendLine("    ]");
            s.AppendLine("    topo=[]");
            s.AppendLine("    for spec in specs:");
            s.AppendLine("        root=bpy.data.objects.get(spec['root'])");
            s.AppendLine("        item={'asset_name':spec['name'],'triangles':0,'score':100}");
            s.AppendLine("        if root is None: item['score']=0; topo.append(item); continue");
            s.AppendLine("        objs=[]; stack=[root]");
            s.AppendLine("        while stack:");
            s.AppendLine("            o=stack.pop()");
            s.AppendLine("            if o in objs: continue");
            s.AppendLine("            objs.append(o); stack.extend(list(o.children))");
            s.AppendLine("        for o in [x for x in objs if x.type=='MESH']:");
            s.AppendLine("            eo=o.evaluated_get(depsgraph); mesh=eo.to_mesh()");
            s.AppendLine("            try:");
            s.AppendLine("                mesh.calc_loop_triangles(); item['triangles'] += len(mesh.loop_triangles)");
            s.AppendLine("            finally:");
            s.AppendLine("                eo.to_mesh_clear()");
            s.AppendLine("        if item['triangles']<=0: item['score']=0");
            s.AppendLine("        if spec['target']>0 and item['triangles']>0:");
            s.AppendLine("            ratio=item['triangles']/float(spec['target'])");
            s.AppendLine("            if ratio<0.25: item['score']-=35");
            s.AppendLine("            elif ratio<0.45: item['score']-=15");
            s.AppendLine("            elif ratio>4.0: item['score']-=10");
            s.AppendLine("        bpy.ops.object.select_all(action='DESELECT')");
            s.AppendLine("        for o in objs: o.select_set(True)");
            s.AppendLine("        bpy.context.view_layer.objects.active=root");
            s.AppendLine("        bpy.ops.export_scene.fbx(filepath=spec['path'], use_selection=True, apply_unit_scale=True, axis_forward='-Z', axis_up='Y')");
            s.AppendLine("        topo.append(item)");

            s.AppendLine("    # Assemble the FINAL environment in Blender so Unity never guesses layout or scale.");
            s.AppendLine("    scene_root=bpy.data.objects.new(" + Py("AIA_SCENE_" + Safe(plan.SceneName)) + ", None)");
            s.AppendLine("    bpy.context.scene.collection.objects.link(scene_root)");
            s.AppendLine("    def clone_tree(src, parent):");
            s.AppendLine("        c=src.copy()");
            s.AppendLine("        if getattr(src,'data',None) is not None: c.data=src.data.copy()");
            s.AppendLine("        bpy.context.scene.collection.objects.link(c)");
            s.AppendLine("        c.parent=parent");
            s.AppendLine("        c.location=src.location.copy(); c.rotation_euler=src.rotation_euler.copy(); c.scale=src.scale.copy()");
            s.AppendLine("        for child in list(src.children): clone_tree(child,c)");
            s.AppendLine("        return c");

            foreach (BuilderInstance instance in plan.Instances)
            {
                float[] p = instance.Position;
                float[] r = instance.Rotation;
                string instanceName = string.IsNullOrWhiteSpace(instance.Name) ? instance.AssetName : instance.Name;
                s.AppendLine("    src=bpy.data.objects.get(" + Py(plan.Assets.First(a => a.AssetName.Equals(instance.AssetName, StringComparison.OrdinalIgnoreCase)).RootObject) + ")");
                s.AppendLine("    if src is not None:");
                s.AppendLine("        inst=clone_tree(src,scene_root)");
                s.AppendLine("        inst.name=" + Py(instanceName));
                s.AppendLine("        inst.location=(" + F(p[0]) + "," + F(-p[2]) + "," + F(p[1]) + ")");
                s.AppendLine("        inst.rotation_euler=(math.radians(" + F(r[0]) + "), math.radians(" + F(-r[2]) + "), math.radians(" + F(r[1]) + "))");
                s.AppendLine("        inst.scale=(1.0,1.0,1.0)");
            }

            s.AppendLine("    bpy.ops.wm.save_as_mainfile(filepath=" + Py(blendPath) + ")");
            s.AppendLine("    bpy.ops.object.select_all(action='DESELECT')");
            s.AppendLine("    scene_objs=[]; stack=[scene_root]");
            s.AppendLine("    while stack:");
            s.AppendLine("        o=stack.pop()");
            s.AppendLine("        if o in scene_objs: continue");
            s.AppendLine("        scene_objs.append(o); stack.extend(list(o.children))");
            s.AppendLine("    for o in scene_objs: o.select_set(True)");
            s.AppendLine("    bpy.context.view_layer.objects.active=scene_root");
            s.AppendLine("    bpy.ops.export_scene.fbx(filepath=" + Py(sceneBundlePath) + ", use_selection=True, apply_unit_scale=True, axis_forward='-Z', axis_up='Y')");
            s.AppendLine("    print('AI_TOPOLOGY_JSON:'+json.dumps(topo,separators=(',',':')))");
            s.AppendLine("    print('AI_SCENE_PREFAB_EXPORT_OK')");
            s.AppendLine("except Exception:");
            s.AppendLine("    print('AI_SCENE_PREFAB_EXPORT_FAILED'); traceback.print_exc(); raise");
            return s.ToString();
        }

        private string? Handoff(
            BuilderScenePlan plan,
            List<RuntimeAsset> assets,
            List<Topology> topology,
            string sceneBundlePath
        )
        {
            string root = settings.UnityProjectRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "Assets"))) return null;

            string safeScene = Safe(plan.SceneName);
            string modelDir = Path.Combine(root, "Assets", "AI_Generated", "Models", safeScene);
            string bundleDir = Path.Combine(root, "Assets", "AI_Generated", "SceneBundles");
            string sceneDir = Path.Combine(root, "Assets", "AI_Generated", "Scenes");
            Directory.CreateDirectory(modelDir);
            Directory.CreateDirectory(bundleDir);
            Directory.CreateDirectory(sceneDir);

            foreach (RuntimeAsset a in assets)
            {
                string file = Safe(a.Plan.AssetName) + ".fbx";
                File.Copy(a.ExportPath, Path.Combine(modelDir, file), true);
            }

            string bundleFile = safeScene + "_Prefab.fbx";
            File.Copy(sceneBundlePath, Path.Combine(bundleDir, bundleFile), true);
            string unityPrefabSource = "Assets/AI_Generated/SceneBundles/" + bundleFile;

            string manifest = Path.Combine(sceneDir, safeScene + ".aiscene.json");
            File.WriteAllText(
                manifest,
                JsonSerializer.Serialize(
                    new
                    {
                        version = 3,
                        sceneName = plan.SceneName,
                        rootName = "AI_Generated_" + safeScene,
                        replaceExisting = true,
                        prefabAssetPath = unityPrefabSource,
                        prefabOutputPath = "Assets/AI_Generated/Prefabs/" + safeScene + ".prefab",
                        instances = Array.Empty<object>()
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );
            File.WriteAllText(
                Path.Combine(sceneDir, safeScene + ".topology.json"),
                JsonSerializer.Serialize(topology, new JsonSerializerOptions { WriteIndented = true })
            );

            activity("[BLENDER UNITY] full Blender-authored prefab bundle copied; Unity layout assembly disabled");
            return manifest;
        }

        private async Task<ProcessResult> RunAsync(string exe, string script, string log, CancellationToken token)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--background --python \"" + script + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using Process p = new Process { StartInfo = psi };
            p.Start();
            Task<string> so = p.StandardOutput.ReadToEndAsync();
            Task<string> se = p.StandardError.ReadToEndAsync();
            Task wait = p.WaitForExitAsync();
            Task timeout = Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds));
            Task cancel = Task.Delay(Timeout.InfiniteTimeSpan, token);
            Task done = await Task.WhenAny(wait, timeout, cancel);
            bool to = done == timeout;
            bool ca = done == cancel || token.IsCancellationRequested;
            if (to || ca)
            {
                try { p.Kill(true); } catch { }
                try { await p.WaitForExitAsync(); } catch { }
            }
            string output = await so + "\n" + await se;
            File.WriteAllText(log, output);
            return new ProcessResult(ca ? -4 : to ? -2 : p.ExitCode, output, to, ca);
        }

        private static async Task<string> ProbeAsync(string exe, CancellationToken token)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using Process p = new Process { StartInfo = psi };
                p.Start();
                string o = await p.StandardOutput.ReadLineAsync() ?? "Blender";
                await p.WaitForExitAsync(token);
                return o.Trim();
            }
            catch { return "Blender 3.6 compatible runtime"; }
        }

        private static List<Topology> ParseTopology(string output)
        {
            const string marker = "AI_TOPOLOGY_JSON:";
            string? line = (output ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(x => x.StartsWith(marker, StringComparison.Ordinal));
            if (line == null) return new();
            try
            {
                return JsonSerializer.Deserialize<List<Topology>>(
                    line.Substring(marker.Length),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? new();
            }
            catch { return new(); }
        }

        private static string DetectQuality(string goal)
        {
            string p = (goal ?? "").ToLowerInvariant();
            if (p.Contains("quality profile: aa") || p.Contains("aa quality") || p.Contains("medium-high") || p.Contains("double a")) return "AA";
            if (p.Contains("quality profile: high") || p.Contains("high quality") || p.Contains("high-detail") || p.Contains("high detail")) return "High";
            if (p.Contains("quality profile: low") || p.Contains("low poly") || p.Contains("low-poly") || p.Contains("low detail")) return "Low";
            return "Medium";
        }

        private static bool ContainsPythonFailure(string s) =>
            (s ?? "").Contains("Traceback (most recent call last)", StringComparison.OrdinalIgnoreCase)
            || (s ?? "").Contains("AI_SCENE_PREFAB_EXPORT_FAILED", StringComparison.OrdinalIgnoreCase);

        private static bool AllowedType(string t) => t is "cube" or "plane" or "cylinder" or "cone" or "sphere" or "uv_sphere" or "torus";
        private static string CleanGoal(string p) { string v = (p ?? "").Trim(); return v.StartsWith("/blender ", StringComparison.OrdinalIgnoreCase) ? v.Substring(9).Trim() : v; }
        private static string Safe(string v) { var b = new StringBuilder(); foreach (char c in string.IsNullOrWhiteSpace(v) ? "AI_Scene" : v) b.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'); return b.ToString(); }
        private static string Py(string v) => "'" + (v ?? "").Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        private static string Compact(string v, int n) => string.IsNullOrEmpty(v) ? "" : v.Length <= n ? v : v.Substring(0, n) + "...";
        private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        private static int Int(JsonElement e, string n, int f = 0) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int x) ? Math.Max(0, x) : f;
        private static float Num(JsonElement e, string n, float f) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float x) ? x : f;
        private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b) && b));
        private static float[] Vec3(JsonElement e, string n, float[] f) => VecN(e, n, f, 3);
        private static float[] Vec4(JsonElement e, string n, float[] f) => VecN(e, n, f, 4);
        private static float[] VecN(JsonElement e, string n, float[] f, int count)
        {
            float[] r = f.ToArray();
            if (!e.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Array) return r;
            int i = 0;
            foreach (var c in v.EnumerateArray())
            {
                if (i >= count) break;
                if (c.ValueKind == JsonValueKind.Number && c.TryGetSingle(out float x)) r[i] = x;
                i++;
            }
            return r;
        }

        private static float MaxDim(float[]? d) => d == null || d.Length < 3 ? 1f : Math.Max(Math.Abs(d[0]), Math.Max(Math.Abs(d[1]), Math.Abs(d[2])));
        private static float Volume(float[]? d) => d == null || d.Length < 3 ? 0f : Math.Abs(d[0] * d[1] * d[2]);
        private static float Length(float[]? p) => p == null || p.Length < 3 ? 0f : (float)Math.Sqrt(p[0] * p[0] + p[1] * p[1] + p[2] * p[2]);
        private static float Distance(float[]? a, float[]? b)
        {
            if (a == null || b == null || a.Length < 3 || b.Length < 3) return 0f;
            float x = a[0] - b[0], y = a[1] - b[1], z = a[2] - b[2];
            return (float)Math.Sqrt(x * x + y * y + z * z);
        }
        private static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        private sealed class BuilderScenePlan
        {
            public string SceneName { get; set; } = "";
            public string Summary { get; set; } = "";
            public List<BuilderAssetPlan> Assets { get; set; } = new();
            public List<BuilderInstance> Instances { get; set; } = new();
        }

        private sealed class BuilderAssetPlan
        {
            public string AssetName { get; set; } = "";
            public string RootObject { get; set; } = "";
            public int TargetTriangles { get; set; }
            public BlenderBuilderAsset Builder { get; set; } = new();
        }

        private sealed class BuilderInstance
        {
            public string AssetName { get; set; } = "";
            public string Name { get; set; } = "";
            public float[] Position { get; set; } = new[] { 0f, 0f, 0f };
            public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f };
            public float[] Scale { get; set; } = new[] { 1f, 1f, 1f };
        }

        private sealed class RuntimeAsset
        {
            public BuilderAssetPlan Plan { get; set; } = new();
            public string ExportPath { get; set; } = "";
        }

        private sealed class Topology
        {
            public string AssetName { get; set; } = "";
            public int Triangles { get; set; }
            public int Score { get; set; }
        }

        private sealed class BuildOutcome
        {
            public bool Success { get; set; }
            public bool ExecutionHealthy { get; set; }
            public bool Cancelled { get; set; }
            public int ExitCode { get; set; }
            public string Output { get; set; } = "";
            public string LogPath { get; set; } = "";
            public string BlendPath { get; set; } = "";
            public bool BlendExists { get; set; }
            public string SceneBundlePath { get; set; } = "";
            public bool SceneBundleExists { get; set; }
            public bool ExportsOk { get; set; }
            public bool TopologyOk { get; set; }
            public bool QualityOk { get; set; }
            public List<RuntimeAsset> RuntimeAssets { get; set; } = new();
            public List<Topology> Topology { get; set; } = new();
        }

        private sealed record ProcessResult(int ExitCode, string Output, bool TimedOut, bool Cancelled);
    }
}
