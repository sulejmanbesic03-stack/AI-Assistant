using AI_Assistant.TempCapabilities;

using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AI_Assistant.AgentV2
{
    internal sealed class AgentCapabilityRegistryV2
    {
        private readonly TempCapabilityManager tempCapabilities;

        public AgentCapabilityRegistryV2(
            TempCapabilityManager tempCapabilities
        )
        {
            this.tempCapabilities = tempCapabilities;
        }

        public string FormatForModel()
        {
            StringBuilder builder = new StringBuilder();

            builder.AppendLine("=== CAPABILITY ROUTING ===");
            builder.AppendLine(
                "Priority 1: persistent script_changes for gameplay code that must survive after the task."
            );
            builder.AppendLine(
                "Priority 2: deterministic scene_actions for normal scene/object/component/material mutations."
            );

            if (tempCapabilities.Library.Entries.Count > 0)
            {
                builder.AppendLine(
                    "Priority 3: reusable capabilities already known by the host:"
                );

                foreach (
                    CapabilityManifestEntry entry
                    in tempCapabilities.Library.Entries
                        .OrderByDescending(item => item.TimesUsed)
                        .ThenBy(item => item.Name)
                        .Take(12)
                )
                {
                    builder.Append("- run_");
                    builder.Append(entry.Name);
                    builder.Append(": ");
                    builder.AppendLine(
                        string.IsNullOrWhiteSpace(entry.Description)
                            ? entry.Name
                            : AgentJsonV2.Compact(entry.Description, 180)
                    );
                }
            }
            else
            {
                builder.AppendLine(
                    "Priority 3: no reusable promoted capabilities are currently registered."
                );
            }

            builder.AppendLine(
                "Priority 4: temporary_capability is the dynamic RunCommand escape hatch. Use it only when priorities 1-3 cannot express the operation safely."
            );
            builder.AppendLine(
                "Never create a temporary capability for persistent gameplay logic."
            );

            return AgentJsonV2.Compact(builder.ToString(), 2600);
        }

        public bool IsKnownReusableCapability(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                return false;
            }

            return tempCapabilities.Library.TryGetEntry(
                toolName,
                out CapabilityManifestEntry? entry
            ) && entry != null;
        }
    }

    internal sealed class UnityCoworkExecutorV2
    {
        private readonly UnityCommandExecutorV2 nativeExecutor;
        private readonly TempCapabilityManager tempCapabilities;
        private readonly AgentCapabilityRegistryV2 capabilities;
        private readonly Action<string> activity;

        public UnityCoworkExecutorV2(
            UnityCommandExecutorV2 nativeExecutor,
            TempCapabilityManager tempCapabilities,
            AgentCapabilityRegistryV2 capabilities,
            Action<string> activity
        )
        {
            this.nativeExecutor = nativeExecutor;
            this.tempCapabilities = tempCapabilities;
            this.capabilities = capabilities;
            this.activity = activity;
        }

        public async Task<AgentExecutionReportV2> ExecuteAsync(
            AgentImplementationV2 implementation,
            string userGoal
        )
        {
            if (implementation.CapabilityCall == null)
            {
                return await nativeExecutor.ExecuteAsync(
                    implementation,
                    userGoal
                );
            }

            AgentExecutionReportV2 report =
                new AgentExecutionReportV2();

            ReusableCapabilityCallV2 call = implementation.CapabilityCall;
            call.ToolName = (call.ToolName ?? "").Trim();
            call.ArgumentsJson = string.IsNullOrWhiteSpace(call.ArgumentsJson)
                ? "{}"
                : call.ArgumentsJson;

            bool mixedMutation =
                implementation.ScriptChanges.Count > 0
                || implementation.SceneActions.Count > 0
                || implementation.TemporaryCapability != null;

            if (mixedMutation)
            {
                report.Fail(
                    "capability_call must be exclusive. The model mixed a reusable capability with other mutations."
                );
                return report;
            }

            if (!capabilities.IsKnownReusableCapability(call.ToolName))
            {
                report.Fail(
                    "Unknown reusable capability '"
                    + call.ToolName
                    + "'. Use deterministic actions or a temporary capability instead."
                );
                return report;
            }

            activity("[V2 CAPABILITY] " + call.ToolName);

            if (
                !tempCapabilities.TryExecuteLibraryCapability(
                    call.ToolName,
                    call.ArgumentsJson,
                    out string result
                )
            )
            {
                report.Fail(
                    "Reusable capability disappeared before execution: "
                    + call.ToolName
                );
                return report;
            }

            if (!AgentJsonV2.LooksSuccessful(result))
            {
                report.Fail(
                    "Reusable capability failed: "
                    + AgentJsonV2.Compact(result, 1400)
                );
                return report;
            }

            report.Steps.Add(
                "Reusable capability: " + call.ToolName
            );

            AgentImplementationV2 verificationOnly =
                new AgentImplementationV2
                {
                    Summary = implementation.Summary,
                    RuntimeObjectPaths = implementation.RuntimeObjectPaths
                };

            AgentExecutionReportV2 verification =
                await nativeExecutor.ExecuteAsync(
                    verificationOnly,
                    userGoal
                );

            report.MergeFrom(verification);
            return report;
        }
    }

    internal static class AgentExecutionPolicyV2
    {
        public static string Fingerprint(
            AgentImplementationV2 implementation
        )
        {
            string json = JsonSerializer.Serialize(implementation);
            byte[] hash = SHA256.HashData(
                Encoding.UTF8.GetBytes(json)
            );

            return Convert.ToHexString(hash).Substring(0, 16);
        }

        public static string BuildObservation(
            AgentExecutionReportV2 report
        )
        {
            StringBuilder builder = new StringBuilder();

            builder.AppendLine(
                report.Success
                    ? "EXECUTION_STATUS: success"
                    : "EXECUTION_STATUS: failed"
            );

            if (report.CompileFailed)
            {
                builder.AppendLine("COMPILE_FAILED: true");
                builder.AppendLine(
                    AgentJsonV2.Compact(
                        report.CompileFailureText,
                        1800
                    )
                );
            }

            if (report.Errors.Count > 0)
            {
                builder.AppendLine("ERRORS:");

                foreach (string error in report.Errors.Take(4))
                {
                    builder.Append("- ");
                    builder.AppendLine(
                        AgentJsonV2.Compact(error, 750)
                    );
                }
            }

            if (report.Steps.Count > 0)
            {
                builder.AppendLine("COMPLETED_LOCAL_STEPS:");

                foreach (string step in report.Steps.Take(10))
                {
                    builder.Append("- ");
                    builder.AppendLine(
                        AgentJsonV2.Compact(step, 240)
                    );
                }
            }

            if (
                !string.IsNullOrWhiteSpace(report.ConsoleResult)
                && !LooksLikeEmptyConsole(report.ConsoleResult)
            )
            {
                builder.AppendLine("UNITY_CONSOLE_AFTER_ATTEMPT:");
                builder.AppendLine(
                    AgentJsonV2.Compact(
                        report.ConsoleResult,
                        1200
                    )
                );
            }

            return AgentJsonV2.Compact(
                builder.ToString(),
                5000
            );
        }

        private static bool LooksLikeEmptyConsole(string value)
        {
            string normalized = value
                .Replace(" ", "", StringComparison.Ordinal)
                .Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", "", StringComparison.Ordinal)
                .ToLowerInvariant();

            return
                normalized.Contains("\"success\":true")
                && (
                    normalized.Contains("\"errors\":[]")
                    || normalized.Contains("\"items\":[]")
                    || normalized.Contains("\"messages\":[]")
                );
        }
    }
}
