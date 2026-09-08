using AI_Assistant.AgentV2;
using AI_Assistant.Runtime;
using AI_Assistant.TempCapabilities;
using AI_Assistant.Tools;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace AI_Assistant.AI
{
    public sealed class AssistantRuntime
    {
        private readonly AIIntegration legacy;
        private readonly AgentOrchestratorV2 agentV2;
        private readonly BlenderMcpAgent blenderMcp;
        private readonly RuntimeSettings settings;
        private readonly UnityBridgeTools unityTools;

        private string lastUnityV2Goal = "";
        private string lastBlenderGoal = "";
        private string pendingHighRiskPrompt = "";

        public event Action<string>? Activity;
        public RuntimeSettings Settings => settings;

        public AssistantRuntime(List<string> allowedRoots, string projectFilePath, string sourceRoot, string updaterProjectPath)
        {
            settings = RuntimeSettings.Load();
            settings.ApplyToProcessEnvironment();
            legacy = new AIIntegration(allowedRoots, projectFilePath, sourceRoot, updaterProjectPath);
            legacy.Activity += ReportActivity;
            unityTools = new UnityBridgeTools();
            TempCapabilityManager tempCapabilities = new TempCapabilityManager(sourceRoot, unityTools);
            agentV2 = new AgentOrchestratorV2(unityTools, tempCapabilities, ReportActivity);
            blenderMcp = new BlenderMcpAgent(ReportActivity);
        }

        public async Task<string> Ask(string prompt)
        {
            AgentCancellationHub.BeginTask();
            string normalizedPrompt = (prompt ?? "").Trim();
            if (IsApproval(normalizedPrompt) && !string.IsNullOrWhiteSpace(pendingHighRiskPrompt))
            {
                string approved = pendingHighRiskPrompt; pendingHighRiskPrompt = ""; ReportActivity("[RISK GATE] approved by user"); return await RouteApprovedAsync(approved);
            }
            if (IsCancellation(normalizedPrompt) && !string.IsNullOrWhiteSpace(pendingHighRiskPrompt))
            {
                pendingHighRiskPrompt = ""; ReportActivity("[RISK GATE] cancelled by user"); return "High-risk task cancelled. No execution was started.";
            }
            if (!string.IsNullOrWhiteSpace(pendingHighRiskPrompt))
            {
                pendingHighRiskPrompt = "";
                ReportActivity("[RISK GATE] held request expired after a non-approval message");
            }
            if (settings.RequireApprovalForDestructiveChanges && IsHighRisk(normalizedPrompt) && !IsPlanOnly(normalizedPrompt))
            {
                pendingHighRiskPrompt = normalizedPrompt; ReportActivity("[RISK GATE] destructive/high-impact task held for approval");
                return "High-risk change detected. I have not executed it. Type APPROVE to run the held task, or CANCEL to discard it.";
            }
            return await RouteApprovedAsync(normalizedPrompt);
        }

        public void CancelCurrentWork() { AgentCancellationHub.CancelCurrent(); ReportActivity("[CANCEL] stop requested by user"); }

        private async Task<string> RouteApprovedAsync(string normalizedPrompt)
        {
            bool continuation = IsContinuation(normalizedPrompt);
            bool explicitUnity = HasExplicitUnitySignal(normalizedPrompt);
            if (IsBlenderUnityRequest(normalizedPrompt))
            {
                ReportActivity("[ROUTER] Blender → Unity character pipeline");
                return await HandleBlenderUnityRequestAsync(normalizedPrompt);
            }
            if (explicitUnity && !IsExplicitBlender(normalizedPrompt) && agentV2.ShouldHandle(normalizedPrompt))
            {
                if (!continuation) lastUnityV2Goal = normalizedPrompt;
                if (!continuation) lastBlenderGoal = "";
                ReportActivity("[ROUTER] Unity Cowork Agent V2 (explicit Unity intent)");
                return await agentV2.HandleAsync(normalizedPrompt);
            }
            if (IsBlenderPrompt(normalizedPrompt) || (continuation && !string.IsNullOrWhiteSpace(lastBlenderGoal)))
            {
                string blenderPrompt = continuation ? lastBlenderGoal : normalizedPrompt;
                if (!continuation) { lastBlenderGoal = normalizedPrompt; lastUnityV2Goal = ""; }
                ReportActivity("[ROUTER] Blender MCP · Groq Qwen 3.6 27B");
                return await blenderMcp.AskAsync(blenderPrompt);
            }
            if (agentV2.ShouldHandle(normalizedPrompt))
            {
                if (!continuation) lastUnityV2Goal = normalizedPrompt;
                ReportActivity("[ROUTER] Unity Cowork Agent V2");
                return await agentV2.HandleAsync(normalizedPrompt);
            }
            if (continuation && IsAgentV2Enabled() && !string.IsNullOrWhiteSpace(lastUnityV2Goal))
            {
                ReportActivity("[ROUTER] Unity Cowork Agent V2 resume recovery");
                return await agentV2.HandleAsync(lastUnityV2Goal);
            }
            ReportActivity("[ROUTER] Legacy compatibility path");
            return await legacy.Ask(normalizedPrompt);
        }

        private async Task<string> HandleBlenderUnityRequestAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(settings.UnityProjectRoot)
                || !Directory.Exists(Path.Combine(settings.UnityProjectRoot, "Assets")))
            {
                return "Blender → Unity pipeline nije spreman: Unity project root nije konfigurisan ili nema Assets folder.";
            }

            string relativeAssetPath =
                "Assets/AI_Generated/Models/GeneratedCharacter/GeneratedCharacter.fbx";
            string absoluteAssetPath = Path.Combine(
                settings.UnityProjectRoot,
                relativeAssetPath.Replace('/', Path.DirectorySeparatorChar)
            );
            string? assetDirectory = Path.GetDirectoryName(absoluteAssetPath);
            if (!string.IsNullOrWhiteSpace(assetDirectory))
            {
                Directory.CreateDirectory(assetDirectory);
            }

            string blenderPrompt =
                "Generate the requested game-ready character in Blender using the available MCP tools. "
                + "This is a Blender-to-Unity pipeline. Do not render, do not use Material Preview or Rendered view, "
                + "and do not open any GPU shader preview. Build the character at the world origin with one root named "
                + "Character_Root. Use a male survival character with an AA-style game-ready silhouette, coherent proportions, connected parts, clothing "
                + "and simple materials. Keep it suitable for real-time Unity use. Save the .blend file and export the "
                + "complete selected character hierarchy as FBX using Forward -Z and Up Y to this exact absolute path: "
                + absoluteAssetPath
                + ". Create the parent directory if needed. Verify that the FBX exists, then report the exact export path. "
                + "Original user request: "
                + prompt;

            lastBlenderGoal = blenderPrompt;
            lastUnityV2Goal = "";
            string blenderResult = await blenderMcp.AskAsync(blenderPrompt);

            if (!File.Exists(absoluteAssetPath))
            {
                return "Blender stage nije završio export. Očekivani FBX nije pronađen: "
                    + absoluteAssetPath
                    + "\n\nBlender odgovor:\n"
                    + blenderResult;
            }

            ReportActivity("[HANDOFF] FBX verified · Unity import and instantiate");
            // Blender is complete; a later "nastavi" must resume Unity V2,
            // not repeat the Blender stage.
            lastBlenderGoal = "";
            string unityPrompt =
                "/agent Import and instantiate the generated Blender character in Unity. "
                + "Use the existing Unity bridge and do not delete or replace existing scene objects. "
                + "The asset is already exported at this Unity-relative path: "
                + relativeAssetPath
                + ". Use scene_actions with exactly these fields: "
                + "{\"type\":\"import_asset\",\"asset_path\":\""
                + relativeAssetPath
                + "\"} and then "
                + "{\"type\":\"instantiate_prefab\",\"asset_path\":\""
                + relativeAssetPath
                + "\",\"name\":\"GeneratedCharacter\",\"parent_path\":\"\"}. "
                + "Place it at the scene origin if the bridge supports it, save the active scene, and verify the result.";

            lastUnityV2Goal = unityPrompt;
            return "Blender export završen.\n\n"
                + blenderResult
                + "\n\nUnity handoff:\n"
                + await agentV2.HandleAsync(unityPrompt);
        }

        public void ResetConversationContext()
        {
            AgentCancellationHub.CancelCurrent();
            agentV2.Reset();
            legacy.ResetConversationContext();
            blenderMcp.Reset();
            lastUnityV2Goal = "";
            lastBlenderGoal = "";
            pendingHighRiskPrompt = "";
        }

        public string BuildDiagnostics()
        {
            List<string> lines = new List<string>();
            lines.Add("Agent: " + AgentVersion.Version);
            lines.Add("Unity root: " + (string.IsNullOrWhiteSpace(settings.UnityProjectRoot) ? "not configured" : settings.UnityProjectRoot));
            string blender = settings.ResolveBlenderExecutable();
            lines.Add("Blender: " + (string.IsNullOrWhiteSpace(blender) ? "not found" : blender));
            lines.Add("Blender engine: official Blender MCP via uvx");
            lines.Add("Blender provider: Groq primary -> OpenRouter fallback");
            lines.Add("Blender model: " + (Environment.GetEnvironmentVariable("GROQ_BLENDER_MODEL") ?? "qwen/qwen3.6-27b"));
            lines.Add("OpenRouter: " + IsKeyConfigured("OPENROUTER_API_KEY"));
            lines.Add("Gemini: " + IsKeyConfigured("GEMINI_API_KEY"));
            lines.Add("Groq: " + IsKeyConfigured("GROQ_API_KEY"));
            lines.Add("Risk gate: " + (settings.RequireApprovalForDestructiveChanges ? "on" : "off"));
            foreach (string issue in settings.Validate()) lines.Add("Warning: " + issue);
            return string.Join(Environment.NewLine, lines);
        }

        private static bool IsBlenderPrompt(string prompt)
        {
            string value = (prompt ?? "").Trim();

            return
                value.Contains("blender", StringComparison.OrdinalIgnoreCase)
                || value.Contains("bpy", StringComparison.OrdinalIgnoreCase)
                || value.Contains(".blend", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsBlenderUnityRequest(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            bool asksForCharacter = ContainsAny(
                p,
                "character",
                "karakter",
                "humanoid",
                "model",
                "3d asset",
                "3d model"
            );
            bool asksForUnity = ContainsAny(
                p,
                "unity",
                "ubaci",
                "ubaciti",
                "pošalji",
                "posalji",
                "send it",
                "import"
            );
            bool asksToGenerate = ContainsAny(
                p,
                "napravi",
                "napraviti",
                "generiši",
                "generisi",
                "create",
                "generate",
                "build"
            );

            return asksForCharacter && asksForUnity && asksToGenerate;
        }

        private static bool IsContinuation(string prompt)
        {
            string value = (prompt ?? "").Trim().ToLowerInvariant();
            return value == "nastavi" || value == "continue" || value == "nastavi dalje" || value == "probaj opet" || value == "try again" || value == "opet";
        }

        private static bool IsHighRisk(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            string[] signals = { "delete ", "delete.", "obrisi", "obriši", "ukloni", "remove all", "delete all", "reset scene", "resetuj scenu", "wipe", "overwrite", "replace entire", "replace all", "zamijeni cijelu", "remove script", "delete script", "delete folder", "remove folder", "clear scene", "ocisti scenu", "destroy all", "rename project", "move project", "drop database", "purge", "format" };
            foreach (string signal in signals) if (p.Contains(signal)) return true; return false;
        }
        private static bool HasExplicitUnitySignal(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            return p.StartsWith("/agent ", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("/plan ", StringComparison.OrdinalIgnoreCase)
                || ContainsAny(p, "unity", "gameobject", "scene hierarchy", "monobehaviour", "prefab", "rigidbody", "collider", "navmesh", "charactercontroller", "unity editor");
        }
        private static bool IsExplicitBlender(string prompt)
        {
            string p = (prompt ?? "").Trim();
            return p.Equals("/blender", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("/blender ", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("/blender:", StringComparison.OrdinalIgnoreCase)
                || p.Equals("blender", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("blender ", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("blender:", StringComparison.OrdinalIgnoreCase);
        }
        private static bool IsPlanOnly(string prompt) => (prompt ?? "").Trim().StartsWith("/plan ", StringComparison.OrdinalIgnoreCase);
        private static bool IsApproval(string prompt) { string p = (prompt ?? "").Trim(); return p.Equals("approve", StringComparison.OrdinalIgnoreCase) || p.Equals("odobri", StringComparison.OrdinalIgnoreCase) || p.Equals("potvrdi", StringComparison.OrdinalIgnoreCase); }
        private static bool IsCancellation(string prompt) { string p = (prompt ?? "").Trim(); return p.Equals("cancel", StringComparison.OrdinalIgnoreCase) || p.Equals("otkazi", StringComparison.OrdinalIgnoreCase) || p.Equals("otkaži", StringComparison.OrdinalIgnoreCase); }
        private static string IsKeyConfigured(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? "not configured" : "configured";
        private static bool IsAgentV2Enabled() => !string.Equals(Environment.GetEnvironmentVariable("AI_AGENT_V2"), "0", StringComparison.OrdinalIgnoreCase);
        private static bool ContainsAny(string text, params string[] values) { foreach (string value in values) if (text.Contains(value, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        private void ReportActivity(string message) => Activity?.Invoke(message);
    }
}
