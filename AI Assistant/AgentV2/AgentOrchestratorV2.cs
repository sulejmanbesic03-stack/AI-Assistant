using AI_Assistant.TempCapabilities;
using AI_Assistant.Tools;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AI_Assistant.AgentV2
{
    public sealed class AgentOrchestratorV2
    {
        private const int MaxModelCallsPerTask = 8;
        private const int MaxExecutionAttempts = 3;

        private readonly UnityContextServiceV2 contextService;
        private readonly ProviderRouterV2 providers;
        private readonly AgentCapabilityRegistryV2 capabilities;
        private readonly UnityCoworkExecutorV2 executor;
        private readonly Action<string> activity;

        private AgentTaskStateV2? activeTask;
        private UnityProjectSnapshotV2? activeSnapshot;
        private AgentImplementationV2? activeImplementation;

        public AgentOrchestratorV2(
            UnityBridgeTools unityTools,
            TempCapabilityManager tempCapabilities,
            Action<string> activity
        )
        {
            this.activity = activity;

            contextService = new UnityContextServiceV2(
                unityTools,
                activity
            );

            providers = new ProviderRouterV2(activity);
            capabilities = new AgentCapabilityRegistryV2(tempCapabilities);

            UnityCommandExecutorV2 nativeExecutor =
                new UnityCommandExecutorV2(
                    unityTools,
                    tempCapabilities,
                    activity
                );

            executor = new UnityCoworkExecutorV2(
                nativeExecutor,
                tempCapabilities,
                capabilities,
                activity
            );
        }

        public void Reset()
        {
            activeTask = null;
            activeSnapshot = null;
            activeImplementation = null;
        }

        public bool ShouldHandle(string prompt)
        {
            if (
                string.Equals(
                    Environment.GetEnvironmentVariable("AI_AGENT_V2"),
                    "0",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return false;
            }

            string text = (prompt ?? "")
                .Trim()
                .ToLowerInvariant();

            if (
                activeTask != null
                && !activeTask.Completed
                && IsContinuation(text)
            )
            {
                return true;
            }

            bool unitySignal = ContainsAny(
                text,
                "unity",
                "gameobject",
                "scene",
                "player",
                "camera",
                "kamera",
                "rigidbody",
                "collider",
                "charactercontroller",
                "navmesh",
                "enemy",
                "neprijatelj",
                "monobehaviour",
                "prefab",
                "material",
                "skript",
                "script",
                "movement",
                "controller",
                "inventory",
                "weapon",
                "interaction",
                "terrain",
                "animator",
                "animation"
            );

            bool actionSignal = ContainsAny(
                text,
                "napravi",
                "create",
                "build",
                "dodaj",
                "add",
                "popravi",
                "fix",
                "repair",
                "izmijeni",
                "promijeni",
                "change",
                "update",
                "attach",
                "povezi",
                "configure",
                "konfigur",
                "testiraj",
                "test",
                "verify",
                "provjeri",
                "setup",
                "set up",
                "implement"
            );

            bool explicitMode =
                text.StartsWith("/agent ")
                || text.StartsWith("/plan ");

            return explicitMode || (unitySignal && actionSignal);
        }

        public async Task<string> HandleAsync(string prompt)
        {
            (AgentModeV2 mode, string cleanedPrompt) = ResolveMode(prompt);

            bool continuation =
                activeTask != null
                && !activeTask.Completed
                && IsContinuation(
                    cleanedPrompt.Trim().ToLowerInvariant()
                );

            if (!continuation)
            {
                activeTask = new AgentTaskStateV2
                {
                    Goal = cleanedPrompt
                };

                activeSnapshot = null;
                activeImplementation = null;
            }

            AgentTaskStateV2 task = activeTask
                ?? throw new InvalidOperationException(
                    "Agent V2 task state was not initialized."
                );

            string goal = continuation
                ? task.Goal
                : cleanedPrompt;

            activity(
                "[V2 COWORK] "
                + task.TaskId
                + " "
                + mode.ToString().ToUpperInvariant()
            );

            try
            {
                task.Advance(AgentTaskPhaseV2.Inspecting);

                if (activeSnapshot == null)
                {
                    activeSnapshot = await contextService.CaptureAsync(goal);
                    task.CompletedSteps.Add(
                        "Captured live Unity project context"
                    );
                }

                if (mode == AgentModeV2.Plan)
                {
                    return await BuildPlanOnlyReply(
                        task,
                        goal,
                        activeSnapshot
                    );
                }

                if (activeImplementation == null)
                {
                    task.Advance(AgentTaskPhaseV2.Designing);

                    ProviderReplyV2 implementationReply =
                        await providers.CompleteAsync(
                            task,
                            BuildImplementationSystemPrompt(),
                            BuildImplementationUserPrompt(
                                goal,
                                activeSnapshot
                            )
                        );

                    if (!implementationReply.Success)
                    {
                        task.Phase = AgentTaskPhaseV2.Failed;
                        return FormatProviderFailure(implementationReply);
                    }

                    if (
                        !AgentJsonV2.TryParseImplementation(
                            implementationReply.Content,
                            out AgentImplementationV2 implementation,
                            out string parseError
                        )
                    )
                    {
                        task.Phase = AgentTaskPhaseV2.Failed;

                        return
                            "Agent V2 received invalid implementation JSON from "
                            + implementationReply.Provider
                            + ": "
                            + parseError
                            + "\n\nRaw response:\n"
                            + AgentJsonV2.Compact(
                                implementationReply.Content,
                                2400
                            );
                    }

                    activeImplementation = implementation;
                    task.LastSummary = implementation.Summary;

                    await RefineWithDocumentationIfNeeded(
                        task,
                        goal
                    );
                }

                while (task.ExecutionAttempts < MaxExecutionAttempts)
                {
                    AgentImplementationV2 implementation =
                        activeImplementation
                        ?? throw new InvalidOperationException(
                            "Agent V2 implementation was not initialized."
                        );

                    if (!implementation.HasConcreteWork())
                    {
                        task.Completed = true;
                        task.Phase = AgentTaskPhaseV2.Completed;

                        return string.IsNullOrWhiteSpace(implementation.Summary)
                            ? "Agent V2 found no safe concrete change to apply."
                            : implementation.Summary;
                    }

                    string fingerprint =
                        AgentExecutionPolicyV2.Fingerprint(implementation);

                    if (
                        task.AttemptFingerprints.Contains(
                            fingerprint,
                            StringComparer.OrdinalIgnoreCase
                        )
                    )
                    {
                        task.Phase = AgentTaskPhaseV2.Failed;

                        return
                            "Agent V2 zaustavio je ponavljanje istog neuspješnog execution plana ("
                            + fingerprint
                            + "). Task state je sačuvan; napiši konkretnu novu instrukciju ili resetuj razgovor.";
                    }

                    task.AttemptFingerprints.Add(fingerprint);
                    task.ExecutionAttempts++;
                    task.Advance(AgentTaskPhaseV2.Executing);

                    activity(
                        "[V2 EXECUTE] attempt "
                        + task.ExecutionAttempts
                        + "/"
                        + MaxExecutionAttempts
                        + " plan="
                        + fingerprint
                    );

                    AgentExecutionReportV2 report =
                        await executor.ExecuteAsync(
                            implementation,
                            goal
                        );

                    RegisterChangedFiles(task, report);

                    task.Advance(AgentTaskPhaseV2.Observing);
                    task.LastObservation =
                        AgentExecutionPolicyV2.BuildObservation(report);

                    activity(
                        "[V2 OBSERVE] "
                        + (report.Success ? "success" : "failure")
                    );

                    if (report.Success)
                    {
                        task.Completed = true;
                        task.Advance(
                            AgentTaskPhaseV2.Completed,
                            "Execution and verification completed"
                        );

                        return FormatExecutionReply(
                            task,
                            implementation,
                            report
                        );
                    }

                    bool canCorrect =
                        task.ExecutionAttempts < MaxExecutionAttempts
                        && task.ModelCalls < MaxModelCallsPerTask;

                    if (!canCorrect)
                    {
                        task.Phase = AgentTaskPhaseV2.Failed;

                        return FormatExecutionReply(
                            task,
                            implementation,
                            report
                        );
                    }

                    task.Advance(AgentTaskPhaseV2.Correcting);
                    activity("[V2 CORRECT] refreshing live state");

                    // A failed attempt may have partially mutated the scene.
                    // Re-inspect so correction is based on reality rather than
                    // replaying the original plan blindly.
                    activeSnapshot =
                        await contextService.CaptureAsync(goal);

                    ProviderReplyV2 correctionReply =
                        await providers.CompleteAsync(
                            task,
                            BuildImplementationSystemPrompt(),
                            BuildCorrectionPrompt(
                                goal,
                                activeSnapshot,
                                implementation,
                                task.LastObservation
                            )
                        );

                    if (!correctionReply.Success)
                    {
                        task.Phase = AgentTaskPhaseV2.Failed;
                        return FormatProviderFailure(correctionReply);
                    }

                    if (
                        !AgentJsonV2.TryParseImplementation(
                            correctionReply.Content,
                            out AgentImplementationV2 corrected,
                            out string correctionParseError
                        )
                    )
                    {
                        task.Phase = AgentTaskPhaseV2.Failed;

                        return
                            "Agent V2 correction JSON was invalid: "
                            + correctionParseError
                            + "\n\nRaw response:\n"
                            + AgentJsonV2.Compact(
                                correctionReply.Content,
                                2400
                            );
                    }

                    activeImplementation = corrected;
                    task.LastSummary = corrected.Summary;
                }

                task.Phase = AgentTaskPhaseV2.Failed;
                return "Agent V2 reached its execution-attempt limit.";
            }
            catch (Exception ex)
            {
                task.Phase = AgentTaskPhaseV2.Failed;

                return
                    "Agent V2 internal error: "
                    + ex.GetType().Name
                    + ": "
                    + ex.Message;
            }
        }

        private async Task RefineWithDocumentationIfNeeded(
            AgentTaskStateV2 task,
            string goal
        )
        {
            if (
                activeImplementation == null
                || activeSnapshot == null
                || !activeImplementation.NeedsDocumentation
                || !string.IsNullOrWhiteSpace(activeSnapshot.Documentation)
                || task.ModelCalls >= MaxModelCallsPerTask
            )
            {
                return;
            }

            activeSnapshot.Documentation =
                await contextService.GetDocumentationAsync(
                    activeImplementation.DocumentationQuery
                );

            if (string.IsNullOrWhiteSpace(activeSnapshot.Documentation))
            {
                return;
            }

            ProviderReplyV2 refinedReply =
                await providers.CompleteAsync(
                    task,
                    BuildImplementationSystemPrompt(),
                    BuildDocumentationRefinePrompt(
                        goal,
                        activeSnapshot,
                        activeImplementation
                    )
                );

            if (
                refinedReply.Success
                && AgentJsonV2.TryParseImplementation(
                    refinedReply.Content,
                    out AgentImplementationV2 refined,
                    out _
                )
            )
            {
                activeImplementation = refined;
                task.LastSummary = refined.Summary;
            }
        }

        private async Task<string> BuildPlanOnlyReply(
            AgentTaskStateV2 task,
            string goal,
            UnityProjectSnapshotV2 snapshot
        )
        {
            ProviderReplyV2 reply =
                await providers.CompleteAsync(
                    task,
                    BuildPlanSystemPrompt(),
                    "USER GOAL:\n"
                    + goal
                    + "\n\nPROJECT CONTEXT:\n"
                    + UnityContextServiceV2.FormatForModel(snapshot)
                    + "\n\n"
                    + capabilities.FormatForModel()
                );

            task.Completed = true;
            task.Phase = reply.Success
                ? AgentTaskPhaseV2.Completed
                : AgentTaskPhaseV2.Failed;

            return reply.Success
                ? reply.Content.Trim()
                : FormatProviderFailure(reply);
        }

        private static string BuildPlanSystemPrompt()
        {
            return
                "You are the planning stage of a Unity engineering Cowork agent. "
                + "The host already inspected the live project and supplied a compact snapshot. "
                + "Return a short numbered implementation plan. Do not write code and do not call tools. "
                + "Prefer existing project systems and deterministic host capabilities. "
                + "Use a dynamic RunCommand-style temporary capability only as an escape hatch. "
                + "Keep the plan under 10 steps and include one final verification step.";
        }

        private string BuildImplementationSystemPrompt()
        {
            return
                "You are the implementation engine inside a Unity Editor Cowork-style agent. The C# host, not you, executes mutations. "
                + "The host follows INSPECT -> DESIGN -> EXECUTE -> OBSERVE -> CORRECT. Return ONE valid JSON object only; no markdown or prose outside JSON.\n\n"
                + "CAPABILITY POLICY:\n"
                + capabilities.FormatForModel()
                + "\n"
                + "Choose the LOWEST capability level that can safely finish the task. Never emit both capability_call and other mutations. "
                + "temporary_capability is analogous to a dynamic RunCommand: one-shot, broad, validated, and LAST RESORT.\n\n"
                + "ENGINEERING RULES:\n"
                + "- Repair existing gameplay systems instead of stacking PlayerController2/EnemyAI2-style duplicates.\n"
                + "- Before adding a body collider, respect colliders already visible in the hierarchy/context. Do not create duplicate physical body colliders.\n"
                + "- Only one system should own player movement and one should own mouse-look/camera pitch. Physics collision must not drive uncontrolled camera rotation.\n"
                + "- For Rigidbody players, keep physics movement in FixedUpdate and separate body yaw from camera pitch; freeze unwanted physical rotation when appropriate.\n"
                + "- Enemy navigation must have explicit Patrol/Chase/Attack/Return transitions. If NavMeshAgent.isStopped becomes true, code must define when it becomes false again.\n"
                + "- Persistent gameplay logic belongs in normal MonoBehaviour scripts under Assets, never in a temporary capability.\n"
                + "- Preserve an existing script asset_path and class_name when repairing it so Unity keeps component references.\n"
                + "- Generated script source must be complete compile-ready C# source, not a patch or excerpt.\n"
                + "- Do not invent an existing hierarchy path. Use exact paths from the snapshot. Create new objects only when needed for the requested result.\n"
                + "- Set needs_documentation=true only for a genuinely uncertain/version-sensitive Unity API.\n"
                + "- Do not enter Play Mode yourself. Runtime verification is host-controlled.\n\n"
                + "SUPPORTED scene_actions types:\n"
                + "add_component, attach_script, create_gameobject, create_primitive, set_position, set_rotation, set_scale, set_active, rename_gameobject, set_parent, duplicate_gameobject, configure_rigidbody, configure_collider, create_material, set_material_color, assign_material, import_asset.\n\n"
                + "TEMP CAPABILITY CONTRACT when the deterministic actions cannot express the operation:\n"
                + "- exactly one concrete IUnityDynamicCapability implementation\n"
                + "- Name property exactly matches temporary_capability.name\n"
                + "- string Execute(UnityDynamicCapabilityContext context, string argumentsJson)\n"
                + "- using UnityEngine is allowed; using UnityEditor is forbidden\n"
                + "- no filesystem, network, Process, Environment, reflection, threads, unsafe, Destroy/DestroyImmediate, build or package-manager APIs\n"
                + "- helpers include context.FindRequired(path), context.GetRequiredComponent<T>(go), context.GetOrAddComponent<T>(go), context.CreateGameObject(name,parent), context.CreatePrimitive(type,name,parent), context.Record(obj,action), context.MarkDirty(obj), context.SaveActiveScene()\n\n"
                + "RETURN THIS JSON SHAPE:\n"
                + "{\n"
                + "  \"summary\": \"short concrete description\",\n"
                + "  \"needs_documentation\": false,\n"
                + "  \"documentation_query\": \"\",\n"
                + "  \"script_changes\": [\n"
                + "    {\"asset_path\":\"Assets/Scripts/Example.cs\",\"class_name\":\"Example\",\"source\":\"complete C# source\",\"overwrite\":true,\"attach_to\":\"Exact/Hierarchy/Path or empty\"}\n"
                + "  ],\n"
                + "  \"scene_actions\": [\n"
                + "    {\"type\":\"add_component\",\"object_path\":\"Exact/Path\",\"component_type\":\"UnityEngine.AI.NavMeshAgent\"}\n"
                + "  ],\n"
                + "  \"capability_call\": null,\n"
                + "  \"temporary_capability\": null,\n"
                + "  \"runtime_object_paths\": [\"Exact/Path\"],\n"
                + "  \"notes\": []\n"
                + "}\n"
                + "For a reusable capability use capability_call={\"tool_name\":\"run_Name\",\"arguments_json\":\"{}\"} and leave script_changes/scene_actions/temporary_capability empty.";
        }

        private string BuildImplementationUserPrompt(
            string goal,
            UnityProjectSnapshotV2 snapshot
        )
        {
            return
                "USER GOAL:\n"
                + goal
                + "\n\nLIVE PROJECT SNAPSHOT:\n"
                + UnityContextServiceV2.FormatForModel(snapshot)
                + "\n\n"
                + capabilities.FormatForModel()
                + "\nProduce the complete implementation JSON now.";
        }

        private string BuildDocumentationRefinePrompt(
            string goal,
            UnityProjectSnapshotV2 snapshot,
            AgentImplementationV2 previous
        )
        {
            return
                "The host fetched official Unity documentation because the previous implementation requested it. Re-evaluate the implementation and return the complete JSON again.\n\n"
                + "USER GOAL:\n"
                + goal
                + "\n\nPROJECT + DOCUMENTATION CONTEXT:\n"
                + UnityContextServiceV2.FormatForModel(snapshot)
                + "\n\nCAPABILITIES:\n"
                + capabilities.FormatForModel()
                + "\n\nPREVIOUS IMPLEMENTATION:\n"
                + JsonSerializer.Serialize(previous);
        }

        private string BuildCorrectionPrompt(
            string goal,
            UnityProjectSnapshotV2 postAttemptSnapshot,
            AgentImplementationV2 previous,
            string observation
        )
        {
            return
                "The previous execution attempt did not finish successfully. This is an OBSERVE -> CORRECT pass, not a blind retry. "
                + "The project snapshot below was captured AFTER the failed attempt and is authoritative. Some earlier steps may already have succeeded. "
                + "Do not repeat successful mutations unnecessarily and do not return the identical implementation again. Return ONE complete corrected JSON object only.\n\n"
                + "USER GOAL:\n"
                + goal
                + "\n\nPOST-ATTEMPT LIVE PROJECT SNAPSHOT:\n"
                + UnityContextServiceV2.FormatForModel(postAttemptSnapshot)
                + "\n\nEXECUTION OBSERVATION:\n"
                + observation
                + "\n\nPREVIOUS IMPLEMENTATION:\n"
                + JsonSerializer.Serialize(previous)
                + "\n\nAVAILABLE CAPABILITIES:\n"
                + capabilities.FormatForModel()
                + "\nCorrect the cause of failure. If a deterministic action is insufficient, escalate one level; use temporary_capability only as the final RunCommand-style fallback.";
        }

        private static void RegisterChangedFiles(
            AgentTaskStateV2 task,
            AgentExecutionReportV2 report
        )
        {
            foreach (string changed in report.FilesChanged)
            {
                if (
                    !task.FilesChanged.Contains(
                        changed,
                        StringComparer.OrdinalIgnoreCase
                    )
                )
                {
                    task.FilesChanged.Add(changed);
                }
            }
        }

        private static string FormatExecutionReply(
            AgentTaskStateV2 task,
            AgentImplementationV2 implementation,
            AgentExecutionReportV2 report
        )
        {
            StringBuilder builder = new StringBuilder();

            if (report.Success)
            {
                builder.Append("Agent V2 završio zadatak");

                if (!string.IsNullOrWhiteSpace(implementation.Summary))
                {
                    builder.Append(": ");
                    builder.Append(implementation.Summary.Trim());
                }

                builder.Append('.');

                if (task.FilesChanged.Count > 0)
                {
                    builder.Append(" Skripte: ");
                    builder.Append(string.Join(", ", task.FilesChanged));
                    builder.Append('.');
                }

                builder.Append(
                    " Execution pokušaja: "
                    + task.ExecutionAttempts
                    + ", AI poziva: "
                    + task.ModelCalls
                    + "."
                );

                return builder.ToString();
            }

            builder.AppendLine(
                "Agent V2 nije označio zadatak kao završen."
            );

            foreach (string error in report.Errors.Take(6))
            {
                builder.AppendLine("- " + error);
            }

            builder.Append(
                "Execution pokušaja: "
                + task.ExecutionAttempts
                + ", AI poziva: "
                + task.ModelCalls
                + ". Task state je sačuvan; možeš napisati 'nastavi'."
            );

            return builder.ToString();
        }

        private static string FormatProviderFailure(
            ProviderReplyV2 reply
        )
        {
            string status = reply.StatusCode > 0
                ? " HTTP " + reply.StatusCode
                : "";

            string model = string.IsNullOrWhiteSpace(reply.Model)
                ? ""
                : "/" + reply.Model;

            return
                "Agent V2 provider error ("
                + (string.IsNullOrWhiteSpace(reply.Provider)
                    ? "no provider"
                    : reply.Provider)
                + model
                + status
                + "): "
                + AgentJsonV2.Compact(reply.Error, 2600)
                + "\nTask state je sačuvan; nakon što provider bude dostupan možeš napisati 'nastavi'.";
        }

        private static (AgentModeV2 Mode, string Prompt) ResolveMode(
            string prompt
        )
        {
            string value = (prompt ?? "").Trim();

            if (
                value.StartsWith(
                    "/plan ",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return (
                    AgentModeV2.Plan,
                    value.Substring(6).Trim()
                );
            }

            if (
                value.StartsWith(
                    "/agent ",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return (
                    AgentModeV2.Agent,
                    value.Substring(7).Trim()
                );
            }

            return (AgentModeV2.Agent, value);
        }

        private static bool IsContinuation(string text)
        {
            string normalized = text.Trim().ToLowerInvariant();

            string[] values =
            {
                "nastavi",
                "continue",
                "nastavi dalje",
                "probaj opet",
                "try again",
                "opet"
            };

            return values.Contains(
                normalized,
                StringComparer.OrdinalIgnoreCase
            );
        }

        private static bool ContainsAny(
            string text,
            params string[] values
        )
        {
            foreach (string value in values)
            {
                if (
                    text.Contains(
                        value,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return true;
                }
            }

            return false;
        }
    }
}
