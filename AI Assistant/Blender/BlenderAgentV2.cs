using AI_Assistant.AgentV2;
using AI_Assistant.Runtime;

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AI_Assistant.Blender
{
    public sealed class BlenderAgentV2
    {
        private const int BlenderRunTimeoutSeconds = 180;
        private const int BlenderProbeTimeoutSeconds = 20;

        private readonly ProviderRouterV2 providers;
        private readonly RuntimeSettings settings;
        private readonly Action<string> activity;

        public BlenderAgentV2(RuntimeSettings settings, Action<string> activity)
        {
            this.settings = settings;
            this.activity = activity;
            providers = new ProviderRouterV2(activity);
        }

        public bool ShouldHandle(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            return p.StartsWith("/blender ")
                || p.Contains(" blender ")
                || p.StartsWith("blender ")
                || p.Contains("napravi model")
                || p.Contains("3d model");
        }

        public async Task<string> HandleAsync(string prompt)
        {
            string goal = CleanGoal(prompt);
            if (string.IsNullOrWhiteSpace(goal))
            {
                return "Blender Agent: napiši šta želiš da napravim, npr. /blender napravi low-poly barrel.";
            }

            string blenderExe = settings.ResolveBlenderExecutable();
            if (string.IsNullOrWhiteSpace(blenderExe) || !File.Exists(blenderExe))
            {
                return "Blender Agent nije spreman: Blender executable nije pronađen. Otvori Settings i postavi blender.exe.";
            }

            string blenderVersion = await ProbeBlenderVersionAsync(blenderExe);
            activity("[BLENDER] target " + blenderVersion);

            string workspace = settings.BlenderWorkspace;
            Directory.CreateDirectory(workspace);

            string runRoot = Path.Combine(
                workspace,
                "AI_Runs",
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
            );
            Directory.CreateDirectory(runRoot);

            activity("[BLENDER] designing controlled scene script");

            AgentTaskStateV2 task = new AgentTaskStateV2
            {
                Goal = goal,
                Phase = AgentTaskPhaseV2.Designing
            };

            ProviderReplyV2 reply = await providers.CompleteAsync(
                task,
                BuildSystemPrompt(blenderVersion),
                BuildUserPrompt(goal, blenderVersion)
            );

            if (!reply.Success)
            {
                return "Blender Agent model failure: " + reply.Error;
            }

            if (!TryParsePlan(reply.Content, out BlenderPlan plan, out string parseError))
            {
                return "Blender Agent received invalid JSON: " + parseError;
            }

            if (!IsSafeScript(plan.Script, out string safetyError))
            {
                return "Blender Agent blocked generated script: " + safetyError;
            }

            BlenderAttemptResult first = await ExecutePlanAsync(
                plan,
                runRoot,
                blenderExe,
                "build_asset.py",
                "blender.log"
            );

            if (first.Success)
            {
                return BuildSuccessReply(first, plan, reply);
            }

            // One bounded model repair pass. Before each Blender run the host also
            // applies deterministic compatibility hardening, so known RNA
            // mutation-during-iteration mistakes do not waste another model call.
            activity("[BLENDER REPAIR] correcting failed headless run from log");
            task.Phase = AgentTaskPhaseV2.Correcting;

            ProviderReplyV2 repairReply = await providers.CompleteAsync(
                task,
                BuildSystemPrompt(blenderVersion),
                BuildRepairPrompt(
                    goal,
                    blenderVersion,
                    plan,
                    first
                )
            );

            if (!repairReply.Success)
            {
                return BuildFailureReply(
                    first,
                    "Blender run failed and the repair model was unavailable: "
                    + repairReply.Error
                );
            }

            if (!TryParsePlan(repairReply.Content, out BlenderPlan repairedPlan, out string repairParseError))
            {
                return BuildFailureReply(
                    first,
                    "Blender run failed and repair JSON was invalid: " + repairParseError
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
                "build_asset_retry.py",
                "blender_retry.log"
            );

            if (!repaired.Success)
            {
                return BuildFailureReply(
                    repaired,
                    "Blender execution and one automatic repair pass both failed."
                );
            }

            return BuildSuccessReply(repaired, repairedPlan, repairReply);
        }

        private async Task<BlenderAttemptResult> ExecutePlanAsync(
            BlenderPlan plan,
            string runRoot,
            string blenderExe,
            string scriptFileName,
            string logFileName
        )
        {
            string safeName = SanitizeFileName(
                string.IsNullOrWhiteSpace(plan.AssetName)
                    ? "AIAsset"
                    : plan.AssetName
            );

            string format = NormalizeFormat(plan.ExportFormat);
            string blendPath = Path.Combine(runRoot, safeName + ".blend");
            string exportPath = Path.Combine(runRoot, safeName + "." + format);
            string scriptPath = Path.Combine(runRoot, scriptFileName);
            string logPath = Path.Combine(runRoot, logFileName);

            // Never let stale files from an earlier attempt make verification pass.
            TryDelete(blendPath);
            TryDelete(exportPath);

            string hardenedBody = HardenGeneratedScript(
                plan.Script,
                out bool hostAdjusted
            );

            if (hostAdjusted)
            {
                activity(
                    "[BLENDER HOSTFIX] snapshotted mutable Blender collections before iteration"
                );
            }

            string finalScript = BuildExecutableScript(
                hardenedBody,
                blendPath,
                exportPath,
                format
            );

            File.WriteAllText(
                scriptPath,
                finalScript,
                new UTF8Encoding(false)
            );

            activity("[BLENDER] executing headless Blender");

            ProcessResult execution = await RunBlenderAsync(
                blenderExe,
                scriptPath,
                logPath
            );

            bool blendExists = File.Exists(blendPath);
            bool exportExists = File.Exists(exportPath);
            bool pythonFailure = ContainsPythonFailure(execution.Output);
            bool success =
                execution.ExitCode == 0
                && !execution.TimedOut
                && !pythonFailure
                && blendExists
                && exportExists;

            BlenderAttemptResult result = new BlenderAttemptResult
            {
                Success = success,
                ExitCode = execution.ExitCode,
                TimedOut = execution.TimedOut,
                Output = execution.Output,
                BlendPath = blendPath,
                ExportPath = exportPath,
                ScriptPath = scriptPath,
                LogPath = logPath,
                SafeName = safeName,
                Format = format,
                BlendExists = blendExists,
                ExportExists = exportExists,
                PythonFailure = pythonFailure,
                HostAdjusted = hostAdjusted
            };

            if (success)
            {
                activity("[BLENDER VERIFY] .blend and export created");
            }
            else
            {
                activity("[BLENDER VERIFY] run failed verification; preparing repair delta");
            }

            return result;
        }

        private string BuildSuccessReply(
            BlenderAttemptResult attempt,
            BlenderPlan plan,
            ProviderReplyV2 reply
        )
        {
            string? unityAsset = TryHandoffToUnity(
                attempt.ExportPath,
                attempt.SafeName,
                attempt.Format
            );

            StringBuilder result = new StringBuilder();
            result.AppendLine(
                string.IsNullOrWhiteSpace(plan.Summary)
                    ? "Blender asset created."
                    : plan.Summary
            );
            result.AppendLine("Blend: " + attempt.BlendPath);
            result.AppendLine("Export: " + attempt.ExportPath);

            if (!string.IsNullOrWhiteSpace(unityAsset))
            {
                result.AppendLine("Unity handoff: " + unityAsset);
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
                + ", pythonFailure=" + attempt.PythonFailure
                + ", blend=" + attempt.BlendExists
                + ", export=" + attempt.ExportExists
                + ", hostAdjusted=" + attempt.HostAdjusted
            );

            string compactLog = Compact(attempt.Output, 1800);
            if (!string.IsNullOrWhiteSpace(compactLog))
            {
                builder.AppendLine(compactLog);
            }

            return builder.ToString().Trim();
        }

        private string? TryHandoffToUnity(
            string exportPath,
            string safeName,
            string format
        )
        {
            string root = settings.UnityProjectRoot;
            if (string.IsNullOrWhiteSpace(root)
                || !Directory.Exists(Path.Combine(root, "Assets")))
            {
                return null;
            }

            string destinationDirectory = Path.Combine(
                root,
                "Assets",
                "AI_Generated",
                "Models"
            );
            Directory.CreateDirectory(destinationDirectory);

            string destination = Path.Combine(
                destinationDirectory,
                safeName + "." + format
            );
            File.Copy(exportPath, destination, true);

            string manifest = Path.Combine(
                destinationDirectory,
                safeName + ".aiasset.json"
            );

            File.WriteAllText(
                manifest,
                JsonSerializer.Serialize(
                    new
                    {
                        asset = safeName,
                        source = exportPath,
                        generatedUtc = DateTime.UtcNow,
                        format
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                )
            );

            return destination;
        }

        private static async Task<string> ProbeBlenderVersionAsync(
            string blenderExe
        )
        {
            ProcessResult result = await RunProcessAsync(
                blenderExe,
                "--version",
                Path.GetDirectoryName(blenderExe) ?? Environment.CurrentDirectory,
                BlenderProbeTimeoutSeconds
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
            string logPath
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
                BlenderRunTimeoutSeconds
            );

            File.WriteAllText(logPath, result.Output ?? "");
            return result;
        }

        private static async Task<ProcessResult> RunProcessAsync(
            string executable,
            string arguments,
            string workingDirectory,
            int timeoutSeconds
        )
        {
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

                Task completed = await Task.WhenAny(waitTask, timeoutTask);
                bool timedOut = completed == timeoutTask;

                if (timedOut)
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

                int exitCode = timedOut
                    ? -2
                    : process.ExitCode;

                return new ProcessResult(exitCode, output, timedOut);
            }
            catch (Exception ex)
            {
                return new ProcessResult(
                    -3,
                    ex.GetType().Name + ": " + ex.Message,
                    false
                );
            }
        }

        private static string BuildExecutableScript(
            string generatedScript,
            string blendPath,
            string exportPath,
            string format
        )
        {
            string pyBlend = PythonLiteral(blendPath);
            string pyExport = PythonLiteral(exportPath);

            StringBuilder script = new StringBuilder();
            script.AppendLine("import bpy");
            script.AppendLine("import math");
            script.AppendLine("import traceback");
            script.AppendLine("from mathutils import Vector");
            script.AppendLine("try:");
            script.AppendLine("    bpy.ops.object.select_all(action='SELECT')");
            script.AppendLine("    bpy.ops.object.delete(use_global=False)");
            script.AppendLine("    # ---- AI generated scene construction ----");
            script.Append(IndentPython(generatedScript, 4));
            script.AppendLine("    # ---- host controlled save/export ----");
            script.AppendLine("    bpy.ops.wm.save_as_mainfile(filepath=" + pyBlend + ")");

            if (format == "glb")
            {
                script.AppendLine(
                    "    bpy.ops.export_scene.gltf(filepath="
                    + pyExport
                    + ", export_format='GLB')"
                );
            }
            else
            {
                // Keep exporter arguments intentionally conservative so this
                // remains compatible with Blender 3.6 LTS and Blender 4.x.
                script.AppendLine(
                    "    bpy.ops.export_scene.fbx(filepath="
                    + pyExport
                    + ", use_selection=False)"
                );
            }

            script.AppendLine("    print('AI_ASSET_EXPORT_OK')");
            script.AppendLine("except Exception:");
            script.AppendLine("    print('AI_ASSET_EXPORT_FAILED')");
            script.AppendLine("    traceback.print_exc()");
            script.AppendLine("    raise");
            return script.ToString();
        }

        // Blender RNA collections invalidate active iterators when the generated
        // code removes/relinks datablocks from the same collection. Free models
        // commonly emit `for material in bpy.data.materials: ... remove(...)`,
        // which throws "structure changed during iteration" on Blender 3.6.
        // Snapshot simple RNA/collection iterations into list(...) deterministically.
        private static string HardenGeneratedScript(
            string script,
            out bool changed
        )
        {
            string value = script ?? "";
            string original = value;

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

        private static string BuildSystemPrompt(string blenderVersion)
        {
            return
                "You are the Blender implementation engine for a controlled 3D asset pipeline. "
                + "Target runtime is " + blenderVersion + ". "
                + "Return strict JSON only with keys asset_name, export_format, summary, script. "
                + "script must be Python using only bpy, math and mathutils. It must construct the requested model/scene but MUST NOT save/export files; the host does that. "
                + "The host already starts from a clean factory scene and removes default scene objects, so DO NOT perform scene cleanup or delete unrelated datablocks. "
                + "Never mutate a Blender RNA collection while directly iterating it. If a collection may be modified, iterate over list(collection), for example `for material in list(bpy.data.materials):`. "
                + "Do not access network, filesystem, subprocesses, shell, environment variables, addons, external files or delete arbitrary files. "
                + "Use APIs available in the target Blender version. For Blender 3.6 do not use Blender 4-only node socket names or APIs. "
                + "Prefer deterministic geometry, sensible transforms, named objects, applied scale where useful, clean topology for simple hard-surface/low-poly assets, and Principled BSDF materials. "
                + "export_format must be fbx or glb. No markdown fences.";
        }

        private static string BuildUserPrompt(
            string goal,
            string blenderVersion
        )
        {
            return
                "Create a Blender implementation for this goal:\n"
                + goal
                + "\nRuntime: " + blenderVersion
                + "\nThe host starts from an empty factory scene and will save/export after your script. "
                + "Do not clear the scene or delete datablocks. Keep the script self-contained, deterministic and compatible with that exact Blender runtime.";
        }

        private static string BuildRepairPrompt(
            string goal,
            string blenderVersion,
            BlenderPlan previous,
            BlenderAttemptResult attempt
        )
        {
            return
                "The first controlled Blender run failed. Return a corrected complete JSON object only. "
                + "Do not repeat the same incompatible API. Preserve the requested visual goal while fixing only the execution problem. "
                + "If the log mentions structure changed during iteration, StructRNA removed, or collection mutation, take a snapshot with list(collection) before removing/relinking anything. "
                + "Do not perform scene cleanup; the host already starts clean.\n\n"
                + "GOAL:\n" + goal
                + "\n\nTARGET RUNTIME:\n" + blenderVersion
                + "\n\nFAILED SCRIPT:\n" + Compact(previous.Script, 5000)
                + "\n\nHOST VERIFICATION:\n"
                + "exit=" + attempt.ExitCode
                + ", timeout=" + attempt.TimedOut
                + ", blend=" + attempt.BlendExists
                + ", export=" + attempt.ExportExists
                + ", pythonFailure=" + attempt.PythonFailure
                + ", hostAdjusted=" + attempt.HostAdjusted
                + "\n\nBLENDER LOG:\n" + Compact(attempt.Output, 4500)
                + "\n\nReturn keys asset_name, export_format, summary, script. The host owns save/export.";
        }

        private static bool TryParsePlan(
            string text,
            out BlenderPlan plan,
            out string error
        )
        {
            plan = new BlenderPlan();
            error = "";

            try
            {
                string json = AgentJsonV2.ExtractObject(text);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                plan.AssetName = ReadString(root, "asset_name");
                plan.ExportFormat = ReadString(root, "export_format");
                plan.Summary = ReadString(root, "summary");
                plan.Script = ReadString(root, "script");

                if (string.IsNullOrWhiteSpace(plan.Script))
                {
                    error = "script is empty";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message + " | raw=" + Compact(text, 1000);
                return false;
            }
        }

        private static string ReadString(
            JsonElement root,
            string name
        )
        {
            return root.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : "";
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
            return "r\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string Compact(string? value, int max)
        {
            value ??= "";
            return value.Length <= max
                ? value
                : value.Substring(0, max) + "...";
        }

        private sealed class BlenderPlan
        {
            public string AssetName { get; set; } = "";
            public string ExportFormat { get; set; } = "fbx";
            public string Summary { get; set; } = "";
            public string Script { get; set; } = "";
        }

        private sealed class BlenderAttemptResult
        {
            public bool Success { get; set; }
            public int ExitCode { get; set; }
            public bool TimedOut { get; set; }
            public string Output { get; set; } = "";
            public string BlendPath { get; set; } = "";
            public string ExportPath { get; set; } = "";
            public string ScriptPath { get; set; } = "";
            public string LogPath { get; set; } = "";
            public string SafeName { get; set; } = "";
            public string Format { get; set; } = "fbx";
            public bool BlendExists { get; set; }
            public bool ExportExists { get; set; }
            public bool PythonFailure { get; set; }
            public bool HostAdjusted { get; set; }
        }

        private readonly record struct ProcessResult(
            int ExitCode,
            string Output,
            bool TimedOut
        );
    }
}
