using AI_Assistant.AgentV2;
using AI_Assistant.Blender;
using AI_Assistant.Runtime;
using AI_Assistant.TempCapabilities;
using AI_Assistant.Tools;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AI_Assistant.AI
{
    public sealed class AssistantRuntime
    {
        private readonly AIIntegration legacy;
        private readonly AgentOrchestratorV2 agentV2;
        private readonly BlenderAgentV3 blenderV3;
        private readonly RuntimeSettings settings;
        private readonly UnityBridgeTools unityTools;

        private string lastUnityV2Goal = "";
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
            blenderV3 = new BlenderAgentV3(settings, ReportActivity);
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
            if (blenderV3.ShouldHandle(normalizedPrompt))
            {
                string qualityProfile = DetectQualityProfile(normalizedPrompt);
                string unityContext = CaptureLiveUnityContext();
                string augmentedPrompt = BuildBlenderAugmentedPrompt(normalizedPrompt, qualityProfile, unityContext);
                ReportActivity("[ROUTER] Blender Agent V3 deterministic builder");
                ReportActivity("[BLENDER QUALITY] " + qualityProfile);
                ReportActivity(string.IsNullOrWhiteSpace(unityContext) ? "[BLENDER UNITY CONTEXT] unavailable; planning around neutral origin" : "[BLENDER UNITY CONTEXT] live scene snapshot attached before layout planning");
                return await blenderV3.HandleAsync(augmentedPrompt);
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

        public void ResetConversationContext()
        {
            AgentCancellationHub.CancelCurrent(); agentV2.Reset(); legacy.ResetConversationContext(); lastUnityV2Goal = ""; pendingHighRiskPrompt = "";
        }

        public string BuildDiagnostics()
        {
            List<string> lines = new List<string>();
            lines.Add("Agent: " + AgentVersion.Version);
            lines.Add("Unity root: " + (string.IsNullOrWhiteSpace(settings.UnityProjectRoot) ? "not configured" : settings.UnityProjectRoot));
            string blender = settings.ResolveBlenderExecutable();
            lines.Add("Blender: " + (string.IsNullOrWhiteSpace(blender) ? "not found" : blender));
            lines.Add("Blender engine: V3 deterministic builder-first");
            lines.Add("Blender model: " + (Environment.GetEnvironmentVariable("BLENDER_OPENROUTER_MODEL") ?? "inclusionai/ling-3.0-flash-fin:free"));
            lines.Add("Blender quality default: Medium");
            lines.Add("Unity-aware Blender layout: on");
            lines.Add("Raw bpy default path: off");
            lines.Add("OpenRouter: " + IsKeyConfigured("OPENROUTER_API_KEY"));
            lines.Add("Gemini: " + IsKeyConfigured("GEMINI_API_KEY"));
            lines.Add("Groq: " + IsKeyConfigured("GROQ_API_KEY"));
            lines.Add("Risk gate: " + (settings.RequireApprovalForDestructiveChanges ? "on" : "off"));
            foreach (string issue in settings.Validate()) lines.Add("Warning: " + issue);
            return string.Join(Environment.NewLine, lines);
        }

        private string CaptureLiveUnityContext()
        {
            try
            {
                string activeScene = unityTools.GetActiveScene();
                string hierarchy = unityTools.GetSceneHierarchy();
                if (LooksLikeConnectionFailure(activeScene) && LooksLikeConnectionFailure(hierarchy)) return "";
                return Compact("ACTIVE SCENE:\n" + activeScene + "\n\nLIVE HIERARCHY WITH CURRENT TRANSFORMS:\n" + hierarchy, 9000);
            }
            catch { return ""; }
        }

        private static string BuildBlenderAugmentedPrompt(string originalPrompt, string qualityProfile, string unityContext)
        {
            string qualityRules = BuildQualityRules(qualityProfile);
            string contextRules = string.IsNullOrWhiteSpace(unityContext)
                ? "Live Unity context was unavailable. Keep scene instances conservatively spaced and centered around a neutral origin."
                : "Use the LIVE UNITY CONTEXT below as authoritative placement context. Respect the existing Ground/terrain, current object locations and available space. Keep floor-standing assets aligned to the ground plane and avoid obvious intersections. Do not invent a disconnected coordinate system.";
            return originalPrompt + "\n\n--- HOST QUALITY PROFILE ---\nQUALITY PROFILE: " + qualityProfile + "\n" + qualityRules
                + "\nThe selected profile controls detail density, silhouette quality, bevel usage and primitive segmentation. Do not downgrade Medium/High/AA to low-poly."
                + "\n\n--- HOST UNITY-AWARE LAYOUT RULES ---\n" + contextRules
                + (string.IsNullOrWhiteSpace(unityContext) ? "" : "\n\nLIVE UNITY CONTEXT:\n" + unityContext);
        }

        private static string BuildQualityRules(string profile) => profile switch
        {
            "Low" => "Use economical low-poly geometry, strong silhouettes, minimal bevels and low segment counts.",
            "High" => "Use refined real-time geometry, realistic proportions, selective bevels, higher segment counts and meaningful secondary details.",
            "AA" => "Target AA / medium-high PC-console quality: polished silhouettes, realistic proportions, bevels on important hard edges, layered primary/secondary/tertiary geometry, smooth curved forms where appropriate and purposeful real-time detail.",
            _ => "Use medium-quality production game assets: clearly more detailed than low-poly, good silhouettes, sensible bevels, moderate secondary detail and efficient real-time geometry."
        };

        private static string DetectQualityProfile(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            if (ContainsAny(p, "aa quality", "aa-quality", "aa model", "medium-high", "medium high", "double a", "the forest style", "sons of the forest style")) return "AA";
            if (ContainsAny(p, "high quality", "high-quality", "high detail", "high-detail", "detailed model", "vrlo detalj")) return "High";
            if (ContainsAny(p, "low poly", "low-poly", "low detail", "low-detail", "mobile quality", "minimal detail")) return "Low";
            if (ContainsAny(p, "medium quality", "medium-quality", "medium detail", "medium-detail")) return "Medium";
            return "Medium";
        }

        private static bool ContainsAny(string text, params string[] values) { foreach (string value in values) if (text.Contains(value, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        private static bool LooksLikeConnectionFailure(string value) { string text = (value ?? "").ToLowerInvariant(); return string.IsNullOrWhiteSpace(text) || text.Contains("connection") || text.Contains("refused") || text.Contains("timed out") || (text.Contains("unity bridge") && text.Contains("error")); }
        private static string Compact(string value, int maxChars) { value ??= ""; return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "\n...[Unity context truncated by host]"; }
        private static bool IsHighRisk(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            string[] signals = { "delete ", "obrisi", "obriši", "remove all", "delete all", "reset scene", "wipe", "overwrite", "replace entire", "replace all", "remove script", "delete script", "delete folder", "remove folder", "clear scene", "destroy all", "rename project", "move project" };
            foreach (string signal in signals) if (p.Contains(signal)) return true; return false;
        }
        private static bool IsPlanOnly(string prompt) => (prompt ?? "").Trim().StartsWith("/plan ", StringComparison.OrdinalIgnoreCase);
        private static bool IsApproval(string prompt) { string p = (prompt ?? "").Trim(); return p.Equals("approve", StringComparison.OrdinalIgnoreCase) || p.Equals("odobri", StringComparison.OrdinalIgnoreCase) || p.Equals("potvrdi", StringComparison.OrdinalIgnoreCase); }
        private static bool IsCancellation(string prompt) { string p = (prompt ?? "").Trim(); return p.Equals("cancel", StringComparison.OrdinalIgnoreCase) || p.Equals("otkazi", StringComparison.OrdinalIgnoreCase) || p.Equals("otkaži", StringComparison.OrdinalIgnoreCase); }
        private static string IsKeyConfigured(string name) => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? "not configured" : "configured";
        private static bool IsAgentV2Enabled() => !string.Equals(Environment.GetEnvironmentVariable("AI_AGENT_V2"), "0", StringComparison.OrdinalIgnoreCase);
        private static bool IsContinuation(string prompt) { string value = (prompt ?? "").Trim().ToLowerInvariant(); return value == "nastavi" || value == "continue" || value == "nastavi dalje" || value == "probaj opet" || value == "try again" || value == "opet"; }
        private void ReportActivity(string message) => Activity?.Invoke(message);
    }
}
