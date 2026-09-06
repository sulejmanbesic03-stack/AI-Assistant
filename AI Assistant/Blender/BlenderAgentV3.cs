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
        private readonly BlenderVisualQualityGate visualQuality;

        public BlenderAgentV3(RuntimeSettings settings, Action<string> activity)
        {
            this.settings = settings;
            this.activity = activity;
            providers = new ProviderRouterV2(activity);
            primary = new GeminiProviderV2();
            visualQuality = new BlenderVisualQualityGate(activity);
        }

        public bool ShouldHandle(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            return p == "/blender" || p.StartsWith("/blender ") || p.StartsWith("/blender:")
                || p == "blender" || p.StartsWith("blender ") || p.StartsWith("blender:")
                || p.Contains(" blender ")
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

            await ApplyVisualQualityAsync(first, goal, quality, token);
            plan.LastVisualFeedback = first.VisualFeedback;

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
                        await ApplyVisualQualityAsync(second, goal, quality, token);
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
                + ", visual=" + first.VisualQualityOk
                + "\n" + Compact(first.Output, 1800);
            }

            HandoffResult handoff = await HandoffAsync(plan, first.RuntimeAssets, first.Topology, first.SceneBundlePath, token);
            if (!handoff.Success)
            {
                return "Blender build passed local verification, but Unity handoff failed: " + handoff.Message;
            }
            int totalTris = first.Topology.Sum(t => t.Triangles);
            int minScore = first.Topology.Count == 0 ? 0 : first.Topology.Min(t => t.Score);

            activity("[BLENDER V3 VERIFY] full Blender-authored prefab bundle passed · " + totalTris + " tris · min " + minScore + "/100");

            StringBuilder result = new StringBuilder();
            result.AppendLine(string.IsNullOrWhiteSpace(plan.Summary) ? "Blender-authored scene prefab created." : plan.Summary);
            result.AppendLine("Engine: Blender V3 deterministic builder + Blender-authored final layout");
            result.AppendLine("Quality: " + quality);
            result.AppendLine("Assets: " + plan.Assets.Count + " reusable model(s), " + plan.Instances.Count + " assembled instance(s).");
            result.AppendLine("Topology: " + totalTris + " triangles total, minimum score " + minScore + "/100.");
            if (first.VisualReviewAvailable)
                result.AppendLine("Visual QA: " + first.VisualScore + "/100 · " + Compact(first.VisualFeedback, 320));
            result.AppendLine("Blend: " + first.BlendPath);
            result.AppendLine("Final prefab FBX: " + first.SceneBundlePath);
            if (!string.IsNullOrWhiteSpace(handoff.ManifestPath)) result.AppendLine("Unity prefab handoff: " + handoff.ManifestPath);
            result.AppendLine("Provider: " + reply.Provider + " / " + reply.Model);
            return result.ToString().Trim();
        }

        private async Task ApplyVisualQualityAsync(
            BuildOutcome outcome,
            string goal,
            string quality,
            CancellationToken token
        )
        {
            bool requiresVisual = quality.Equals("High", StringComparison.OrdinalIgnoreCase)
                || quality.Equals("AA", StringComparison.OrdinalIgnoreCase);
            if (!requiresVisual || !outcome.ExecutionHealthy || token.IsCancellationRequested)
            {
                outcome.VisualQualityOk = !requiresVisual || outcome.QualityOk;
                outcome.Success = outcome.ExecutionHealthy && outcome.TopologyOk && outcome.QualityOk && outcome.VisualQualityOk;
                return;
            }

            BlenderVisualQualityResult review = await visualQuality.EvaluateAsync(
                goal,
                quality,
                JsonSerializer.Serialize(outcome.Topology),
                outcome.PreviewPaths,
                token
            );
            outcome.VisualReviewAvailable = review.Available;
            outcome.VisualScore = review.Score;
            outcome.VisualFeedback = review.Feedback;
            outcome.VisualQualityOk = review.Available && review.Passed;
            outcome.QualityOk = outcome.QualityOk && outcome.VisualQualityOk;
            outcome.Success = outcome.ExecutionHealthy && outcome.TopologyOk && outcome.QualityOk;

            activity(review.Available
                ? "[BLENDER VISUAL QA] " + review.Score + "/100 · " + (review.Passed ? "PASS" : "REJECT")
                : "[BLENDER VISUAL QA] unavailable · High/AA output will not be claimed or imported");
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
            List<string> previewPaths = new()
            {
                Path.Combine(runRoot, safeScene + "_preview_iso.png"),
                Path.Combine(runRoot, safeScene + "_preview_front.png"),
                Path.Combine(runRoot, safeScene + "_preview_side.png")
            };
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
                BuildExecutableScript(generated, blendPath, sceneBundlePath, previewPaths, runtimeAssets, plan),
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
                Topology = topology,
                PreviewPaths = previewPaths
            };
        }

        private async Task<ProviderReplyV2> CompleteAsync(
            AgentTaskStateV2 task,
            string system,
            string user,
            CancellationToken token
        )
        {
            int maxCalls = int.TryParse(Environment.GetEnvironmentVariable("AI_MAX_MODEL_CALLS"), out int configuredCalls)
                ? Math.Clamp(configuredCalls, 1, 20)
                : 8;
            if (primary.IsConfigured)
            {
                if (task.ModelCalls >= maxCalls)
                    return new ProviderReplyV2 { Success = false, Error = "Blender model-call budget exhausted (AI_MAX_MODEL_CALLS=" + maxCalls + ")." };
                task.ActiveProvider = primary.Name;
                task.ModelCalls++;
                activity("[V2 MODEL] " + primary.Name + " / " + primary.ModelName + " call " + task.ModelCalls);
                ProviderReplyV2 r = await primary.CompleteAsync(system, user, token);
                if (r.Success || r.StatusCode == 499) return r;
                activity("[V2 PROVIDER] Blender Gemini unavailable; using fallback chain");
            }
            return await providers.CompleteAsync(task, system, user, token);
        }

        private static string BuildSystemPrompt(string version)
        {
            return "You are a production 3D asset architect with strong spatial and anatomical reasoning. You DO NOT write Python or bpy. Target runtime is " + version + ". Return strict JSON only. "
                + "Schema: {\"request_kind\":\"environment|hard_surface|character|prop\",\"scene_name\":\"GasStation\",\"summary\":\"short\",\"assets\":[{\"asset_name\":\"LightPole\",\"root_object\":\"AIA_LightPole\",\"target_triangles\":3500,\"materials\":[{\"name\":\"Metal\",\"color\":[0.15,0.15,0.15,1],\"metallic\":0.7,\"roughness\":0.35}],\"parts\":[{\"type\":\"cylinder\",\"name\":\"Pole\",\"parent\":\"\",\"position\":[0,0,2.5],\"rotation\":[0,0,0],\"dimensions\":[0.22,0.22,5],\"material\":\"Metal\",\"vertices\":48,\"bevel\":0.02,\"bevel_segments\":3,\"shade_smooth\":true,\"subdivision_levels\":0}]}],\"instances\":[{\"asset_name\":\"LightPole\",\"name\":\"LightPole_01\",\"position\":[4,0,8],\"rotation\":[0,0,0],\"scale\":[1,1,1]}]}. "
                + "Allowed part types: cube, plane, cylinder, cone, sphere, uv_sphere, torus, curve, extruded_polygon, mesh, skin, text. Common fields: name, parent, position, rotation, dimensions, material, radius, radius2, depth, vertices, major_segments, minor_segments, bevel, bevel_segments, shade_smooth, subdivision_levels. curve requires points:[[x,y,z],...] and radius. extruded_polygon requires at least 3 points:[[x,y],...] and extrude. mesh requires points and faces:[[index,...],...]. skin requires points, edges:[[a,b],...], radii:[...] and subdivision_levels; use it as a connected organic base. text uses text and extrude. "
                + "PART COORDINATES are Blender-local Z-up. If parent is non-empty, position/rotation are LOCAL TO THAT PARENT. Use parent relationships for attached structures: lamp housing -> arm -> pole, nozzle -> pump body, canopy fascia -> canopy roof, handles -> doors, etc. Attached parts must physically touch or overlap their parent enough to look constructed, never float meters away. "
                + "INSTANCE positions are Unity world coordinates [x,y,z]. The host converts them into Blender coordinates and exports the entire assembled hierarchy as one final prefab FBX, so you must design the COMPLETE scene composition here. "
                + "INSTANCE scale should ALWAYS be [1,1,1]. If an object needs a different physical size, make a correctly sized unique asset; never use scene-instance downscaling as a layout shortcut. "
                + "Create up to 12 reusable assets and 48 instances. Reuse identical assets through instances. Every major environment request should include enough reusable architecture and props to read clearly as the requested place. "
                + "For AA quality, target production-ready medium-high detail: important props commonly 2k-8k triangles, hero architecture commonly 8k-25k triangles, and a complete multi-asset environment should normally exceed 15k triangles before instancing. Spend geometry on silhouette, bevels, curved forms, frames, trim, panels, handles, housings, supports, seams and era-specific details. Do not fake AA with a few primitive boxes and do not inflate invisible geometry. "
                + "CHARACTERS: never build a disconnected mannequin from floating cubes/cylinders/spheres. Use one connected skin or authored mesh as the body base, human proportions (about 7.5 heads tall), bilateral symmetry, joined shoulders/hips/limbs, recognizable hands/feet/head silhouette, then layer clothing, hair, facial masses and accessories. A character without a connected body base is invalid. "
                + "ENVIRONMENTS: first establish a readable footprint and functional zones. Keep buildings, canopy, pumps, roads and props grounded; preserve believable clearance and relationships. Do not stack all instances at the origin. "
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
                + "\nCURRENT PLAN:\n" + Compact(SerializePlanForModel(plan), 28000);
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
                + "\nVISUAL QA FEEDBACK: " + Compact(plan.LastVisualFeedback, 2400)
                + "\nTOPOLOGY: " + JsonSerializer.Serialize(topology)
                + "\nCURRENT PLAN:\n" + Compact(SerializePlanForModel(plan), 28000);
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

            if (plan.Instances.Count > 1)
            {
                Dictionary<string, int> positions = new(StringComparer.Ordinal);
                foreach (BuilderInstance instance in plan.Instances)
                {
                    float[] p = instance.Position;
                    string key = Math.Round(p[0], 1).ToString(CultureInfo.InvariantCulture) + "|"
                        + Math.Round(p[1], 1).ToString(CultureInfo.InvariantCulture) + "|"
                        + Math.Round(p[2], 1).ToString(CultureInfo.InvariantCulture);
                    positions[key] = positions.TryGetValue(key, out int count) ? count + 1 : 1;
                    if (Math.Abs(p[0]) > 250f || Math.Abs(p[1]) > 250f || Math.Abs(p[2]) > 250f)
                        warnings.Add("Instance '" + instance.Name + "' uses an extreme world position; keep the composition inside a believable footprint.");
                }
                foreach (KeyValuePair<string, int> duplicate in positions.Where(pair => pair.Value > 1))
                    warnings.Add(duplicate.Value + " instances share effectively the same world position " + duplicate.Key + "; separate them unless exact stacking is intentional.");

                if (positions.Count <= Math.Max(1, plan.Instances.Count / 3))
                    warnings.Add("Most scene instances collapse onto too few positions; create a functional readable layout instead of a central pile.");
            }
            return warnings;
        }

        private static string SerializePlanForModel(BuilderScenePlan plan)
        {
            object dto = new
            {
                request_kind = plan.RequestKind,
                scene_name = plan.SceneName,
                summary = plan.Summary,
                assets = plan.Assets.Select(asset => new
                {
                    asset_name = asset.AssetName,
                    root_object = asset.RootObject,
                    target_triangles = asset.TargetTriangles,
                    materials = asset.Builder.Materials.Select(material => new
                    {
                        name = material.Name,
                        color = material.Color,
                        metallic = material.Metallic,
                        roughness = material.Roughness
                    }),
                    parts = asset.Builder.Parts.Select(part => new
                    {
                        type = part.Type,
                        name = part.Name,
                        parent = part.ParentPart,
                        position = part.Position,
                        rotation = part.Rotation,
                        dimensions = part.Dimensions,
                        material = part.Material,
                        radius = part.Radius,
                        radius2 = part.Radius2,
                        depth = part.Depth,
                        vertices = part.Vertices,
                        major_segments = part.MajorSegments,
                        minor_segments = part.MinorSegments,
                        bevel = part.Bevel,
                        bevel_segments = part.BevelSegments,
                        shade_smooth = part.ShadeSmooth,
                        subdivision_levels = part.SubdivisionLevels,
                        text = part.Text,
                        extrude = part.Extrude,
                        points = part.Points,
                        edges = part.Edges,
                        faces = part.Faces,
                        radii = part.Radii
                    })
                }),
                instances = plan.Instances.Select(instance => new
                {
                    asset_name = instance.AssetName,
                    name = instance.Name,
                    position = instance.Position,
                    rotation = instance.Rotation,
                    scale = new[] { 1f, 1f, 1f }
                })
            };
            return JsonSerializer.Serialize(dto);
        }

        private static bool PassQualityGate(BuilderScenePlan plan, List<Topology> topology, string quality)
        {
            if (topology.Count == 0 || topology.Any(t => t.Score < 70 || t.Triangles <= 0 || t.Vertices <= 0 || t.MeshObjects <= 0)) return false;
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
                if (item.DegenerateFaces > Math.Max(4, item.Faces / 100)) return false;
                if (item.LooseVertices > Math.Max(4, item.Vertices / 100)) return false;
                if (item.BoundsX < 0.005f || item.BoundsY < 0.005f || item.BoundsZ < 0.005f) return false;
                if (quality.Equals("AA", StringComparison.OrdinalIgnoreCase) && item.Triangles < target * 0.45f)
                    return false;
                if (quality.Equals("AA", StringComparison.OrdinalIgnoreCase) && target >= 4000 && asset.Builder.Parts.Count < 6) return false;
                if (quality.Equals("AA", StringComparison.OrdinalIgnoreCase) && target >= 4000 && asset.Builder.Materials.Count < 2) return false;
            }

            if (quality.Equals("AA", StringComparison.OrdinalIgnoreCase)
                && plan.Assets.Sum(a => a.Builder.Parts.Count) < Math.Max(10, plan.Assets.Count * 4)) return false;

            if (plan.RequestKind.Equals("character", StringComparison.OrdinalIgnoreCase))
            {
                int parts = plan.Assets.Sum(a => a.Builder.Parts.Count);
                bool connectedBase = plan.Assets.SelectMany(a => a.Builder.Parts)
                    .Any(p => p.Type.Equals("skin", StringComparison.OrdinalIgnoreCase) || p.Type.Equals("mesh", StringComparison.OrdinalIgnoreCase));
                if (!connectedBase || parts < 10) return false;
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
                plan.RequestKind = Str(root, "request_kind").Trim().ToLowerInvariant();
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
                                    ShadeSmooth = Bool(p, "shade_smooth"),
                                    SubdivisionLevels = Int(p, "subdivision_levels", 0),
                                    Text = Str(p, "text"),
                                    Extrude = Num(p, "extrude", 0.08f),
                                    Points = FloatVectors(p, "points", 3),
                                    Edges = IntVectors(p, "edges", 2),
                                    Faces = IntVectors(p, "faces", 3),
                                    Radii = FloatValues(p, "radii")
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
                if (string.IsNullOrWhiteSpace(plan.RequestKind)) plan.RequestKind = "prop";
                foreach (BuilderAssetPlan asset in plan.Assets)
                {
                    foreach (BlenderBuilderPart part in asset.Builder.Parts)
                    {
                        if (part.Type == "curve" && part.Points.Count < 2) { error = asset.AssetName + ": curve requires at least 2 points"; return false; }
                        if (part.Type == "extruded_polygon" && part.Points.Count < 3) { error = asset.AssetName + ": extruded_polygon requires at least 3 points"; return false; }
                        if (part.Type == "mesh" && (part.Points.Count < 3 || part.Faces.Count == 0)) { error = asset.AssetName + ": mesh requires points and faces"; return false; }
                        if (part.Type == "skin" && (part.Points.Count < 2 || part.Edges.Count == 0)) { error = asset.AssetName + ": skin requires points and edges"; return false; }
                        if (part.Type == "mesh" && part.Faces.Any(face => face.Any(index => index >= part.Points.Count))) { error = asset.AssetName + ": mesh face index is outside points"; return false; }
                        if (part.Type == "skin" && part.Edges.Any(edge => edge.Any(index => index >= part.Points.Count))) { error = asset.AssetName + ": skin edge index is outside points"; return false; }
                    }
                }
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
            List<string> previewPaths,
            List<RuntimeAsset> assets,
            BuilderScenePlan plan
        )
        {
            StringBuilder s = new StringBuilder();
            s.AppendLine("import bpy, bmesh, json, math, traceback");
            s.AppendLine("from mathutils import Vector");
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
            s.AppendLine("        item={'AssetName':spec['name'],'Triangles':0,'Vertices':0,'Edges':0,'Faces':0,'MeshObjects':0,'MaterialSlots':0,'LooseVertices':0,'NonManifoldEdges':0,'DegenerateFaces':0,'BoundsX':0.0,'BoundsY':0.0,'BoundsZ':0.0,'Score':100}");
            s.AppendLine("        if root is None: item['Score']=0; topo.append(item); continue");
            s.AppendLine("        objs=[]; stack=[root]");
            s.AppendLine("        while stack:");
            s.AppendLine("            o=stack.pop()");
            s.AppendLine("            if o in objs: continue");
            s.AppendLine("            objs.append(o); stack.extend(list(o.children))");
            s.AppendLine("        world_corners=[]");
            s.AppendLine("        for o in [x for x in objs if x.type in {'MESH','CURVE','FONT'}]:");
            s.AppendLine("            item['MeshObjects'] += 1");
            s.AppendLine("            item['MaterialSlots'] += len(getattr(o.data,'materials',[]))");
            s.AppendLine("            world_corners.extend([o.matrix_world @ Vector(c) for c in o.bound_box])");
            s.AppendLine("            eo=o.evaluated_get(depsgraph); mesh=eo.to_mesh()");
            s.AppendLine("            try:");
            s.AppendLine("                if mesh is None: continue");
            s.AppendLine("                mesh.calc_loop_triangles(); item['Triangles'] += len(mesh.loop_triangles)");
            s.AppendLine("                item['Vertices'] += len(mesh.vertices); item['Edges'] += len(mesh.edges); item['Faces'] += len(mesh.polygons)");
            s.AppendLine("                bm=bmesh.new(); bm.from_mesh(mesh)");
            s.AppendLine("                item['LooseVertices'] += sum(1 for v in bm.verts if len(v.link_edges)==0)");
            s.AppendLine("                item['NonManifoldEdges'] += sum(1 for e in bm.edges if not e.is_manifold)");
            s.AppendLine("                item['DegenerateFaces'] += sum(1 for f in bm.faces if f.calc_area() < 1e-8)");
            s.AppendLine("                bm.free()");
            s.AppendLine("            finally:");
            s.AppendLine("                eo.to_mesh_clear()");
            s.AppendLine("        if world_corners:");
            s.AppendLine("            mn=Vector((min(v.x for v in world_corners),min(v.y for v in world_corners),min(v.z for v in world_corners)))");
            s.AppendLine("            mx=Vector((max(v.x for v in world_corners),max(v.y for v in world_corners),max(v.z for v in world_corners)))");
            s.AppendLine("            size=mx-mn; item['BoundsX']=round(size.x,5); item['BoundsY']=round(size.y,5); item['BoundsZ']=round(size.z,5)");
            s.AppendLine("        if item['Triangles']<=0 or item['Vertices']<=0: item['Score']=0");
            s.AppendLine("        if item['LooseVertices']>max(4,item['Vertices']//100): item['Score']-=25");
            s.AppendLine("        if item['DegenerateFaces']>max(4,item['Faces']//100): item['Score']-=25");
            s.AppendLine("        if item['NonManifoldEdges']>max(12,int(item['Edges']*0.35)): item['Score']-=15");
            s.AppendLine("        if min(item['BoundsX'],item['BoundsY'],item['BoundsZ'])<0.005: item['Score']-=20");
            s.AppendLine("        if item['MaterialSlots']==0: item['Score']-=10");
            s.AppendLine("        if spec['target']>0 and item['Triangles']>0:");
            s.AppendLine("            ratio=item['Triangles']/float(spec['target'])");
            s.AppendLine("            if ratio<0.25: item['Score']-=35");
            s.AppendLine("            elif ratio<0.45: item['Score']-=15");
            s.AppendLine("            elif ratio>4.0: item['Score']-=10");
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

            s.AppendLine("    # Host-authored multi-angle preview for visual quality validation.");
            s.AppendLine("    bpy.context.view_layer.update()");
            s.AppendLine("    preview_objs=[]; stack=[scene_root]");
            s.AppendLine("    while stack:");
            s.AppendLine("        o=stack.pop()");
            s.AppendLine("        if o in preview_objs: continue");
            s.AppendLine("        preview_objs.append(o); stack.extend(list(o.children))");
            s.AppendLine("    corners=[]");
            s.AppendLine("    for o in [x for x in preview_objs if x.type in {'MESH','CURVE','FONT'}]: corners.extend([o.matrix_world @ Vector(c) for c in o.bound_box])");
            s.AppendLine("    if corners:");
            s.AppendLine("        mn=Vector((min(v.x for v in corners),min(v.y for v in corners),min(v.z for v in corners)))");
            s.AppendLine("        mx=Vector((max(v.x for v in corners),max(v.y for v in corners),max(v.z for v in corners)))");
            s.AppendLine("        center=(mn+mx)*0.5; radius=max(1.5,(mx-mn).length*0.62)");
            s.AppendLine("        scene=bpy.context.scene");
            s.AppendLine("        scene.render.resolution_x=768; scene.render.resolution_y=768; scene.render.resolution_percentage=100");
            s.AppendLine("        scene.render.image_settings.file_format='PNG'");
            s.AppendLine("        scene.world.color=(0.045,0.055,0.075)");
            s.AppendLine("        try: scene.render.engine='BLENDER_EEVEE_NEXT'");
            s.AppendLine("        except: scene.render.engine='BLENDER_EEVEE'");
            s.AppendLine("        cam_data=bpy.data.cameras.new('AIA_QA_Camera'); cam=bpy.data.objects.new('AIA_QA_Camera',cam_data); scene.collection.objects.link(cam); scene.camera=cam");
            s.AppendLine("        key_data=bpy.data.lights.new('AIA_QA_Key','AREA'); key_data.energy=1400; key_data.shape='DISK'; key_data.size=max(4.0,radius)");
            s.AppendLine("        key=bpy.data.objects.new('AIA_QA_Key',key_data); scene.collection.objects.link(key); key.location=center+Vector((radius*0.7,-radius*0.8,radius*1.2))");
            s.AppendLine("        fill_data=bpy.data.lights.new('AIA_QA_Fill','AREA'); fill_data.energy=800; fill_data.size=max(3.0,radius)");
            s.AppendLine("        fill=bpy.data.objects.new('AIA_QA_Fill',fill_data); scene.collection.objects.link(fill); fill.location=center+Vector((-radius*0.8,radius*0.3,radius*0.6))");
            s.AppendLine("        for lamp in (key,fill): lamp.rotation_euler=((center-lamp.location).to_track_quat('-Z','Y')).to_euler()");
            s.AppendLine("        views=[((1.15,-1.35,0.85)," + Py(previewPaths[0]) + "),((0.0,-1.8,0.35)," + Py(previewPaths[1]) + "),((1.8,0.0,0.35)," + Py(previewPaths[2]) + ")]");
            s.AppendLine("        for direction,path in views:");
            s.AppendLine("            cam.location=center+Vector(direction)*radius; cam.rotation_euler=((center-cam.location).to_track_quat('-Z','Y')).to_euler(); cam.data.lens=52");
            s.AppendLine("            scene.render.filepath=path; bpy.ops.render.render(write_still=True)");

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

        private async Task<HandoffResult> HandoffAsync(
            BuilderScenePlan plan,
            List<RuntimeAsset> assets,
            List<Topology> topology,
            string sceneBundlePath,
            CancellationToken token
        )
        {
            string root = settings.UnityProjectRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "Assets")))
                return new HandoffResult { Success = false, Message = "Unity project root is not configured." };

            string safeScene = Safe(plan.SceneName);
            string modelDir = Path.Combine(root, "Assets", "AI_Generated", "Models", safeScene);
            string bundleDir = Path.Combine(root, "Assets", "AI_Generated", "SceneBundles");
            string sceneDir = Path.Combine(root, "Assets", "AI_Generated", "Scenes");
            string requestId = Guid.NewGuid().ToString("N");
            string resultRelativePath = "Library/AI_Assistant/Handoffs/" + requestId + ".airesult.json";
            string resultAbsolutePath = Path.Combine(root, resultRelativePath.Replace('/', Path.DirectorySeparatorChar));
            string? resultDirectory = Path.GetDirectoryName(resultAbsolutePath);
            if (!string.IsNullOrWhiteSpace(resultDirectory)) Directory.CreateDirectory(resultDirectory);
            if (File.Exists(resultAbsolutePath)) File.Delete(resultAbsolutePath);
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
                        requestId,
                        resultPath = resultRelativePath,
                        importScale = 100f,
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
            DateTime deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(resultAbsolutePath))
                {
                    try
                    {
                        using JsonDocument result = JsonDocument.Parse(File.ReadAllText(resultAbsolutePath));
                        JsonElement rootElement = result.RootElement;
                        bool success = rootElement.TryGetProperty("success", out JsonElement successElement) && successElement.GetBoolean();
                        string message = rootElement.TryGetProperty("message", out JsonElement messageElement) ? messageElement.GetString() ?? "" : "";
                        return new HandoffResult { Success = success, Message = message, ManifestPath = manifest };
                    }
                    catch (JsonException) { }
                }
                await Task.Delay(500, token);
            }
            return new HandoffResult { Success = false, Message = "Unity did not acknowledge prefab import within 120 seconds.", ManifestPath = manifest };
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
            if (p.Contains("quality profile: aa") || p.Contains("aa quality") || p.Contains("aa model") || p.Contains("medium-high") || p.Contains("medium high") || p.Contains("double a") || p.Contains("the forest style") || p.Contains("sons of the forest style")) return "AA";
            if (p.Contains("quality profile: high") || p.Contains("high quality") || p.Contains("high-detail") || p.Contains("high detail")) return "High";
            if (p.Contains("quality profile: low") || p.Contains("low poly") || p.Contains("low-poly") || p.Contains("low detail")) return "Low";
            return "Medium";
        }

        private static bool ContainsPythonFailure(string s) =>
            (s ?? "").Contains("Traceback (most recent call last)", StringComparison.OrdinalIgnoreCase)
            || (s ?? "").Contains("AI_SCENE_PREFAB_EXPORT_FAILED", StringComparison.OrdinalIgnoreCase);

        private static bool AllowedType(string t) => t is "cube" or "plane" or "cylinder" or "cone" or "sphere" or "uv_sphere" or "torus" or "curve" or "extruded_polygon" or "mesh" or "skin" or "text";
        private static string CleanGoal(string p)
        {
            string value = (p ?? "").Trim();
            if (value.StartsWith("/blender", StringComparison.OrdinalIgnoreCase))
                return value.Substring(8).TrimStart(' ', ':', '-').Trim();
            if (value.StartsWith("blender", StringComparison.OrdinalIgnoreCase))
                return value.Substring(7).TrimStart(' ', ':', '-').Trim();
            return value;
        }
        private static string Safe(string v) { var b = new StringBuilder(); foreach (char c in string.IsNullOrWhiteSpace(v) ? "AI_Scene" : v) b.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_'); return b.ToString(); }
        private static string Py(string v) => "'" + (v ?? "").Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        private static string Compact(string v, int n) => string.IsNullOrEmpty(v) ? "" : v.Length <= n ? v : v.Substring(0, n) + "...";
        private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        private static int Int(JsonElement e, string n, int f = 0) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int x) ? Math.Max(0, x) : f;
        private static float Num(JsonElement e, string n, float f) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out float x) ? x : f;
        private static bool Bool(JsonElement e, string n) => e.TryGetProperty(n, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out bool b) && b));
        private static float[] Vec3(JsonElement e, string n, float[] f) => VecN(e, n, f, 3);
        private static float[] Vec4(JsonElement e, string n, float[] f) => VecN(e, n, f, 4);
        private static List<float[]> FloatVectors(JsonElement e, string n, int maxDimensions)
        {
            List<float[]> result = new();
            if (!e.TryGetProperty(n, out JsonElement values) || values.ValueKind != JsonValueKind.Array) return result;
            foreach (JsonElement value in values.EnumerateArray().Take(512))
            {
                if (value.ValueKind != JsonValueKind.Array) continue;
                List<float> row = new();
                foreach (JsonElement item in value.EnumerateArray().Take(maxDimensions))
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out float number)) row.Add(number);
                if (row.Count >= 2) { while (row.Count < 3) row.Add(0f); result.Add(row.ToArray()); }
            }
            return result;
        }
        private static List<int[]> IntVectors(JsonElement e, string n, int minimumDimensions)
        {
            List<int[]> result = new();
            if (!e.TryGetProperty(n, out JsonElement values) || values.ValueKind != JsonValueKind.Array) return result;
            foreach (JsonElement value in values.EnumerateArray().Take(1024))
            {
                if (value.ValueKind != JsonValueKind.Array) continue;
                List<int> row = new();
                foreach (JsonElement item in value.EnumerateArray().Take(32))
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int number) && number >= 0) row.Add(number);
                if (row.Count >= minimumDimensions) result.Add(row.ToArray());
            }
            return result;
        }
        private static List<float> FloatValues(JsonElement e, string n)
        {
            List<float> result = new();
            if (!e.TryGetProperty(n, out JsonElement values) || values.ValueKind != JsonValueKind.Array) return result;
            foreach (JsonElement item in values.EnumerateArray().Take(512))
                if (item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out float number)) result.Add(number);
            return result;
        }
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
            public string RequestKind { get; set; } = "prop";
            public string SceneName { get; set; } = "";
            public string Summary { get; set; } = "";
            public string LastVisualFeedback { get; set; } = "";
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
            public int Vertices { get; set; }
            public int Edges { get; set; }
            public int Faces { get; set; }
            public int MeshObjects { get; set; }
            public int MaterialSlots { get; set; }
            public int LooseVertices { get; set; }
            public int NonManifoldEdges { get; set; }
            public int DegenerateFaces { get; set; }
            public float BoundsX { get; set; }
            public float BoundsY { get; set; }
            public float BoundsZ { get; set; }
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
            public bool VisualReviewAvailable { get; set; }
            public bool VisualQualityOk { get; set; }
            public int VisualScore { get; set; }
            public string VisualFeedback { get; set; } = "";
            public List<string> PreviewPaths { get; set; } = new();
            public List<RuntimeAsset> RuntimeAssets { get; set; } = new();
            public List<Topology> Topology { get; set; } = new();
        }

        private sealed class HandoffResult
        {
            public bool Success { get; init; }
            public string Message { get; init; } = "";
            public string ManifestPath { get; init; } = "";
        }

        private sealed record ProcessResult(int ExitCode, string Output, bool TimedOut, bool Cancelled);
    }
}
