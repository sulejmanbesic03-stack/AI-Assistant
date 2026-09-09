using AI_Assistant.AgentV2;
using AI_Assistant.Runtime;
using AI_Assistant.TempCapabilities;
using AI_Assistant.Tools;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace AI_Assistant.AI
{
    public sealed class AssistantRuntime : IDisposable
    {
        private readonly AIIntegration legacy;
        private readonly AgentOrchestratorV2 agentV2;
        private readonly BlenderMcpAgent blenderMcp;
        private readonly IntentRouter intentRouter;
        private readonly VisualPreviewService visualPreview;
        private readonly RuntimeSettings settings;
        private readonly UnityBridgeTools unityTools;

        private string lastUnityV2Goal = "";
        private string lastBlenderGoal = "";
        private string? lastBlenderReferencePath;
        private string pendingHighRiskPrompt = "";

        public event Action<string>? Activity;
        public event Action<string>? PreviewReady;
        public RuntimeSettings Settings => settings;
        public string? LastPreviewPath => visualPreview.LastPreviewPath;

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
            intentRouter = new IntentRouter(ReportActivity);
            visualPreview = new VisualPreviewService(ReportActivity);
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

        public void Dispose()
        {
            AgentCancellationHub.CancelCurrent();
            blenderMcp.Dispose();
            intentRouter.Dispose();
            visualPreview.Dispose();
        }

        private async Task<string> RouteApprovedAsync(string normalizedPrompt)
        {
            bool continuation = IsContinuation(normalizedPrompt);

            // A continuation belongs to the task that is already in progress.
            // Do this before classification so "nastavi" cannot accidentally
            // start a new provider or execution domain.
            if (continuation && !string.IsNullOrWhiteSpace(lastBlenderGoal))
            {
                ReportActivity("[ROUTER] Blender MCP resume");
                return await blenderMcp.AskAsync(
                    lastBlenderGoal,
                    lastBlenderReferencePath,
                    true
                );
            }

            if (continuation && IsAgentV2Enabled() && !string.IsNullOrWhiteSpace(lastUnityV2Goal))
            {
                ReportActivity("[ROUTER] Unity Cowork Agent V2 resume");
                return await agentV2.HandleAsync(lastUnityV2Goal);
            }

            // Explicit commands remain deterministic and are useful for
            // recovery/debugging. Natural-language requests use the model
            // router below instead of a growing keyword table.
            if (IsExplicitBlender(normalizedPrompt))
            {
                lastBlenderGoal = normalizedPrompt;
                lastBlenderReferencePath = await GenerateVisualPreviewAsync(normalizedPrompt);
                lastUnityV2Goal = "";
                ReportActivity("[ROUTER] Blender MCP · Groq Qwen 3.6 27B");
                return await blenderMcp.AskAsync(
                    normalizedPrompt,
                    lastBlenderReferencePath,
                    true
                );
            }

            if (IsExplicitAgentCommand(normalizedPrompt))
            {
                lastUnityV2Goal = normalizedPrompt;
                lastBlenderGoal = "";
                ReportActivity("[ROUTER] Unity Cowork Agent V2 (explicit command)");
                return await agentV2.HandleAsync(normalizedPrompt);
            }

            IntentResult intent = await intentRouter.ClassifyAsync(
                normalizedPrompt,
                AgentCancellationHub.Token
            );
            ReportActivity(
                "[ROUTER MODEL] "
                + intent.Intent
                + " · confidence="
                + intent.Confidence.ToString("0.00")
                + (string.IsNullOrWhiteSpace(intent.Reason) ? "" : " · " + intent.Reason)
            );

            switch (intent.Intent)
            {
                case AssistantIntent.BlenderUnity:
                    ReportActivity("[ROUTER] Blender MCP → Unity handoff");
                    return await HandleBlenderUnityRequestAsync(normalizedPrompt);

                case AssistantIntent.Blender:
                    lastBlenderGoal = normalizedPrompt;
                    lastBlenderReferencePath = await GenerateVisualPreviewAsync(normalizedPrompt);
                    lastUnityV2Goal = "";
                    ReportActivity("[ROUTER] Blender MCP · Groq Qwen 3.6 27B");
                    return await blenderMcp.AskAsync(
                        normalizedPrompt,
                        lastBlenderReferencePath,
                        true
                    );

                case AssistantIntent.Unity:
                case AssistantIntent.Plan:
                    lastUnityV2Goal = normalizedPrompt;
                    lastBlenderGoal = "";
                    ReportActivity("[ROUTER] Unity Cowork Agent V2 · model-routed");
                    return await agentV2.HandleAsync(normalizedPrompt);

                case AssistantIntent.Conversation:
                    ReportActivity("[ROUTER] Conversation / compatibility path");
                    return await legacy.Ask(normalizedPrompt);
            }

            // If the small classifier is unavailable (quota, network, or an
            // invalid response), retain a narrow emergency route so the app
            // remains usable. This is deliberately last-resort behavior.
            if (LooksLikeBlenderToUnityRequest(normalizedPrompt))
            {
                ReportActivity("[ROUTER] Blender MCP → Unity handoff · emergency fallback");
                return await HandleBlenderUnityRequestAsync(normalizedPrompt);
            }

            if (IsBlenderPrompt(normalizedPrompt))
            {
                lastBlenderGoal = normalizedPrompt;
                lastBlenderReferencePath = await GenerateVisualPreviewAsync(normalizedPrompt);
                lastUnityV2Goal = "";
                ReportActivity("[ROUTER] Blender MCP · emergency fallback");
                return await blenderMcp.AskAsync(
                    normalizedPrompt,
                    lastBlenderReferencePath,
                    true
                );
            }

            if (HasExplicitUnitySignal(normalizedPrompt) || agentV2.ShouldHandle(normalizedPrompt))
            {
                lastUnityV2Goal = normalizedPrompt;
                lastBlenderGoal = "";
                ReportActivity("[ROUTER] Unity Cowork Agent V2 · emergency fallback");
                return await agentV2.HandleAsync(normalizedPrompt);
            }

            ReportActivity("[ROUTER] Legacy compatibility path");
            return await legacy.Ask(normalizedPrompt);
        }

        private static bool LooksLikeBlenderToUnityRequest(string prompt)
        {
            string value = (prompt ?? "").Trim();
            bool asksForAsset = ContainsAny(
                value,
                "model",
                "modela",
                "character",
                "charactera",
                "asset",
                "objekat",
                "lik",
                "3d",
                "mesha",
                "mesh"
            );
            bool asksForUnityDelivery = ContainsAny(
                value,
                "unity",
                "u unity",
                "pošalji",
                "posalji",
                "ubaci",
                "import",
                "prefab",
                "project"
            );
            return asksForAsset && asksForUnityDelivery;
        }

        private async Task<string> HandleBlenderUnityRequestAsync(string prompt)
        {
            if (string.IsNullOrWhiteSpace(settings.UnityProjectRoot)
                || !Directory.Exists(Path.Combine(settings.UnityProjectRoot, "Assets")))
            {
                return "Blender → Unity pipeline nije spreman: Unity project root nije konfigurisan ili nema Assets folder.";
            }

            // Visual feedback is deliberately best-effort. It runs before the
            // Blender stage so the user can see what the asset request was
            // interpreted as, but a Gemini/image failure must never block MCP
            // execution or make the app claim that Blender failed.
            string? referenceImagePath = await GenerateVisualPreviewAsync(prompt);

            const string generatedAssetName = "GeneratedAsset";
            string relativeAssetPath =
                "Assets/AI_Generated/Models/GeneratedAsset/GeneratedAsset.fbx";
            string absoluteAssetPath = Path.Combine(
                settings.UnityProjectRoot,
                relativeAssetPath.Replace('/', Path.DirectorySeparatorChar)
            );
            string? assetDirectory = Path.GetDirectoryName(absoluteAssetPath);
            if (!string.IsNullOrWhiteSpace(assetDirectory))
            {
                Directory.CreateDirectory(assetDirectory);
            }

            bool hadPreviousExport = File.Exists(absoluteAssetPath);
            DateTime previousExportWriteUtc = hadPreviousExport
                ? File.GetLastWriteTimeUtc(absoluteAssetPath)
                : DateTime.MinValue;
            long previousExportLength = hadPreviousExport
                ? new FileInfo(absoluteAssetPath).Length
                : -1;

            string blenderPrompt =
                "Create the 3D asset requested by the user in Blender using the official Blender MCP tools. "
                + "Preserve the user's requested subject, style, proportions, materials, and level of detail. "
                + "Do not substitute a generic humanoid, primitive blockout, or placeholder unless the user asked for one. "
                + "Inspect the current scene first. If a root named "
                + generatedAssetName
                + " already exists from an interrupted attempt, reuse and repair it instead of duplicating it. "
                + "This is a Blender-to-Unity pipeline. Do not render, do not use Material Preview or Rendered view, "
                + "and do not open any GPU shader preview. Keep the asset at the world origin, use a clean root named "
                + generatedAssetName
                + ", and keep it suitable for real-time Unity use. Preserve the current .blend file if it already has a known path; "
                + "do not invent a new .blend path. Do not export during initial modeling. First create the complete asset "
                + "and wait for the orchestrator's real viewport visual review. Export only after that review reports pass=true. "
                + "Never claim success from object names or a text description alone. Export the complete created "
                + "asset hierarchy as FBX using Forward -Z and Up Y to this exact absolute path: "
                + absoluteAssetPath
                + ". Create the parent directory if needed. Verify that the FBX exists, then report the exact export path. "
                + "Original user request: "
                + prompt;

            lastBlenderGoal = blenderPrompt;
            lastBlenderReferencePath = referenceImagePath;
            lastUnityV2Goal = "";
            string blenderResult = await blenderMcp.AskAsync(
                blenderPrompt,
                referenceImagePath,
                true
            );

            bool exportChanged = HasNewExport(
                absoluteAssetPath,
                hadPreviousExport,
                previousExportWriteUtc,
                previousExportLength
            );
            if (!exportChanged)
            {
                return "Blender stage nije potvrdio novi export. FBX nije kreiran ili se postojeći fajl nije promijenio: "
                    + absoluteAssetPath
                    + "\n\nBlender odgovor:\n"
                    + blenderResult;
            }

            ReportActivity("[HANDOFF] FBX verified · Unity import and instantiate");
            // Blender is complete; a later "nastavi" must resume Unity V2,
            // not repeat the Blender stage.
            lastBlenderGoal = "";
            string unityPrompt =
                "/agent Import and instantiate the generated Blender asset in Unity. "
                + "Use the existing Unity bridge and do not delete or replace existing scene objects. "
                + "The asset is already exported at this Unity-relative path: "
                + relativeAssetPath
                + ". Use scene_actions with exactly these fields: "
                + "{\"type\":\"import_asset\",\"asset_path\":\""
                + relativeAssetPath
                + "\"} and then "
                + "{\"type\":\"instantiate_prefab\",\"asset_path\":\""
                + relativeAssetPath
                + "\",\"name\":\""
                + generatedAssetName
                + "\",\"parent_path\":\"\"}. "
                + "Place it at the scene origin if the bridge supports it, save the active scene, and verify the result.";

            lastUnityV2Goal = unityPrompt;
            return "Blender export završen.\n\n"
                + blenderResult
                + "\n\nUnity handoff:\n"
                + await agentV2.HandleAsync(unityPrompt);
        }

        private async Task<string?> GenerateVisualPreviewAsync(string prompt)
        {
            string? previewPath = await visualPreview.GenerateAsync(
                prompt,
                AgentCancellationHub.Token
            );
            if (!string.IsNullOrWhiteSpace(previewPath))
            {
                PreviewReady?.Invoke(previewPath);
            }

            return previewPath;
        }

        private static bool HasNewExport(
            string path,
            bool hadPreviousExport,
            DateTime previousWriteUtc,
            long previousLength
        )
        {
            try
            {
                FileInfo current = new FileInfo(path);
                if (!current.Exists || current.Length < 64)
                {
                    return false;
                }

                bool changed = !hadPreviousExport
                    || current.LastWriteTimeUtc != previousWriteUtc
                    || current.Length != previousLength;
                if (!changed)
                {
                    return false;
                }

                using FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read
                );
                byte[] header = new byte[32];
                int read = stream.Read(header, 0, header.Length);
                string text = Encoding.ASCII.GetString(header, 0, read);
                return text.StartsWith("Kaydara FBX Binary", StringComparison.Ordinal)
                    || text.StartsWith("; FBX", StringComparison.Ordinal);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public void ResetConversationContext()
        {
            AgentCancellationHub.CancelCurrent();
            agentV2.Reset();
            legacy.ResetConversationContext();
            blenderMcp.Reset();
            lastUnityV2Goal = "";
            lastBlenderGoal = "";
            lastBlenderReferencePath = null;
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
            lines.Add("Blender provider: Groq -> OpenRouter aggregator");
            lines.Add("Intent router: Groq -> OpenRouter aggregator");
            lines.Add("Blender model: " + (Environment.GetEnvironmentVariable("GROQ_BLENDER_MODEL") ?? "qwen/qwen3.6-27b"));
            lines.Add("Blender fallback model: " + (Environment.GetEnvironmentVariable("GROQ_BLENDER_FALLBACK_MODEL") ?? Environment.GetEnvironmentVariable("GROQ_MODEL") ?? "openai/gpt-oss-120b"));
            lines.Add("Blender vision reviewer: Groq -> OpenRouter · " + (Environment.GetEnvironmentVariable("GROQ_BLENDER_VISION_MODEL") ?? "qwen/qwen3.6-27b"));
            lines.Add("OpenRouter: " + IsKeyConfigured("OPENROUTER_API_KEY") + " · " + (Environment.GetEnvironmentVariable("BLENDER_OPENROUTER_MODEL") ?? Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "nex-agi/nex-n2.5-pro:free"));
            lines.Add("OpenRouter vision: " + (Environment.GetEnvironmentVariable("BLENDER_OPENROUTER_VISION_MODEL") ?? Environment.GetEnvironmentVariable("OPENROUTER_VISION_MODEL") ?? "same multimodal model"));
            lines.Add("Blender timeouts: provider " + (Environment.GetEnvironmentVariable("GROQ_BLENDER_REQUEST_TIMEOUT_SECONDS") ?? "180s") + " · MCP " + (Environment.GetEnvironmentVariable("BLENDER_MCP_REQUEST_TIMEOUT_SECONDS") ?? "240s") + " · vision " + (Environment.GetEnvironmentVariable("GROQ_BLENDER_VISION_TIMEOUT_SECONDS") ?? "90s"));
            lines.Add("Gemini: " + IsKeyConfigured("GEMINI_API_KEY"));
            lines.Add("Visual preview: " + (IsKeyConfigured("GEMINI_API_KEY") == "configured"
                ? (Environment.GetEnvironmentVariable("AI_PREVIEW_ENABLED") == "0" ? "disabled" : "Gemini image")
                : "unavailable (GEMINI_API_KEY missing)"));
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
        private static bool IsExplicitAgentCommand(string prompt)
        {
            string p = (prompt ?? "").Trim();
            return p.Equals("/agent", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("/agent ", StringComparison.OrdinalIgnoreCase)
                || p.Equals("/plan", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith("/plan ", StringComparison.OrdinalIgnoreCase);
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
