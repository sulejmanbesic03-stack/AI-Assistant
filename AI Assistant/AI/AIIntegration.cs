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
    public class AIIntegration
    {
        private readonly HttpClient client;

        private readonly FileSystemTools fileTools;

        private readonly SelfDevelopmentTools selfTools;

        private readonly List<ChatMessage> conversationHistory;


        private const string Model =
            "openai/gpt-oss-20b";


        private const int MaxIterations =
            16;


        private const int MaxToolResultChars =
            7000;


        private const int MaxChatHistoryMessages =
            6;


        private const int MaxToolCyclesInContext =
            3;


        private const int MaxToolUseRecoveryAttempts =
            2;


        private const int MaxRateLimitRetries =
            4;


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


            conversationHistory =
                new List<ChatMessage>();
        }


        // ============================================
        // ASK
        // ============================================

        public async Task<string> Ask(
            string prompt
        )
        {
            string? apiKey =
                Environment.GetEnvironmentVariable(
                    "GROQ_API_KEY"
                );


            if (string.IsNullOrWhiteSpace(apiKey))
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


            HashSet<string> executedToolCalls =
                new HashSet<string>(
                    StringComparer.Ordinal
                );


            List<object> baseMessages =
                BuildBaseMessages();


            List<List<object>> toolCycles =
                new List<List<object>>();


            int iteration =
                0;


            int toolUseRecoveryAttempts =
                0;


            while (iteration < MaxIterations)
            {
                iteration++;


                List<object> requestMessages =
                    BuildRequestMessages(
                        baseMessages,
                        toolCycles
                    );


                object[] toolDefinitions =
                    BuildToolDefinitions();


                object requestBody =
                    new
                    {
                        model =
                            Model,

                        messages =
                            requestMessages,

                        tool_choice =
                            "auto",

                        reasoning_effort =
                            "low",

                        tools =
                            toolDefinitions
                    };


                string json =
                    JsonSerializer.Serialize(
                        requestBody
                    );


                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue(
                        "Bearer",
                        apiKey
                    );


                HttpResponseMessage response =
                    await SendWithRateLimitRetry(
                        "https://api.groq.com/openai/v1/chat/completions",
                        json
                    );


                string responseText =
                    await response.Content
                        .ReadAsStringAsync();


                // ====================================
                // API ERROR
                // ====================================

                if (!response.IsSuccessStatusCode)
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


                        Console.WriteLine(
                            $"[TOOL RECOVERY] Invalid/hallucinated tool call. Retry {toolUseRecoveryAttempts}/{MaxToolUseRecoveryAttempts}..."
                        );


                        string registeredTools =
                            GetRegisteredToolNames();


                        baseMessages.Add(
                            new
                            {
                                role = "system",

                                content =
                                    $"""
                                    Your previous tool call was rejected by the API.

                                    Retry the user's task using ONLY tools actually registered in this request.

                                    Registered tool names:
                                    {registeredTools}

                                    Tool names must match exactly.
                                    Do not append channel names, markup, punctuation, suffixes, prefixes, or internal tokens to a tool name.

                                    Tool arguments must be strict valid JSON.
                                    Never invent parameters that are not defined by the selected tool.

                                    If you expected a tool that is not registered, do not call it.
                                    """
                            }
                        );


                        response.Dispose();

                        continue;
                    }


                    response.Dispose();


                    return
                        $"Groq API greška:\n{responseText}";
                }


                using JsonDocument document =
                    JsonDocument.Parse(
                        responseText
                    );


                response.Dispose();


                JsonElement message =
                    document
                        .RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("message");


                // ====================================
                // TOOL CALL
                // ====================================

                if (
                    message.TryGetProperty(
                        "tool_calls",
                        out JsonElement toolCalls
                    )
                    &&
                    toolCalls.ValueKind ==
                        JsonValueKind.Array
                    &&
                    toolCalls.GetArrayLength() > 0
                )
                {
                    List<object> currentCycle =
                        new List<object>();


                    object assistantMessage =
                        JsonSerializer.Deserialize<object>(
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
                            ?? "";


                        JsonElement function =
                            toolCall
                                .GetProperty("function");


                        string functionName =
                            function
                                .GetProperty("name")
                                .GetString()
                            ?? "";


                        string argumentsJson =
                            function
                                .GetProperty("arguments")
                                .GetString()
                            ?? "{}";


                        Console.WriteLine(
                            $"[TOOL {iteration}] {functionName} | {argumentsJson}"
                        );


                        string normalizedArguments =
                            NormalizeJson(
                                argumentsJson
                            );


                        string signature =
                            functionName +
                            "|" +
                            normalizedArguments;


                        string toolResult;


                        if (
                            executedToolCalls.Contains(
                                signature
                            )
                        )
                        {
                            toolResult =
                                "DUPLICATE TOOL CALL BLOCKED. " +
                                "This exact tool with equivalent arguments already ran during this task.";
                        }
                        else
                        {
                            executedToolCalls.Add(
                                signature
                            );


                            toolResult =
                                ExecuteTool(
                                    functionName,
                                    argumentsJson
                                );
                        }


                        toolResult =
                            TrimToolResult(
                                toolResult
                            );


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


                // ====================================
                // FINAL RESPONSE
                // ====================================

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
                        ?? "";


                    conversationHistory.Add(
                        new ChatMessage(
                            "assistant",
                            answer
                        )
                    );


                    return answer;
                }


                return
                    "Model nije vratio finalni odgovor niti tool call.";
            }


            return
                $"Agent je dostigao maksimalan broj tool koraka ({MaxIterations}).";
        }


        // ============================================
        // BASE MESSAGES
        // ============================================

        private List<object> BuildBaseMessages()
        {
            List<object> messages =
                new List<object>
                {
                    new
                    {
                        role = "system",

                        content =
                        """
                        You are an efficient local AI development agent written in C#/.NET.

                        CORE ARCHITECTURE:

                        AI/AIIntegration.cs contains:
                        - Ask
                        - ExecuteTool
                        - BuildToolDefinitions
                        - Groq request/tool loop

                        Tools/AgentVersion.cs contains the agent version.

                        Tools/FileSystemTools.cs contains generic filesystem operations.

                        Tools/SelfDevelopmentTools.cs contains controlled self-development operations.

                        There is NO ToolDefinitions.cs.
                        There is NO ToolRegistry.cs.
                        There is NO ITool interface.
                        There is NO Tool base class.

                        Never invent those files or types unless the user explicitly requests an architecture refactor.


                        GENERAL TOOL RULES:

                        Use as few tool calls as possible.

                        Never repeatedly call the same tool with equivalent arguments.

                        Use only tools actually registered in request.tools.

                        Tool names must match exactly.

                        Never append internal tokens, channel names, markup, punctuation or suffixes to tool names.

                        Tool arguments must be strict valid JSON.

                        Do not invent parameters.

                        Stop calling tools when the task is complete.


                        NORMAL FILESYSTEM:

                        Generic filesystem tools use ABSOLUTE paths.

                        Use list_allowed_roots when you need to discover valid roots.

                        Use search_and_read_file when you know a filename but not its location.

                        For large files use read_file_section.


                        SELF DEVELOPMENT:

                        Modify your own source only when the user explicitly asks you to modify yourself.

                        Self-development paths are RELATIVE to the AI Assistant source root.

                        Required workflow:

                        1. backup_project
                        2. inspect_self_structure only if needed
                        3. find_self_text when you know a symbol/method name but not its line
                        4. read_self_file_section for only the relevant range
                        5. replace_self_text for targeted changes to EXISTING source files
                        6. write_self_file mainly for NEW source files
                        7. build_self
                        8. if build fails, inspect only relevant code and repair it
                        9. build again
                        10. restart_self only after BUILD SUCCESS

                        Never use create_file, copy_file or move_file to modify your own source.

                        Before using replace_self_text, copy oldText exactly from the current source.
                        oldText should be large enough to occur exactly once.

                        Do not rewrite an entire large AIIntegration.cs for a small modification.

                        BuildToolDefinitions and ExecuteTool are both inside AI/AIIntegration.cs.
                        Use find_self_text to locate them.

                        Never assume a new tool exists merely because its class/file exists.
                        A local agent tool is functional only when it is registered in BuildToolDefinitions and routed in ExecuteTool.

                        The runtime enforces backup/build/restart safety.
                        Do not attempt to bypass it.
                        """
                    }
                };


            IEnumerable<ChatMessage> recent =
                conversationHistory
                    .TakeLast(
                        MaxChatHistoryMessages
                    );


            messages.AddRange(
                recent.Select(
                    item => (object)new
                    {
                        role =
                            item.Role,

                        content =
                            item.Message
                    }
                )
            );


            return messages;
        }


        // ============================================
        // REQUEST MESSAGES
        // ============================================

        private List<object> BuildRequestMessages(
            List<object> baseMessages,
            List<List<object>> toolCycles
        )
        {
            List<object> messages =
                new List<object>(
                    baseMessages
                );


            foreach (
                List<object> cycle
                in toolCycles
            )
            {
                messages.AddRange(
                    cycle
                );
            }


            return messages;
        }


        // ============================================
        // 429 RETRY
        // ============================================

        private async Task<HttpResponseMessage> SendWithRateLimitRetry(
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
                    return response;
                }


                string body =
                    await response.Content
                        .ReadAsStringAsync();


                // Waiting does not fix a request which is
                // itself larger than the permitted token budget.
                if (
                    body.Contains(
                        "Request too large",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    response.Content =
                        new StringContent(
                            body,
                            Encoding.UTF8,
                            "application/json"
                        );


                    return response;
                }


                retry++;


                if (retry > MaxRateLimitRetries)
                {
                    response.Content =
                        new StringContent(
                            body,
                            Encoding.UTF8,
                            "application/json"
                        );


                    return response;
                }


                double waitSeconds =
                    GetRetryAfterSeconds(
                        response,
                        retry
                    );


                Console.WriteLine(
                    $"[RATE LIMIT] Čekam {Math.Ceiling(waitSeconds)} sekundi... ({retry}/{MaxRateLimitRetries})"
                );


                response.Dispose();


                await Task.Delay(
                    TimeSpan.FromSeconds(
                        waitSeconds
                    )
                );
            }
        }


        private double GetRetryAfterSeconds(
            HttpResponseMessage response,
            int retry
        )
        {
            if (
                response.Headers.RetryAfter?.Delta
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
                8 +
                retry * 4;
        }


        // ============================================
        // TOOL ERROR RECOVERY
        // ============================================

        private bool IsRecoverableToolUseError(
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
                    !document.RootElement.TryGetProperty(
                        "error",
                        out JsonElement error
                    )
                )
                {
                    return false;
                }


                string? code =
                    null;


                if (
                    error.TryGetProperty(
                        "code",
                        out JsonElement codeElement
                    )
                    &&
                    codeElement.ValueKind ==
                        JsonValueKind.String
                )
                {
                    code =
                        codeElement.GetString();
                }


                return
                    string.Equals(
                        code,
                        "tool_use_failed",
                        StringComparison.OrdinalIgnoreCase
                    );
            }
            catch
            {
                return false;
            }
        }


        // ============================================
        // NORMALIZE JSON
        // ============================================

        private string NormalizeJson(
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
                return json;
            }
        }


        // ============================================
        // TOOL ROUTER
        // ============================================

        private string ExecuteTool(
            string functionName,
            string argumentsJson
        )
        {
            try
            {
                using JsonDocument argsDocument =
                    JsonDocument.Parse(
                        argumentsJson
                    );


                JsonElement args =
                    argsDocument.RootElement;


                // ====================================
                // VERSION
                // ====================================

                if (
                    functionName ==
                    "get_agent_version"
                )
                {
                    return
                        AgentVersion.Version;
                }


                // ====================================
                // NORMAL FILESYSTEM
                // ====================================

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
                    string filePath =
                        GetStringArg(
                            args,
                            "filePath"
                        );


                    if (
                        selfTools.IsSelfPath(
                            filePath
                        )
                    )
                    {
                        return
                            "SELF WRITE DENIED: use backup_project + write_self_file/replace_self_text.";
                    }


                    return
                        fileTools.CreateFile(
                            filePath,
                            GetStringArg(
                                args,
                                "content"
                            )
                        );
                }


                if (
                    functionName ==
                    "list_files"
                )
                {
                    return
                        fileTools.ListFiles(
                            GetStringArg(
                                args,
                                "folderPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "list_directories"
                )
                {
                    return
                        fileTools.ListDirectories(
                            GetStringArg(
                                args,
                                "folderPath"
                            )
                        );
                }


                if (
                    functionName ==
                    "find_file"
                )
                {
                    return
                        fileTools.FindFile(
                            GetStringArg(
                                args,
                                "rootPath"
                            ),
                            GetStringArg(
                                args,
                                "fileName"
                            )
                        );
                }


                if (
                    functionName ==
                    "copy_file"
                )
                {
                    string destination =
                        GetStringArg(
                            args,
                            "destinationPath"
                        );


                    if (
                        selfTools.IsSelfPath(
                            destination
                        )
                    )
                    {
                        return
                            "SELF WRITE DENIED: copy_file cannot write into AI Assistant source.";
                    }


                    return
                        fileTools.CopyFile(
                            GetStringArg(
                                args,
                                "sourcePath"
                            ),
                            destination,
                            GetBoolArg(
                                args,
                                "overwrite"
                            )
                        );
                }


                if (
                    functionName ==
                    "move_file"
                )
                {
                    string destination =
                        GetStringArg(
                            args,
                            "destinationPath"
                        );


                    if (
                        selfTools.IsSelfPath(
                            destination
                        )
                    )
                    {
                        return
                            "SELF WRITE DENIED: move_file cannot write into AI Assistant source.";
                    }


                    return
                        fileTools.MoveFile(
                            GetStringArg(
                                args,
                                "sourcePath"
                            ),
                            destination,
                            GetBoolArg(
                                args,
                                "overwrite"
                            )
                        );
                }


                // ====================================
                // SELF DEVELOPMENT
                // ====================================

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
                    "backup_project"
                )
                {
                    return
                        selfTools.BackupProject();
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
                UnauthorizedAccessException ex
            )
            {
                return
                    $"ACCESS DENIED: {ex.Message}";
            }
            catch (
                JsonException ex
            )
            {
                return
                    $"TOOL JSON ERROR: {ex.Message}";
            }
            catch (
                Exception ex
            )
            {
                return
                    $"TOOL ERROR: {ex.Message}";
            }
        }


        // ============================================
        // TOOL RESULT LIMIT
        // ============================================

        private string TrimToolResult(
            string result
        )
        {
            if (string.IsNullOrEmpty(result))
            {
                return
                    "Tool returned an empty result.";
            }


            if (
                result.Length <=
                MaxToolResultChars
            )
            {
                return result;
            }


            return
                result.Substring(
                    0,
                    MaxToolResultChars
                )
                +
                "\n\n[TOOL RESULT TRUNCATED]";
        }


        // ============================================
        // ARG HELPERS
        // ============================================

        private string GetStringArg(
            JsonElement args,
            string name
        )
        {
            if (
                args.TryGetProperty(
                    name,
                    out JsonElement element
                )
                &&
                element.ValueKind ==
                    JsonValueKind.String
            )
            {
                return
                    element.GetString()
                    ?? "";
            }


            return "";
        }


        private int GetIntArg(
            JsonElement args,
            string name
        )
        {
            if (
                args.TryGetProperty(
                    name,
                    out JsonElement element
                )
                &&
                element.TryGetInt32(
                    out int value
                )
            )
            {
                return value;
            }


            return 0;
        }


        private bool GetBoolArg(
            JsonElement args,
            string name
        )
        {
            if (
                args.TryGetProperty(
                    name,
                    out JsonElement element
                )
                &&
                (
                    element.ValueKind ==
                    JsonValueKind.True
                    ||
                    element.ValueKind ==
                    JsonValueKind.False
                )
            )
            {
                return
                    element.GetBoolean();
            }


            return false;
        }


        // ============================================
        // REGISTERED TOOL NAMES
        // ============================================

        private string GetRegisteredToolNames()
        {
            string json =
                JsonSerializer.Serialize(
                    BuildToolDefinitions()
                );


            using JsonDocument document =
                JsonDocument.Parse(
                    json
                );


            IEnumerable<string> names =
                document
                    .RootElement
                    .EnumerateArray()
                    .Select(item =>
                        item
                            .GetProperty("function")
                            .GetProperty("name")
                            .GetString()
                        ?? ""
                    );


            return
                string.Join(
                    ", ",
                    names
                );
        }


        // ============================================
        // TOOL DEFINITIONS
        // ============================================

        private object[] BuildToolDefinitions()
        {
            return new object[]
            {
                ToolNoArgs(
                    "get_agent_version",
                    "Returns the current AI Assistant version. Takes no arguments."
                ),


                ToolNoArgs(
                    "list_allowed_roots",
                    "Returns the absolute filesystem roots that normal filesystem tools may access. Takes no arguments."
                ),


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "search_and_read_file",

                        description =
                            "Searches allowed roots for a filename. Reads it if small, otherwise returns path/size/line count. fileName must be a filename or pattern, not a full path.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                fileName = new
                                {
                                    type = "string"
                                }
                            },

                            required = new[]
                            {
                                "fileName"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "read_file",

                        description =
                            "Reads a small normal file using an ABSOLUTE allowed path.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                filePath = new
                                {
                                    type = "string"
                                }
                            },

                            required = new[]
                            {
                                "filePath"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "read_file_section",

                        description =
                            "Reads a limited line range from a normal file using an ABSOLUTE allowed path.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                filePath = new
                                {
                                    type = "string"
                                },

                                startLine = new
                                {
                                    type = "integer"
                                },

                                endLine = new
                                {
                                    type = "integer"
                                }
                            },

                            required = new[]
                            {
                                "filePath",
                                "startLine",
                                "endLine"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "create_folder",

                        description =
                            "Creates a folder using an ABSOLUTE path inside an allowed root.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                folderPath = new
                                {
                                    type = "string"
                                }
                            },

                            required = new[]
                            {
                                "folderPath"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "create_file",

                        description =
                            "Creates or overwrites a normal file using an ABSOLUTE allowed path. Cannot modify AI Assistant source.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                filePath = new
                                {
                                    type = "string"
                                },

                                content = new
                                {
                                    type = "string"
                                }
                            },

                            required = new[]
                            {
                                "filePath",
                                "content"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "list_files",

                        description =
                            "Lists files directly inside an ABSOLUTE allowed directory.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                folderPath = new
                                {
                                    type = "string"
                                }
                            },

                            required = new[]
                            {
                                "folderPath"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "list_directories",

                        description =
                            "Lists direct child directories of an ABSOLUTE allowed folder.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                folderPath = new
                                {
                                    type = "string"
                                }
                            },

                            required = new[]
                            {
                                "folderPath"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "find_file",

                        description =
                            "Recursively finds files inside one ABSOLUTE allowed root.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                rootPath = new
                                {
                                    type = "string"
                                },

                                fileName = new
                                {
                                    type = "string"
                                }
                            },

                            required = new[]
                            {
                                "rootPath",
                                "fileName"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "copy_file",

                        description =
                            "Copies a normal file between ABSOLUTE allowed paths. Cannot write into AI Assistant source.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                sourcePath = new
                                {
                                    type = "string"
                                },

                                destinationPath = new
                                {
                                    type = "string"
                                },

                                overwrite = new
                                {
                                    type = "boolean"
                                }
                            },

                            required = new[]
                            {
                                "sourcePath",
                                "destinationPath",
                                "overwrite"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "move_file",

                        description =
                            "Moves a normal file between ABSOLUTE allowed paths. Cannot write into AI Assistant source.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                sourcePath = new
                                {
                                    type = "string"
                                },

                                destinationPath = new
                                {
                                    type = "string"
                                },

                                overwrite = new
                                {
                                    type = "boolean"
                                }
                            },

                            required = new[]
                            {
                                "sourcePath",
                                "destinationPath",
                                "overwrite"
                            }
                        }
                    }
                },


                ToolNoArgs(
                    "inspect_self_structure",
                    "Returns AI Assistant C# source structure and current self-development state. Takes no arguments."
                ),


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "find_self_text",

                        description =
                            "Finds source text inside one AI Assistant file and returns matching line numbers plus a recommended read range. Use this to locate methods like ExecuteTool or BuildToolDefinitions.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                relativePath = new
                                {
                                    type = "string"
                                },

                                searchText = new
                                {
                                    type = "string"
                                }
                            },

                            required = new[]
                            {
                                "relativePath",
                                "searchText"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "read_self_file_section",

                        description =
                            "Reads a limited line range from an AI Assistant source file. relativePath is relative to the source root.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                relativePath = new
                                {
                                    type = "string"
                                },

                                startLine = new
                                {
                                    type = "integer"
                                },

                                endLine = new
                                {
                                    type = "integer"
                                }
                            },

                            required = new[]
                            {
                                "relativePath",
                                "startLine",
                                "endLine"
                            }
                        }
                    }
                },


                ToolNoArgs(
                    "backup_project",
                    "Creates a source backup. Must be called before self-modification. Takes no arguments."
                ),


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "write_self_file",

                        description =
                            "Creates or completely overwrites an AI Assistant source file. Use primarily for NEW files. Requires backup_project first.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                relativePath = new
                                {
                                    type = "string"
                                },

                                content = new
                                {
                                    type = "string"
                                }
                            },

                            required = new[]
                            {
                                "relativePath",
                                "content"
                            }
                        }
                    }
                },


                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            "replace_self_text",

                        description =
                            "Safely replaces one unique exact text block inside an existing AI Assistant source file. Prefer for targeted self-edits. Requires backup_project first.",

                        parameters = new
                        {
                            type = "object",

                            properties = new
                            {
                                relativePath = new
                                {
                                    type = "string"
                                },

                                oldText = new
                                {
                                    type = "string"
                                },

                                newText = new
                                {
                                    type = "string"
                                }
                            },

                            required = new[]
                            {
                                "relativePath",
                                "oldText",
                                "newText"
                            }
                        }
                    }
                },


                ToolNoArgs(
                    "build_self",
                    "Builds AI Assistant into a Release staging directory. Takes no arguments."
                ),


                ToolNoArgs(
                    "restart_self",
                    "Deploys and restarts the last successful build. Requires backup, source modification and an unchanged successful build. Takes no arguments."
                )
            };
        }


        // ============================================
        // SIMPLE NO-ARG TOOL FACTORY
        // ============================================

        private object ToolNoArgs(
            string name,
            string description
        )
        {
            return
                new
                {
                    type = "function",

                    function = new
                    {
                        name =
                            name,

                        description =
                            description,

                        parameters = new
                        {
                            type = "object",

                            properties =
                                new { }
                        }
                    }
                };
        }
    }
}