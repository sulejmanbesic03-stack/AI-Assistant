using AI_Assistant.AgentV2;
using AI_Assistant.Runtime;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Assistant.Blender
{
    public sealed class BlenderAgentV2
    {
        private const int BlenderRunTimeoutSeconds = 240;
        private const int BlenderProbeTimeoutSeconds = 20;
        private const int MaxUniqueAssets = 12;
        private const int MaxInstances = 48;

        private readonly ProviderRouterV2 providers;
        private readonly IAIProviderV2 blenderPrimary;
        private readonly RuntimeSettings settings;
        private readonly Action<string> activity;
        private readonly BlenderVisualQualityGate visualQuality;

        public BlenderAgentV2(RuntimeSettings settings, Action<string> activity)
        {
            this.settings = settings;
            this.activity = activity;
            providers = new ProviderRouterV2(activity);
            visualQuality = new BlenderVisualQualityGate(activity);

            blenderPrimary = new OpenAiCompatibleProviderV2(
                "Blender-InclusionAI",
                "https://openrouter.ai/api/v1/chat/completions",
                Environment.GetEnvironmentVariable("BLENDER_OPENROUTER_MODEL")
                    ?? "inclusionai/ling-3.0-flash-fin:free",
                "OPENROUTER_API_KEY",
                180,
                16000
            );
        }

        public bool ShouldHandle(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            return p.StartsWith("/blender ")
                || p.Contains(" blender ")
                || p.StartsWith("blender ")
                || p.Contains("napravi model")
                || p.Contains("3d model")
                || p.Contains("napravi scenu")
                || p.Contains("build a scene")
                || p.Contains("benzinsk")
                || p.Contains("gas station");
        }

        public async Task<string> HandleAsync(string prompt)
        {
            CancellationToken cancellationToken = AgentCancellationHub.Token;
            string goal = CleanGoal(prompt);
            string quality = DetectQuality(goal);

            if (string.IsNullOrWhiteSpace(goal))
            {
                return "Blender Agent: napiši šta želiš da napravim, npr. /blender napravi low-poly barrel ili cijelu benzinsku scenu.";
            }

            string blenderExe = settings.ResolveBlenderExecutable();
            if (string.IsNullOrWhiteSpace(blenderExe) || !File.Exists(blenderExe))
            {
                return "Blender Agent nije spreman: Blender executable nije pronađen. Otvori Settings i postavi blender.exe.";
            }

            string blenderVersion = await ProbeBlenderVersionAsync(
                blenderExe,
                cancellationToken
            );

            if (cancellationToken.IsCancellationRequested)
            {
                return "Blender task cancelled by user.";
            }

            activity("[BLENDER] target " + blenderVersion);
            activity("[BLENDER ORGANIC] controlled direct mesh · " + quality);

            string workspace = settings.BlenderWorkspace;
            Directory.CreateDirectory(workspace);

            string runRoot = Path.Combine(
                workspace,
                "AI_Runs",
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
            );
            Directory.CreateDirectory(runRoot);

            activity("[BLENDER] designing scene bundle");

            AgentTaskStateV2 task = new AgentTaskStateV2
            {
                Goal = goal,
                Phase = AgentTaskPhaseV2.Designing
            };

            ProviderReplyV2 reply = await CompleteBlenderModelAsync(
                task,
                BuildSystemPrompt(blenderVersion, goal),
                BuildUserPrompt(goal, blenderVersion),
                cancellationToken
            );

            if (reply.StatusCode == 499 || cancellationToken.IsCancellationRequested)
            {
                return "Blender task cancelled by user.";
            }

            if (!reply.Success)
            {
                return "Blender Agent model failure: " + reply.Error;
            }

            if (!TryParsePlan(reply.Content, out BlenderScenePlan plan, out string parseError))
            {
                return "Blender Agent received invalid scene JSON: " + parseError;
            }

            if (IsCharacterGoal(goal))
            {
                if (plan.Assets.Count != 1)
                {
                    return "Blender character planner was rejected: expected exactly one complete character asset, received "
                        + plan.Assets.Count + ". No partial model was imported.";
                }

                BlenderAssetPlan characterAsset = plan.Assets[0];
                characterAsset.TargetTriangles = Math.Clamp(characterAsset.TargetTriangles, 15000, 45000);
                plan.Instances = new List<BlenderInstancePlan>
                {
                    new BlenderInstancePlan
                    {
                        AssetName = characterAsset.AssetName,
                        Name = characterAsset.AssetName,
                        Position = new[] { 0f, 0f, 0f },
                        Rotation = new[] { 0f, 0f, 0f },
                        Scale = new[] { 1f, 1f, 1f }
                    }
                };
            }

            if (!IsSafeScript(plan.Script, out string safetyError))
            {
                return "Blender Agent blocked generated script: " + safetyError;
            }

            BlenderAttemptResult first = await ExecutePlanAsync(
                plan,
                runRoot,
                blenderExe,
                "build_scene.py",
                "blender.log",
                cancellationToken
            );

            await ApplyVisualQualityAsync(first, goal, quality, cancellationToken);

            if (first.Cancelled || cancellationToken.IsCancellationRequested)
            {
                return "Blender task cancelled by user. Any unfinished export was discarded.";
            }

            if (first.Success)
            {
                return BuildSuccessReply(first, plan, reply);
            }

            activity("[BLENDER REPAIR] one focused correction from execution/topology/visual report");
            task.Phase = AgentTaskPhaseV2.Correcting;

            ProviderReplyV2 repairReply = await CompleteBlenderModelAsync(
                task,
                BuildSystemPrompt(blenderVersion, goal),
                BuildRepairPrompt(goal, blenderVersion, plan, first),
                cancellationToken
            );

            if (repairReply.StatusCode == 499 || cancellationToken.IsCancellationRequested)
            {
                return "Blender task cancelled by user.";
            }

            if (!repairReply.Success)
            {
                return BuildFailureReply(
                    first,
                    "Blender character failed verification and the single repair model was unavailable: "
                    + repairReply.Error
                );
            }

            if (!TryParsePlan(repairReply.Content, out BlenderScenePlan repairedPlan, out string repairParseError))
            {
                return BuildFailureReply(
                    first,
                    "Blender character failed verification and repair JSON was invalid: " + repairParseError
                );
            }

            if (!IsSafeScript(repairedPlan.Script, out string repairSafetyError))
            {
                return BuildFailureReply(
                    first,
                    "Blender repair was blocked by safety validation: " + repairSafetyError
                );
            }

            BlenderAttemptResult repaired = await ExecutePlanAsync(
                repairedPlan,
                runRoot,
                blenderExe,
                "build_scene_retry.py",
                "blender_retry.log",
                cancellationToken
            );

            await ApplyVisualQualityAsync(repaired, goal, quality, cancellationToken);

            if (repaired.Cancelled || cancellationToken.IsCancellationRequested)
            {
                return "Blender task cancelled by user. Any unfinished export was discarded.";
            }

            if (!repaired.Success)
            {
                return BuildFailureReply(
                    repaired,
                    "Blender character generation and its single focused repair both failed verification. No model was imported."
                );
            }

            return BuildSuccessReply(repaired, repairedPlan, repairReply);
        }

        private async Task<ProviderReplyV2> CompleteBlenderModelAsync(
            AgentTaskStateV2 task,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken
        )
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return CancelledProviderReply();
            }

            if (blenderPrimary.IsConfigured)
            {
                task.ActiveProvider = blenderPrimary.Name;
                task.ModelCalls++;

                int approxTokens = Math.Max(
                    1,
                    (systemPrompt.Length + userPrompt.Length + 3) / 4
                );

                activity(
                    "[V2 MODEL] " + blenderPrimary.Name
                    + " / " + blenderPrimary.ModelName
                    + " call " + task.ModelCalls
                );
                activity(
                    "[V2 TOKENS] approx input " + approxTokens
                    + " · Blender pinned route"
                );

                ProviderReplyV2 preferred = await blenderPrimary.CompleteAsync(
                    systemPrompt,
                    userPrompt,
                    cancellationToken
                );

                if (preferred.Success)
                {
                    activity("[V2 MODEL] resolved " + preferred.Model);
                    return preferred;
                }

                if (preferred.StatusCode == 499 || cancellationToken.IsCancellationRequested)
                {
                    return CancelledProviderReply();
                }

                activity(
                    "[V2 PROVIDER] Blender InclusionAI failed HTTP "
                    + preferred.StatusCode
                    + "; using normal free fallback chain"
                );
            }

            return await providers.CompleteAsync(
                task,
                systemPrompt,
                userPrompt,
                cancellationToken
            );
        }

        private async Task ApplyVisualQualityAsync(
            BlenderAttemptResult attempt,
            string goal,
            string quality,
            CancellationToken cancellationToken
        )
        {
            bool requiresVisual = quality.Equals("High", StringComparison.OrdinalIgnoreCase)
                || quality.Equals("AA", StringComparison.OrdinalIgnoreCase);

            if (!requiresVisual || !attempt.ExecutionHealthy || cancellationToken.IsCancellationRequested)
            {
                attempt.VisualQualityOk = !requiresVisual;
                attempt.Success = attempt.ExecutionHealthy && !attempt.TopologyCritical && attempt.VisualQualityOk;
                return;
            }

            BlenderVisualQualityResult review = await visualQuality.EvaluateAsync(
                goal,
                quality,
                JsonSerializer.Serialize(attempt.Topology),
                attempt.PreviewPaths,
                cancellationToken
            );
            attempt.VisualReviewAvailable = review.Available;
            attempt.VisualScore = review.Score;
            attempt.VisualFeedback = review.Feedback;
            attempt.VisualQualityOk = review.Available && review.Passed;
            attempt.Success = attempt.ExecutionHealthy && !attempt.TopologyCritical && attempt.VisualQualityOk;

            activity(review.Available
                ? "[BLENDER VISUAL QA] " + review.Score + "/100 · " + (review.Passed ? "PASS" : "REJECT")
                : "[BLENDER VISUAL QA] unavailable · High/AA character will not be imported");
        }

        private async Task<BlenderAttemptResult> ExecutePlanAsync(
            BlenderScenePlan plan,
            string runRoot,
            string blenderExe,
            string scriptFileName,
            string logFileName,
            CancellationToken cancellationToken
        )
        {
            string safeSceneName = SanitizeFileName(plan.SceneName);
            string blendPath = Path.Combine(runRoot, safeSceneName + ".blend");
            List<string> previewPaths = new List<string>
            {
                Path.Combine(runRoot, safeSceneName + "_preview_iso.png"),
                Path.Combine(runRoot, safeSceneName + "_preview_front.png"),
                Path.Combine(runRoot, safeSceneName + "_preview_side.png")
            };
            string scriptPath = Path.Combine(runRoot, scriptFileName);
            string logPath = Path.Combine(runRoot, logFileName);

            List<AssetRuntimeSpec> runtimeAssets = new List<AssetRuntimeSpec>();
            foreach (BlenderAssetPlan asset in plan.Assets)
            {
                string safeAssetName = SanitizeFileName(asset.AssetName);
                string format = NormalizeFormat(asset.ExportFormat);
                runtimeAssets.Add(
                    new AssetRuntimeSpec
                    {
                        Plan = asset,
                        SafeName = safeAssetName,
                        Format = format,
                        ExportPath = Path.Combine(runRoot, safeAssetName + "." + format)
                    }
                );
            }

            TryDelete(blendPath);
            foreach (AssetRuntimeSpec asset in runtimeAssets)
            {
                TryDelete(asset.ExportPath);
            }

            string hardenedBody = HardenGeneratedScript(
                plan.Script,
                out bool hostAdjusted
            );

            if (hostAdjusted)
            {
                activity(
                    "[BLENDER HOSTFIX] Blender compatibility patch applied before execution"
                );
            }

            string finalScript = BuildExecutableScript(
                hardenedBody,
                blendPath,
                runtimeAssets,
                previewPaths
            );

            File.WriteAllText(
                scriptPath,
                finalScript,
                new UTF8Encoding(false)
            );

            if (cancellationToken.IsCancellationRequested)
            {
                return BlenderAttemptResult.CancelledResult(
                    blendPath,
                    scriptPath,
                    logPath
                );
            }

            activity(
                "[BLENDER] executing headless Blender for "
                + runtimeAssets.Count
                + " unique asset(s)"
            );

            ProcessResult execution = await RunBlenderAsync(
                blenderExe,
                scriptPath,
                logPath,
                cancellationToken
            );

            bool blendExists = File.Exists(blendPath);
            bool pythonFailure = ContainsPythonFailure(execution.Output);
            List<TopologyItem> topology = ParseTopology(execution.Output);

            foreach (AssetRuntimeSpec asset in runtimeAssets)
            {
                asset.ExportExists = File.Exists(asset.ExportPath);
            }

            bool everyExportExists = runtimeAssets.All(asset => asset.ExportExists);
            bool topologyCritical = IsTopologyCritical(
                runtimeAssets,
                topology
            );

            bool success =
                execution.ExitCode == 0
                && !execution.TimedOut
                && !execution.Cancelled
                && !pythonFailure
                && blendExists
                && everyExportExists
                && !topologyCritical;

            BlenderAttemptResult result = new BlenderAttemptResult
            {
                Success = success,
                ExitCode = execution.ExitCode,
                TimedOut = execution.TimedOut,
                Cancelled = execution.Cancelled,
                Output = execution.Output,
                BlendPath = blendPath,
                ScriptPath = scriptPath,
                LogPath = logPath,
                SafeSceneName = safeSceneName,
                BlendExists = blendExists,
                PythonFailure = pythonFailure,
                HostAdjusted = hostAdjusted,
                ExecutionHealthy = execution.ExitCode == 0
                    && !execution.TimedOut
                    && !execution.Cancelled
                    && !pythonFailure
                    && blendExists
                    && everyExportExists,
                TopologyCritical = topologyCritical,
                RuntimeAssets = runtimeAssets,
                Topology = topology,
                PreviewPaths = previewPaths
            };

            if (execution.Cancelled)
            {
                activity("[BLENDER] process cancelled and killed");
                return result;
            }

            if (topology.Count > 0)
            {
                int totalTriangles = topology.Sum(item => item.Triangles);
                int minimumScore = topology.Min(item => item.Score);
                activity(
                    "[BLENDER TOPOLOGY] "
                    + totalTriangles
                    + " tris · min score "
                    + minimumScore
                    + "/100"
                );
            }

            if (success)
            {
                activity("[BLENDER VERIFY] blend + all exports + topology passed");
            }
            else
            {
                activity("[BLENDER VERIFY] execution or topology gate failed; preparing repair delta");
            }

            return result;
        }

        private string BuildSuccessReply(
            BlenderAttemptResult attempt,
            BlenderScenePlan plan,
            ProviderReplyV2 reply
        )
        {
            string? unityManifest = TryHandoffSceneToUnity(
                attempt,
                plan
            );

            int totalTriangles = attempt.Topology.Sum(item => item.Triangles);
            int minScore = attempt.Topology.Count == 0
                ? 0
                : attempt.Topology.Min(item => item.Score);

            StringBuilder result = new StringBuilder();
            result.AppendLine(
                string.IsNullOrWhiteSpace(plan.Summary)
                    ? "Blender scene bundle created."
                    : plan.Summary
            );
            result.AppendLine(
                "Assets: " + attempt.RuntimeAssets.Count
                + " unique model(s), " + plan.Instances.Count
                + " Unity instance(s)."
            );
            result.AppendLine(
                "Topology: " + totalTriangles
                + " triangles total, minimum topology score "
                + minScore + "/100."
            );
            if (attempt.VisualReviewAvailable)
            {
                result.AppendLine(
                    "Visual QA: " + attempt.VisualScore + "/100 · "
                    + Compact(attempt.VisualFeedback, 320)
                );
            }
            result.AppendLine("Blend: " + attempt.BlendPath);

            if (!string.IsNullOrWhiteSpace(unityManifest))
            {
                result.AppendLine("Unity scene handoff: " + unityManifest);
                result.AppendLine("Unity will assemble the scene from the manifest after import.");
            }

            result.AppendLine("Provider: " + reply.Provider + " / " + reply.Model);
            return result.ToString().Trim();
        }

        private static string BuildFailureReply(
            BlenderAttemptResult attempt,
            string headline
        )
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine(headline);
            builder.AppendLine("Log: " + attempt.LogPath);
            builder.AppendLine(
                "Verification: exit=" + attempt.ExitCode
                + ", timeout=" + attempt.TimedOut
                + ", cancelled=" + attempt.Cancelled
                + ", pythonFailure=" + attempt.PythonFailure
                + ", blend=" + attempt.BlendExists
                + ", topologyCritical=" + attempt.TopologyCritical
                + ", visual=" + attempt.VisualQualityOk
            );

            if (!string.IsNullOrWhiteSpace(attempt.VisualFeedback))
            {
                builder.AppendLine(
                    "Visual QA: " + attempt.VisualScore + "/100 · "
                    + Compact(attempt.VisualFeedback, 900)
                );
            }

            if (attempt.Topology.Count > 0)
            {
                builder.AppendLine(
                    "Topology: " + BuildTopologySummary(attempt.Topology)
                );
            }

            string compactLog = Compact(attempt.Output, 1800);
            if (!string.IsNullOrWhiteSpace(compactLog))
            {
                builder.AppendLine(compactLog);
            }

            return builder.ToString().Trim();
        }

        private string? TryHandoffSceneToUnity(
            BlenderAttemptResult attempt,
            BlenderScenePlan plan
        )
        {
            string root = settings.UnityProjectRoot;
            if (string.IsNullOrWhiteSpace(root)
                || !Directory.Exists(Path.Combine(root, "Assets")))
            {
                return null;
            }

            activity("[BLENDER UNITY] copying model bundle and scene manifest");

            string modelDirectory = Path.Combine(
                root,
                "Assets",
                "AI_Generated",
                "Models",
                attempt.SafeSceneName
            );
            string sceneDirectory = Path.Combine(
                root,
                "Assets",
                "AI_Generated",
                "Scenes"
            );
            Directory.CreateDirectory(modelDirectory);
            Directory.CreateDirectory(sceneDirectory);

            Dictionary<string, string> unityAssetPaths =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (AssetRuntimeSpec runtimeAsset in attempt.RuntimeAssets)
            {
                string fileName = runtimeAsset.SafeName + "." + runtimeAsset.Format;
                string destination = Path.Combine(modelDirectory, fileName);
                File.Copy(runtimeAsset.ExportPath, destination, true);

                string unityPath =
                    "Assets/AI_Generated/Models/"
                    + attempt.SafeSceneName
                    + "/"
                    + fileName;

                unityAssetPaths[runtimeAsset.Plan.AssetName] = unityPath;
            }

            List<object> manifestInstances = new List<object>();
            foreach (BlenderInstancePlan instance in plan.Instances)
            {
                if (!unityAssetPaths.TryGetValue(instance.AssetName, out string? assetPath))
                {
                    continue;
                }

                manifestInstances.Add(
                    new
                    {
                        assetPath,
                        name = string.IsNullOrWhiteSpace(instance.Name)
                            ? instance.AssetName
                            : instance.Name,
                        position = instance.Position,
                        rotation = instance.Rotation,
                        scale = instance.Scale
                    }
                );
            }

            string manifestPath = Path.Combine(
                sceneDirectory,
                attempt.SafeSceneName + ".aiscene.json"
            );

            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(
                    new
                    {
                        version = 1,
                        sceneName = plan.SceneName,
                        rootName = "AI_Generated_" + attempt.SafeSceneName,
                        replaceExisting = true,
                        generatedUtc = DateTime.UtcNow,
                        instances = manifestInstances
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );

            string topologyPath = Path.Combine(
                sceneDirectory,
                attempt.SafeSceneName + ".topology.json"
            );
            File.WriteAllText(
                topologyPath,
                JsonSerializer.Serialize(
                    attempt.Topology,
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );

            activity(
                "[BLENDER UNITY] queued "
                + manifestInstances.Count
                + " instance(s) for automatic scene assembly"
            );

            return manifestPath;
        }

        private static async Task<string> ProbeBlenderVersionAsync(
            string blenderExe,
            CancellationToken cancellationToken
        )
        {
            ProcessResult result = await RunProcessAsync(
                blenderExe,
                "--version",
                Path.GetDirectoryName(blenderExe) ?? Environment.CurrentDirectory,
                BlenderProbeTimeoutSeconds,
                cancellationToken
            );

            string firstLine = (result.Output ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line =>
                    line.StartsWith("Blender ", StringComparison.OrdinalIgnoreCase)
                )
                ?? "";

            return string.IsNullOrWhiteSpace(firstLine)
                ? "Blender (version probe unavailable)"
                : firstLine.Trim();
        }

        private static async Task<ProcessResult> RunBlenderAsync(
            string blenderExe,
            string scriptPath,
            string logPath,
            CancellationToken cancellationToken
        )
        {
            string arguments =
                "--background --factory-startup --python \""
                + scriptPath
                + "\"";

            ProcessResult result = await RunProcessAsync(
                blenderExe,
                arguments,
                Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory,
                BlenderRunTimeoutSeconds,
                cancellationToken
            );

            File.WriteAllText(logPath, result.Output ?? "");
            return result;
        }

        private static async Task<ProcessResult> RunProcessAsync(
            string executable,
            string arguments,
            string workingDirectory,
            int timeoutSeconds,
            CancellationToken cancellationToken
        )
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ProcessResult(-4, "AI_HOST_CANCELLED", false, true);
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                };

                using Process process = new Process { StartInfo = psi };
                process.Start();

                Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                Task waitTask = process.WaitForExitAsync();
                Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
                Task cancelTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

                Task completed = await Task.WhenAny(
                    waitTask,
                    timeoutTask,
                    cancelTask
                );

                bool timedOut = completed == timeoutTask;
                bool cancelled = completed == cancelTask
                    || cancellationToken.IsCancellationRequested;

                if (timedOut || cancelled)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                    }

                    try
                    {
                        await process.WaitForExitAsync();
                    }
                    catch
                    {
                    }
                }
                else
                {
                    await waitTask;
                }

                string stdout = await stdoutTask;
                string stderr = await stderrTask;
                string output = stdout
                    + (string.IsNullOrWhiteSpace(stderr)
                        ? ""
                        : "\n" + stderr);

                if (timedOut)
                {
                    output += "\nAI_HOST_TIMEOUT: process exceeded "
                        + timeoutSeconds
                        + " seconds.";
                }

                if (cancelled)
                {
                    output += "\nAI_HOST_CANCELLED: user stopped the task.";
                }

                int exitCode = cancelled
                    ? -4
                    : timedOut
                        ? -2
                        : process.ExitCode;

                return new ProcessResult(
                    exitCode,
                    output,
                    timedOut,
                    cancelled
                );
            }
            catch (Exception ex)
            {
                return new ProcessResult(
                    -3,
                    ex.GetType().Name + ": " + ex.Message,
                    false,
                    false
                );
            }
        }

        private static string BuildExecutableScript(
            string generatedScript,
            string blendPath,
            List<AssetRuntimeSpec> assets,
            List<string> previewPaths
        )
        {
            string pyBlend = PythonLiteral(blendPath);

            StringBuilder script = new StringBuilder();
            script.AppendLine("import bpy");
            script.AppendLine("import bmesh");
            script.AppendLine("import json");
            script.AppendLine("import math");
            script.AppendLine("import traceback");
            script.AppendLine("from mathutils import Vector");
            script.AppendLine("try:");
            script.AppendLine("    bpy.ops.object.select_all(action='SELECT')");
            script.AppendLine("    bpy.ops.object.delete(use_global=False)");
            script.AppendLine("    # ---- AI generated reusable asset construction ----");
            script.Append(IndentPython(generatedScript, 4));
            script.AppendLine("    # ---- host controlled topology inspection + per-asset export ----");
            script.AppendLine("    asset_specs = [");

            foreach (AssetRuntimeSpec asset in assets)
            {
                script.AppendLine(
                    "        {"
                    + "'name': " + PythonLiteral(asset.Plan.AssetName) + ", "
                    + "'root': " + PythonLiteral(asset.Plan.RootObject) + ", "
                    + "'format': " + PythonLiteral(asset.Format) + ", "
                    + "'path': " + PythonLiteral(asset.ExportPath) + ", "
                    + "'target': " + Math.Max(0, asset.Plan.TargetTriangles)
                    + "},"
                );
            }

            script.AppendLine("    ]");
            script.AppendLine("    bpy.ops.wm.save_as_mainfile(filepath=" + pyBlend + ")");
            script.AppendLine("    topology = []");
            script.AppendLine("    for spec in asset_specs:");
            script.AppendLine("        root = bpy.data.objects.get(spec['root'])");
            script.AppendLine("        item = {'asset_name': spec['name'], 'root_object': spec['root'], 'triangles': 0, 'vertices': 0, 'edges': 0, 'polygons': 0, 'mesh_objects': 0, 'material_slots': 0, 'nonmanifold_edges': 0, 'loose_vertices': 0, 'degenerate_faces': 0, 'dimensions': [0.0,0.0,0.0], 'target_triangles': spec['target'], 'score': 100, 'warnings': [], 'missing_root': False}");
            script.AppendLine("        if root is None:");
            script.AppendLine("            item['missing_root'] = True");
            script.AppendLine("            item['score'] = 0");
            script.AppendLine("            item['warnings'].append('root object missing')");
            script.AppendLine("            topology.append(item)");
            script.AppendLine("            continue");
            script.AppendLine("        objs = []");
            script.AppendLine("        stack = [root]");
            script.AppendLine("        while stack:");
            script.AppendLine("            current = stack.pop()");
            script.AppendLine("            if current in objs:");
            script.AppendLine("                continue");
            script.AppendLine("            objs.append(current)");
            script.AppendLine("            stack.extend(list(current.children))");
            script.AppendLine("        mesh_objs = [o for o in objs if o.type == 'MESH']");
            script.AppendLine("        item['mesh_objects'] = len(mesh_objs)");
            script.AppendLine("        item['material_slots'] = sum(len(o.material_slots) for o in mesh_objs)");
            script.AppendLine("        coords = []");
            script.AppendLine("        for obj in mesh_objs:");
            script.AppendLine("            mesh = obj.data");
            script.AppendLine("            mesh.calc_loop_triangles()");
            script.AppendLine("            item['triangles'] += len(mesh.loop_triangles)");
            script.AppendLine("            item['vertices'] += len(mesh.vertices)");
            script.AppendLine("            item['edges'] += len(mesh.edges)");
            script.AppendLine("            item['polygons'] += len(mesh.polygons)");
            script.AppendLine("            item['degenerate_faces'] += sum(1 for p in mesh.polygons if p.area <= 1e-10)");
            script.AppendLine("            bm = bmesh.new()");
            script.AppendLine("            bm.from_mesh(mesh)");
            script.AppendLine("            item['nonmanifold_edges'] += sum(1 for e in bm.edges if not e.is_manifold)");
            script.AppendLine("            item['loose_vertices'] += sum(1 for v in bm.verts if len(v.link_edges) == 0)");
            script.AppendLine("            bm.free()");
            script.AppendLine("            for corner in obj.bound_box:");
            script.AppendLine("                coords.append(obj.matrix_world @ Vector(corner))");
            script.AppendLine("        if coords:");
            script.AppendLine("            xs = [v.x for v in coords]; ys = [v.y for v in coords]; zs = [v.z for v in coords]");
            script.AppendLine("            item['dimensions'] = [max(xs)-min(xs), max(ys)-min(ys), max(zs)-min(zs)]");
            script.AppendLine("        if item['triangles'] <= 0:");
            script.AppendLine("            item['score'] = 0");
            script.AppendLine("            item['warnings'].append('no mesh triangles')");
            script.AppendLine("        if spec['target'] > 0 and item['triangles'] > 0:");
            script.AppendLine("            deviation = abs(item['triangles'] - spec['target']) / float(spec['target'])");
            script.AppendLine("            if deviation > 0.5:");
            script.AppendLine("                item['score'] -= min(30, int(deviation * 18))");
            script.AppendLine("                item['warnings'].append('triangle target deviation {:.0%}'.format(deviation))");
            script.AppendLine("        if item['polygons'] > 0 and item['degenerate_faces'] / float(item['polygons']) > 0.01:");
            script.AppendLine("            item['score'] -= 20");
            script.AppendLine("            item['warnings'].append('degenerate faces detected')");
            script.AppendLine("        if item['edges'] > 0 and item['nonmanifold_edges'] / float(item['edges']) > 0.25:");
            script.AppendLine("            item['score'] -= 12");
            script.AppendLine("            item['warnings'].append('high non-manifold edge ratio')");
            script.AppendLine("        if item['loose_vertices'] > 0:");
            script.AppendLine("            item['score'] -= min(15, item['loose_vertices'])");
            script.AppendLine("            item['warnings'].append('loose vertices detected')");
            script.AppendLine("        if coords and min(item['dimensions']) <= 0.0001:");
            script.AppendLine("            item['score'] -= 20");
            script.AppendLine("            item['warnings'].append('near-zero bounding dimension')");
            script.AppendLine("        item['score'] = max(0, min(100, item['score']))");
            script.AppendLine("        bpy.ops.object.select_all(action='DESELECT')");
            script.AppendLine("        for obj in objs:");
            script.AppendLine("            obj.select_set(True)");
            script.AppendLine("        bpy.context.view_layer.objects.active = root");
            script.AppendLine("        if spec['format'] == 'glb':");
            script.AppendLine("            bpy.ops.export_scene.gltf(filepath=spec['path'], export_format='GLB', use_selection=True)");
            script.AppendLine("        else:");
            script.AppendLine("            bpy.ops.export_scene.fbx(filepath=spec['path'], use_selection=True)");
            script.AppendLine("        topology.append(item)");
            script.AppendLine("    # Host-controlled multi-angle preview; generated code cannot fake or skip visual QA.");
            script.AppendLine("    bpy.context.view_layer.update()");
            script.AppendLine("    preview_objects = [o for o in bpy.context.scene.objects if o.type in {'MESH','CURVE','FONT'}]");
            script.AppendLine("    preview_corners = []");
            script.AppendLine("    for obj in preview_objects:");
            script.AppendLine("        preview_corners.extend([obj.matrix_world @ Vector(corner) for corner in obj.bound_box])");
            script.AppendLine("    if preview_corners:");
            script.AppendLine("        preview_min = Vector((min(v.x for v in preview_corners), min(v.y for v in preview_corners), min(v.z for v in preview_corners)))");
            script.AppendLine("        preview_max = Vector((max(v.x for v in preview_corners), max(v.y for v in preview_corners), max(v.z for v in preview_corners)))");
            script.AppendLine("        preview_center = (preview_min + preview_max) * 0.5");
            script.AppendLine("        preview_radius = max(1.5, (preview_max - preview_min).length * 0.62)");
            script.AppendLine("        scene = bpy.context.scene");
            script.AppendLine("        scene.render.resolution_x = 768; scene.render.resolution_y = 768; scene.render.resolution_percentage = 100");
            script.AppendLine("        scene.render.image_settings.file_format = 'PNG'");
            script.AppendLine("        scene.world.color = (0.045, 0.055, 0.075)");
            script.AppendLine("        try: scene.render.engine = 'BLENDER_EEVEE_NEXT'");
            script.AppendLine("        except: scene.render.engine = 'BLENDER_EEVEE'");
            script.AppendLine("        camera_data = bpy.data.cameras.new('AIA_QA_Camera')");
            script.AppendLine("        camera = bpy.data.objects.new('AIA_QA_Camera', camera_data)");
            script.AppendLine("        scene.collection.objects.link(camera); scene.camera = camera");
            script.AppendLine("        key_data = bpy.data.lights.new('AIA_QA_Key', 'AREA'); key_data.energy = 1400; key_data.size = max(4.0, preview_radius)");
            script.AppendLine("        key = bpy.data.objects.new('AIA_QA_Key', key_data); scene.collection.objects.link(key); key.location = preview_center + Vector((preview_radius*0.7, -preview_radius*0.8, preview_radius*1.2))");
            script.AppendLine("        fill_data = bpy.data.lights.new('AIA_QA_Fill', 'AREA'); fill_data.energy = 800; fill_data.size = max(3.0, preview_radius)");
            script.AppendLine("        fill = bpy.data.objects.new('AIA_QA_Fill', fill_data); scene.collection.objects.link(fill); fill.location = preview_center + Vector((-preview_radius*0.8, preview_radius*0.3, preview_radius*0.6))");
            script.AppendLine("        for lamp in (key, fill): lamp.rotation_euler = ((preview_center-lamp.location).to_track_quat('-Z','Y')).to_euler()");
            script.AppendLine("        preview_views = [((1.15,-1.35,0.85), " + PythonLiteral(previewPaths[0]) + "), ((0.0,-1.8,0.35), " + PythonLiteral(previewPaths[1]) + "), ((1.8,0.0,0.35), " + PythonLiteral(previewPaths[2]) + ")]");
            script.AppendLine("        for direction, path in preview_views:");
            script.AppendLine("            camera.location = preview_center + Vector(direction) * preview_radius");
            script.AppendLine("            camera.rotation_euler = ((preview_center-camera.location).to_track_quat('-Z','Y')).to_euler(); camera.data.lens = 52");
            script.AppendLine("            scene.render.filepath = path; bpy.ops.render.render(write_still=True)");
            script.AppendLine("    print('AI_TOPOLOGY_JSON:' + json.dumps(topology, separators=(',', ':')))");
            script.AppendLine("    print('AI_ASSET_EXPORT_OK')");
            script.AppendLine("except Exception:");
            script.AppendLine("    print('AI_ASSET_EXPORT_FAILED')");
            script.AppendLine("    traceback.print_exc()");
            script.AppendLine("    raise");
            return script.ToString();
        }

        private static string HardenGeneratedScript(
            string script,
            out bool changed
        )
        {
            string value = script ?? "";
            string original = value;

            // Deterministic Blender 2.7x/early-2.8 API shims for scripts produced
            // by free models. These are safe one-to-one renames for Blender 3.6+
            // and avoid wasting a model repair call on known compatibility drift.
            value = value
                .Replace(".empty_draw_type", ".empty_display_type", StringComparison.Ordinal)
                .Replace(".empty_draw_size", ".empty_display_size", StringComparison.Ordinal)
                .Replace("bpy.data.lamps", "bpy.data.lights", StringComparison.Ordinal)
                .Replace("bpy.ops.object.lamp_add", "bpy.ops.object.light_add", StringComparison.Ordinal)
                .Replace("bpy.types.Lamp", "bpy.types.Light", StringComparison.Ordinal)
                .Replace("bpy.context.scene.objects.active", "bpy.context.view_layer.objects.active", StringComparison.Ordinal)
                .Replace("bpy.context.scene.cursor_location", "bpy.context.scene.cursor.location", StringComparison.Ordinal)
                .Replace("bpy.context.scene.objects.link(", "bpy.context.scene.collection.objects.link(", StringComparison.Ordinal);

            const string legacySelectPattern =
                @"(?m)^(?<indent>[ \t]*)(?<expr>[A-Za-z_][A-Za-z0-9_\.]*)\.select\s*=\s*(?<state>True|False)\s*$";

            value = Regex.Replace(
                value,
                legacySelectPattern,
                match =>
                    match.Groups["indent"].Value
                    + match.Groups["expr"].Value
                    + ".select_set("
                    + match.Groups["state"].Value
                    + ")"
            );

            const string dottedCollectionPattern =
                @"(?m)^(?<indent>[ \t]*)for\s+(?<target>[A-Za-z_][A-Za-z0-9_]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*)\s+in\s+(?<expr>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\s*:\s*$";

            value = Regex.Replace(
                value,
                dottedCollectionPattern,
                match =>
                    match.Groups["indent"].Value
                    + "for "
                    + match.Groups["target"].Value
                    + " in list("
                    + match.Groups["expr"].Value
                    + "):"
            );

            const string namedCollectionPattern =
                @"(?m)^(?<indent>[ \t]*)for\s+(?<target>[A-Za-z_][A-Za-z0-9_]*(?:\s*,\s*[A-Za-z_][A-Za-z0-9_]*)*)\s+in\s+(?<expr>nodes|objects|materials|meshes|curves|collections|modifiers|slots|polygons|vertices|edges|faces|children|inputs|outputs)\s*:\s*$";

            value = Regex.Replace(
                value,
                namedCollectionPattern,
                match =>
                    match.Groups["indent"].Value
                    + "for "
                    + match.Groups["target"].Value
                    + " in list("
                    + match.Groups["expr"].Value
                    + "):"
            );

            changed = !string.Equals(
                original,
                value,
                StringComparison.Ordinal
            );

            return value;
        }

        private static string IndentPython(string value, int spaces)
        {
            string prefix = new string(' ', spaces);
            string normalized = (value ?? "")
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');

            StringBuilder builder = new StringBuilder();
            foreach (string line in normalized.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    builder.AppendLine();
                }
                else
                {
                    builder.Append(prefix);
                    builder.AppendLine(line);
                }
            }

            return builder.ToString();
        }

        private static string BuildSystemPrompt(string blenderVersion, string goal)
        {
            bool character = IsCharacterGoal(goal);
            string schemaExample = character
                ? "{\"scene_name\":\"SurvivalCharacter\",\"summary\":\"short\",\"script\":\"complete Python\",\"assets\":[{\"asset_name\":\"SurvivalCharacter\",\"root_object\":\"AIA_SurvivalCharacter\",\"export_format\":\"fbx\",\"target_triangles\":25000}],\"instances\":[{\"asset_name\":\"SurvivalCharacter\",\"name\":\"SurvivalCharacter\",\"position\":[0,0,0],\"rotation\":[0,0,0],\"scale\":[1,1,1]}]}"
                : "{\"scene_name\":\"Name\",\"summary\":\"short\",\"script\":\"complete Python\",\"assets\":[{\"asset_name\":\"Pump\",\"root_object\":\"AIA_Pump\",\"export_format\":\"fbx\",\"target_triangles\":600}],\"instances\":[{\"asset_name\":\"Pump\",\"name\":\"Pump_01\",\"position\":[0,0,0],\"rotation\":[0,0,0],\"scale\":[1,1,1]}]}";
            string common =
                "You are the Blender implementation engine for a controlled autonomous game-scene pipeline. "
                + "Target runtime is " + blenderVersion + ". "
                + "Return strict JSON only with this schema: "
                + schemaExample + ". "
                + "Every asset MUST have one exact root object named by root_object. Parent every mesh/material object belonging to that asset under that root. Keep each reusable asset root at world origin with identity rotation/scale; scene placement belongs ONLY in the instances array so Unity can assemble it correctly. "
                + "script must be Python using only bpy, bmesh, math and mathutils. It constructs geometry/materials only and MUST NOT save or export; the host owns save/export and topology inspection. "
                + "The host starts clean, so do not clear the scene or delete unrelated datablocks. Never mutate an RNA collection while directly iterating it; use list(collection) snapshots. "
                + "For Blender 3.6 use current 3.x APIs only: use empty_display_type/empty_display_size (never empty_draw_type/empty_draw_size), bpy.data.lights (not lamps), object.select_set(...), bpy.context.view_layer.objects.active and scene.collection.objects.link(...). Do not use Blender 4-only node socket names or APIs. "
                + "Set realistic target_triangles per UNIQUE asset. Use FBX unless GLB is materially better. No markdown fences and no prose outside JSON.";

            if (!character)
            {
                return common
                    + " The script must create all unique reusable models requested by the user in one Blender run. For a complete environment, make a coherent asset kit and define the final layout through instances. Reuse assets instead of duplicating identical geometry. Use at most 12 unique assets and 48 instances."
                    + " Create purposeful game-ready geometry with sensible proportions, clean silhouettes, applied transforms where useful, named objects and Principled BSDF materials.";
            }

            return common
                + " This is an ORGANIC CHARACTER build, not a primitive asset kit. Return exactly ONE character asset and exactly ONE identity instance at the origin. "
                + "Create a complete 1.7-1.9 meter humanoid in a neutral A-pose, facing Blender -Y/front, with believable approximately 7.5-head proportions. "
                + "The anatomical base must read as one continuous connected body: head, neck, torso, shoulders, arms, hands, pelvis, legs and feet. Clothing, hair and equipment may be separate fitted child meshes, but must sit on the body without floating gaps or obvious intersections. "
                + "Do not use cubes, capsules, spheres or cylinders as finished visible anatomy. Do not make a mannequin assembled from disconnected primitive objects. Use authored mesh vertices/faces, bmesh operations, connected cross-section loops, skin/subdivision workflows, or curves converted and joined into purposeful continuous surfaces. "
                + "Include readable hands and feet, facial planes/features, joint transitions, layered shirt/jacket/pants/boots as appropriate, hair, seams/panels and several intentionally different materials. Smooth shading alone is not detail. "
                + "For AA or High quality target roughly 15,000-35,000 purposeful final triangles and at least 8 meaningful material slots across the complete character; do not inflate counts with hidden or duplicated geometry. "
                + "Apply transforms and relevant modifiers before export. Parent every character object beneath the exact root_object. Keep loose vertices at zero and non-manifold geometry minimal. The host will render three angles and reject incomplete, primitive-looking or visually weak results before Unity import.";
        }

        private static string BuildUserPrompt(
            string goal,
            string blenderVersion
        )
        {
            if (IsCharacterGoal(goal))
            {
                return
                    "Build only the requested standalone character from this ONE instruction. Do not include or reconstruct any environment from Unity.\n"
                    + "USER GOAL:\n" + goal
                    + "\nRUNTIME:\n" + blenderVersion
                    + "\nProduce one complete character asset, one root and one origin instance. The host—not your script—will save, inspect topology, render visual QA, export FBX and import it into Unity. A technically valid primitive mannequin is a failed result.";
            }

            return
                "Build the complete requested result from this ONE user instruction. Do not require a second prompt to export or place assets in Unity.\n"
                + "USER GOAL:\n" + goal
                + "\nRUNTIME:\n" + blenderVersion
                + "\nCreate all unique reusable models, define every scene instance transform, and make the layout coherent immediately. For environment requests, include the essential supporting props needed to read as the requested place/era while keeping the requested detail level. The host will inspect topology, export every unique asset separately, copy them to Unity, and assemble all instances automatically.";
        }

        private static string BuildRepairPrompt(
            string goal,
            string blenderVersion,
            BlenderScenePlan previous,
            BlenderAttemptResult attempt
        )
        {
            return
                "The controlled Blender character build failed execution, topology, or visual verification. Return one corrected COMPLETE scene JSON object only. Preserve the user's goal while directly fixing the reported geometry/API/root/topology/visual issue. Do not repeat the same failure and do not add an environment. "
                + "If an asset root is missing, create the exact root_object and parent that asset beneath it. If topology reports zero triangles or a very low score, repair that asset. If the log mentions structure changed during iteration, snapshot the collection with list(collection). For Blender 3.6 use empty_display_type/empty_display_size, never legacy empty_draw_type/empty_draw_size. Do not perform scene cleanup; the host already starts clean.\n\n"
                + "GOAL:\n" + goal
                + "\n\nTARGET RUNTIME:\n" + blenderVersion
                + "\n\nFAILED SCENE PLAN:\n" + Compact(JsonSerializer.Serialize(previous), 9000)
                + "\n\nHOST VERIFICATION:\n"
                + "exit=" + attempt.ExitCode
                + ", timeout=" + attempt.TimedOut
                + ", blend=" + attempt.BlendExists
                + ", pythonFailure=" + attempt.PythonFailure
                + ", topologyCritical=" + attempt.TopologyCritical
                + ", visualQuality=" + attempt.VisualQualityOk
                + "\nTOPOLOGY:\n" + Compact(JsonSerializer.Serialize(attempt.Topology), 5000)
                + "\n\nVISUAL QA:\nscore=" + attempt.VisualScore + "/100\n" + Compact(attempt.VisualFeedback, 3500)
                + "\n\nBLENDER LOG:\n" + Compact(attempt.Output, 4500)
                + "\n\nReturn the same strict scene_name, summary, script, assets, instances schema. The host owns save/export/Unity assembly.";
        }

        private static bool TryParsePlan(
            string text,
            out BlenderScenePlan plan,
            out string error
        )
        {
            plan = new BlenderScenePlan();
            error = "";

            try
            {
                string json = AgentJsonV2.ExtractObject(text);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                plan.SceneName = ReadString(root, "scene_name");
                plan.Summary = ReadString(root, "summary");
                plan.Script = ReadString(root, "script");

                if (root.TryGetProperty("assets", out JsonElement assets)
                    && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in assets.EnumerateArray())
                    {
                        if (plan.Assets.Count >= MaxUniqueAssets)
                        {
                            break;
                        }

                        BlenderAssetPlan asset = new BlenderAssetPlan
                        {
                            AssetName = ReadString(item, "asset_name"),
                            RootObject = ReadString(item, "root_object"),
                            ExportFormat = NormalizeFormat(ReadString(item, "export_format")),
                            TargetTriangles = ReadInt(item, "target_triangles")
                        };

                        if (!string.IsNullOrWhiteSpace(asset.AssetName)
                            && !string.IsNullOrWhiteSpace(asset.RootObject)
                            && !plan.Assets.Any(existing => existing.AssetName.Equals(asset.AssetName, StringComparison.OrdinalIgnoreCase)))
                        {
                            plan.Assets.Add(asset);
                        }
                    }
                }

                if (root.TryGetProperty("instances", out JsonElement instances)
                    && instances.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in instances.EnumerateArray())
                    {
                        if (plan.Instances.Count >= MaxInstances)
                        {
                            break;
                        }

                        BlenderInstancePlan instance = new BlenderInstancePlan
                        {
                            AssetName = ReadString(item, "asset_name"),
                            Name = ReadString(item, "name"),
                            Position = ReadVector(item, "position", new[] { 0f, 0f, 0f }),
                            Rotation = ReadVector(item, "rotation", new[] { 0f, 0f, 0f }),
                            Scale = ReadVector(item, "scale", new[] { 1f, 1f, 1f })
                        };

                        if (!string.IsNullOrWhiteSpace(instance.AssetName))
                        {
                            plan.Instances.Add(instance);
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(plan.SceneName))
                {
                    plan.SceneName = plan.Assets.FirstOrDefault()?.AssetName ?? "AIScene";
                }

                if (string.IsNullOrWhiteSpace(plan.Script))
                {
                    error = "script is empty";
                    return false;
                }

                if (plan.Assets.Count == 0)
                {
                    error = "assets array is empty or missing valid root_object entries";
                    return false;
                }

                HashSet<string> known = new HashSet<string>(
                    plan.Assets.Select(asset => asset.AssetName),
                    StringComparer.OrdinalIgnoreCase
                );
                plan.Instances = plan.Instances
                    .Where(instance => known.Contains(instance.AssetName))
                    .Take(MaxInstances)
                    .ToList();

                if (plan.Instances.Count == 0)
                {
                    foreach (BlenderAssetPlan asset in plan.Assets)
                    {
                        plan.Instances.Add(
                            new BlenderInstancePlan
                            {
                                AssetName = asset.AssetName,
                                Name = asset.AssetName,
                                Position = new[] { 0f, 0f, 0f },
                                Rotation = new[] { 0f, 0f, 0f },
                                Scale = new[] { 1f, 1f, 1f }
                            }
                        );
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message + " | raw=" + Compact(text, 1400);
                return false;
            }
        }

        private static string ReadString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
        }

        private static int ReadInt(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out JsonElement value))
            {
                return 0;
            }

            if (value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int result))
            {
                return Math.Max(0, result);
            }

            return 0;
        }

        private static float[] ReadVector(
            JsonElement root,
            string name,
            float[] fallback
        )
        {
            if (!root.TryGetProperty(name, out JsonElement value)
                || value.ValueKind != JsonValueKind.Array
                || value.GetArrayLength() < 3)
            {
                return fallback.ToArray();
            }

            float[] result = fallback.ToArray();
            int index = 0;
            foreach (JsonElement component in value.EnumerateArray())
            {
                if (index >= 3)
                {
                    break;
                }

                if (component.ValueKind == JsonValueKind.Number
                    && component.TryGetSingle(out float number))
                {
                    result[index] = number;
                }
                index++;
            }

            if (name == "scale")
            {
                for (int i = 0; i < result.Length; i++)
                {
                    if (Math.Abs(result[i]) < 0.0001f)
                    {
                        result[i] = 1f;
                    }
                }
            }

            return result;
        }

        private static List<TopologyItem> ParseTopology(string output)
        {
            const string marker = "AI_TOPOLOGY_JSON:";
            string[] lines = (output ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            string? line = lines
                .LastOrDefault(value => value.StartsWith(marker, StringComparison.Ordinal));

            if (line == null)
            {
                return new List<TopologyItem>();
            }

            try
            {
                string json = line.Substring(marker.Length);
                return JsonSerializer.Deserialize<List<TopologyItem>>(
                    json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }
                ) ?? new List<TopologyItem>();
            }
            catch
            {
                return new List<TopologyItem>();
            }
        }

        private static bool IsTopologyCritical(
            List<AssetRuntimeSpec> assets,
            List<TopologyItem> topology
        )
        {
            if (topology.Count < assets.Count)
            {
                return true;
            }

            foreach (AssetRuntimeSpec asset in assets)
            {
                TopologyItem? item = topology.FirstOrDefault(
                    candidate => candidate.AssetName.Equals(
                        asset.Plan.AssetName,
                        StringComparison.OrdinalIgnoreCase
                    )
                );

                if (item == null
                    || item.MissingRoot
                    || item.Triangles <= 0
                    || item.Score < 60)
                {
                    return true;
                }
            }

            return false;
        }

        private static string BuildTopologySummary(List<TopologyItem> topology)
        {
            if (topology.Count == 0)
            {
                return "no topology report";
            }

            return string.Join(
                "; ",
                topology.Select(item =>
                    item.AssetName
                    + "=" + item.Triangles + " tris"
                    + ", score " + item.Score + "/100"
                    + (item.Warnings.Count == 0
                        ? ""
                        : " [" + string.Join(", ", item.Warnings.Take(3)) + "]")
                )
            );
        }

        private static bool IsSafeScript(
            string script,
            out string error
        )
        {
            string lower = (script ?? "").ToLowerInvariant();
            string[] blocked =
            {
                "import os",
                "import sys",
                "import subprocess",
                "import socket",
                "import requests",
                "import urllib",
                "shutil",
                "pathlib",
                "open(",
                "eval(",
                "exec(",
                "__import__",
                "bpy.ops.wm.open_mainfile",
                "bpy.ops.wm.save_as_mainfile",
                "bpy.ops.wm.quit_blender",
                "bpy.ops.wm.read_factory_settings",
                "bpy.ops.export_"
            };

            foreach (string token in blocked)
            {
                if (lower.Contains(token, StringComparison.Ordinal))
                {
                    error = "blocked token: " + token;
                    return false;
                }
            }

            error = "";
            return true;
        }

        private static bool ContainsPythonFailure(string output)
        {
            string text = (output ?? "").ToLowerInvariant();

            return
                text.Contains("ai_asset_export_failed")
                || text.Contains("traceback (most recent call last)")
                || text.Contains("error: python")
                || text.Contains("syntaxerror:")
                || text.Contains("nameerror:")
                || text.Contains("typeerror:")
                || text.Contains("attributeerror:")
                || text.Contains("runtimeerror:");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static string CleanGoal(string prompt)
        {
            string p = (prompt ?? "").Trim();
            if (p.StartsWith("/blender ", StringComparison.OrdinalIgnoreCase))
            {
                return p.Substring(9).Trim();
            }

            return p;
        }

        private static bool IsCharacterGoal(string goal)
        {
            string value = (goal ?? "").ToLowerInvariant();
            return value.Contains("request kind hint: character", StringComparison.Ordinal)
                || value.Contains("humanoid", StringComparison.Ordinal)
                || value.Contains("character", StringComparison.Ordinal)
                || value.Contains("karakter", StringComparison.Ordinal)
                || value.Contains("body mesh", StringComparison.Ordinal)
                || value.Contains("npc", StringComparison.Ordinal);
        }

        private static string DetectQuality(string goal)
        {
            string value = (goal ?? "").ToLowerInvariant();
            if (value.Contains("quality profile: aa", StringComparison.Ordinal)
                || value.Contains("aa-quality", StringComparison.Ordinal)
                || value.Contains("aa quality", StringComparison.Ordinal)) return "AA";
            if (value.Contains("quality profile: high", StringComparison.Ordinal)
                || value.Contains("high-quality", StringComparison.Ordinal)
                || value.Contains("high quality", StringComparison.Ordinal)) return "High";
            if (value.Contains("quality profile: low", StringComparison.Ordinal)
                || value.Contains("low-poly", StringComparison.Ordinal)
                || value.Contains("low poly", StringComparison.Ordinal)) return "Low";
            return "Medium";
        }

        private static string NormalizeFormat(string format)
        {
            return string.Equals(
                format,
                "glb",
                StringComparison.OrdinalIgnoreCase
            )
                ? "glb"
                : "fbx";
        }

        private static string SanitizeFileName(string value)
        {
            value ??= "";
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            value = value.Trim();
            return string.IsNullOrWhiteSpace(value)
                ? "AIAsset"
                : value;
        }

        private static string PythonLiteral(string value)
        {
            string safe = (value ?? "")
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "\\'", StringComparison.Ordinal)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal);

            return "'" + safe + "'";
        }

        private static string Compact(string? value, int max)
        {
            value ??= "";
            return value.Length <= max
                ? value
                : value.Substring(0, max) + "...";
        }

        private static ProviderReplyV2 CancelledProviderReply()
        {
            return new ProviderReplyV2
            {
                Success = false,
                Provider = "cancelled",
                StatusCode = 499,
                Error = "Agent work was cancelled by the user."
            };
        }

        private sealed class BlenderScenePlan
        {
            [JsonPropertyName("scene_name")]
            public string SceneName { get; set; } = "AIScene";

            [JsonPropertyName("summary")]
            public string Summary { get; set; } = "";

            [JsonPropertyName("script")]
            public string Script { get; set; } = "";

            [JsonPropertyName("assets")]
            public List<BlenderAssetPlan> Assets { get; set; } = new List<BlenderAssetPlan>();

            [JsonPropertyName("instances")]
            public List<BlenderInstancePlan> Instances { get; set; } = new List<BlenderInstancePlan>();
        }

        private sealed class BlenderAssetPlan
        {
            [JsonPropertyName("asset_name")]
            public string AssetName { get; set; } = "";

            [JsonPropertyName("root_object")]
            public string RootObject { get; set; } = "";

            [JsonPropertyName("export_format")]
            public string ExportFormat { get; set; } = "fbx";

            [JsonPropertyName("target_triangles")]
            public int TargetTriangles { get; set; }
        }

        private sealed class BlenderInstancePlan
        {
            [JsonPropertyName("asset_name")]
            public string AssetName { get; set; } = "";

            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("position")]
            public float[] Position { get; set; } = new[] { 0f, 0f, 0f };

            [JsonPropertyName("rotation")]
            public float[] Rotation { get; set; } = new[] { 0f, 0f, 0f };

            [JsonPropertyName("scale")]
            public float[] Scale { get; set; } = new[] { 1f, 1f, 1f };
        }

        private sealed class AssetRuntimeSpec
        {
            public BlenderAssetPlan Plan { get; set; } = new BlenderAssetPlan();
            public string SafeName { get; set; } = "";
            public string Format { get; set; } = "fbx";
            public string ExportPath { get; set; } = "";
            public bool ExportExists { get; set; }
        }

        private sealed class BlenderAttemptResult
        {
            public bool Success { get; set; }
            public bool ExecutionHealthy { get; set; }
            public int ExitCode { get; set; }
            public bool TimedOut { get; set; }
            public bool Cancelled { get; set; }
            public string Output { get; set; } = "";
            public string BlendPath { get; set; } = "";
            public string ScriptPath { get; set; } = "";
            public string LogPath { get; set; } = "";
            public string SafeSceneName { get; set; } = "";
            public bool BlendExists { get; set; }
            public bool PythonFailure { get; set; }
            public bool HostAdjusted { get; set; }
            public bool TopologyCritical { get; set; }
            public bool VisualReviewAvailable { get; set; }
            public bool VisualQualityOk { get; set; }
            public int VisualScore { get; set; }
            public string VisualFeedback { get; set; } = "";
            public List<string> PreviewPaths { get; set; } = new List<string>();
            public List<AssetRuntimeSpec> RuntimeAssets { get; set; } = new List<AssetRuntimeSpec>();
            public List<TopologyItem> Topology { get; set; } = new List<TopologyItem>();

            public static BlenderAttemptResult CancelledResult(
                string blendPath,
                string scriptPath,
                string logPath
            )
            {
                return new BlenderAttemptResult
                {
                    Cancelled = true,
                    ExitCode = -4,
                    BlendPath = blendPath,
                    ScriptPath = scriptPath,
                    LogPath = logPath,
                    Output = "AI_HOST_CANCELLED"
                };
            }
        }

        private sealed class TopologyItem
        {
            [JsonPropertyName("asset_name")]
            public string AssetName { get; set; } = "";

            [JsonPropertyName("root_object")]
            public string RootObject { get; set; } = "";

            [JsonPropertyName("triangles")]
            public int Triangles { get; set; }

            [JsonPropertyName("vertices")]
            public int Vertices { get; set; }

            [JsonPropertyName("edges")]
            public int Edges { get; set; }

            [JsonPropertyName("polygons")]
            public int Polygons { get; set; }

            [JsonPropertyName("mesh_objects")]
            public int MeshObjects { get; set; }

            [JsonPropertyName("material_slots")]
            public int MaterialSlots { get; set; }

            [JsonPropertyName("nonmanifold_edges")]
            public int NonmanifoldEdges { get; set; }

            [JsonPropertyName("loose_vertices")]
            public int LooseVertices { get; set; }

            [JsonPropertyName("degenerate_faces")]
            public int DegenerateFaces { get; set; }

            [JsonPropertyName("dimensions")]
            public float[] Dimensions { get; set; } = new[] { 0f, 0f, 0f };

            [JsonPropertyName("target_triangles")]
            public int TargetTriangles { get; set; }

            [JsonPropertyName("score")]
            public int Score { get; set; }

            [JsonPropertyName("warnings")]
            public List<string> Warnings { get; set; } = new List<string>();

            [JsonPropertyName("missing_root")]
            public bool MissingRoot { get; set; }
        }

        private readonly record struct ProcessResult(
            int ExitCode,
            string Output,
            bool TimedOut,
            bool Cancelled
        );
    }
}
