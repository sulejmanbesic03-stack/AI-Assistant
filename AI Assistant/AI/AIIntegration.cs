using AI_Assistant.TempCapabilities;
using AI_Assistant.Tools;

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AI_Assistant.AI
{
    public sealed class AIIntegration
    {
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


            Console.WriteLine(
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


                        Console.WriteLine(
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


                            Console.WriteLine(
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
                                toolResult
                            );


                        Console.WriteLine(
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
                                "Temporary capability stopped after a runtime or Unity batch failure. " +
                                "It was not retried because a timeout may occur after Unity already performed part of the batch.\n" +
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

execute_temp_capability is an escape hatch for complex, missing or inefficient behavior.

Do NOT use execute_temp_capability when one simple existing tool can efficiently complete the task.

Use execute_temp_capability when:
- existing tools cannot perform the requested behavior,
- one generated capability can replace many individual tool calls,
- or a complex Unity task can be executed locally as one batch.

Prefer ONE broad task-specific temporary capability.

Good:
- SetupFpsController
- BuildEnemySystem
- CreateInteractionSetup

Bad:
- CreatePlayer
- AddCamera
- AddController
- SetSpeed
- SetJump

Do not create many small temporary capabilities for one task.

Temporary capability lifecycle is automatic:

generate
-> validate
-> compile
-> execute
-> unload
-> delete

Do not manually create TempTools files.

Generated temporary capability source must:
- contain exactly one concrete ITempCapability implementation,
- have a Name property exactly matching the requested capability name,
- implement ExecuteAsync,
- remain compact,
- use only the controlled TempCapabilityContext.
The host automatically injects these required using directives:

using AI_Assistant.TempCapabilities;
using System.Text.Json;
using System.Threading.Tasks;

Never add using UnityEngine or using UnityEditor to the temporary capability itself.
The temporary DLL does not reference Unity assemblies.
Interact with Unity only through context.NewUnityBatch().
UnityEngine code is allowed only as script TEXT passed to CreateScript(...).

Use this exact capability structure:

public sealed class ExampleCapability : ITempCapability
{
    public string Name => "ExampleCapability";

    public Task<string> ExecuteAsync(
        TempCapabilityContext context,
        JsonElement arguments
    )
    {
        string result =
            context
                .NewUnityBatch()
                .CreateGameObject(
                    "ExampleObject",
                    ""
                )
                .SaveScene()
                .Execute();

        return Task.FromResult(
            result
        );
    }
}

Before calling execute_temp_capability, silently check:
- every statement that requires a semicolon has one,
- parentheses and braces are balanced,
- Name exactly matches the tool argument name,
- ExecuteAsync returns Task<string>,
- there are no UnityEngine or UnityEditor references in the host capability.
Do NOT use:
- System.IO
- System.Net
- HttpClient
- Process
- Environment
- reflection loading
- operating-system filesystem APIs
- unsafe code

If compilation fails:
- read ALL returned compiler diagnostics,
- fix ALL of them in ONE complete rewritten capability,
- then retry.
- Do not repair only one compiler error per retry.

If a compiled capability returns a runtime, Unity batch, offline or timeout failure:
- do not generate another capability,
- do not repeat the batch,
- report the exact failure because Unity may already have executed part of it.
UNITY TEMP CAPABILITY RULES:

For multi-step Unity work, ALWAYS prefer:

context.NewUnityBatch()

Do NOT manually construct Unity batch JSON.

Do NOT manually call context.Unity.ExecuteBatch with a hand-written JSON string.

Use the fluent UnityBatchBuilder API.

Available Unity batch methods include:

CreateGameObject(name, parentPath)
CreatePrimitive(primitiveType, name, parentPath)

DeleteGameObject(objectPath)
RenameGameObject(objectPath, newName)
SetParent(objectPath, parentPath)
SetActive(objectPath, active)

SetPosition(objectPath, x, y, z)
SetRotation(objectPath, x, y, z)
SetScale(objectPath, x, y, z)

AddComponent(objectPath, componentType)
RemoveComponent(objectPath, componentType)

SetInt(objectPath, componentType, propertyName, value)
SetFloat(objectPath, componentType, propertyName, value)
SetBool(objectPath, componentType, propertyName, value)
SetString(objectPath, componentType, propertyName, value)

SetVector2(objectPath, componentType, propertyName, x, y)

SetVector3(objectPath, componentType, propertyName, x, y, z)

SetColor(objectPath, componentType, propertyName, red, green, blue, alpha)

CreateScript(assetPath, content)

SaveScene()

Execute()

Build ONE Unity batch whenever possible.

Call Execute() ONCE at the end of the batch.

Example pattern:

string result =
    context
        .NewUnityBatch()
        .CreateGameObject("Player")
        .AddComponent(
            "Player",
            "UnityEngine.CharacterController"
        )
        .SetPosition(
            "Player",
            0f,
            1f,
            0f
        )
        .SaveScene()
        .Execute();

return Task.FromResult(result);


For multiline Unity script content, use a C# raw string literal.

Use three double-quote characters to open and close the script string.

Example structure:

string script = """
using UnityEngine;

public class Example : MonoBehaviour
{
}
""";

Then pass the string to:

.CreateScript(
    "Assets/Scripts/Example.cs",
    script
)

The local batch builder serializes all JSON.
The generated capability must NOT perform JSON escaping manually.


UNITY WORKFLOW:

For a simple Unity action, use the simple registered action tool.

For a complex Unity setup:
- avoid many direct action tool calls,
- prefer one execute_temp_capability call,
- build one NewUnityBatch(),
- execute one local Unity batch.

Read get_scene_hierarchy only when object identity or current hierarchy is actually needed.

Do not automatically inspect hierarchy before creating entirely new known root objects unless existing state matters.

Do not re-read hierarchy after every successful operation.

Use exact hierarchy paths when addressing existing GameObjects.

After complex scene modification, perform one final verification when useful.

If the user explicitly asks to verify compiler errors, call get_console_errors after Unity has processed the changes.

Never claim success if a tool result reports failure.


UNITY SCRIPT CREATION:

Creating a .cs script causes Unity compilation.

Therefore a complex task that creates a script should normally:

1. execute one temporary capability / Unity batch,
2. allow Unity to import/compile the generated script,
3. then use get_console_errors once if verification was requested.

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
                    "terrain",
                    "object",
                    "objekat",
                    "enemy",
                    "weapon",
                    "inventory"
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
            // COMPLEX UNITY TASK
            //
            // Important:
            // Do NOT send 15 simple mutation schemas.
            // ========================================================

            if (
                complexTask
            )
            {
                tools.Add(
                    TempCapabilityTool()
                );


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

                    "Creates, Roslyn-compiles, executes, unloads and deletes one temporary C# capability in one call. " +
                    "Use for complex or missing Unity behavior. " +
                    "Source must contain exactly one concrete ITempCapability. " +
                    "For multi-step Unity tasks ALWAYS use context.NewUnityBatch() and one fluent batch ending with Execute(). " +
                    "Do NOT manually construct batch JSON. " +
                    "Do NOT use System.IO, System.Net, HttpClient, Process or Environment. " +
                    "Use CreateScript on the batch builder when a Unity C# script is needed.",

                    new Dictionary<string, object>
                    {
                        ["name"] =
                            StringProperty(
                                "Short capability name. Must exactly match the generated capability Name property."
                            ),

                        ["source"] =
                            StringProperty(
                                "Complete compact C# source implementing ITempCapability."
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
            string result
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


            if (
                result.Length <=
                MaxToolResultChars
            )
            {
                return
                    result;
            }


            return
                result.Substring(
                    0,
                    MaxToolResultChars
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

                return
                    root.TryGetProperty(
                        "success",
                        out JsonElement success
                    )
                    &&
                    success.ValueKind ==
                    JsonValueKind.False;
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


                Console.WriteLine(
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