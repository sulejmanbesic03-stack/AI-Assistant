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
        private const int MaxModelCallsPerTask = 3;

        private readonly UnityContextServiceV2 contextService;
        private readonly ProviderRouterV2 providers;
        private readonly UnityCommandExecutorV2 executor;
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

            contextService =
                new UnityContextServiceV2(
                    unityTools,
                    activity
                );

            providers =
                new ProviderRouterV2(
                    activity
                );

            executor =
                new UnityCommandExecutorV2(
                    unityTools,
                    tempCapabilities,
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
                    Environment.GetEnvironmentVariable(
                        "AI_AGENT_V2"
                    ),
                    "0",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return false;
            }

            string text =
                (prompt ?? "")
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

            bool unitySignal =
                ContainsAny(
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
                    "interaction"
                );

            bool actionSignal =
                ContainsAny(
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
                    "provjeri"
                );

            bool explicitMode =
                text.StartsWith("/agent ")
                || text.StartsWith("/plan ");

            return
                explicitMode
                || (unitySignal && actionSignal);
        }

        public async Task<string> HandleAsync(string prompt)
        {
            (AgentModeV2 mode, string cleanedPrompt) =
                ResolveMode(prompt);

            bool continuation =
                activeTask != null
                && !activeTask.Completed
                && IsContinuation(
                    cleanedPrompt
                        .Trim()
                        .ToLowerInvariant()
                );

            if (!continuation)
            {
                activeTask =
                    new AgentTaskStateV2
                    {
                        Goal = cleanedPrompt
                    };

                activeSnapshot = null;
                activeImplementation = null;
            }

            AgentTaskStateV2 task =
                activeTask
                ?? throw new InvalidOperationException(
                    "Agent V2 task state was not initialized."
                );

            string goal =
                continuation
                    ? task.Goal
                    : cleanedPrompt;

            activity(
                "[V2 TASK] "
                + task.TaskId
                + " "
                + mode.ToString().ToUpperInvariant()
            );

            try
            {
                task.Advance(
                    AgentTaskPhaseV2.Inspecting
                );

                if (activeSnapshot == null)
                {
                    activeSnapshot =
                        await contextService.CaptureAsync(
                            goal
                        );

                    task.CompletedSteps.Add(
                        "Captured compact Unity project context"
                    );
                }

                if (mode == AgentModeV2.Plan)
                {
                    return
                        await BuildPlanOnlyReply(
                            task,
                            goal,
                            activeSnapshot
                        );
                }

                task.Advance(
                    AgentTaskPhaseV2.Designing
                );

                if (activeImplementation == null)
                {
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

                        return
                            FormatProviderFailure(
                                implementationReply
                            );
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
                            "Agent V2 received an invalid implementation JSON from "
                            + implementationReply.Provider
                            + ": "
                            + parseError
                            + "\n\nRaw response:\n"
                            + AgentJsonV2.Compact(
                                implementationReply.Content,
                                2200
                            );
                    }

                    activeImplementation = implementation;
                    task.LastSummary = implementation.Summary;
                }

                if (
                    activeImplementation.NeedsDocumentation
                    && string.IsNullOrWhiteSpace(
                        activeSnapshot.Documentation
                    )
                    && task.ModelCalls < MaxModelCallsPerTask
                )
                {
                    activeSnapshot.Documentation =
                        await contextService.GetDocumentationAsync(
                            activeImplementation.DocumentationQuery
                        );

                    if (
                        !string.IsNullOrWhiteSpace(
                            activeSnapshot.Documentation
                        )
                    )
                    {
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
                }

                if (
                    activeImplementation.ScriptChanges.Count == 0
                    && activeImplementation.SceneActions.Count == 0
                    && activeImplementation.TemporaryCapability == null
                )
                {
                    task.Completed = true;
                    task.Phase = AgentTaskPhaseV2.Completed;

                    return
                        string.IsNullOrWhiteSpace(
                            activeImplementation.Summary
                        )
                            ? "Agent V2 found no safe concrete change to apply."
                            : activeImplementation.Summary;
                }

                task.Advance(
                    AgentTaskPhaseV2.Executing
                );

                AgentExecutionReportV2 report =
                    await executor.ExecuteAsync(
                        activeImplementation,
                        goal
                    );

                if (
                    report.CompileFailed
                    && task.ModelCalls < MaxModelCallsPerTask
                )
                {
                    activity("[V2 REPAIR] one compile repair pass");

                    task.Advance(
                        AgentTaskPhaseV2.Repairing
                    );

                    ProviderReplyV2 repairReply =
                        await providers.CompleteAsync(
                            task,
                            BuildImplementationSystemPrompt(),
                            BuildRepairPrompt(
                                goal,
                                activeSnapshot,
                                activeImplementation,
                                report.CompileFailureText
                            )
                        );

                    if (
                        repairReply.Success
                        && AgentJsonV2.TryParseImplementation(
                            repairReply.Content,
                            out AgentImplementationV2 repaired,
                            out _
                        )
                    )
                    {
                        activeImplementation = repaired;

                        task.Advance(
                            AgentTaskPhaseV2.Executing
                        );

                        report =
                            await executor.ExecuteAsync(
                                activeImplementation,
                                goal
                            );
                    }
                }

                task.Advance(
                    AgentTaskPhaseV2.Verifying
                );

                foreach (
                    string changed
                    in report.FilesChanged
                )
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

                if (report.Success)
                {
                    task.Completed = true;
                    task.Advance(
                        AgentTaskPhaseV2.Completed,
                        "Execution and final verification completed"
                    );
                }
                else
                {
                    task.Phase =
                        AgentTaskPhaseV2.Failed;
                }

                return
                    FormatExecutionReply(
                        task,
                        activeImplementation,
                        report
                    );
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
                    + UnityContextServiceV2.FormatForModel(
                        snapshot
                    )
                );

            task.Completed = true;
            task.Phase =
                reply.Success
                    ? AgentTaskPhaseV2.Completed
                    : AgentTaskPhaseV2.Failed;

            return reply.Success
                ? reply.Content.Trim()
                : FormatProviderFailure(reply);
        }

        private static string BuildPlanSystemPrompt()
        {
            return
                "You are the planning stage of a Unity engineering agent. "
                + "The host already inspected the live project and supplied a compact snapshot. "
                + "Return a short numbered implementation plan. Do not write code and do not call tools. "
                + "Prefer repairing existing systems over adding duplicates. Mention the exact existing scripts and GameObjects from the supplied context when known. "
                + "Keep the plan under 10 steps and include one final verification step.";
        }

        private static string BuildImplementationSystemPrompt()
        {
            return
                "You are the implementation engine inside a Unity Editor agent. The C# host, not you, executes tools. "
                + "You receive a compact live project snapshot and must return ONE valid JSON object only. No markdown and no prose outside JSON.\n\n"
                + "Your goal is to finish the requested task in one implementation pass with the smallest safe set of mutations. Inspection has already happened; do not ask to inspect the same data again.\n\n"
                + "ENGINEERING RULES:\n"
                + "- Repair existing gameplay systems instead of stacking PlayerController2/EnemyAI2-style duplicates.\n"
                + "- Before adding a body collider, respect colliders already visible in the hierarchy/context. Do not create duplicate physical body colliders.\n"
                + "- Only one system should own player movement and one should own mouse-look/camera pitch. Physics collision must not be able to drive uncontrolled camera rotation.\n"
                + "- For Rigidbody players, keep physics movement in FixedUpdate and separate body yaw from camera pitch; freeze unwanted physical rotation when appropriate.\n"
                + "- Enemy navigation must have explicit Patrol/Chase/Attack/Return transitions. If NavMeshAgent.isStopped becomes true, code must define when it becomes false again.\n"
                + "- Persistent gameplay logic belongs in normal MonoBehaviour scripts under Assets, never in a temporary capability.\n"
                + "- Preserve an existing script asset_path and class_name when repairing it so Unity keeps component references.\n"
                + "- Generated script source must be complete compile-ready C# source, not a patch or excerpt.\n"
                + "- Do not invent an existing hierarchy path. Use exact paths from the snapshot. You may create a new object only when the user requested one.\n"
                + "- Set needs_documentation=true only for a genuinely uncertain/version-sensitive Unity API. Most normal C# gameplay repairs should keep it false.\n"
                + "- Do not enter Play Mode yourself. The host does runtime verification only when the user explicitly asked to test/run/observe.\n\n"
                + "SUPPORTED scene_actions types:\n"
                + "add_component, attach_script, create_gameobject, create_primitive, set_position, set_rotation, set_scale, set_active, rename_gameobject, set_parent, duplicate_gameobject, configure_rigidbody, configure_collider, create_material, set_material_color, assign_material, import_asset.\n"
                + "If a one-shot Editor setup cannot be expressed with those actions, you may return temporary_capability. Use at most ONE broad temporary capability.\n\n"
                + "TEMP CAPABILITY CONTRACT when needed:\n"
                + "- exactly one concrete IUnityDynamicCapability implementation\n"
                + "- Name property exactly matches temporary_capability.name\n"
                + "- string Execute(UnityDynamicCapabilityContext context, string argumentsJson)\n"
                + "- using UnityEngine is allowed; using UnityEditor is forbidden\n"
                + "- no filesystem, network, Process, Environment, reflection, threads, unsafe, Destroy/DestroyImmediate, build or package-manager APIs\n"
                + "- useful helpers: context.FindRequired(path), context.GetRequiredComponent<T>(go), context.GetOrAddComponent<T>(go), context.CreateGameObject(name,parent), context.CreatePrimitive(type,name,parent), context.Record(obj,action), context.MarkDirty(obj), context.SaveActiveScene()\n\n"
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
                + "  \"temporary_capability\": null,\n"
                + "  \"runtime_object_paths\": [\"Exact/Path\"],\n"
                + "  \"notes\": []\n"
                + "}\n"
                + "Omit unnecessary script changes and scene actions. Do not use unsupported scene action types.";
        }

        private static string BuildImplementationUserPrompt(
            string goal,
            UnityProjectSnapshotV2 snapshot
        )
        {
            return
                "USER GOAL:\n"
                + goal
                + "\n\nLIVE PROJECT SNAPSHOT:\n"
                + UnityContextServiceV2.FormatForModel(
                    snapshot
                )
                + "\nProduce the complete implementation JSON now.";
        }

        private static string BuildDocumentationRefinePrompt(
            string goal,
            UnityProjectSnapshotV2 snapshot,
            AgentImplementationV2 previous
        )
        {
            return
                "The host fetched official Unity documentation because your previous implementation requested it. Re-evaluate the implementation and return the complete JSON again.\n\n"
                + "USER GOAL:\n"
                + goal
                + "\n\nPROJECT + DOCUMENTATION CONTEXT:\n"
                + UnityContextServiceV2.FormatForModel(
                    snapshot
                )
                + "\n\nPREVIOUS IMPLEMENTATION:\n"
                + JsonSerializer.Serialize(previous);
        }

        private static string BuildRepairPrompt(
            string goal,
            UnityProjectSnapshotV2 snapshot,
            AgentImplementationV2 previous,
            string compileFailure
        )
        {
            return
                "The previous persistent script implementation failed Unity compilation. Fix ALL compiler problems in one pass and return the COMPLETE implementation JSON again. Do not create a second replacement controller/AI script. Preserve existing asset paths and class names.\n\n"
                + "USER GOAL:\n"
                + goal
                + "\n\nPROJECT CONTEXT:\n"
                + UnityContextServiceV2.FormatForModel(
                    snapshot
                )
                + "\n\nPREVIOUS IMPLEMENTATION:\n"
                + JsonSerializer.Serialize(previous)
                + "\n\nUNITY COMPILATION RESULT:\n"
                + AgentJsonV2.Compact(
                    compileFailure,
                    5000
                );
        }

        private static string FormatExecutionReply(
            AgentTaskStateV2 task,
            AgentImplementationV2 implementation,
            AgentExecutionReportV2 report
        )
        {
            StringBuilder builder =
                new StringBuilder();

            if (report.Success)
            {
                builder.Append("Agent V2 završio zadatak");

                if (!string.IsNullOrWhiteSpace(implementation.Summary))
                {
                    builder.Append(": ");
                    builder.Append(implementation.Summary.Trim());
                }

                builder.Append('.');

                if (report.FilesChanged.Count > 0)
                {
                    builder.Append(" Skripte: ");
                    builder.Append(
                        string.Join(
                            ", ",
                            report.FilesChanged
                        )
                    );
                    builder.Append('.');
                }

                builder.Append(
                    " Izvršeno je "
                    + report.Steps.Count
                    + " lokalnih Unity koraka uz "
                    + task.ModelCalls
                    + " AI poziv(a)."
                );

                if (report.RuntimeResults.Count > 0)
                {
                    builder.Append(
                        " Runtime provjera je odrađena za "
                        + report.RuntimeResults.Count
                        + " objekt(a)."
                    );
                }

                return builder.ToString();
            }

            builder.AppendLine(
                "Agent V2 nije označio zadatak kao završen."
            );

            foreach (string error in report.Errors.Take(4))
            {
                builder.AppendLine(
                    "- "
                    + error
                );
            }

            builder.Append(
                "AI pozivi: "
                + task.ModelCalls
                + ". Task state je sačuvan; možeš napisati 'nastavi'."
            );

            return builder.ToString();
        }

        private static string FormatProviderFailure(
            ProviderReplyV2 reply
        )
        {
            string status =
                reply.StatusCode > 0
                    ? " HTTP " + reply.StatusCode
                    : "";

            return
                "Agent V2 provider error ("
                + (string.IsNullOrWhiteSpace(reply.Provider)
                    ? "no provider"
                    : reply.Provider)
                + status
                + "): "
                + AgentJsonV2.Compact(
                    reply.Error,
                    2400
                )
                + "\nTask state je sačuvan; nakon što provider bude dostupan možeš napisati 'nastavi'.";
        }

        private static (AgentModeV2 Mode, string Prompt) ResolveMode(
            string prompt
        )
        {
            string value =
                (prompt ?? "").Trim();

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

            return (
                AgentModeV2.Agent,
                value
            );
        }

        private static bool IsContinuation(string text)
        {
            string normalized =
                text.Trim().ToLowerInvariant();

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
