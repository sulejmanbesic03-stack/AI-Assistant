using AI_Assistant.AgentV2;
using AI_Assistant.TempCapabilities;
using AI_Assistant.Tools;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AI_Assistant.AI
{
    /// <summary>
    /// Top-level runtime router.
    /// Unity engineering requests go through the Cowork-style Agent V2.
    /// The legacy AIIntegration remains available for chat, filesystem and
    /// self-development workflows that have not yet moved to the V2 kernel.
    /// </summary>
    public sealed class AssistantRuntime
    {
        private readonly AIIntegration legacy;
        private readonly AgentOrchestratorV2 agentV2;

        // Recovery anchor for Unity continuations. A Unity domain reload or a
        // no-mutation response can leave the V2 task marked non-resumable even
        // though the user still means "continue the Unity task". In that case
        // we replay the original V2 goal through a fresh inspect instead of
        // accidentally routing the bare word "nastavi" into legacy V1.
        private string lastUnityV2Goal = "";

        public event Action<string>? Activity;

        public AssistantRuntime(
            List<string> allowedRoots,
            string projectFilePath,
            string sourceRoot,
            string updaterProjectPath
        )
        {
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
        }

        public async Task<string> Ask(string prompt)
        {
            string normalizedPrompt = (prompt ?? "").Trim();
            bool continuation = IsContinuation(normalizedPrompt);

            if (agentV2.ShouldHandle(normalizedPrompt))
            {
                if (!continuation)
                {
                    lastUnityV2Goal = normalizedPrompt;
                }

                ReportActivity(
                    "[ROUTER] Unity Cowork Agent V2"
                );

                return await agentV2.HandleAsync(normalizedPrompt);
            }

            // Important recovery path: never send a bare Unity continuation to
            // legacy V1 just because V2 lost/closed its in-memory task state.
            // Re-run the remembered Unity goal so V2 performs a fresh inspect
            // against the real post-reload project state.
            if (
                continuation
                && IsAgentV2Enabled()
                && !string.IsNullOrWhiteSpace(lastUnityV2Goal)
            )
            {
                ReportActivity(
                    "[ROUTER] Unity Cowork Agent V2 resume recovery"
                );

                return await agentV2.HandleAsync(lastUnityV2Goal);
            }

            ReportActivity(
                "[ROUTER] Legacy compatibility path"
            );

            return await legacy.Ask(normalizedPrompt);
        }

        public void ResetConversationContext()
        {
            agentV2.Reset();
            legacy.ResetConversationContext();
            lastUnityV2Goal = "";
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
