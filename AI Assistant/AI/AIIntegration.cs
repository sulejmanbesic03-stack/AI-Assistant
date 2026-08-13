using AI_Assistant.AI;
using AI_Assistant.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class AIIntegration
{
    private readonly HttpClient client;
    private readonly FileSystemTools fileTools;

    private readonly List<ChatMessage> conversationHistory;

    private const string Model =
        "openai/gpt-oss-20b";


    public AIIntegration(List<string> allowedRoots)
    {
        client = new HttpClient();

        fileTools =
            new FileSystemTools(allowedRoots);

        conversationHistory =
            new List<ChatMessage>();
    }


    public async Task<string> Ask(string prompt)
    {
        string? apiKey =
            Environment.GetEnvironmentVariable(
                "GROQ_API_KEY"
            );


        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "Groq API key nije pronađen!";
        }


        // ============================================
        // USER PORUKA U CHAT HISTORY
        // ============================================

        conversationHistory.Add(
            new ChatMessage(
                "user",
                prompt
            )
        );


        // ============================================
        // PRETVORI HISTORY U GROQ MESSAGES
        // ============================================

        List<object> messages =
            conversationHistory
                .Select(message => (object)new
                {
                    role = message.Role,
                    content = message.Message
                })
                .ToList();


        // ============================================
        // AGENT LOOP
        // ============================================

        int iteration = 0;
        const int maxIterations = 25;


        while (iteration < maxIterations)
        {
            iteration++;


            // ========================================
            // REQUEST BODY
            // ========================================

            var requestBody = new
            {
                model = Model,

                messages = messages,

                tools = new object[]
                {new
                            {
                                type = "function",

                                function = new
                                {
                                    name = "find_file",

                                    description =
                                        "Searches recursively for a file inside an allowed directory.",

                                    parameters = new
                                    {
                                        type = "object",

                                        properties = new
                                        {
                                            rootPath = new
                                            {
                                                type = "string",

                                                description =
                                                    "Allowed root directory where the search should begin."
                                            },

                                            fileName = new
                                            {
                                                type = "string",

                                                description =
                                                    "File name or search pattern, for example Player.cs or *.fbx."
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
                                    name = "move_file",

                                    description =
                                        "Moves a file from one allowed path to another allowed path.",

                                    parameters = new
                                    {
                                        type = "object",

                                        properties = new
                                        {
                                            sourcePath = new
                                            {
                                                type = "string",

                                                description =
                                                    "Full path of the source file."
                                            },

                                            destinationPath = new
                                            {
                                                type = "string",

                                                description =
                                                    "Full destination path including file name."
                                            },

                                            overwrite = new
                                            {
                                                type = "boolean",

                                                description =
                                                    "Whether an existing destination file may be overwritten."
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
                            }, // =================================
                                                // CREATE FOLDER
                                               new
                            {
                                type = "function",

                                function = new
                                {
                                    name = "copy_file",

                                    description =
                                        "Copies a file from one allowed path to another allowed path.",

                                    parameters = new
                                    {
                                        type = "object",

                                        properties = new
                                        {
                                            sourcePath = new
                                            {
                                                type = "string",

                                                description =
                                                    "Full path of the source file."
                                            },

                                            destinationPath = new
                                            {
                                                type = "string",

                                                description =
                                                    "Full destination path including file name."
                                            },

                                            overwrite = new
                                            {
                                                type = "boolean",

                                                description =
                                                    "Whether an existing destination file may be overwritten."
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
                            }, // =================================
                    new
                        {
                            type = "function",

                            function = new
                            {
                                name = "list_directories",

                                description =
                                    "Lists subdirectories inside an allowed folder.",

                                parameters = new
                                {
                                    type = "object",

                                    properties = new
                                    {
                                        folderPath = new
                                        {
                                            type = "string",

                                            description =
                                                "Full path of the allowed folder."
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
                            name = "create_folder",

                            description =
                                "Creates a folder inside the allowed workspace.",

                            parameters = new
                            {
                                type = "object",

                                properties = new
                                {
                                    folderName = new
                                    {
                                        type = "string",

                                        description =
                                            "Relative path of the folder to create inside the workspace."
                                    }
                                },

                                required = new[]
                                {
                                    "folderName"
                                }
                            }
                        }
                    },


                    // =================================
                    // CREATE FILE
                    // =================================

                    new
                    {
                        type = "function",

                        function = new
                        {
                            name = "create_file",

                            description =
                                "Creates or overwrites a file inside the allowed workspace and writes text content into it.",

                            parameters = new
                            {
                                type = "object",

                                properties = new
                                {
                                    fileName = new
                                    {
                                        type = "string",

                                        description =
                                            "Relative path and file name inside the workspace."
                                    },

                                    content = new
                                    {
                                        type = "string",

                                        description =
                                            "Text content that should be written into the file."
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


                    // =================================
                    // READ FILE
                    // =================================

                    new
                    {
                        type = "function",

                        function = new
                        {
                            name = "read_file",

                            description =
                                "Reads the text contents of a file inside the allowed workspace.",

                            parameters = new
                            {
                                type = "object",

                                properties = new
                                {
                                    fileName = new
                                    {
                                        type = "string",

                                        description =
                                            "Relative path of the file to read."
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
                                name = "list_allowed_roots",

                                description =
                                    "Returns all filesystem root directories that the agent is allowed to access.",

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
                            name = "list_files",

                            description =
                                "Lists files inside a folder in the allowed workspace.",

                            parameters = new
                            {
                                type = "object",

                                properties = new
                                {
                                    folderName = new
                                    {
                                        type = "string",

                                        description =
                                            "Relative folder path. Use an empty string for the workspace root."
                                    }
                                },

                                required = new[]
                                {
                                    "folderName"
                                }
                            }
                        }
                    }
                },

                tool_choice = "auto"
            };


            string json =
                JsonSerializer.Serialize(
                    requestBody
                );


            using StringContent content =
                new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"
                );


            // ========================================
            // AUTHORIZATION
            // ========================================

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    apiKey
                );


            string url =
                "https://api.groq.com/openai/v1/chat/completions";


            // ========================================
            // SEND REQUEST
            // ========================================

            HttpResponseMessage response =
                await client.PostAsync(
                    url,
                    content
                );


            string responseText =
                await response.Content
                    .ReadAsStringAsync();


            if (!response.IsSuccessStatusCode)
            {
                return
                    $"Groq API greška:\n{responseText}";
            }


            // ========================================
            // PARSE RESPONSE
            // ========================================

            using JsonDocument document =
                JsonDocument.Parse(
                    responseText
                );


            JsonElement message =
                document
                    .RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message");


            // ========================================
            // TOOL CALLS?
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
                // ====================================
                // MORAMO SAČUVATI ASSISTANT TOOL CALL
                // ====================================

                object assistantMessage =
                    JsonSerializer.Deserialize<object>(
                        message.GetRawText()
                    )!;


                messages.Add(
                    assistantMessage
                );


                // ====================================
                // OBRADI SVE TOOL CALLOVE
                // ====================================

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


                    using JsonDocument argsDocument =
                        JsonDocument.Parse(
                            argumentsJson
                        );


                    JsonElement args =
                        argsDocument.RootElement;


                    string toolResult;


                    // =================================
                    // CREATE FOLDER
                    // =================================
            

                    try
                    {
                        if (functionName == "create_folder")
                        {
                            string folderPath =
                                args
                                    .GetProperty("folderName")
                                    .GetString()
                                ?? "";

                            toolResult =
                                fileTools.CreateFolder(
                                    folderPath
                                );
                        }

                        else if (functionName == "create_file")
                        {
                            string filePath =
                                args
                                    .GetProperty("fileName")
                                    .GetString()
                                ?? "";

                            string fileContent =
                                args
                                    .GetProperty("content")
                                    .GetString()
                                ?? "";

                            toolResult =
                                fileTools.CreateFile(
                                    filePath,
                                    fileContent
                                );
                        }

                        else if (functionName == "read_file")
                        {
                            string filePath =
                                args
                                    .GetProperty("fileName")
                                    .GetString()
                                ?? "";

                            toolResult =
                                fileTools.ReadFile(
                                    filePath
                                );
                        }

                        else if (functionName == "list_files")
                        {
                            string folderPath =
                                args
                                    .GetProperty("folderName")
                                    .GetString()
                                ?? "";

                            toolResult =
                                fileTools.ListFiles(
                                    folderPath
                                );
                        }

                        else if (functionName == "list_directories")
                        {
                            string folderPath =
                                args
                                    .GetProperty("folderPath")
                                    .GetString()
                                ?? "";

                            toolResult =
                                fileTools.ListDirectories(
                                    folderPath
                                );
                        }

                        else if (functionName == "find_file")
                        {
                            string rootPath =
                                args
                                    .GetProperty("rootPath")
                                    .GetString()
                                ?? "";

                            string fileName =
                                args
                                    .GetProperty("fileName")
                                    .GetString()
                                ?? "";

                            toolResult =
                                fileTools.FindFile(
                                    rootPath,
                                    fileName
                                );
                        }

                        else if (functionName == "copy_file")
                        {
                            string sourcePath =
                                args
                                    .GetProperty("sourcePath")
                                    .GetString()
                                ?? "";

                            string destinationPath =
                                args
                                    .GetProperty("destinationPath")
                                    .GetString()
                                ?? "";

                            bool overwrite =
                                args
                                    .GetProperty("overwrite")
                                    .GetBoolean();

                            toolResult =
                                fileTools.CopyFile(
                                    sourcePath,
                                    destinationPath,
                                    overwrite
                                );
                        }

                        else if (functionName == "move_file")
                        {
                            string sourcePath =
                                args
                                    .GetProperty("sourcePath")
                                    .GetString()
                                ?? "";

                            string destinationPath =
                                args
                                    .GetProperty("destinationPath")
                                    .GetString()
                                ?? "";

                            bool overwrite =
                                args
                                    .GetProperty("overwrite")
                                    .GetBoolean();

                            toolResult =
                                fileTools.MoveFile(
                                    sourcePath,
                                    destinationPath,
                                    overwrite
                                );
                        }

                        else if (functionName == "list_allowed_roots")
                        {
                            toolResult =
                                fileTools.ListAllowedRoots();
                        }

                        else
                        {
                            toolResult =
                                $"Nepoznat tool: {functionName}";
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        toolResult =
                            $"ACCESS DENIED: {ex.Message}";
                    }
                    catch (Exception ex)
                    {
                        toolResult =
                            $"TOOL ERROR: {ex.Message}";
                    }

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


                // Nakon svih toolova,
                // while ponovo šalje Groq-u.
                continue;
            }


            // ========================================
            // NORMALNI FINALNI ODGOVOR
            // ========================================

            if (
                message.TryGetProperty(
                    "content",
                    out JsonElement contentElement
                )
                &&
                contentElement.ValueKind
                    != JsonValueKind.Null
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
                "Groq nije vratio ni odgovor ni tool call.";
        }


        return
            "Agent je dostigao maksimalan broj tool koraka.";
    }
}