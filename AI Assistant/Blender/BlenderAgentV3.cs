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
        private const int TimeoutSeconds = 240;

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
                150
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
            activity("[BLENDER V3] builder-first · " + version);

            string runRoot = Path.Combine(settings.BlenderWorkspace, "AI_Runs", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
            Directory.CreateDirectory(runRoot);

            AgentTaskStateV2 task = new AgentTaskStateV2 { Goal = goal, Phase = AgentTaskPhaseV2.Designing };
            ProviderReplyV2 reply = await CompleteAsync(task, BuildSystemPrompt(version), BuildUserPrompt(goal), token);
            if (!reply.Success) return "Blender Agent model failure: " + reply.Error;

            if (!TryParsePlan(reply.Content, out BuilderScenePlan plan, out string error))
            {
                activity("[BLENDER V3 SCHEMA] repairing malformed structured plan");
                ProviderReplyV2 retry = await CompleteAsync(
                    task,
                    BuildSystemPrompt(version),
                    BuildUserPrompt(goal) + "\nPrevious output was rejected: " + error + "\nReturn the COMPLETE strict builder JSON schema with non-empty assets, materials/parts as needed, and instances.",
                    token
                );
                if (!retry.Success || !TryParsePlan(retry.Content, out plan, out error))
                    return "Blender Agent received invalid builder plan after recovery: " + error;
                reply = retry;
            }

            if (token.IsCancellationRequested) return "Blender task cancelled by user.";

            string generated = BlenderDeterministicBuilder.BuildPython(plan.Assets.Select(a => a.Builder));
            string blendPath = Path.Combine(runRoot, Safe(plan.SceneName) + ".blend");
            string scriptPath = Path.Combine(runRoot, "builder_scene.py");
            string logPath = Path.Combine(runRoot, "blender.log");

            List<RuntimeAsset> runtimeAssets = plan.Assets.Select(a => new RuntimeAsset
            {
                Plan = a,
                ExportPath = Path.Combine(runRoot, Safe(a.AssetName) + ".fbx")
            }).ToList();

            File.WriteAllText(scriptPath, BuildExecutableScript(generated, blendPath, runtimeAssets), new UTF8Encoding(false));
            activity("[BLENDER V3] executing deterministic host builder for " + runtimeAssets.Count + " asset(s)");
            ProcessResult execution = await RunAsync(blenderExe, scriptPath, logPath, token);

            if (execution.Cancelled || token.IsCancellationRequested) return "Blender task cancelled by user.";

            List<Topology> topology = ParseTopology(execution.Output);
            bool exportsOk = runtimeAssets.All(a => File.Exists(a.ExportPath));
            bool topologyOk = topology.Count == runtimeAssets.Count && topology.All(t => t.Triangles > 0 && t.Score >= 55);
            bool success = execution.ExitCode == 0 && File.Exists(blendPath) && exportsOk && topologyOk && !ContainsPythonFailure(execution.Output);

            if (!success)
            {
                return "Blender V3 deterministic build failed verification.\nLog: " + logPath
                    + "\nexit=" + execution.ExitCode + ", blend=" + File.Exists(blendPath)
                    + ", exports=" + exportsOk + ", topology=" + topologyOk
                    + "\n" + Compact(execution.Output, 1800);
            }

            string? manifest = Handoff(plan, runtimeAssets, topology);
            int totalTris = topology.Sum(t => t.Triangles);
            int minScore = topology.Count == 0 ? 0 : topology.Min(t => t.Score);
            activity("[BLENDER V3 VERIFY] deterministic build passed · " + totalTris + " tris · min " + minScore + "/100");

            StringBuilder result = new StringBuilder();
            result.AppendLine(string.IsNullOrWhiteSpace(plan.Summary) ? "Builder-first Blender scene created." : plan.Summary);
            result.AppendLine("Engine: Blender V3 deterministic builder-first");
            result.AppendLine("Assets: " + plan.Assets.Count + " unique model(s), " + plan.Instances.Count + " Unity instance(s).");
            result.AppendLine("Topology: " + totalTris + " triangles total, minimum score " + minScore + "/100.");
            result.AppendLine("Blend: " + blendPath);
            if (!string.IsNullOrWhiteSpace(manifest)) result.AppendLine("Unity scene handoff: " + manifest);
            result.AppendLine("Provider: " + reply.Provider + " / " + reply.Model);
            return result.ToString().Trim();
        }

        private async Task<ProviderReplyV2> CompleteAsync(AgentTaskStateV2 task, string system, string user, CancellationToken token)
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
            return "You are a 3D asset architect. You DO NOT write Python or bpy. Target is " + version + ". Return strict JSON only. Schema: "
                + "{\"scene_name\":\"GasStation\",\"summary\":\"short\",\"assets\":[{\"asset_name\":\"Building\",\"root_object\":\"AIA_Building\",\"target_triangles\":4000,\"materials\":[{\"name\":\"Wall\",\"color\":[0.6,0.55,0.5,1],\"metallic\":0,\"roughness\":0.7}],\"parts\":[{\"type\":\"cube\",\"name\":\"Body\",\"position\":[0,0,1.5],\"rotation\":[0,0,0],\"dimensions\":[10,6,3],\"material\":\"Wall\",\"bevel\":0.08,\"bevel_segments\":2,\"shade_smooth\":false}]}],\"instances\":[{\"asset_name\":\"Building\",\"name\":\"Building_01\",\"position\":[0,0,0],\"rotation\":[0,0,0],\"scale\":[1,1,1]}]}. "
                + "Allowed part types ONLY: cube, plane, cylinder, cone, sphere, uv_sphere, torus. Allowed fields: name, position, rotation(degrees), dimensions, material, radius, radius2, depth, vertices, major_segments, minor_segments, bevel, bevel_segments, shade_smooth. "
                + "Create up to 12 reusable assets and 48 instances. Use multiple simple parts to form detailed objects. For Medium/High/AA profiles, increase geometric detail by adding meaningful secondary/tertiary parts, bevels, smoother cylinders, trim, frames, handles, panels, curbs, roof structure and other silhouette/detail geometry—not by random triangle inflation. "
                + "Every asset root stays at local origin; all scene placement goes in instances. Reuse identical assets through instances. Do not output code, nodes, world settings, lights, cameras, file paths, save/export calls or unsupported operations. No markdown.";
        }

        private static string BuildUserPrompt(string goal)
        {
            return "Turn this ONE instruction into a complete reusable asset kit and Unity scene layout. The host safely builds, verifies, exports and assembles it.\nUSER GOAL:\n" + goal;
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
                            foreach (JsonElement m in mats.EnumerateArray().Take(24))
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
                            foreach (JsonElement p in parts.EnumerateArray().Take(160))
                            {
                                string type = Str(p, "type").ToLowerInvariant();
                                if (!AllowedType(type)) continue;
                                asset.Builder.Parts.Add(new BlenderBuilderPart
                                {
                                    Type = type,
                                    Name = Str(p, "name"),
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

                if (plan.Assets.Count == 0) { error = "assets missing or no valid builder parts"; return false; }
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
                            Scale = Vec3(i, "scale", new[] { 1f, 1f, 1f })
                        });
                    }
                }

                if (plan.Instances.Count == 0)
                {
                    foreach (BuilderAssetPlan a in plan.Assets)
                        plan.Instances.Add(new BuilderInstance { AssetName = a.AssetName, Name = a.AssetName, Position = new[] { 0f,0f,0f }, Rotation = new[] {0f,0f,0f}, Scale = new[] {1f,1f,1f} });
                }
                if (string.IsNullOrWhiteSpace(plan.SceneName)) plan.SceneName = "AI_Scene";
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private static string BuildExecutableScript(string generated, string blendPath, List<RuntimeAsset> assets)
        {
            StringBuilder s = new StringBuilder();
            s.AppendLine("import bpy, bmesh, json, traceback");
            s.AppendLine("from mathutils import Vector");
            s.AppendLine("try:");
            s.AppendLine("    bpy.ops.object.select_all(action='SELECT')");
            s.AppendLine("    bpy.ops.object.delete(use_global=False)");
            foreach (string line in generated.Replace("\r\n","\n").Split('\n')) s.AppendLine("    " + line);
            s.AppendLine("    bpy.ops.wm.save_as_mainfile(filepath=" + Py(blendPath) + ")");
            s.AppendLine("    specs = [");
            foreach (RuntimeAsset a in assets)
                s.AppendLine("        {'name':" + Py(a.Plan.AssetName) + ",'root':" + Py(a.Plan.RootObject) + ",'path':" + Py(a.ExportPath) + ",'target':" + Math.Max(0,a.Plan.TargetTriangles) + "},");
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
            s.AppendLine("        meshes=[o for o in objs if o.type=='MESH']");
            s.AppendLine("        for o in meshes:");
            s.AppendLine("            o.data.calc_loop_triangles(); item['triangles'] += len(o.data.loop_triangles)");
            s.AppendLine("        if item['triangles']<=0: item['score']=0");
            s.AppendLine("        if spec['target']>0 and item['triangles']>0:");
            s.AppendLine("            d=abs(item['triangles']-spec['target'])/float(spec['target'])");
            s.AppendLine("            if d>1.5: item['score']-=20");
            s.AppendLine("        bpy.ops.object.select_all(action='DESELECT')");
            s.AppendLine("        for o in objs: o.select_set(True)");
            s.AppendLine("        bpy.context.view_layer.objects.active=root");
            s.AppendLine("        bpy.ops.export_scene.fbx(filepath=spec['path'], use_selection=True)");
            s.AppendLine("        topo.append(item)");
            s.AppendLine("    print('AI_TOPOLOGY_JSON:'+json.dumps(topo,separators=(',',':')))");
            s.AppendLine("    print('AI_ASSET_EXPORT_OK')");
            s.AppendLine("except Exception:");
            s.AppendLine("    print('AI_ASSET_EXPORT_FAILED'); traceback.print_exc(); raise");
            return s.ToString();
        }

        private string? Handoff(BuilderScenePlan plan, List<RuntimeAsset> assets, List<Topology> topology)
        {
            string root = settings.UnityProjectRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "Assets"))) return null;
            string safeScene = Safe(plan.SceneName);
            string modelDir = Path.Combine(root, "Assets", "AI_Generated", "Models", safeScene);
            string sceneDir = Path.Combine(root, "Assets", "AI_Generated", "Scenes");
            Directory.CreateDirectory(modelDir); Directory.CreateDirectory(sceneDir);
            Dictionary<string,string> paths = new(StringComparer.OrdinalIgnoreCase);
            foreach (RuntimeAsset a in assets)
            {
                string file = Safe(a.Plan.AssetName) + ".fbx";
                File.Copy(a.ExportPath, Path.Combine(modelDir,file), true);
                paths[a.Plan.AssetName] = "Assets/AI_Generated/Models/" + safeScene + "/" + file;
            }
            var instances = plan.Instances.Where(i=>paths.ContainsKey(i.AssetName)).Select(i=>new { assetPath=paths[i.AssetName], name=string.IsNullOrWhiteSpace(i.Name)?i.AssetName:i.Name, position=i.Position, rotation=i.Rotation, scale=i.Scale }).ToArray();
            string manifest = Path.Combine(sceneDir, safeScene + ".aiscene.json");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new { version=2, sceneName=plan.SceneName, rootName="AI_Generated_"+safeScene, replaceExisting=true, instances }, new JsonSerializerOptions{WriteIndented=true}));
            File.WriteAllText(Path.Combine(sceneDir, safeScene + ".topology.json"), JsonSerializer.Serialize(topology, new JsonSerializerOptions{WriteIndented=true}));
            activity("[BLENDER UNITY] builder scene manifest written");
            return manifest;
        }

        private async Task<ProcessResult> RunAsync(string exe, string script, string log, CancellationToken token)
        {
            ProcessStartInfo psi = new ProcessStartInfo { FileName=exe, Arguments="--background --python \""+script+"\"", UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true };
            using Process p = new Process{StartInfo=psi}; p.Start();
            Task<string> so=p.StandardOutput.ReadToEndAsync(), se=p.StandardError.ReadToEndAsync();
            Task wait=p.WaitForExitAsync(), timeout=Task.Delay(TimeSpan.FromSeconds(TimeoutSeconds)), cancel=Task.Delay(Timeout.InfiniteTimeSpan,token);
            Task done=await Task.WhenAny(wait,timeout,cancel); bool to=done==timeout, ca=done==cancel||token.IsCancellationRequested;
            if(to||ca){try{p.Kill(true);}catch{} try{await p.WaitForExitAsync();}catch{}}
            string output=await so + "\n" + await se; File.WriteAllText(log,output);
            return new ProcessResult(ca?-4:to?-2:p.ExitCode,output,to,ca);
        }

        private static async Task<string> ProbeAsync(string exe, CancellationToken token)
        {
            try
            {
                ProcessStartInfo psi=new ProcessStartInfo{FileName=exe,Arguments="--version",UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
                using Process p=new Process{StartInfo=psi}; p.Start(); string o=await p.StandardOutput.ReadLineAsync() ?? "Blender"; await p.WaitForExitAsync(token); return o.Trim();
            } catch { return "Blender 3.6 compatible runtime"; }
        }

        private static List<Topology> ParseTopology(string output)
        {
            const string marker="AI_TOPOLOGY_JSON:"; string? line=(output??"").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries).LastOrDefault(x=>x.StartsWith(marker,StringComparison.Ordinal));
            if(line==null)return new();
            try{return JsonSerializer.Deserialize<List<Topology>>(line.Substring(marker.Length),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??new();}catch{return new();}
        }
        private static bool ContainsPythonFailure(string s)=> (s??"").Contains("Traceback (most recent call last)",StringComparison.OrdinalIgnoreCase)|| (s??"").Contains("AI_ASSET_EXPORT_FAILED",StringComparison.OrdinalIgnoreCase);
        private static bool AllowedType(string t)=>t is "cube" or "plane" or "cylinder" or "cone" or "sphere" or "uv_sphere" or "torus";
        private static string CleanGoal(string p){string v=(p??"").Trim();return v.StartsWith("/blender ",StringComparison.OrdinalIgnoreCase)?v.Substring(9).Trim():v;}
        private static string Safe(string v){var b=new StringBuilder();foreach(char c in string.IsNullOrWhiteSpace(v)?"AI_Scene":v)b.Append(char.IsLetterOrDigit(c)||c=='_'||c=='-'?c:'_');return b.ToString();}
        private static string Py(string v)=>"'"+(v??"").Replace("\\","\\\\").Replace("'","\\'")+"'";
        private static string Compact(string v,int n)=>string.IsNullOrEmpty(v)?"":v.Length<=n?v:v.Substring(0,n)+"...";
        private static string Str(JsonElement e,string n)=>e.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
        private static int Int(JsonElement e,string n,int f=0)=>e.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt32(out int x)?Math.Max(0,x):f;
        private static float Num(JsonElement e,string n,float f)=>e.TryGetProperty(n,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetSingle(out float x)?x:f;
        private static bool Bool(JsonElement e,string n)=>e.TryGetProperty(n,out var v)&&(v.ValueKind==JsonValueKind.True||(v.ValueKind==JsonValueKind.String&&bool.TryParse(v.GetString(),out bool b)&&b));
        private static float[] Vec3(JsonElement e,string n,float[] f)=>VecN(e,n,f,3);
        private static float[] Vec4(JsonElement e,string n,float[] f)=>VecN(e,n,f,4);
        private static float[] VecN(JsonElement e,string n,float[] f,int count){float[] r=f.ToArray();if(!e.TryGetProperty(n,out var v)||v.ValueKind!=JsonValueKind.Array)return r;int i=0;foreach(var c in v.EnumerateArray()){if(i>=count)break;if(c.ValueKind==JsonValueKind.Number&&c.TryGetSingle(out float x))r[i]=x;i++;}return r;}

        private sealed class BuilderScenePlan { public string SceneName=""; public string Summary=""; public List<BuilderAssetPlan> Assets=new(); public List<BuilderInstance> Instances=new(); }
        private sealed class BuilderAssetPlan { public string AssetName=""; public string RootObject=""; public int TargetTriangles; public BlenderBuilderAsset Builder=new(); }
        private sealed class BuilderInstance { public string AssetName=""; public string Name=""; public float[] Position=new[]{0f,0f,0f}; public float[] Rotation=new[]{0f,0f,0f}; public float[] Scale=new[]{1f,1f,1f}; }
        private sealed class RuntimeAsset { public BuilderAssetPlan Plan=new(); public string ExportPath=""; }
        private sealed class Topology { public string AssetName {get;set;}=""; public int Triangles {get;set;} public int Score {get;set;} }
        private sealed record ProcessResult(int ExitCode,string Output,bool TimedOut,bool Cancelled);
    }
}
