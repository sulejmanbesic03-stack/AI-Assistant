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
        private readonly BlenderAgentV2 blenderV2;
        private readonly RuntimeSettings settings;

        private string lastUnityV2Goal = "";
        private string pendingHighRiskPrompt = "";

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
            TempCapabilityManager tempCapabilities = new TempCapabilityManager(
                sourceRoot,
                unityTools
            );

            agentV2 = new AgentOrchestratorV2(
                unityTools,
                tempCapabilities,
                ReportActivity
            );

            blenderV2 = new BlenderAgentV2(settings, ReportActivity);
        }

        public async Task<string> Ask(string prompt)
        {
            AgentCancellationHub.BeginTask();
            string normalizedPrompt = (prompt ?? "").Trim();

            if (IsApproval(normalizedPrompt) && !string.IsNullOrWhiteSpace(pendingHighRiskPrompt))
            {
                string approved = pendingHighRiskPrompt;
                pendingHighRiskPrompt = "";
                ReportActivity("[RISK GATE] approved by user");
                return await RouteApprovedAsync(approved);
            }

            if (IsCancellation(normalizedPrompt) && !string.IsNullOrWhiteSpace(pendingHighRiskPrompt))
            {
                pendingHighRiskPrompt = "";
                ReportActivity("[RISK GATE] cancelled by user");
                return "High-risk task cancelled. No execution was started.";
            }

            if (
                settings.RequireApprovalForDestructiveChanges
                && IsHighRisk(normalizedPrompt)
                && !IsPlanOnly(normalizedPrompt)
            )
            {
                pendingHighRiskPrompt = normalizedPrompt;
                ReportActivity("[RISK GATE] destructive/high-impact task held for approval");
                return "High-risk change detected. I have not executed it. Type APPROVE to run the held task, or CANCEL to discard it.";
            }

            return await RouteApprovedAsync(normalizedPrompt);
        }

        public void CancelCurrentWork()
        {
            AgentCancellationHub.CancelCurrent();
            ReportActivity("[CANCEL] stop requested by user");
        }

        private async Task<string> RouteApprovedAsync(string normalizedPrompt)
        {
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
            AgentCancellationHub.CancelCurrent();
            agentV2.Reset();
            legacy.ResetConversationContext();
            lastUnityV2Goal = "";
            pendingHighRiskPrompt = "";
        }

        public string BuildDiagnostics()
        {
            List<string> lines = new List<string>();
            lines.Add("Agent: " + AgentVersion.Version);
            lines.Add("Unity root: " + (string.IsNullOrWhiteSpace(settings.UnityProjectRoot) ? "not configured" : settings.UnityProjectRoot));
            string blender = settings.ResolveBlenderExecutable();
            lines.Add("Blender: " + (string.IsNullOrWhiteSpace(blender) ? "not found" : blender));
            lines.Add("Blender model: " + (Environment.GetEnvironmentVariable("BLENDER_OPENROUTER_MODEL") ?? "inclusionai/ling-3.0-flash-fin:free"));
            lines.Add("OpenRouter: " + IsKeyConfigured("OPENROUTER_API_KEY"));
            lines.Add("Gemini: " + IsKeyConfigured("GEMINI_API_KEY"));
            lines.Add("Groq: " + IsKeyConfigured("GROQ_API_KEY"));
            lines.Add("Risk gate: " + (settings.RequireApprovalForDestructiveChanges ? "on" : "off"));
            foreach (string issue in settings.Validate())
            {
                lines.Add("Warning: " + issue);
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static bool IsHighRisk(string prompt)
        {
            string p = (prompt ?? "").Trim().ToLowerInvariant();
            string[] signals =
            {
                "delete ", "obrisi", "obriši", "remove all", "delete all",
                "reset scene", "wipe", "overwrite", "replace entire", "replace all",
                "remove script", "delete script", "delete folder", "remove folder",
                "clear scene", "destroy all", "rename project", "move project"
            };

            foreach (string signal in signals)
            {
                if (p.Contains(signal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsPlanOnly(string prompt)
        {
            return (prompt ?? "").Trim().StartsWith("/plan ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsApproval(string prompt)
        {
            string p = (prompt ?? "").Trim();
            return p.Equals("approve", StringComparison.OrdinalIgnoreCase)
                || p.Equals("odobri", StringComparison.OrdinalIgnoreCase)
                || p.Equals("potvrdi", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCancellation(string prompt)
        {
            string p = (prompt ?? "").Trim();
            return p.Equals("cancel", StringComparison.OrdinalIgnoreCase)
                || p.Equals("otkazi", StringComparison.OrdinalIgnoreCase)
                || p.Equals("otkaži", StringComparison.OrdinalIgnoreCase);
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
            string value = (prompt ?? "").Trim().ToLowerInvariant();
            return value == "nastavi"
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
