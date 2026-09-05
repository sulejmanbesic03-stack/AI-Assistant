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
    /// <summary>
    /// Top-level runtime router.
    /// Unity engineering requests go through Cowork Agent V2.
    /// Blender modeling requests go through the controlled headless Blender agent.
    /// Legacy AIIntegration remains available for compatibility workflows.
    /// </summary>
    public sealed class AssistantRuntime
    {
        private readonly AIIntegration legacy;
        private readonly AgentOrchestratorV2 agentV2;
        private readonly BlenderAgentV2 blenderV2;
        private readonly RuntimeSettings settings;

        private string lastUnityV2Goal = "";

        public event Action<string>? Activity;

        public RuntimeSettings Settings => settings;

        public AssistantRuntime(
            List<string> allowedRoots,
            string projectFilePath,
            string sourceRoot,
            string updaterProjectPath
        )
        {
            settings = RuntimeSettings.Load();
            settings.ApplyToProcessEnvironment();

            legacy = new AIIntegration(
                allowedRoots,
                projectFilePath,
                sourceRoot,
                updaterProjectPath
            );

            legacy.Activity += ReportActivity;

            UnityBridgeTools unityTools = new UnityBridgeTools();

            TempCapabilityManager tempCapabilities =
                new TempCapabilityManager(
                    sourceRoot,
                    unityTools
                );

            agentV2 = new AgentOrchestratorV2(
                unityTools,
                tempCapabilities,
                ReportActivity
            );

            blenderV2 = new BlenderAgentV2(
                settings,
                ReportActivity
            );
        }

        public async Task<string> Ask(string prompt)
        {
            string normalizedPrompt = (prompt ?? "").Trim();
            bool continuation = IsContinuation(normalizedPrompt);

            if (blenderV2.ShouldHandle(normalizedPrompt))
            {
                ReportActivity("[ROUTER] Blender Agent V2");
                return await blenderV2.HandleAsync(normalizedPrompt);
            }

            if (agentV2.ShouldHandle(normalizedPrompt))
            {
                if (!continuation)
                {
                    lastUnityV2Goal = normalizedPrompt;
                }

                ReportActivity("[ROUTER] Unity Cowork Agent V2");
                return await agentV2.HandleAsync(normalizedPrompt);
            }

            if (
                continuation
                && IsAgentV2Enabled()
                && !string.IsNullOrWhiteSpace(lastUnityV2Goal)
            )
            {
                ReportActivity("[ROUTER] Unity Cowork Agent V2 resume recovery");
                return await agentV2.HandleAsync(lastUnityV2Goal);
            }

            ReportActivity("[ROUTER] Legacy compatibility path");
            return await legacy.Ask(normalizedPrompt);
        }

        public void ResetConversationContext()
        {
            agentV2.Reset();
            legacy.ResetConversationContext();
            lastUnityV2Goal = "";
        }

        public string BuildDiagnostics()
        {
            List<string> lines = new List<string>();
            lines.Add("Agent: " + AgentVersion.Version);
            lines.Add("Unity root: " + (string.IsNullOrWhiteSpace(settings.UnityProjectRoot) ? "not configured" : settings.UnityProjectRoot));
            string blender = settings.ResolveBlenderExecutable();
            lines.Add("Blender: " + (string.IsNullOrWhiteSpace(blender) ? "not found" : blender));
            lines.Add("OpenRouter: " + IsKeyConfigured("OPENROUTER_API_KEY"));
            lines.Add("Gemini: " + IsKeyConfigured("GEMINI_API_KEY"));
            lines.Add("Groq: " + IsKeyConfigured("GROQ_API_KEY"));
            foreach (string issue in settings.Validate())
            {
                lines.Add("Warning: " + issue);
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static string IsKeyConfigured(string name)
        {
            return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
                ? "not configured"
                : "configured";
        }

        private static bool IsAgentV2Enabled()
        {
            return !string.Equals(
                Environment.GetEnvironmentVariable("AI_AGENT_V2"),
                "0",
                StringComparison.OrdinalIgnoreCase
            );
        }

        private static bool IsContinuation(string prompt)
        {
            string value = (prompt ?? "")
                .Trim()
                .ToLowerInvariant();

            return
                value == "nastavi"
                || value == "continue"
                || value == "nastavi dalje"
                || value == "probaj opet"
                || value == "try again"
                || value == "opet";
        }

        private void ReportActivity(string message)
        {
            Activity?.Invoke(message);
        }
    }
}
