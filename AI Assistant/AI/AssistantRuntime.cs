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
            if (explicitUnity && !IsExplicitBlender(normalizedPrompt) && agentV2.ShouldHandle(normalizedPrompt))
            {
                if (!continuation) lastUnityV2Goal = normalizedPrompt;
                if (!continuation) lastBlenderGoal = "";
                ReportActivity("[ROUTER] Unity Cowork Agent V2 (explicit Unity intent)");
                return await agentV2.HandleAsync(normalizedPrompt);
            }
            if (blenderV3.ShouldHandle(normalizedPrompt) || (continuation && !string.IsNullOrWhiteSpace(lastBlenderGoal)))
            {
                string blenderPrompt = continuation ? lastBlenderGoal : normalizedPrompt;
                if (!continuation) { lastBlenderGoal = normalizedPrompt; lastUnityV2Goal = ""; }
                string qualityProfile = DetectQualityProfile(blenderPrompt);
                string unityContext = CaptureLiveUnityContext();
                string augmentedPrompt = BuildBlenderAugmentedPrompt(blenderPrompt, qualityProfile, unityContext);
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
            AgentCancellationHub.CancelCurrent(); agentV2.Reset(); legacy.ResetConversationContext(); lastUnityV2Goal = ""; lastBlenderGoal = ""; pendingHighRiskPrompt = "";
        }

        public string BuildDiagnostics()
        {
            List<string> lines = new List<string>();
            lines.Add("Agent: " + AgentVersion.Version);
            lines.Add("Unity root: " + (string.IsNullOrWhiteSpace(settings.UnityProjectRoot) ? "not configured" : settings.UnityProjectRoot));
            string blender = settings.ResolveBlenderExecutable();
            lines.Add("Blender: " + (string.IsNullOrWhiteSpace(blender) ? "not found" : blender));
            lines.Add("Blender engine: V3 semantic plan -> host-owned Blender API");
            lines.Add("Blender planning model: " + (Environment.GetEnvironmentVariable("BLENDER_OPENROUTER_MODEL") ?? "inclusionai/ling-3.0-flash-fin:free") + " (InclusionAI first; Gemini/Groq/OpenRouter fallback)");
            lines.Add("Blender quality default: Medium");
            lines.Add("Unity-aware Blender layout: on");
            lines.Add("AA production quality floor: on");
            lines.Add("Character topology owner: semantic humanoid generator");
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
            string requestKind = DetectBlenderRequestKind(originalPrompt);
            bool environmentRequest = requestKind == "environment";
            string qualityRules = BuildQualityRules(qualityProfile, requestKind);
            string contextRules = environmentRequest
                ? string.IsNullOrWhiteSpace(unityContext)
                    ? "Live Unity context was unavailable. Keep the generated environment compact, grounded and logically grouped around a neutral origin. Do not scatter props over arbitrary coordinates."
                    : "Use the LIVE UNITY CONTEXT below only as placement context. Respect the existing Ground/terrain and current scene scale, but do not recreate existing objects unless the user explicitly asks for them. Build one coherent composition with believable functional zones, grounding, clearance and relationships. Avoid floating objects, intersections, duplicated coordinates, extreme offsets and disconnected placement."
                : "This is a standalone " + requestKind + " request. Generate only the requested subject at neutral origin with one scene instance. Do not copy, recreate or include buildings, pumps, props or characters from the existing Unity hierarchy; that hierarchy is unrelated import context.";
            return originalPrompt + "\n\n--- HOST QUALITY PROFILE ---\nQUALITY PROFILE: " + qualityProfile + "\n" + qualityRules
                + "\nThe selected profile is a HARD production requirement. Do not downgrade Medium/High/AA to low-poly. target_triangles is a real budget, not decorative metadata."
                + "\n\n--- HOST REQUEST SCOPE ---\nREQUEST KIND HINT: " + requestKind.ToUpperInvariant()
                + "\n" + contextRules
                + (environmentRequest && !string.IsNullOrWhiteSpace(unityContext) ? "\n\nLIVE UNITY CONTEXT:\n" + unityContext : "");
        }

        private static string BuildQualityRules(string profile, string requestKind)
        {
            if (requestKind == "character")
            {
                return profile switch
                {
                    "Low" => "Create one connected lightweight character with readable anatomy and clothing silhouette, roughly 1k-4k purposeful triangles.",
                    "High" => "Create one connected detailed real-time character, roughly 8k-25k purposeful triangles, with believable anatomy, face/head masses, hands, feet and layered clothing.",
                    "AA" => "Create one connected AA character, roughly 15k-45k purposeful triangles. Use a continuous skin/mesh body base, about 7.5-head human proportions, joined shoulders/hips/limbs, recognizable hands/feet/head, layered fitted clothing, footwear, hair and meaningful surface/silhouette detail. Never use floating primitives or include an environment.",
                    _ => "Create one connected game-ready character, roughly 4k-12k purposeful triangles, with believable proportions, joined anatomy and readable clothing."
                };
            }

            if (requestKind is "prop" or "hard_surface")
            {
                return profile switch
                {
                    "Low" => "Use economical low-poly geometry and a strong readable silhouette for the single requested asset.",
                    "High" => "Create one refined real-time asset with realistic proportions, selective 2-4 segment bevels and meaningful secondary construction detail.",
                    "AA" => "Create one AA hero asset with polished silhouette, realistic proportions, purposeful bevels, layered construction, seams, panels, fasteners, handles and material separation where appropriate; normally 4k-20k purposeful triangles depending on size.",
                    _ => "Create one medium-quality game-ready asset with good silhouette, sensible bevels and moderate secondary detail."
                };
            }

            return profile switch
            {
                "Low" => "Use economical low-poly geometry, strong silhouettes, minimal bevels and low segment counts. Keep the complete environment intentionally lightweight.",
                "High" => "Use refined real-time geometry, realistic proportions, selective 2-4 segment bevels, higher segment counts and meaningful secondary details. Target roughly 12k-30k triangles total depending on scope.",
                "AA" => "Target genuine AA / medium-high PC-console production quality. A full environment such as a gas station should normally use about 25k-60k purposeful triangles across 6-12 reusable assets. Use polished silhouettes, realistic proportions, 3-4 segment bevels, layered geometry, frames, trims, seams, panels, supports and other physically readable construction details.",
                _ => "Use medium-quality production geometry with good silhouettes, sensible bevels, moderate secondary detail and several thousand to low tens-of-thousands of triangles for the complete environment."
            };
        }

        private static string DetectBlenderRequestKind(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            if (ContainsAny(p, "humanoid", "character", "karakter", "npc", "person", "osoba", "covjek", "čovjek", "body mesh", "enemy", "neprijatelj")) return "character";
            if (ContainsAny(p, "scene", "scena", "environment", "okruzenje", "okruženje", "level", "benzinsk", "gas station", "building", "zgrada", "house", "kuca", "kuća", "room", "soba", "forest", "suma", "šuma", "city", "grad")) return "environment";
            if (ContainsAny(p, "vehicle", "vozilo", "car", "auto", "weapon", "oruzje", "oružje", "gun", "puska", "puška", "machine", "masina", "mašina", "tool", "alat")) return "hard_surface";
            return "prop";
        }

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
        private static bool IsContinuation(string prompt) { string value = (prompt ?? "").Trim().ToLowerInvariant(); return value == "nastavi" || value == "continue" || value == "nastavi dalje" || value == "probaj opet" || value == "try again" || value == "opet"; }
        private void ReportActivity(string message) => Activity?.Invoke(message);
    }
}
