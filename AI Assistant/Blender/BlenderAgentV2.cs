using AI_Assistant.AgentV2;
using AI_Assistant.Runtime;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AI_Assistant.Blender
{
    public sealed class BlenderAgentV2
    {
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

            string workspace = settings.BlenderWorkspace;
            Directory.CreateDirectory(workspace);
            string runRoot = Path.Combine(
                workspace,
                "AI_Runs",
                DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")
            );
            Directory.CreateDirectory(runRoot);

            activity("[BLENDER] designing controlled scene script");

            AgentTaskStateV2 task = new AgentTaskStateV2 { Goal = goal };
            ProviderReplyV2 reply = await providers.CompleteAsync(
                task,
                BuildSystemPrompt(),
                BuildUserPrompt(goal)
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

            string safeName = SanitizeFileName(
                string.IsNullOrWhiteSpace(plan.AssetName) ? "AIAsset" : plan.AssetName
            );
            string format = NormalizeFormat(plan.ExportFormat);
            string blendPath = Path.Combine(runRoot, safeName + ".blend");
            string exportPath = Path.Combine(runRoot, safeName + "." + format);
            string scriptPath = Path.Combine(runRoot, "build_asset.py");
            string logPath = Path.Combine(runRoot, "blender.log");

            string finalScript = BuildExecutableScript(
                plan.Script,
                blendPath,
                exportPath,
                format
            );
            File.WriteAllText(scriptPath, finalScript, new UTF8Encoding(false));

            activity("[BLENDER] executing headless Blender");
            ProcessResult execution = await RunBlenderAsync(blenderExe, scriptPath, logPath);
            if (execution.ExitCode != 0)
            {
                return "Blender execution failed (exit " + execution.ExitCode + "). Log: " + logPath
                    + "\n" + Compact(execution.Output, 1800);
            }

            if (!File.Exists(blendPath) || !File.Exists(exportPath))
            {
                return "Blender finished but verification failed: expected .blend/export file was not created. Log: " + logPath;
            }

            string? unityAsset = TryHandoffToUnity(exportPath, safeName, format);
            activity("[BLENDER VERIFY] .blend and export created");

            StringBuilder result = new StringBuilder();
            result.AppendLine(string.IsNullOrWhiteSpace(plan.Summary) ? "Blender asset created." : plan.Summary);
            result.AppendLine("Blend: " + blendPath);
            result.AppendLine("Export: " + exportPath);
            if (!string.IsNullOrWhiteSpace(unityAsset))
            {
                result.AppendLine("Unity handoff: " + unityAsset);
            }
            result.AppendLine("Provider: " + reply.Provider + " / " + reply.Model);
            return result.ToString().Trim();
        }

        private string? TryHandoffToUnity(string exportPath, string safeName, string format)
        {
            string root = settings.UnityProjectRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(Path.Combine(root, "Assets")))
            {
                return null;
            }

            string destinationDirectory = Path.Combine(root, "Assets", "AI_Generated", "Models");
            Directory.CreateDirectory(destinationDirectory);
            string destination = Path.Combine(destinationDirectory, safeName + "." + format);
            File.Copy(exportPath, destination, true);

            string manifest = Path.Combine(destinationDirectory, safeName + ".aiasset.json");
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

        private static async Task<ProcessResult> RunBlenderAsync(
            string blenderExe,
            string scriptPath,
            string logPath
        )
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = blenderExe,
                Arguments = "--background --factory-startup --python \"" + scriptPath + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory
            };

            using Process process = new Process { StartInfo = psi };
            StringBuilder output = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            string text = output.ToString();
            File.WriteAllText(logPath, text);
            return new ProcessResult(process.ExitCode, text);
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
            script.AppendLine("from mathutils import Vector");
            script.AppendLine("bpy.ops.object.select_all(action='SELECT')");
            script.AppendLine("bpy.ops.object.delete(use_global=False)");
            script.AppendLine("# ---- AI generated scene construction ----");
            script.AppendLine(generatedScript);
            script.AppendLine("# ---- host controlled save/export ----");
            script.AppendLine("bpy.ops.wm.save_as_mainfile(filepath=" + pyBlend + ")");
            if (format == "glb")
            {
                script.AppendLine("bpy.ops.export_scene.gltf(filepath=" + pyExport + ", export_format='GLB', use_selection=False)");
            }
            else
            {
                script.AppendLine("bpy.ops.export_scene.fbx(filepath=" + pyExport + ", use_selection=False, apply_unit_scale=True, bake_space_transform=False)");
            }
            script.AppendLine("print('AI_ASSET_EXPORT_OK')");
            return script.ToString();
        }

        private static string BuildSystemPrompt()
        {
            return "You are the Blender implementation engine for a controlled 3D asset pipeline. "
                + "Return strict JSON only with keys asset_name, export_format, summary, script. "
                + "script must be Python using only bpy, math and mathutils. It must construct the requested model/scene but MUST NOT save/export files; the host does that. "
                + "Do not access network, filesystem, subprocesses, shell, environment variables, addons, external files or delete arbitrary files. "
                + "Prefer deterministic geometry, sensible transforms, named objects, applied scale where useful, clean topology for simple hard-surface/low-poly assets, and materials using Principled BSDF. "
                + "export_format must be fbx or glb. No markdown fences.";
        }

        private static string BuildUserPrompt(string goal)
        {
            return "Create a Blender implementation for this goal:\n" + goal
                + "\nThe host starts from an empty factory scene and will save/export after your script. Keep the script self-contained and deterministic.";
        }

        private static bool TryParsePlan(string text, out BlenderPlan plan, out string error)
        {
            plan = new BlenderPlan();
            error = "";
            try
            {
                using JsonDocument doc = JsonDocument.Parse(text.Trim());
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

        private static string ReadString(JsonElement root, string name)
        {
            return root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }

        private static bool IsSafeScript(string script, out string error)
        {
            string lower = script.ToLowerInvariant();
            string[] blocked =
            {
                "import os", "import sys", "import subprocess", "import socket",
                "import requests", "import urllib", "shutil", "pathlib", "open(",
                "eval(", "exec(", "__import__", "bpy.ops.wm.open_mainfile",
                "bpy.ops.wm.save_as_mainfile", "bpy.ops.export_"
            };

            foreach (string token in blocked)
            {
                if (lower.Contains(token))
                {
                    error = "blocked token: " + token;
                    return false;
                }
            }

            error = "";
            return true;
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
            return string.Equals(format, "glb", StringComparison.OrdinalIgnoreCase) ? "glb" : "fbx";
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }
            value = value.Trim();
            return string.IsNullOrWhiteSpace(value) ? "AIAsset" : value;
        }

        private static string PythonLiteral(string value)
        {
            return "r\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string Compact(string value, int max)
        {
            value ??= "";
            return value.Length <= max ? value : value.Substring(0, max) + "...";
        }

        private sealed class BlenderPlan
        {
            public string AssetName { get; set; } = "";
            public string ExportFormat { get; set; } = "fbx";
            public string Summary { get; set; } = "";
            public string Script { get; set; } = "";
        }

        private readonly record struct ProcessResult(int ExitCode, string Output);
    }
}
