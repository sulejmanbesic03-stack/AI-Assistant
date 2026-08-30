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
            if (agentV2.ShouldHandle(prompt))
            {
                ReportActivity(
                    "[ROUTER] Unity Cowork Agent V2"
                );

                return await agentV2.HandleAsync(prompt);
            }

            ReportActivity(
                "[ROUTER] Legacy compatibility path"
            );

            return await legacy.Ask(prompt);
        }

        public void ResetConversationContext()
        {
            agentV2.Reset();
            legacy.ResetConversationContext();
        }

        private void ReportActivity(string message)
        {
            Activity?.Invoke(message);
        }
    }
}
