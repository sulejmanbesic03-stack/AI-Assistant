using AI_Assistant.TempCapabilities;
using AI_Assistant.Tools;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AI_Assistant.AI
{
    public sealed class AIIntegration
    {
        public event Action<string>? Activity;

        // ============================================================
        // SERVICES
        // ============================================================

        private readonly HttpClient client;

        private readonly FileSystemTools fileTools;

        private readonly SelfDevelopmentTools selfTools;

        private readonly UnityBridgeTools unityTools;

        private readonly TempCapabilityManager tempCapabilities;

        private readonly List<ChatMessage> conversationHistory;


        // ============================================================
        // MODEL / RESOURCE LIMITS
        // ============================================================

        private const string Model =
            "openai/gpt-oss-120b";


        // Normal task should finish in only a few cycles.
        private const int MaxIterations =
            8;


        // Tool results are sent back to the model.
        // Keep them compact.
        private const int MaxToolResultChars =
            4500;

        private const int MaxUnityScriptReadResultChars =
            16000;


        private const int MaxChatHistoryMessages =
            4;


        private const int MaxToolCyclesInContext =
            3;


        private const int MaxToolUseRecoveryAttempts =
            1;


        private const int MaxRateLimitRetries =
            4;


        // ============================================================
        // CONSTRUCTOR
        // ============================================================

        public AIIntegration(
            List<string> allowedRoots,
            string projectFilePath,
            string sourceRoot,
            string updaterProjectPath
        )
        {
            client =
                new HttpClient();


            fileTools =
                new FileSystemTools(
                    allowedRoots
                );


            selfTools =
                new SelfDevelopmentTools(
                    projectFilePath,
                    sourceRoot,
                    updaterProjectPath
                );


            unityTools =
                new UnityBridgeTools();


            tempCapabilities =
                new TempCapabilityManager(
                    sourceRoot,
                    unityTools
                );


            conversationHistory =
                new List<ChatMessage>();
        }


        // ============================================================
        // ASK
        // ============================================================

        public async Task<string> Ask(
            string prompt
        )
        {
            string? apiKey =
                Environment.GetEnvironmentVariable(
                    "GROQ_API_KEY"
                );


            if (
                string.IsNullOrWhiteSpace(
                    apiKey
                )
            )
            {
                return
                    "GROQ_API_KEY nije pronađen.";
            }


            conversationHistory.Add(
                new ChatMessage(
                    "user",
                    prompt
                )
            );


            Dictionary<string, string> executedToolResults =
                new Dictionary<string, string>(
                    StringComparer.Ordinal
                );


            List<object> baseMessages =
                BuildBaseMessages();


            List<List<object>> toolCycles =
                new List<List<object>>();


            object[] toolDefinitions =
                BuildToolDefinitionsForTask(
                    prompt
                );


            ReportActivity(
                "[TOOLS] "
                +
                (
                    toolDefinitions.Length == 0
                        ?
                        "none"
                        :
                        GetRegisteredToolNames(
                            toolDefinitions
                        )
                )
            );


            int iteration =
                0;


            int toolUseRecoveryAttempts =
                0;


            while (
                iteration <
                MaxIterations
            )
            {
                iteration++;


                List<object> requestMessages =
                    BuildRequestMessages(
                        baseMessages,
                        toolCycles
                    );


                string requestJson =
                    BuildRequestJson(
                        requestMessages,
                        toolDefinitions
                    );


                client
                    .DefaultRequestHeaders
                    .Authorization =
                        new AuthenticationHeaderValue(
                            "Bearer",
                            apiKey
                        );


                using HttpResponseMessage response =
                    await SendWithRateLimitRetry(
                        "https://api.groq.com/openai/v1/chat/completions",
                        requestJson
                    );


                string responseText =
                    await response.Content
                        .ReadAsStringAsync();


                // ====================================================
                // API ERROR
                // ====================================================

                if (
                    !response.IsSuccessStatusCode
                )
                {
                    bool recoverableToolError =
                        IsRecoverableToolUseError(
                            responseText
                        );


                    if (
                        recoverableToolError
                        &&
                        toolUseRecoveryAttempts <
                        MaxToolUseRecoveryAttempts
                    )
                    {
                        toolUseRecoveryAttempts++;


                        baseMessages.Add(
                            new
                            {
                                role =
                                    "system",

                                content =
                                    "Previous tool call was invalid. " +
                                    "Retry once using ONLY registered tool names and exact JSON parameters: "
                                    +
                                    GetRegisteredToolNames(
                                        toolDefinitions
                                    )
                            }
                        );


                        continue;
                    }


                    return
                        $"Groq API greška:\n{responseText}";
                }


                using JsonDocument document =
                    JsonDocument.Parse(
                        responseText
                    );


                JsonElement message =
                    document
                        .RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message");


                // ====================================================
                // TOOL CALLS
                // ====================================================

                if (
                    message.TryGetProperty(
                        "tool_calls",
                        out JsonElement toolCalls
                    )
                    &&
                    toolCalls.ValueKind ==
                    JsonValueKind.Array
                    &&
                    toolCalls.GetArrayLength() >
                    0
                )
                {
                    List<object> currentCycle =
                        new List<object>();


                    object assistantMessage =
                        JsonSerializer
                            .Deserialize<object>(
                                message.GetRawText()
                            )!;


                    currentCycle.Add(
                        assistantMessage
                    );


                    foreach (
                        JsonElement toolCall
                        in toolCalls.EnumerateArray()
                    )
                    {
                        string toolCallId =
                            toolCall
                                .GetProperty("id")
                                .GetString()
                            ??
                            "";


                        JsonElement function =
                            toolCall
                                .GetProperty("function");


                        string functionName =
                            function
                                .GetProperty("name")
                                .GetString()
                            ??
                            "";


                        string argumentsJson =
                            function
                                .GetProperty("arguments")
                                .GetString()
                            ??
                            "{}";


                        if (
                            IsNoArgTool(
                                functionName
                            )
                        )
                        {
                            argumentsJson =
                                "{}";
                        }


                        ReportActivity(
                            $"[TOOL {iteration}] {functionName} | {FormatArgumentsForConsole(argumentsJson)}"
                        );


                        string signature =
                            functionName
                            +
                            "|"
                            +
                            NormalizeJson(
                                argumentsJson
                            );


                        string toolResult;


                        if (
                            executedToolResults
                                .TryGetValue(
                                    signature,
                                    out string? cachedResult
                                )
                        )
                        {
                            toolResult =
                                cachedResult;


                            ReportActivity(
                                $"[CACHE {iteration}] reused"
                            );
                        }
                        else
                        {
                            toolResult =
                                ExecuteTool(
                                    functionName,
                                    argumentsJson
                                );


                            executedToolResults[
                                signature
                            ] =
                                toolResult;
                        }


                        toolResult =
                            TrimToolResult(
                                toolResult,
                                functionName
                            );


                        ReportActivity(
                            $"[RESULT {iteration}] {TrimConsoleResult(toolResult)}"
                        );
                        if (
                                    functionName ==
                                    "execute_temp_capability"
                                    &&
                                    IsTerminalTempCapabilityFailure(
                                        toolResult
                                    )
                                )
                        {
                            string answer =
                                "Unity-side capability stopped after a runtime or transport failure. " +
                                "It was not retried because Unity may already have performed part of the operation.\n" +
                                toolResult;

                            conversationHistory.Add(
                                new ChatMessage(
                                    "assistant",
                                    answer
                                )
                            );

                            return answer;
                        }

                        currentCycle.Add(
                            new
                            {
                                role =
                                    "tool",

                                tool_call_id =
                                    toolCallId,

                                name =
                                    functionName,

                                content =
                                    toolResult
                            }
                        );
                    }


                    toolCycles.Add(
                        currentCycle
                    );


                    while (
                        toolCycles.Count >
                        MaxToolCyclesInContext
                    )
                    {
                        toolCycles.RemoveAt(
                            0
                        );
                    }


                    continue;
                }


                // ====================================================
                // FINAL RESPONSE
                // ====================================================

                if (
                    message.TryGetProperty(
                        "content",
                        out JsonElement contentElement
                    )
                    &&
                    contentElement.ValueKind !=
                    JsonValueKind.Null
                )
                {
                    string answer =
                        contentElement.GetString()
                        ??
                        "";


                    conversationHistory.Add(
                        new ChatMessage(
                            "assistant",
                            answer
                        )
                    );


                    return
                        answer;
                }


                return
                    "Model nije vratio odgovor.";
            }


            return
                $"Agent je dostigao maksimalno {MaxIterations} koraka.";
        }


        // ============================================================
        // REQUEST JSON
        // ============================================================

        private static string BuildRequestJson(
            List<object> messages,
            object[] tools
        )
        {
            Dictionary<string, object> body =
                new Dictionary<string, object>
                {
                    ["model"] =
                        Model,

                    ["messages"] =
                        messages,

                    ["reasoning_effort"] =
                    tools.Any(definition =>
                        GetToolName(definition)
                        ==
                        "execute_temp_capability"
                    )
                        ?
                        "medium"
                        :
                        "low"
                };


            if (
                tools.Length >
                0
            )
            {
                body["tool_choice"] =
                    "auto";


                body["tools"] =
                    tools;
            }


            return
                JsonSerializer.Serialize(
                    body
                );
        }


        // ============================================================
        // SYSTEM PROMPT + CHAT HISTORY
        // ============================================================

        private List<object> BuildBaseMessages()
        {
            List<object> messages =
                new List<object>
                {
                    new
                    {
                        role =
                            "system",

                                              content =
""""
You are a highly efficient local C#/.NET game-development agent.

PRIMARY OBJECTIVE:

Complete the user's requested task correctly while using the minimum practical number of AI calls, tool calls, tokens and local operations.


GENERAL COST RULES:

- Prefer one broad local operation over many tiny operations.
- Never call a tool merely to confirm information already known in the current task.
- Never repeat an equivalent tool call.
- Batch related work.
- Inspect expensive state only when necessary before acting, after uncertainty/failure, or for final verification.
- Stop immediately when the requested task is complete.
- Do not narrate planned tool calls when you can execute them.
- Tool arguments must be strict valid JSON.
- Use only tools actually registered in the current request.
- Do not invent tool names or parameters.


TEMPORARY CAPABILITIES:

FIRST CLASSIFY THE REQUESTED CODE:

- Temporary capability = one-shot Editor automation that creates, finds or configures Unity objects/assets/components and then may disappear.
- Persistent runtime script = gameplay code that must remain attached, run in Play Mode or survive editor/domain reload.

Controllers, movement, mouse look, cursor locking, camera follow, enemy AI, weapons, inventory, interactions and runtime managers MUST be persistent Assets/Scripts/*.cs MonoBehaviour scripts.

Never define MonoBehaviour or ScriptableObject inside execute_temp_capability.
Never claim that a persistent script was created, updated, compiled or attached unless the corresponding create_unity_script, wait_for_unity_script_compile and attach_script tool results succeeded.

Persistent script workflow:
1. Inspect hierarchy when the target object/path is not already certain.
2. Inspect get_unity_project_settings before generating input, physics or camera code.
3. For an update, locate the exact asset with find_unity_scripts, read the existing source with read_unity_script and review it with review_unity_script before rewriting it.
4. Call create_unity_script with the COMPLETE source and safe Assets/Scripts/... path. Use overwrite=true only for a requested update or compiler repair.
5. Call wait_for_unity_script_compile with the returned jobId.
6. If compilation failed, rewrite the COMPLETE persistent source once and create it again with overwrite=true.
7. Only after compilation succeeds, call attach_script when the component is not already attached.
8. Review the updated script and check Console when verification was requested.
9. Save the scene after a successful attachment or scene change.

Code review rules:
- Compilation proves syntax and type correctness, not gameplay correctness.
- For Rigidbody characters, read input in Update, cache it, and apply physics in FixedUpdate.
- Do not rotate or move a Rigidbody-controlled player through Transform; prefer MoveRotation, MovePosition or velocity in FixedUpdate.
- Match generated input code to the project's actual inputHandling value.
- Do not assume a Head child or camera exists without inspecting hierarchy or providing a safe fallback.
- Ground checks must account for Collider bounds or use an explicit ground-check Transform.
- Preserve the existing class name and asset path during updates so Unity keeps the component reference.

Play Mode observation:
- Enter Play Mode only when the user explicitly asks to run, test or observe runtime behavior.
- After entering, use get_unity_runtime_state for the exact object path.
- Runtime state can prove components, transforms and Rigidbody state, but it cannot prove human input feel by itself.
- Exit Play Mode after observation unless the user explicitly asks to leave it running.

Creating or updating a persistent script is intentionally a multi-stage task. Do not replace it with a temporary DLL merely to reduce tool calls.

execute_temp_capability is an escape hatch for complex, missing or inefficient behavior.

Do NOT use execute_temp_capability when one simple existing tool can efficiently complete the task.

Use execute_temp_capability when:
- existing tools cannot perform the requested behavior,
- one generated capability can replace many individual tool calls,
- or direct Unity component APIs are more reliable than many generic bridge operations.

Prefer ONE broad task-specific temporary capability.
Do not create many small temporary capabilities for one task.

The capability source is sent to the Unity Editor. Unity compiles it as a temporary DLL and executes it on the Unity main thread:

generate
-> validate
-> compile inside Unity
-> execute
-> delete temporary source and DLL files

Generated temporary capability source must:
- contain exactly one concrete IUnityDynamicCapability implementation,
- have a Name property exactly matching the requested capability name,
- implement string Execute(UnityDynamicCapabilityContext context, string argumentsJson),
- remain compact and task-specific,
- use UnityEngine directly for scene objects and component properties,
- use the supplied context for hierarchy lookup, Undo-aware creation, dirty marking and scene saving.

using UnityEngine is allowed and expected.
using UnityEditor is forbidden. The context performs allowed editor-only operations.

Never use direct OS filesystem, network, process, reflection, threading, unsafe, build, package-manager or destructive deletion APIs.
Temporary capability source must never declare MonoBehaviour or ScriptableObject types.

Use this exact capability structure:

using UnityEngine;

public sealed class ExampleCapability : IUnityDynamicCapability
{
    public string Name => "ExampleCapability";

    public string Execute(
        UnityDynamicCapabilityContext context,
        string argumentsJson
    )
    {
        GameObject target =
            context.FindRequired("ExampleObject");

        Rigidbody body =
            context.GetOrAddComponent<Rigidbody>(target);

        context.Record(body, "AI set Rigidbody mass");
        body.mass = 3f;
        context.MarkDirty(body);
        context.SaveActiveScene();

        return "Updated ExampleObject Rigidbody mass to 3.";
    }
}

Before calling execute_temp_capability, silently check:
- every statement that requires a semicolon has one,
- parentheses and braces are balanced,
- Name exactly matches the tool argument name,
- Execute returns string,
- the source implements IUnityDynamicCapability,
- UnityEngine is used only for the requested scene/component work,
- UnityEditor is not referenced.

Do NOT use:
- System.IO
- System.Net
- HttpClient
- Process
- Environment
- reflection or dynamic assembly loading
- threads or Task.Run
- operating-system filesystem APIs
- unsafe code
- UnityEditor
- Destroy or DestroyImmediate
- build, package-manager or project-settings APIs

If compilation fails:
- read ALL returned compiler diagnostics,
- fix ALL of them in ONE complete rewritten capability,
- then retry.
- Do not repair only one compiler error per retry.

If a compiled capability returns a runtime, offline or timeout failure:
- do not generate another capability,
- do not repeat execution,
- report the exact failure because Unity may already have executed some direct API calls.

UNITY DYNAMIC CAPABILITY CONTEXT:

Available context helpers include:
- FindRequired(hierarchyPath)
- GetRequiredComponent<T>(gameObject)
- GetOrAddComponent<T>(gameObject)
- CreateGameObject(name, optionalParent)
- CreatePrimitive(primitiveType, name, optionalParent)
- Record(unityObject, actionName)
- MarkDirty(unityObject)
- SaveActiveScene()

For existing objects, use exact hierarchy paths with context.FindRequired.
Before changing an existing Unity object or component, call context.Record.
After changing a component or asset, call context.MarkDirty.
Use context creation helpers so Undo is registered.
Call context.SaveActiveScene only after all required steps succeed.


UNITY WORKFLOW:

For a simple Unity action, use the simple registered action tool.

For a complex Unity setup:
- avoid many direct action tool calls,
- prefer one execute_temp_capability call,
- generate one IUnityDynamicCapability,
- execute all related direct Unity API operations in that single capability.

Read get_scene_hierarchy only when object identity or current hierarchy is actually needed.

Do not automatically inspect hierarchy before creating entirely new known root objects unless existing state matters.

Do not re-read hierarchy after every successful operation.

Use exact hierarchy paths when addressing existing GameObjects.

After complex scene modification, perform one final verification when useful.

If the user explicitly asks to verify compiler errors, call get_console_errors after Unity has processed the changes.

Never claim success if a tool result reports failure.


UNITY SCRIPT CREATION:

Creating a .cs script causes Unity compilation.

Dynamic capabilities must not write project files.
Use the registered attach_script or script-creation bridge tool for persistent project scripts.
After Unity imports a persistent script, use get_console_errors once if verification was requested.

Do not repeatedly poll console errors unless necessary.


FILESYSTEM:

Normal filesystem paths are absolute and must be inside allowed roots.

Avoid reading entire large files when a targeted section is sufficient.


SELF DEVELOPMENT:

Modify AI Assistant source only when the user explicitly asks to modify the agent itself.

Self-development requires backup before source writes.

Prefer targeted source reads and targeted edits.

Build after source modifications.

Never restart after a failed build.


RESPONSE:

When a requested task succeeds, respond briefly with what was completed.

When something fails, report the useful error instead of pretending success.

Never say that you added, changed, updated, compiled, attached or verified something when the current request had no successful tool result proving that action.
When no tools are available, answer only with known information or explicitly say that the state was not verified.
""""

                    }
                };
            IEnumerable<ChatMessage> recentHistory =
                conversationHistory
                    .TakeLast(
                        MaxChatHistoryMessages
                    );


            messages.AddRange(
                recentHistory.Select(
                    item =>
                        (object)new
                        {
                            role =
                                item.Role,

                            content =
                                item.Message
                        }
                )
            );


            return
                messages;
        }


        // ============================================================
        // TOOL-CYCLE CONTEXT
        // ============================================================

        private static List<object> BuildRequestMessages(
            List<object> baseMessages,
            List<List<object>> toolCycles
        )
        {
            List<object> result =
                new List<object>(
                    baseMessages
                );


            foreach (
                List<object> cycle
                in toolCycles
            )
            {
                result.AddRange(
                    cycle
                );
            }


            return
                result;
        }


        // ============================================================
        // TASK-AWARE TOOL SELECTION
        // ============================================================

        private object[] BuildToolDefinitionsForTask(
            string prompt
        )
        {
            string text =
                prompt.ToLowerInvariant();


            List<object> tools =
                new List<object>();


            // ========================================================
            // VERSION
            // ========================================================

            if (
                ContainsAny(
                    text,
                    "version",
                    "verzija",
                    "verziju"
                )
            )
            {
                tools.Add(
                    ToolNoArgs(
                        "get_agent_version",
                        "Returns current AI Assistant version."
                    )
                );


                return
                    tools.ToArray();
            }


            // ========================================================
            // SELF DEVELOPMENT
            // ========================================================

            bool selfDevelopment =
                ContainsAny(
                    text,
                    "modify yourself",
                    "change yourself",
                    "improve yourself",
                    "your source",
                    "agent source",
                    "aiintegration",
                    "self development",
                    "self-development",
                    "izmijeni sebe",
                    "promijeni sebe",
                    "doradi sebe",
                    "svoj kod",
                    "tvoj kod",
                    "agent kod",
                    "agentu dodaj",
                    "tool agent",
                    "capability agent"
                );


            if (
                selfDevelopment
            )
            {
                AddSelfDevelopmentTools(
                    tools
                );


                return
                    tools.ToArray();
            }


            // ========================================================
            // FILESYSTEM
            // ========================================================

            bool filesystemTask =
                ContainsAny(
                    text,
                    "file",
                    "folder",
                    "directory",
                    "fajl",
                    "fajlu",
                    "folderu",
                    "datotek",
                    "read code",
                    "create code",
                    "write code"
                );


            // ========================================================
            // UNITY
            // ========================================================

            bool unityTask =
                ContainsAny(
                    text,
                    "unity",
                    "gameobject",
                    "scene",
                    "player",
                    "camera",
                    "rigidbody",
                    "collider",
                    "prefab",
                    "material",
                    "primitive",
                    "component",
                    "fps",
                    "controller",
                    "input system",
                    "input manager",
                    "player settings",
                    "terrain",
                    "object",
                    "objekat",
                    "enemy",
                    "weapon",
                    "inventory",
                    "script",
                    "skript",
                    "play mode",
                    "runtime"
                );


            if (
                unityTask
            )
            {
                AddUnityToolsForTask(
                    tools,
                    text
                );
            }


            if (
                filesystemTask
                &&
                !unityTask
            )
            {
                AddFilesystemTools(
                    tools,
                    text
                );
            }


            return
                tools
                    .GroupBy(
                        GetToolName,
                        StringComparer.Ordinal
                    )
                    .Select(group =>
                        group.First()
                    )
                    .ToArray();
        }


        // ============================================================
        // UNITY TOOL SELECTION
        // ============================================================

        private void AddUnityToolsForTask(
            List<object> tools,
            string text
        )
        {
            bool complexTask =
                ContainsAny(
                    text,
                    "setup",
                    "system",
                    "controller",
                    "complete",
                    "komplet",
                    "napravi mi",
                    "build",
                    "create a",
                    "fps",
                    "inventory",
                    "movement",
                    "character",
                    "enemy ai",
                    "vehicle",
                    "weapon",
                    "interaction",
                    "day night",
                    "day/night",
                    "whole",
                    "entire",
                    "cijeli",
                    "čitav",
                    "citav"
                );


            bool persistentRuntimeScript =
                ContainsAny(
                    text,
                    "script",
                    "skript",
                    "player",
                    "fps",
                    "controller",
                    "movement",
                    "kretanj",
                    "input system",
                    "input manager",
                    "mouse look",
                    "mouselook",
                    "cursor lock",
                    "camera",
                    "camera follow",
                    "enemy ai",
                    "weapon system",
                    "inventory",
                    "interaction",
                    "runtime manager"
                );


            bool needsHierarchy =
                !complexTask
                &&
                ContainsAny(
                    text,
                    "move",
                    "pomjeri",
                    "rotate",
                    "rotir",
                    "scale",
                    "skal",
                    "rename",
                    "preimenuj",
                    "parent",
                    "child",
                    "component",
                    "material",
                    "duplicate",
                    "dupl",
                    "rigidbody",
                    "collider",
                    "prefab"
                );


            if (
                needsHierarchy
            )
            {
                tools.Add(
                    ToolNoArgs(
                        "get_scene_hierarchy",
                        "Returns Unity hierarchy with exact GameObject paths."
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "active scene",
                    "current scene",
                    "aktivna scena"
                )
            )
            {
                tools.Add(
                    ToolNoArgs(
                        "get_active_scene",
                        "Returns active Unity scene information."
                    )
                );
            }


            bool verificationRequested =
                ContainsAny(
                    text,
                    "error",
                    "errors",
                    "console",
                    "grešk",
                    "gresk",
                    "provjeri",
                    "verify",
                    "compile"
                );


            bool runtimeObservationRequested =
                ContainsAny(
                    text,
                    "play mode",
                    "run it",
                    "pokreni",
                    "testiraj",
                    "runtime",
                    "observe",
                    "posmatraj"
                );


            if (
                verificationRequested
            )
            {
                tools.Add(
                    ToolNoArgs(
                        "get_console_errors",
                        "Returns Unity console errors/exceptions. Prefer one final check after changes."
                    )
                );
            }


            // ========================================================
            // UNITY-SIDE DYNAMIC CAPABILITY ESCAPE HATCH
            //
            // execute_temp_capability must NEVER be gated behind the
            // complexTask keyword heuristic: the system prompt always
            // describes it as available, so hiding it here caused the
            // model to call a tool that wasn't in `tools` at all.
            //
            // Old host-side promoted capability DLLs are intentionally
            // not exposed: they run outside Unity and cannot safely use
            // direct UnityEngine scene APIs.
            // ========================================================

            tools.Add(
                TempCapabilityTool()
            );


            if (persistentRuntimeScript)
            {
                tools.Add(
                    ToolNoArgs(
                        "get_unity_project_settings",
                        "Returns Unity version, active input backend, Play Mode state, compilation state and active scene. Call before generating input, physics or camera code."
                    )
                );

                tools.Add(
                    OneStringTool(
                        "find_unity_scripts",
                        "Lists persistent C# scripts under Assets/Scripts whose paths contain searchText. Use when the exact existing asset path is uncertain.",
                        "searchText"
                    )
                );

                tools.Add(
                    ReadUnityScriptTool()
                );

                tools.Add(
                    OneStringTool(
                        "review_unity_script",
                        "Reviews an existing persistent Unity script for input-backend mismatch, Rigidbody/Transform conflicts, Update/FixedUpdate mistakes, fragile camera lookup and ground-check risks.",
                        "assetPath"
                    )
                );

                tools.Add(
                    ToolNoArgs(
                        "get_scene_hierarchy",
                        "Returns Unity hierarchy and attached component names for exact verification."
                    )
                );

                tools.Add(
                    CreateUnityScriptTool()
                );

                tools.Add(
                    OneStringTool(
                        "wait_for_unity_script_compile",
                        "Waits locally for a persistent Unity script compilation job to finish and returns compiler diagnostics.",
                        "jobId"
                    )
                );

                tools.Add(
                    TwoStringTool(
                        "attach_script",
                        "Attaches an already-compiled persistent MonoBehaviour type to an existing GameObject.",
                        "objectPath",
                        "scriptType"
                    )
                );

                tools.Add(
                    ToolNoArgs(
                        "save_scene",
                        "Saves the active Unity scene after the persistent script is attached."
                    )
                );

                tools.Add(
                    ToolNoArgs(
                        "get_console_errors",
                        "Returns Unity compiler/runtime errors for final verification."
                    )
                );

            }


            if (runtimeObservationRequested)
            {
                tools.Add(
                    ToolNoArgs(
                        "get_unity_project_settings",
                        "Returns current Unity compilation and Play Mode state."
                    )
                );

                tools.Add(
                    OneStringTool(
                        "get_unity_runtime_state",
                        "Returns current Play Mode, transform, components, Collider count, camera path and Rigidbody state for an exact GameObject hierarchy path.",
                        "objectPath"
                    )
                );

                tools.Add(
                    UnityPlayModeTool()
                );
            }

            // ========================================================
            // COMPLEX UNITY TASK
            //
            // Important:
            // Do NOT send 15 simple mutation schemas.
            // ========================================================

            if (
                complexTask
            )
            {
                return;
            }


            // ========================================================
            // SIMPLE CREATE
            // ========================================================

            if (
                ContainsAny(
                    text,
                    "create gameobject",
                    "new gameobject",
                    "napravi gameobject",
                    "kreiraj gameobject"
                )
            )
            {
                tools.Add(
                    CreateGameObjectTool()
                );
            }


            if (
                ContainsAny(
                    text,
                    "primitive",
                    "cube",
                    "sphere",
                    "capsule",
                    "cylinder",
                    "plane",
                    "quad",
                    "kock",
                    "sfer"
                )
            )
            {
                tools.Add(
                    CreatePrimitiveTool()
                );
            }


            // ========================================================
            // TRANSFORMS
            // ========================================================

            if (
                ContainsAny(
                    text,
                    "position",
                    "move",
                    "pomjeri",
                    "pozic"
                )
            )
            {
                tools.Add(
                    VectorTool(
                        "set_position",
                        "Sets Unity GameObject world position."
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "rotation",
                    "rotate",
                    "rotir"
                )
            )
            {
                tools.Add(
                    VectorTool(
                        "set_rotation",
                        "Sets Unity GameObject Euler rotation."
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "scale",
                    "skal",
                    "size",
                    "velič",
                    "velic"
                )
            )
            {
                tools.Add(
                    VectorTool(
                        "set_scale",
                        "Sets Unity GameObject local scale."
                    )
                );
            }


            // ========================================================
            // OBJECT MANAGEMENT
            // ========================================================

            if (
                ContainsAny(
                    text,
                    "rename",
                    "preimenuj"
                )
            )
            {
                tools.Add(
                    TwoStringTool(
                        "rename_gameobject",
                        "Renames Unity GameObject.",
                        "objectPath",
                        "newName"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "parent",
                    "child",
                    "roditelj"
                )
            )
            {
                tools.Add(
                    TwoStringTool(
                        "set_parent",
                        "Changes Unity GameObject parent.",
                        "objectPath",
                        "parentPath"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "active",
                    "disable",
                    "enable",
                    "ugasi",
                    "upali"
                )
            )
            {
                tools.Add(
                    SetActiveTool()
                );
            }


            if (
                ContainsAny(
                    text,
                    "duplicate",
                    "copy object",
                    "dupl"
                )
            )
            {
                tools.Add(
                    DuplicateTool()
                );
            }


            // ========================================================
            // COMPONENTS
            // ========================================================

            if (
                ContainsAny(
                    text,
                    "add component",
                    "component",
                    "komponent"
                )
            )
            {
                tools.Add(
                    TwoStringTool(
                        "add_component",
                        "Adds a Unity component.",
                        "objectPath",
                        "componentType"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "attach script",
                    "script",
                    "skript"
                )
            )
            {
                tools.Add(
                    TwoStringTool(
                        "attach_script",
                        "Attaches an existing Unity MonoBehaviour type.",
                        "objectPath",
                        "scriptType"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "rigidbody",
                    "physics",
                    "fizik"
                )
            )
            {
                tools.Add(
                    ConfigureRigidbodyTool()
                );
            }


            if (
                ContainsAny(
                    text,
                    "collider",
                    "trigger"
                )
            )
            {
                tools.Add(
                    ConfigureColliderTool()
                );
            }


            // ========================================================
            // ASSETS
            // ========================================================

            if (
                ContainsAny(
                    text,
                    "find asset",
                    "search asset",
                    "pronadji asset",
                    "pronađi asset"
                )
            )
            {
                tools.Add(
                    TwoStringTool(
                        "find_assets",
                        "Finds Unity assets.",
                        "filter",
                        "searchFolder"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "asset info"
                )
            )
            {
                tools.Add(
                    OneStringTool(
                        "get_asset_info",
                        "Returns Unity asset information.",
                        "assetPath"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "create material",
                    "napravi material",
                    "napravi materijal"
                )
            )
            {
                tools.Add(
                    TwoStringTool(
                        "create_material",
                        "Creates Unity material.",
                        "assetPath",
                        "shaderName"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "material color",
                    "boju materijala",
                    "color material"
                )
            )
            {
                tools.Add(
                    MaterialColorTool()
                );
            }


            if (
                ContainsAny(
                    text,
                    "assign material",
                    "dodijeli material",
                    "dodijeli materijal"
                )
            )
            {
                tools.Add(
                    TwoStringTool(
                        "assign_material",
                        "Assigns material to renderer.",
                        "objectPath",
                        "materialPath"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "import asset",
                    "importuj"
                )
            )
            {
                tools.Add(
                    OneStringTool(
                        "import_asset",
                        "Imports or refreshes Unity asset.",
                        "assetPath"
                    )
                );
            }


            // ========================================================
            // PREFABS
            // ========================================================

            if (
                ContainsAny(
                    text,
                    "create prefab",
                    "napravi prefab"
                )
            )
            {
                tools.Add(
                    TwoStringTool(
                        "create_prefab",
                        "Creates prefab from GameObject.",
                        "objectPath",
                        "assetPath"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "instantiate prefab",
                    "spawn prefab"
                )
            )
            {
                tools.Add(
                    InstantiatePrefabTool()
                );
            }


            if (
                ContainsAny(
                    text,
                    "save",
                    "sačuv",
                    "sacuv"
                )
            )
            {
                tools.Add(
                    ToolNoArgs(
                        "save_scene",
                        "Saves current Unity scene."
                    )
                );
            }
        }


        // ============================================================
        // FILESYSTEM TOOL SELECTION
        // ============================================================

        private static void AddFilesystemTools(
            List<object> tools,
            string text
        )
        {
            if (
                ContainsAny(
                    text,
                    "root",
                    "where",
                    "gdje"
                )
            )
            {
                tools.Add(
                    ToolNoArgs(
                        "list_allowed_roots",
                        "Lists allowed filesystem roots."
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "search",
                    "find",
                    "pronad",
                    "pronađ"
                )
            )
            {
                tools.Add(
                    OneStringTool(
                        "search_and_read_file",
                        "Searches allowed roots for filename and reads it when small.",
                        "fileName"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "read",
                    "pročitaj",
                    "procitaj"
                )
            )
            {
                tools.Add(
                    OneStringTool(
                        "read_file",
                        "Reads a file from an absolute allowed path.",
                        "filePath"
                    )
                );


                tools.Add(
                    ReadSectionTool()
                );
            }


            if (
                ContainsAny(
                    text,
                    "create folder",
                    "napravi folder"
                )
            )
            {
                tools.Add(
                    OneStringTool(
                        "create_folder",
                        "Creates directory inside an allowed root.",
                        "folderPath"
                    )
                );
            }


            if (
                ContainsAny(
                    text,
                    "create file",
                    "write file",
                    "napravi fajl"
                )
            )
            {
                tools.Add(
                    TwoStringTool(
                        "create_file",
                        "Creates or overwrites normal file.",
                        "filePath",
                        "content"
                    )
                );
            }
        }


        // ============================================================
        // SELF DEVELOPMENT TOOL SELECTION
        // ============================================================

        private static void AddSelfDevelopmentTools(
            List<object> tools
        )
        {
            tools.Add(
                ToolNoArgs(
                    "backup_project",
                    "Creates required backup before self modification."
                )
            );


            tools.Add(
                ToolNoArgs(
                    "inspect_self_structure",
                    "Lists AI Assistant source structure."
                )
            );


            tools.Add(
                TwoStringTool(
                    "find_self_text",
                    "Finds text in AI Assistant source file.",
                    "relativePath",
                    "searchText"
                )
            );


            tools.Add(
                SelfReadSectionTool()
            );


            tools.Add(
                ThreeStringTool(
                    "replace_self_text",
                    "Replaces one exact unique text block.",
                    "relativePath",
                    "oldText",
                    "newText"
                )
            );


            tools.Add(
                TwoStringTool(
                    "write_self_file",
                    "Writes a complete AI Assistant source file.",
                    "relativePath",
                    "content"
                )
            );


            tools.Add(
                ToolNoArgs(
                    "build_self",
                    "Builds AI Assistant."
                )
            );


            tools.Add(
                ToolNoArgs(
                    "restart_self",
                    "Restarts AI Assistant after successful build."
                )
            );
        }


        // ============================================================
        // TOOL ROUTER
        // ============================================================

        private string ExecuteTool(
            string functionName,
            string argumentsJson
        )
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(
                        argumentsJson
                    );


                JsonElement args =
                    document.RootElement;


                // ====================================================
                // VERSION
                // ====================================================

                if (
                    functionName ==
                    "get_agent_version"
                )
                {
                    return
                        AgentVersion.Version;
                }


                // ====================================================
                // PROMOTED CAPABILITY LIBRARY
                //
                // Tools named "run_<Name>" come from CapabilityLibrary:
                // capabilities originally written via
                // execute_temp_capability and promoted after a
                // successful run. Executed directly from their
                // persisted DLL — no recompiling from source.
                // ====================================================

                if (
                    functionName.StartsWith(
                        "run_",
                        StringComparison.Ordinal
                    )
                )
                {
                    string libraryArguments =
                        "{}";


                    if (
                        args.TryGetProperty(
                            "arguments",
                            out JsonElement libraryArgumentElement
                        )
                    )
                    {
                        libraryArguments =
                            libraryArgumentElement.GetRawText();
                    }


                    if (
                        tempCapabilities.TryExecuteLibraryCapability(
                            functionName,
                            libraryArguments,
                            out string libraryResult
                        )
                    )
                    {
                        return
                            libraryResult;
                    }
                }


                // ====================================================
                // TEMP CAPABILITY
                // ====================================================

                if (
                    functionName ==
                    "execute_temp_capability"
                )
                {
                    string capabilityArguments =
                        "{}";


                    if (
                        args.TryGetProperty(
                            "arguments",
                            out JsonElement argumentElement
                        )
                    )
                    {
                        capabilityArguments =
                            argumentElement.GetRawText();
                    }


                    return
                        tempCapabilities
                            .ExecuteTemporaryCapability(
                                GetStringArg(
                                    args,
                                    "name"
                                ),
                                GetStringArg(
                                    args,
                                    "source"
                                ),
                                capabilityArguments
                            );
                }


                // ====================================================
                // UNITY READ
                // ====================================================

                if (
                    functionName ==
                    "get_active_scene"
                )
                {
                    return
                        unityTools.GetActiveScene();
                }


                if (
                    functionName ==
                    "get_scene_hierarchy"
                )
                {
                    return
                        unityTools.GetSceneHierarchy();
                }


                if (
                    functionName ==
                    "get_console_errors"
                )
                {
                    return
                        unityTools.GetConsoleErrors();
                }


                if (
                    functionName ==
                    "get_unity_project_settings"
                )
                {
                    return
                        unityTools.GetUnityProjectSettings();
                }


                if (
                    functionName ==
                    "find_unity_scripts"
                )
                {
                    return
                        unityTools.FindUnityScripts(
                            GetStringArg(
                                args,
                                "searchText"
                            )
                        );
                }


                if (
                    functionName ==
                    "read_unity_script"
                )
                {
                    return
                        unityTools.ReadUnityScript(
                            GetStringArg(
                                args,
                                "assetPath"
                            ),
                            GetIntArg(
                                args,
                                "startLine"
                            ),
                            GetIntArg(
                                args,
                                "endLine"
                            )
                        );
                }


                if (
                    functionName ==
                    "review_unity_script"
                )
                {
                    return
                        unityTools.ReviewUnityScript(
                            GetStringArg(
                                args,
                                "assetPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "get_unity_runtime_state"
                )
                {
                    return
                        unityTools.GetUnityRuntimeState(
                            GetStringArg(
                                args,
                                "objectPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "set_unity_play_mode"
                )
                {
                    return
                        unityTools.SetUnityPlayMode(
                            GetStringArg(
                                args,
                                "action"
                            )
                        );
                }


                // ====================================================
                // UNITY OBJECTS
                // ====================================================

                if (
                    functionName ==
                    "create_gameobject"
                )
                {
                    return
                        unityTools.CreateGameObject(
                            GetStringArg(
                                args,
                                "name"
                            ),
                            GetStringArg(
                                args,
                                "parentPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "create_primitive"
                )
                {
                    return
                        unityTools.CreatePrimitive(
                            GetStringArg(
                                args,
                                "primitiveType"
                            ),
                            GetStringArg(
                                args,
                                "name"
                            ),
                            GetStringArg(
                                args,
                                "parentPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "set_position"
                )
                {
                    return
                        unityTools.SetPosition(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetFloatArg(
                                args,
                                "x"
                            ),
                            GetFloatArg(
                                args,
                                "y"
                            ),
                            GetFloatArg(
                                args,
                                "z"
                            )
                        );
                }


                if (
                    functionName ==
                    "set_rotation"
                )
                {
                    return
                        unityTools.SetRotation(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetFloatArg(
                                args,
                                "x"
                            ),
                            GetFloatArg(
                                args,
                                "y"
                            ),
                            GetFloatArg(
                                args,
                                "z"
                            )
                        );
                }


                if (
                    functionName ==
                    "set_scale"
                )
                {
                    return
                        unityTools.SetScale(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetFloatArg(
                                args,
                                "x"
                            ),
                            GetFloatArg(
                                args,
                                "y"
                            ),
                            GetFloatArg(
                                args,
                                "z"
                            )
                        );
                }


                if (
                    functionName ==
                    "rename_gameobject"
                )
                {
                    return
                        unityTools.RenameGameObject(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetStringArg(
                                args,
                                "newName"
                            )
                        );
                }


                if (
                    functionName ==
                    "set_parent"
                )
                {
                    return
                        unityTools.SetParent(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetStringArg(
                                args,
                                "parentPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "set_active"
                )
                {
                    return
                        unityTools.SetActive(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetBoolArg(
                                args,
                                "active"
                            )
                        );
                }


                if (
                    functionName ==
                    "duplicate_gameobject"
                )
                {
                    return
                        unityTools.DuplicateGameObject(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetStringArg(
                                args,
                                "newName"
                            ),
                            GetStringArg(
                                args,
                                "parentPath"
                            )
                        );
                }


                // ====================================================
                // COMPONENTS
                // ====================================================

                if (
                    functionName ==
                    "add_component"
                )
                {
                    return
                        unityTools.AddComponent(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetStringArg(
                                args,
                                "componentType"
                            )
                        );
                }


                if (
                    functionName ==
                    "create_unity_script"
                )
                {
                    return
                        unityTools.CreatePersistentScript(
                            GetStringArg(
                                args,
                                "assetPath"
                            ),
                            GetStringArg(
                                args,
                                "className"
                            ),
                            GetStringArg(
                                args,
                                "source"
                            ),
                            GetBoolArg(
                                args,
                                "overwrite"
                            )
                        );
                }


                if (
                    functionName ==
                    "wait_for_unity_script_compile"
                )
                {
                    return
                        unityTools.WaitForPersistentScript(
                            GetStringArg(
                                args,
                                "jobId"
                            )
                        );
                }


                if (
                    functionName ==
                    "attach_script"
                )
                {
                    return
                        unityTools.AttachScript(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetStringArg(
                                args,
                                "scriptType"
                            )
                        );
                }


                if (
                    functionName ==
                    "configure_rigidbody"
                )
                {
                    return
                        unityTools.ConfigureRigidbody(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetFloatArg(
                                args,
                                "mass"
                            ),
                            GetBoolArg(
                                args,
                                "useGravity"
                            ),
                            GetBoolArg(
                                args,
                                "isKinematic"
                            )
                        );
                }


                if (
                    functionName ==
                    "configure_collider"
                )
                {
                    return
                        unityTools.ConfigureCollider(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetBoolArg(
                                args,
                                "enabled"
                            ),
                            GetBoolArg(
                                args,
                                "isTrigger"
                            )
                        );
                }


                // ====================================================
                // ASSETS
                // ====================================================

                if (
                    functionName ==
                    "find_assets"
                )
                {
                    return
                        unityTools.FindAssets(
                            GetStringArg(
                                args,
                                "filter"
                            ),
                            GetStringArg(
                                args,
                                "searchFolder"
                            )
                        );
                }


                if (
                    functionName ==
                    "get_asset_info"
                )
                {
                    return
                        unityTools.GetAssetInfo(
                            GetStringArg(
                                args,
                                "assetPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "create_material"
                )
                {
                    return
                        unityTools.CreateMaterial(
                            GetStringArg(
                                args,
                                "assetPath"
                            ),
                            GetStringArg(
                                args,
                                "shaderName"
                            )
                        );
                }


                if (
                    functionName ==
                    "set_material_color"
                )
                {
                    return
                        unityTools.SetMaterialColor(
                            GetStringArg(
                                args,
                                "materialPath"
                            ),
                            GetFloatArg(
                                args,
                                "red"
                            ),
                            GetFloatArg(
                                args,
                                "green"
                            ),
                            GetFloatArg(
                                args,
                                "blue"
                            ),
                            GetFloatArg(
                                args,
                                "alpha"
                            )
                        );
                }


                if (
                    functionName ==
                    "assign_material"
                )
                {
                    return
                        unityTools.AssignMaterial(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetStringArg(
                                args,
                                "materialPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "import_asset"
                )
                {
                    return
                        unityTools.ImportAsset(
                            GetStringArg(
                                args,
                                "assetPath"
                            )
                        );
                }


                // ====================================================
                // PREFABS
                // ====================================================

                if (
                    functionName ==
                    "create_prefab"
                )
                {
                    return
                        unityTools.CreatePrefab(
                            GetStringArg(
                                args,
                                "objectPath"
                            ),
                            GetStringArg(
                                args,
                                "assetPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "instantiate_prefab"
                )
                {
                    return
                        unityTools.InstantiatePrefab(
                            GetStringArg(
                                args,
                                "assetPath"
                            ),
                            GetStringArg(
                                args,
                                "name"
                            ),
                            GetStringArg(
                                args,
                                "parentPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "save_scene"
                )
                {
                    return
                        unityTools.SaveScene();
                }


                // ====================================================
                // FILESYSTEM
                // ====================================================

                if (
                    functionName ==
                    "list_allowed_roots"
                )
                {
                    return
                        fileTools.ListAllowedRoots();
                }


                if (
                    functionName ==
                    "search_and_read_file"
                )
                {
                    return
                        fileTools.SearchAndReadFile(
                            GetStringArg(
                                args,
                                "fileName"
                            )
                        );
                }


                if (
                    functionName ==
                    "read_file"
                )
                {
                    return
                        fileTools.ReadFile(
                            GetStringArg(
                                args,
                                "filePath"
                            )
                        );
                }


                if (
                    functionName ==
                    "read_file_section"
                )
                {
                    return
                        fileTools.ReadFileSection(
                            GetStringArg(
                                args,
                                "filePath"
                            ),
                            GetIntArg(
                                args,
                                "startLine"
                            ),
                            GetIntArg(
                                args,
                                "endLine"
                            )
                        );
                }


                if (
                    functionName ==
                    "create_folder"
                )
                {
                    return
                        fileTools.CreateFolder(
                            GetStringArg(
                                args,
                                "folderPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "create_file"
                )
                {
                    string path =
                        GetStringArg(
                            args,
                            "filePath"
                        );


                    if (
                        selfTools.IsSelfPath(
                            path
                        )
                    )
                    {
                        return
                            "SELF WRITE DENIED.";
                    }


                    return
                        fileTools.CreateFile(
                            path,
                            GetStringArg(
                                args,
                                "content"
                            )
                        );
                }


                // ====================================================
                // SELF DEVELOPMENT
                // ====================================================

                if (
                    functionName ==
                    "backup_project"
                )
                {
                    return
                        selfTools.BackupProject();
                }


                if (
                    functionName ==
                    "inspect_self_structure"
                )
                {
                    return
                        selfTools.InspectSelfStructure();
                }


                if (
                    functionName ==
                    "find_self_text"
                )
                {
                    return
                        selfTools.FindSelfText(
                            GetStringArg(
                                args,
                                "relativePath"
                            ),
                            GetStringArg(
                                args,
                                "searchText"
                            )
                        );
                }


                if (
                    functionName ==
                    "read_self_file_section"
                )
                {
                    return
                        selfTools.ReadSelfFileSection(
                            GetStringArg(
                                args,
                                "relativePath"
                            ),
                            GetIntArg(
                                args,
                                "startLine"
                            ),
                            GetIntArg(
                                args,
                                "endLine"
                            )
                        );
                }


                if (
                    functionName ==
                    "replace_self_text"
                )
                {
                    return
                        selfTools.ReplaceSelfText(
                            GetStringArg(
                                args,
                                "relativePath"
                            ),
                            GetStringArg(
                                args,
                                "oldText"
                            ),
                            GetStringArg(
                                args,
                                "newText"
                            )
                        );
                }


                if (
                    functionName ==
                    "write_self_file"
                )
                {
                    return
                        selfTools.WriteSelfFile(
                            GetStringArg(
                                args,
                                "relativePath"
                            ),
                            GetStringArg(
                                args,
                                "content"
                            )
                        );
                }


                if (
                    functionName ==
                    "build_self"
                )
                {
                    return
                        selfTools.BuildSelf();
                }


                if (
                    functionName ==
                    "restart_self"
                )
                {
                    return
                        selfTools.RestartSelf();
                }


                return
                    $"UNKNOWN TOOL: {functionName}";
            }
            catch (
                JsonException ex
            )
            {
                return
                    "TOOL JSON ERROR: "
                    +
                    ex.Message;
            }
            catch (
                Exception ex
            )
            {
                return
                    "TOOL ERROR: "
                    +
                    ex.GetType().Name
                    +
                    ": "
                    +
                    ex.Message;
            }
        }


        // ============================================================
        // TEMP CAPABILITY TOOL
        // ============================================================

        private static object TempCapabilityTool()
        {
            return
                Tool(
                    "execute_temp_capability",

                    "Sends one temporary C# capability to the Unity Editor, where Unity compiles it into a DLL and executes it on the main thread. " +
                    "Use for complex or missing Unity behavior. " +
                    "Source must contain exactly one concrete IUnityDynamicCapability and may use UnityEngine directly. " +
                    "Do not use UnityEditor, System.IO, System.Net, reflection, processes, threads, unsafe code or destructive APIs. " +
                    "Use UnityDynamicCapabilityContext for exact hierarchy lookup, Undo-aware creation, dirty marking and scene saving.",

                    new Dictionary<string, object>
                    {
                        ["name"] =
                            StringProperty(
                                "Short capability name. Must exactly match the generated capability Name property."
                            ),

                        ["source"] =
                            StringProperty(
                                "Complete compact C# source implementing IUnityDynamicCapability."
                            ),

                        ["arguments"] =
                            new
                            {
                                type =
                                    "object",

                                description =
                                    "Optional compact runtime values for the capability.",

                                additionalProperties =
                                    true
                            }
                    },

                    new[]
                    {
                        "name",
                        "source"
                    }
                );
        }


        private static object CreateUnityScriptTool()
        {
            return
                Tool(
                    "create_unity_script",

                    "Creates or deliberately updates one persistent Unity MonoBehaviour source file inside Assets/Scripts. " +
                    "Use for gameplay/runtime behavior that must survive domain reload and run in Play Mode. " +
                    "The filename must exactly match className. Source may use UnityEngine but not UnityEditor, filesystem, network, process, reflection, threading, unsafe or editor automation APIs. " +
                    "Set overwrite=false for a new script. Set overwrite=true only when the user requested an update or a compiler-error repair. " +
                    "After this tool succeeds, always call wait_for_unity_script_compile with its jobId before attach_script.",

                    new Dictionary<string, object>
                    {
                        ["assetPath"] =
                            StringProperty(
                                "Safe path inside Assets/Scripts ending in ClassName.cs, for example Assets/Scripts/Player/FpsPlayerController.cs."
                            ),

                        ["className"] =
                            StringProperty(
                                "Exact public MonoBehaviour class name and filename without .cs."
                            ),

                        ["source"] =
                            StringProperty(
                                "Complete persistent C# MonoBehaviour source."
                            ),

                        ["overwrite"] =
                            BooleanProperty()
                    },

                    new[]
                    {
                        "assetPath",
                        "className",
                        "source",
                        "overwrite"
                    }
                );
        }


        private static object ReadUnityScriptTool()
        {
            return
                Tool(
                    "read_unity_script",

                    "Reads up to 220 exact lines from an existing persistent C# script inside Assets/Scripts. " +
                    "Use before updating a script so existing behavior and class identity are preserved. " +
                    "Use startLine=1 and endLine=0 to read the first section and inspect totalLines/truncated in the result.",

                    new Dictionary<string, object>
                    {
                        ["assetPath"] =
                            StringProperty(
                                "Exact safe path under Assets/Scripts ending in .cs."
                            ),

                        ["startLine"] =
                            IntegerProperty(),

                        ["endLine"] =
                            IntegerProperty()
                    },

                    new[]
                    {
                        "assetPath",
                        "startLine",
                        "endLine"
                    }
                );
        }


        private static object UnityPlayModeTool()
        {
            return
                Tool(
                    "set_unity_play_mode",

                    "Schedules entering or exiting Unity Play Mode. Use only when the user explicitly requested a runtime test or observation. " +
                    "After enter, inspect the target with get_unity_runtime_state. Exit after observation unless the user requested otherwise.",

                    new Dictionary<string, object>
                    {
                        ["action"] =
                            new
                            {
                                type = "string",
                                @enum = new[]
                                {
                                    "enter",
                                    "exit"
                                }
                            }
                    },

                    new[]
                    {
                        "action"
                    }
                );
        }


        // ============================================================
        // OTHER TOOL SCHEMAS
        // ============================================================

        private static object CreateGameObjectTool()
        {
            return
                Tool(
                    "create_gameobject",
                    "Creates Unity GameObject. Empty parentPath means root.",

                    new Dictionary<string, object>
                    {
                        ["name"] =
                            StringProperty(),

                        ["parentPath"] =
                            StringProperty()
                    },

                    new[]
                    {
                        "name",
                        "parentPath"
                    }
                );
        }


        private static object CreatePrimitiveTool()
        {
            return
                Tool(
                    "create_primitive",
                    "Creates Unity primitive.",

                    new Dictionary<string, object>
                    {
                        ["primitiveType"] =
                            StringProperty(
                                "Cube, Sphere, Capsule, Cylinder, Plane or Quad."
                            ),

                        ["name"] =
                            StringProperty(),

                        ["parentPath"] =
                            StringProperty()
                    },

                    new[]
                    {
                        "primitiveType",
                        "name",
                        "parentPath"
                    }
                );
        }


        private static object VectorTool(
            string name,
            string description
        )
        {
            return
                Tool(
                    name,
                    description,

                    new Dictionary<string, object>
                    {
                        ["objectPath"] =
                            StringProperty(),

                        ["x"] =
                            NumberProperty(),

                        ["y"] =
                            NumberProperty(),

                        ["z"] =
                            NumberProperty()
                    },

                    new[]
                    {
                        "objectPath",
                        "x",
                        "y",
                        "z"
                    }
                );
        }


        private static object SetActiveTool()
        {
            return
                Tool(
                    "set_active",
                    "Enables or disables Unity GameObject.",

                    new Dictionary<string, object>
                    {
                        ["objectPath"] =
                            StringProperty(),

                        ["active"] =
                            BooleanProperty()
                    },

                    new[]
                    {
                        "objectPath",
                        "active"
                    }
                );
        }


        private static object DuplicateTool()
        {
            return
                Tool(
                    "duplicate_gameobject",
                    "Duplicates existing Unity GameObject.",

                    new Dictionary<string, object>
                    {
                        ["objectPath"] =
                            StringProperty(),

                        ["newName"] =
                            StringProperty(),

                        ["parentPath"] =
                            StringProperty()
                    },

                    new[]
                    {
                        "objectPath",
                        "newName",
                        "parentPath"
                    }
                );
        }


        private static object ConfigureRigidbodyTool()
        {
            return
                Tool(
                    "configure_rigidbody",
                    "Configures Rigidbody.",

                    new Dictionary<string, object>
                    {
                        ["objectPath"] =
                            StringProperty(),

                        ["mass"] =
                            NumberProperty(),

                        ["useGravity"] =
                            BooleanProperty(),

                        ["isKinematic"] =
                            BooleanProperty()
                    },

                    new[]
                    {
                        "objectPath",
                        "mass",
                        "useGravity",
                        "isKinematic"
                    }
                );
        }


        private static object ConfigureColliderTool()
        {
            return
                Tool(
                    "configure_collider",
                    "Configures Collider.",

                    new Dictionary<string, object>
                    {
                        ["objectPath"] =
                            StringProperty(),

                        ["enabled"] =
                            BooleanProperty(),

                        ["isTrigger"] =
                            BooleanProperty()
                    },

                    new[]
                    {
                        "objectPath",
                        "enabled",
                        "isTrigger"
                    }
                );
        }


        private static object MaterialColorTool()
        {
            return
                Tool(
                    "set_material_color",
                    "Sets material RGBA values from 0 to 1.",

                    new Dictionary<string, object>
                    {
                        ["materialPath"] =
                            StringProperty(),

                        ["red"] =
                            NumberProperty(),

                        ["green"] =
                            NumberProperty(),

                        ["blue"] =
                            NumberProperty(),

                        ["alpha"] =
                            NumberProperty()
                    },

                    new[]
                    {
                        "materialPath",
                        "red",
                        "green",
                        "blue",
                        "alpha"
                    }
                );
        }


        private static object InstantiatePrefabTool()
        {
            return
                Tool(
                    "instantiate_prefab",
                    "Instantiates Unity prefab.",

                    new Dictionary<string, object>
                    {
                        ["assetPath"] =
                            StringProperty(),

                        ["name"] =
                            StringProperty(),

                        ["parentPath"] =
                            StringProperty()
                    },

                    new[]
                    {
                        "assetPath",
                        "name",
                        "parentPath"
                    }
                );
        }


        private static object ReadSectionTool()
        {
            return
                Tool(
                    "read_file_section",
                    "Reads selected line range.",

                    new Dictionary<string, object>
                    {
                        ["filePath"] =
                            StringProperty(),

                        ["startLine"] =
                            IntegerProperty(),

                        ["endLine"] =
                            IntegerProperty()
                    },

                    new[]
                    {
                        "filePath",
                        "startLine",
                        "endLine"
                    }
                );
        }


        private static object SelfReadSectionTool()
        {
            return
                Tool(
                    "read_self_file_section",
                    "Reads selected AI Assistant source lines.",

                    new Dictionary<string, object>
                    {
                        ["relativePath"] =
                            StringProperty(),

                        ["startLine"] =
                            IntegerProperty(),

                        ["endLine"] =
                            IntegerProperty()
                    },

                    new[]
                    {
                        "relativePath",
                        "startLine",
                        "endLine"
                    }
                );
        }


        private static object ToolNoArgs(
            string name,
            string description
        )
        {
            return
                Tool(
                    name,
                    description,
                    new Dictionary<string, object>(),
                    Array.Empty<string>()
                );
        }


        private static object OneStringTool(
            string name,
            string description,
            string property
        )
        {
            return
                Tool(
                    name,
                    description,

                    new Dictionary<string, object>
                    {
                        [property] =
                            StringProperty()
                    },

                    new[]
                    {
                        property
                    }
                );
        }


        private static object TwoStringTool(
            string name,
            string description,
            string first,
            string second
        )
        {
            return
                Tool(
                    name,
                    description,

                    new Dictionary<string, object>
                    {
                        [first] =
                            StringProperty(),

                        [second] =
                            StringProperty()
                    },

                    new[]
                    {
                        first,
                        second
                    }
                );
        }


        private static object ThreeStringTool(
            string name,
            string description,
            string first,
            string second,
            string third
        )
        {
            return
                Tool(
                    name,
                    description,

                    new Dictionary<string, object>
                    {
                        [first] =
                            StringProperty(),

                        [second] =
                            StringProperty(),

                        [third] =
                            StringProperty()
                    },

                    new[]
                    {
                        first,
                        second,
                        third
                    }
                );
        }


        private static object Tool(
            string name,
            string description,
            Dictionary<string, object> properties,
            string[] required
        )
        {
            return
                new
                {
                    type =
                        "function",

                    function =
                        new
                        {
                            name,

                            description,

                            parameters =
                                new
                                {
                                    type =
                                        "object",

                                    properties,

                                    required,

                                    additionalProperties =
                                        false
                                }
                        }
                };
        }


        private static object StringProperty(
            string? description = null
        )
        {
            if (
                description == null
            )
            {
                return
                    new
                    {
                        type =
                            "string"
                    };
            }


            return
                new
                {
                    type =
                        "string",

                    description
                };
        }


        private static object NumberProperty()
        {
            return
                new
                {
                    type =
                        "number"
                };
        }


        private static object IntegerProperty()
        {
            return
                new
                {
                    type =
                        "integer"
                };
        }


        private static object BooleanProperty()
        {
            return
                new
                {
                    type =
                        "boolean"
                };
        }


        // ============================================================
        // ARGUMENT HELPERS
        // ============================================================

        private static string GetStringArg(
            JsonElement args,
            string name
        )
        {
            if (
                args.TryGetProperty(
                    name,
                    out JsonElement value
                )
                &&
                value.ValueKind ==
                JsonValueKind.String
            )
            {
                return
                    value.GetString()
                    ??
                    "";
            }


            return
                "";
        }


        private static int GetIntArg(
            JsonElement args,
            string name
        )
        {
            if (
                args.TryGetProperty(
                    name,
                    out JsonElement value
                )
                &&
                value.TryGetInt32(
                    out int result
                )
            )
            {
                return
                    result;
            }


            throw new JsonException(
                $"Missing/invalid integer argument: {name}"
            );
        }


        private static float GetFloatArg(
            JsonElement args,
            string name
        )
        {
            if (
                args.TryGetProperty(
                    name,
                    out JsonElement value
                )
                &&
                value.ValueKind ==
                JsonValueKind.Number
            )
            {
                return
                    value.GetSingle();
            }


            throw new JsonException(
                $"Missing/invalid number argument: {name}"
            );
        }


        private static bool GetBoolArg(
            JsonElement args,
            string name
        )
        {
            if (
                args.TryGetProperty(
                    name,
                    out JsonElement value
                )
                &&
                (
                    value.ValueKind ==
                    JsonValueKind.True
                    ||
                    value.ValueKind ==
                    JsonValueKind.False
                )
            )
            {
                return
                    value.GetBoolean();
            }


            throw new JsonException(
                $"Missing/invalid boolean argument: {name}"
            );
        }


        // ============================================================
        // TOOL UTILITIES
        // ============================================================

        private static bool IsNoArgTool(
            string toolName
        )
        {
            return
                toolName ==
                "get_agent_version"
                ||
                toolName ==
                "get_active_scene"
                ||
                toolName ==
                "get_scene_hierarchy"
                ||
                toolName ==
                "get_console_errors"
                ||
                toolName ==
                "get_unity_project_settings"
                ||
                toolName ==
                "save_scene"
                ||
                toolName ==
                "list_allowed_roots"
                ||
                toolName ==
                "backup_project"
                ||
                toolName ==
                "inspect_self_structure"
                ||
                toolName ==
                "build_self"
                ||
                toolName ==
                "restart_self";
        }


        private static string NormalizeJson(
            string json
        )
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(
                        json
                    );


                return
                    JsonSerializer.Serialize(
                        document.RootElement
                    );
            }
            catch
            {
                return
                    json;
            }
        }


        private static string GetRegisteredToolNames(
            object[] definitions
        )
        {
            if (
                definitions.Length ==
                0
            )
            {
                return
                    "";
            }


            List<string> names =
                new List<string>();


            foreach (
                object definition
                in definitions
            )
            {
                string name =
                    GetToolName(
                        definition
                    );


                if (
                    !string.IsNullOrWhiteSpace(
                        name
                    )
                )
                {
                    names.Add(
                        name
                    );
                }
            }


            return
                string.Join(
                    ", ",
                    names
                );
        }


        private static string GetToolName(
            object definition
        )
        {
            string json =
                JsonSerializer.Serialize(
                    definition
                );


            using JsonDocument document =
                JsonDocument.Parse(
                    json
                );


            return
                document
                    .RootElement
                    .GetProperty("function")
                    .GetProperty("name")
                    .GetString()
                ??
                "";
        }


        private static bool ContainsAny(
            string text,
            params string[] terms
        )
        {
            foreach (
                string term
                in terms
            )
            {
                if (
                    text.Contains(
                        term,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return
                        true;
                }
            }


            return
                false;
        }


        // ============================================================
        // TOOL RESULT LIMITS
        // ============================================================

        private static string TrimToolResult(
            string result,
            string toolName
        )
        {
            if (
                string.IsNullOrWhiteSpace(
                    result
                )
            )
            {
                return
                    "Tool completed with empty result.";
            }


            int maxChars =
                toolName == "read_unity_script"
                    ? MaxUnityScriptReadResultChars
                    : MaxToolResultChars;


            if (result.Length <= maxChars)
            {
                return
                    result;
            }


            return
                result.Substring(
                    0,
                    maxChars
                )
                +
                "\n[RESULT TRUNCATED]";
        }
        private static bool IsTerminalTempCapabilityFailure(
    string result
)
        {
            if (
                result.StartsWith(
                    "TEMP COMPILE FAILED",
                    StringComparison.Ordinal
                )
                ||
                result.StartsWith(
                    "TEMP CAPABILITY DENIED",
                    StringComparison.Ordinal
                )
                ||
                result.StartsWith(
                    "TEMP CAPABILITY ERROR",
                    StringComparison.Ordinal
                )
            )
            {
                // Ove greške model može popraviti pisanjem
                // jedne kompletne nove capability verzije.
                return false;
            }

            if (
                result.StartsWith(
                    "TEMP RUNTIME ERROR",
                    StringComparison.Ordinal
                )
                ||
                result.StartsWith(
                    "TEMP LOAD ERROR",
                    StringComparison.Ordinal
                )
                ||
                result.StartsWith(
                    "TEMP ARGUMENT ERROR",
                    StringComparison.Ordinal
                )
            )
            {
                return true;
            }

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(
                        result
                    );

                JsonElement root =
                    document.RootElement;

                bool failed =
                    root.TryGetProperty(
                        "success",
                        out JsonElement success
                    )
                    &&
                    success.ValueKind ==
                    JsonValueKind.False;


                if (!failed)
                {
                    return false;
                }


                string phase =
                    root.TryGetProperty(
                        "phase",
                        out JsonElement phaseElement
                    )
                    &&
                    phaseElement.ValueKind ==
                    JsonValueKind.String
                        ? phaseElement.GetString() ?? ""
                        : "";


                // These failures are safe to repair with one complete
                // rewritten source because Unity did not execute it.
                if (
                    phase == "compile"
                    || phase == "validation"
                    || phase == "contract"
                )
                {
                    return false;
                }


                // Execute/timeout/offline/load/busy failures must not
                // be replayed automatically.
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string TrimConsoleResult(
            string result
        )
        {
            const int max =
                500;


            string oneLine =
                result
                    .Replace(
                        "\r",
                        " "
                    )
                    .Replace(
                        "\n",
                        " "
                    );


            if (
                oneLine.Length <=
                max
            )
            {
                return
                    oneLine;
            }


            return
                oneLine.Substring(
                    0,
                    max
                )
                +
                "...";
        }


        private static string FormatArgumentsForConsole(
            string arguments
        )
        {
            // Generated C# may be thousands of chars.
            // Do not spam terminal.
            if (
                arguments.Length >
                800
            )
            {
                return
                    $"[arguments {arguments.Length} chars]";
            }


            return
                arguments;
        }


        // ============================================================
        // RATE LIMIT
        // ============================================================

        private void ReportActivity(string message)
        {
            Console.WriteLine(message);
            Activity?.Invoke(message);
        }


        private async Task<HttpResponseMessage>
            SendWithRateLimitRetry(
                string url,
                string json
            )
        {
            int retry =
                0;


            while (true)
            {
                using StringContent content =
                    new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"
                    );


                HttpResponseMessage response =
                    await client.PostAsync(
                        url,
                        content
                    );


                if (
                    response.StatusCode !=
                    HttpStatusCode.TooManyRequests
                )
                {
                    return
                        response;
                }


                string responseBody =
                    await response.Content
                        .ReadAsStringAsync();


                if (
                    responseBody.Contains(
                        "Request too large",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    response.Content =
                        new StringContent(
                            responseBody,
                            Encoding.UTF8,
                            "application/json"
                        );


                    return
                        response;
                }


                retry++;


                if (
                    retry >
                    MaxRateLimitRetries
                )
                {
                    response.Content =
                        new StringContent(
                            responseBody,
                            Encoding.UTF8,
                            "application/json"
                        );


                    return
                        response;
                }


                double seconds =
                    GetRetryAfterSeconds(
                        response,
                        retry
                    );


                ReportActivity(
                    $"[RATE LIMIT] Čekam {Math.Ceiling(seconds)} sekundi... ({retry}/{MaxRateLimitRetries})"
                );


                response.Dispose();


                await Task.Delay(
                    TimeSpan.FromSeconds(
                        seconds
                    )
                );
            }
        }


        private static double GetRetryAfterSeconds(
            HttpResponseMessage response,
            int retry
        )
        {
            if (
                response
                    .Headers
                    .RetryAfter?
                    .Delta
                is TimeSpan delta
            )
            {
                return
                    Math.Max(
                        1,
                        delta.TotalSeconds + 1
                    );
            }


            if (
                response.Headers.TryGetValues(
                    "retry-after",
                    out IEnumerable<string>? values
                )
            )
            {
                string? value =
                    values.FirstOrDefault();


                if (
                    double.TryParse(
                        value,
                        NumberStyles.Any,
                        CultureInfo.InvariantCulture,
                        out double parsed
                    )
                )
                {
                    return
                        Math.Max(
                            1,
                            parsed + 1
                        );
                }
            }


            return
                6
                +
                retry * 4;
        }


        // ============================================================
        // TOOL-USE RECOVERY
        // ============================================================

        private static bool IsRecoverableToolUseError(
            string responseText
        )
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(
                        responseText
                    );


                if (
                    !document
                        .RootElement
                        .TryGetProperty(
                            "error",
                            out JsonElement error
                        )
                )
                {
                    return
                        false;
                }


                if (
                    error.TryGetProperty(
                        "code",
                        out JsonElement code
                    )
                    &&
                    code.ValueKind ==
                    JsonValueKind.String
                )
                {
                    return
                        string.Equals(
                            code.GetString(),
                            "tool_use_failed",
                            StringComparison.OrdinalIgnoreCase
                        );
                }


                return
                    false;
            }
            catch
            {
                return
                    false;
            }
        }
    }
}
