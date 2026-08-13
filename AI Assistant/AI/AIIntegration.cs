using AI_Assistant.AI;
using AI_Assistant.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class AIIntegration
{
    private readonly HttpClient client;

    private readonly FileSystemTools fileTools;
    private readonly SelfDevelopmentTools selfTools;

    private readonly List<ChatMessage> conversationHistory;


    private const string Model =
        "openai/gpt-oss-20b";


    private const int MaxIterations =
        20;


    private const int MaxToolResultChars =
        12000;


    private const int MaxChatHistoryMessages =
        6;


    // ============================================
    // CONSTRUCTOR
    // ============================================

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
                "Groq API key nije pronađen!";
        }


        conversationHistory.Add(
            new ChatMessage(
                "user",
                prompt
            )
        );


        // Sprečava glupe loopove tipa:
        // read Program.cs 10 puta.

        HashSet<string> executedToolCalls =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase
            );


        // ============================================
        // SYSTEM MESSAGE
        // ============================================

        List<object> messages =
            new List<object>
            {
                new
                {
                    role = "system",

                    content =
                    """
                    You are an efficient AI development agent.

                    Use the minimum number of tool calls required to complete a task.

                    FILESYSTEM RULES:

                    Prefer high-level tools.

                    Use search_and_read_file for small files when the location is unknown.

                    For large source files, do NOT request the entire file.
                    Use inspect_self_structure first when working on yourself,
                    then use read_file_section to inspect only relevant line ranges.

                    Do not read the same file repeatedly.

                    Do not call the same tool with identical arguments repeatedly.

                    Do not manually traverse folders if find_file or search_and_read_file can do the job.

                    Use list_allowed_roots only when needed.

                    If ACCESS DENIED is returned, do not retry the same path.

                    SELF-DEVELOPMENT RULES:

                    Only modify your own source when the user explicitly asks.

                    When modifying yourself:
                    1. Call backup_project once.
                    2. Call inspect_self_structure once.
                    3. Read only relevant source sections.
                    4. Modify only necessary files.
                    5. Call build_self.
                    6. If BUILD FAILED, use compiler errors to repair the relevant source.
                    7. Build again.
                    8. Never call restart_self after a failed build.
                    9. Restart only after BUILD SUCCESS.

                    Never repeatedly inspect the whole project.

                    When the user's task is finished, stop using tools and give a concise final answer.
                    """
                }
            };


        // ============================================
        // ONLY RECENT CHAT HISTORY
        // ============================================

        IEnumerable<ChatMessage> recentHistory =
            conversationHistory
                .TakeLast(
                    MaxChatHistoryMessages
                );


        messages.AddRange(
            recentHistory.Select(
                message => (object)new
                {
                    role =
                        message.Role,

                    content =
                        message.Message
                }
            )
        );


        int iteration = 0;


        // ============================================
        // AGENT LOOP
        // ============================================

        while (iteration < MaxIterations)
        {
            iteration++;


            var requestBody =
                new
                {
                    model = Model,

                    messages = messages,

                    tool_choice = "auto",

                    // Manje reasoning tokena.
                    reasoning_effort = "low",

                    tools = BuildToolDefinitions()
                };


            string json =
                JsonSerializer.Serialize(
                    requestBody
                );


            string url =
                "https://api.groq.com/openai/v1/chat/completions";


            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    apiKey
                );


            HttpResponseMessage response =
                await SendWithRetry(
                    url,
                    json
                );


            string responseText =
                await response.Content
                    .ReadAsStringAsync();


            if (!response.IsSuccessStatusCode)
            {
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


            // ========================================
            // TOOL CALLS
            // ========================================

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
                object assistantMessage =
                    JsonSerializer.Deserialize<object>(
                        message.GetRawText()
                    )!;


                messages.Add(
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


                    // =================================
                    // DUPLICATE TOOL PROTECTION
                    // =================================

                    string toolSignature =
                        functionName +
                        "|" +
                        argumentsJson;


                    string toolResult;


                    if (
                        executedToolCalls.Contains(
                            toolSignature
                        )
                    )
                    {
                        toolResult =
                            "DUPLICATE TOOL CALL BLOCKED. " +
                            "You already executed this exact tool with the same arguments. " +
                            "Use the existing result or choose another approach.";
                    }
                    else
                    {
                        executedToolCalls.Add(
                            toolSignature
                        );


                        toolResult =
                            ExecuteTool(
                                functionName,
                                argumentsJson
                            );
                    }


                    // =================================
                    // LIMIT TOOL RESULT SIZE
                    // =================================

                    toolResult =
                        TrimToolResult(
                            toolResult
                        );


                    messages.Add(
                        new
                        {
                            role = "tool",

                            tool_call_id =
                                toolCallId,

                            name =
                                functionName,

                            content =
                                toolResult
                        }
                    );
                }


                continue;
            }


            // ========================================
            // FINAL RESPONSE
            // ========================================

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
                    contentElement
                        .GetString()
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
                "Groq nije vratio ni finalni odgovor ni tool call.";
        }


        return
            $"Agent je dostigao maksimalan broj tool koraka ({MaxIterations}).";
    }


    // ============================================
    // SEND + RATE LIMIT RETRY
    // ============================================

    private async Task<HttpResponseMessage> SendWithRetry(
        string url,
        string json
    )
    {
        while (true)
        {
            using StringContent requestContent =
                new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"
                );


            HttpResponseMessage response =
                await client.PostAsync(
                    url,
                    requestContent
                );


            if (
                response.StatusCode !=
                HttpStatusCode.TooManyRequests
            )
            {
                return response;
            }


            double waitSeconds = 10;


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
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double parsed
                    )
                )
                {
                    waitSeconds =
                        parsed + 1;
                }
            }


            Console.WriteLine(
                $"[RATE LIMIT] Čekam {Math.Ceiling(waitSeconds)} sekundi..."
            );


            response.Dispose();


            await Task.Delay(
                TimeSpan.FromSeconds(
                    waitSeconds
                )
            );
        }
    }


    // ============================================
    // EXECUTE TOOL
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
                "inspect_self_structure"
            )
            {
                return
                    selfTools.InspectSelfStructure();
            }


            if (
                functionName ==
                "search_and_read_file"
            )
            {
                string fileName =
                    GetStringArg(
                        args,
                        "fileName"
                    );


                return
                    fileTools.SearchAndReadFile(
                        fileName
                    );
            }


            if (
                functionName ==
                "read_file_section"
            )
            {
                string filePath =
                    GetStringArg(
                        args,
                        "filePath"
                    );


                int startLine =
                    GetIntArg(
                        args,
                        "startLine"
                    );


                int endLine =
                    GetIntArg(
                        args,
                        "endLine"
                    );


                return
                    fileTools.ReadFileSection(
                        filePath,
                        startLine,
                        endLine
                    );
            }


            if (
                functionName ==
                "read_file"
            )
            {
                string fileName =
                    GetStringArg(
                        args,
                        "fileName"
                    );


                return
                    fileTools.ReadFile(
                        fileName
                    );
            }


            if (
                functionName ==
                "create_file"
            )
            {
                string fileName =
                    GetStringArg(
                        args,
                        "fileName"
                    );


                string fileContent =
                    GetStringArg(
                        args,
                        "content"
                    );


                return
                    fileTools.CreateFile(
                        fileName,
                        fileContent
                    );
            }


            if (
                functionName ==
                "create_folder"
            )
            {
                string folderName =
                    GetStringArg(
                        args,
                        "folderName"
                    );


                return
                    fileTools.CreateFolder(
                        folderName
                    );
            }


            if (
                functionName ==
                "list_files"
            )
            {
                string folderName =
                    GetStringArg(
                        args,
                        "folderName"
                    );


                return
                    fileTools.ListFiles(
                        folderName
                    );
            }


            if (
                functionName ==
                "list_directories"
            )
            {
                string folderPath =
                    GetStringArg(
                        args,
                        "folderPath"
                    );


                return
                    fileTools.ListDirectories(
                        folderPath
                    );
            }


            if (
                functionName ==
                "find_file"
            )
            {
                string rootPath =
                    GetStringArg(
                        args,
                        "rootPath"
                    );


                string fileName =
                    GetStringArg(
                        args,
                        "fileName"
                    );


                return
                    fileTools.FindFile(
                        rootPath,
                        fileName
                    );
            }


            if (
                functionName ==
                "copy_file"
            )
            {
                string source =
                    GetStringArg(
                        args,
                        "sourcePath"
                    );


                string destination =
                    GetStringArg(
                        args,
                        "destinationPath"
                    );


                bool overwrite =
                    GetBoolArg(
                        args,
                        "overwrite"
                    );


                return
                    fileTools.CopyFile(
                        source,
                        destination,
                        overwrite
                    );
            }


            if (
                functionName ==
                "move_file"
            )
            {
                string source =
                    GetStringArg(
                        args,
                        "sourcePath"
                    );


                string destination =
                    GetStringArg(
                        args,
                        "destinationPath"
                    );


                bool overwrite =
                    GetBoolArg(
                        args,
                        "overwrite"
                    );


                return
                    fileTools.MoveFile(
                        source,
                        destination,
                        overwrite
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
                $"Nepoznat tool: {functionName}";
        }


        catch (
            UnauthorizedAccessException ex
        )
        {
            return
                $"ACCESS DENIED: {ex.Message}";
        }


        catch (Exception ex)
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
    // TOOL DEFINITIONS
    // ============================================

    private object[] BuildToolDefinitions()
    {
        return new object[]
        {
            new
            {
                type = "function",

                function = new
                {
                    name =
                        "list_allowed_roots",

                    description =
                        "Returns filesystem roots the agent may access.",

                    parameters = new
                    {
                        type = "object",
                        properties = new { }
                    }
                }
            },


            new
            {
                type = "function",

                function = new
                {
                    name =
                        "inspect_self_structure",

                    description =
                        "Returns the AI Assistant source structure with C# file paths, line counts and sizes. Use this before inspecting your own source code.",

                    parameters = new
                    {
                        type = "object",
                        properties = new { }
                    }
                }
            },


            new
            {
                type = "function",

                function = new
                {
                    name =
                        "search_and_read_file",

                    description =
                        "Finds a file across allowed roots and reads it if small. Large files return metadata and should then be read using read_file_section.",

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
                        "read_file_section",

                    description =
                        "Reads only a specified line range from a file. Prefer this for large source files.",

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
                        "read_file",

                    description =
                        "Reads an entire text file. Avoid this for large source files.",

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
                        "create_file",

                    description =
                        "Creates or completely overwrites a text file.",

                    parameters = new
                    {
                        type = "object",

                        properties = new
                        {
                            fileName = new
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
                            "fileName",
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
                        "create_folder",

                    description =
                        "Creates a folder.",

                    parameters = new
                    {
                        type = "object",

                        properties = new
                        {
                            folderName = new
                            {
                                type = "string"
                            }
                        },

                        required = new[]
                        {
                            "folderName"
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
                        "Lists files directly inside a folder.",

                    parameters = new
                    {
                        type = "object",

                        properties = new
                        {
                            folderName = new
                            {
                                type = "string"
                            }
                        },

                        required = new[]
                        {
                            "folderName"
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
                        "Lists direct subdirectories.",

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
                        "Recursively searches one allowed root for a filename or pattern.",

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
                        "Copies a file between allowed paths.",

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
                        "Moves a file between allowed paths.",

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
                        "backup_project",

                    description =
                        "Creates a backup of the AI Assistant source before self-modification.",

                    parameters = new
                    {
                        type = "object",
                        properties = new { }
                    }
                }
            },


            new
            {
                type = "function",

                function = new
                {
                    name =
                        "build_self",

                    description =
                        "Builds AI Assistant into a staging folder. Must succeed before restart_self.",

                    parameters = new
                    {
                        type = "object",
                        properties = new { }
                    }
                }
            },


            new
            {
                type = "function",

                function = new
                {
                    name =
                        "restart_self",

                    description =
                        "Restarts AI Assistant using the successful staging build. Only use after BUILD SUCCESS.",

                    parameters = new
                    {
                        type = "object",
                        properties = new { }
                    }
                }
            }
        };
    }
}