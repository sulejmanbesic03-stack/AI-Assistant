using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Assistant.AI
{
    /// <summary>
    /// Small MCP client for the official Blender Lab server.
    /// The MCP server is launched through uvx and talks to Blender's addon on localhost:9876.
    /// Groq is the primary model provider; OpenRouter is used when Groq is unavailable.
    /// </summary>
    public sealed class BlenderMcpAgent : IDisposable
    {
        private const string GroqEndpoint =
            "https://api.groq.com/openai/v1/chat/completions";

        private const string OpenRouterEndpoint =
            "https://openrouter.ai/api/v1/chat/completions";

        private const string DefaultGroqModel =
            "qwen/qwen3.6-27b";

        private const string OfficialMcpSource =
            "git+https://projects.blender.org/lab/blender_mcp.git#subdirectory=mcp";

        private const int MaxToolCycles = 8;
        private const int RequestTimeoutSeconds = 120;
        private const int MaxToolResultChars = 4000;
        private const int DefaultGroqMaxCompletionTokens = 1200;
        private const int DefaultOpenRouterMaxCompletionTokens = 1000;

        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

        private readonly HttpClient httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };

        private readonly Action<string> activity;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        private Process? mcpProcess;
        private StreamWriter? mcpInput;
        private StreamReader? mcpOutput;
        private int rpcId;
        private bool initialized;
        private List<McpTool> tools = new List<McpTool>();

        public BlenderMcpAgent(Action<string> activity)
        {
            this.activity = activity;
        }

        public async Task<string> AskAsync(string prompt)
        {
            await gate.WaitAsync();

            try
            {
                await EnsureConnectedAsync();

                string? apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
                string? openRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
                if (string.IsNullOrWhiteSpace(apiKey)
                    && string.IsNullOrWhiteSpace(openRouterKey))
                {
                    return "Ni GROQ_API_KEY ni OPENROUTER_API_KEY nisu pronađeni.";
                }

                string model = Environment.GetEnvironmentVariable("GROQ_BLENDER_MODEL");
                if (string.IsNullOrWhiteSpace(model))
                {
                    model = DefaultGroqModel;
                }

                List<object> messages = new List<object>
                {
                    new
                    {
                        role = "system",
                        content =
                            "You are a Blender automation agent. Use the available Blender MCP tools directly. "
                            + "Inspect the current scene before changing it when needed. Prefer the highest-level "
                            + "registered tool. Use execute_blender_code only when no dedicated tool can do the job. "
                            + "Make the smallest reliable change, save when the user asks, and report exactly what happened. "
                            + "Keep reasoning concise, emit only the required tool arguments, and keep the final confirmation short."
                    },
                    new
                    {
                        role = "user",
                        content = prompt
                    }
                };

                for (int cycle = 0; cycle < MaxToolCycles; cycle++)
                {
                    JsonDocument response = await SendWithFallbackAsync(apiKey, model, messages);
                    JsonElement message = ReadAssistantMessage(response);

                    string? content = null;
                    if (message.TryGetProperty("content", out JsonElement contentElement)
                        && contentElement.ValueKind == JsonValueKind.String)
                    {
                        content = contentElement.GetString();
                    }

                    List<GroqToolCall> calls = new List<GroqToolCall>();
                    if (message.TryGetProperty("tool_calls", out JsonElement callsElement)
                        && callsElement.ValueKind == JsonValueKind.Array)
                    {
                        calls = JsonSerializer.Deserialize<List<GroqToolCall>>(
                            callsElement.GetRawText(),
                            JsonOptions
                        ) ?? new List<GroqToolCall>();
                    }

                    if (calls.Count == 0)
                    {
                        return string.IsNullOrWhiteSpace(content)
                            ? "Blender agent nije vratio tekstualni odgovor."
                            : content.Trim();
                    }

                    Dictionary<string, object?> assistantMessage =
                        new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = content,
                            ["tool_calls"] = calls.Select(call => new
                            {
                                id = call.Id,
                                type = "function",
                                function = new
                                {
                                    name = call.Function?.Name ?? "",
                                    arguments = call.Function?.Arguments ?? "{}"
                                }
                            }).ToList()
                        };

                    messages.Add(assistantMessage);

                    foreach (GroqToolCall call in calls)
                    {
                        string toolName = call.Function?.Name ?? "";
                        string arguments = call.Function?.Arguments ?? "{}";
                        activity("[BLENDER MCP] " + toolName);

                        string toolResult = await CallMcpToolAsync(toolName, arguments);
                        messages.Add(new
                        {
                            role = "tool",
                            tool_call_id = call.Id,
                            content = Trim(toolResult, MaxToolResultChars)
                        });
                    }
                }

                return "Blender agent je dostigao maksimalan broj MCP ciklusa.";
            }
            catch (Exception ex)
            {
                activity("[BLENDER MCP ERROR] " + ex.Message);
                return "Blender MCP greška: " + ex.Message;
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task EnsureConnectedAsync()
        {
            if (initialized && mcpProcess != null && !mcpProcess.HasExited)
            {
                return;
            }

            DisposeProcess();

            string command = Environment.GetEnvironmentVariable("BLENDER_MCP_COMMAND");
            if (string.IsNullOrWhiteSpace(command))
            {
                command = "uvx";
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            };

            // Blender Lab currently imports mcp.server.fastmcp. MCP 2.x renamed
            // that module, so constrain the uvx environment to the compatible line.
            startInfo.ArgumentList.Add("--with");
            startInfo.ArgumentList.Add("mcp<2");
            startInfo.ArgumentList.Add("--from");
            startInfo.ArgumentList.Add(OfficialMcpSource);
            startInfo.ArgumentList.Add("blender-mcp");

            mcpProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException(
                    "Nisam mogao pokrenuti Blender MCP server. Provjeri da je uvx instaliran ili postavi BLENDER_MCP_COMMAND."
                );

            mcpInput = mcpProcess.StandardInput;
            mcpOutput = mcpProcess.StandardOutput;
            _ = ReadErrorsAsync(mcpProcess.StandardError);

            JsonElement initializeResult = await SendRequestAsync(
                "initialize",
                new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new
                    {
                        name = "AI Assistant",
                        version = "1.0"
                    }
                }
            );

            await SendNotificationAsync("notifications/initialized", null);
            JsonElement toolsResult = await SendRequestAsync("tools/list", new { });

            if (!toolsResult.TryGetProperty("tools", out JsonElement toolArray)
                || toolArray.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Blender MCP nije vratio tools/list.");
            }

            tools = JsonSerializer.Deserialize<List<McpTool>>(
                toolArray.GetRawText(),
                JsonOptions
            ) ?? new List<McpTool>();

            if (tools.Count == 0)
            {
                throw new InvalidOperationException(
                    "Blender MCP server je pokrenut, ali nije objavio nijedan alat."
                );
            }

            initialized = true;
            activity("[BLENDER MCP] connected · " + tools.Count + " tools");
        }

        private async Task<string> CallMcpToolAsync(string name, string arguments)
        {
            JsonElement result = await SendRequestAsync(
                "tools/call",
                new
                {
                    name,
                    arguments = ParseObject(arguments)
                }
            );

            return result.GetRawText();
        }

        private async Task<JsonDocument> SendWithFallbackAsync(
            string? groqApiKey,
            string groqModel,
            List<object> messages
        )
        {
            Exception? groqError = null;

            if (!string.IsNullOrWhiteSpace(groqApiKey))
            {
                try
                {
                    activity("[BLENDER PROVIDER] trying Groq · " + groqModel);
                    return await SendCompletionAsync(
                        GroqEndpoint,
                        groqApiKey ?? "",
                        groqModel,
                        messages,
                        ResolveMaxCompletionTokens(
                            "GROQ_BLENDER_MAX_TOKENS",
                            DefaultGroqMaxCompletionTokens
                        )
                    );
                }
                catch (OperationCanceledException ex)
                {
                    groqError = new InvalidOperationException(
                        "Groq provider timeout/abort: " + ex.Message,
                        ex
                    );
                }
                catch (Exception ex)
                {
                    groqError = ex;
                }
            }
            else
            {
                groqError = new InvalidOperationException("GROQ_API_KEY nije konfigurisan.");
            }

            string openRouterApiKey =
                Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
            if (string.IsNullOrWhiteSpace(openRouterApiKey))
            {
                throw groqError
                    ?? new InvalidOperationException("Groq provider nije vratio grešku.");
            }

            Exception failure = groqError
                ?? new InvalidOperationException("Groq provider nije vratio odgovor.");

            string? openRouterModel =
                Environment.GetEnvironmentVariable("OPENROUTER_BLENDER_MODEL");
            if (string.IsNullOrWhiteSpace(openRouterModel))
            {
                // Do not inherit OPENROUTER_MODEL here. That variable may point
                // to a large reasoning model used by the general agent and can
                // exceed the free provider's token budget for Blender schemas.
                openRouterModel = "openrouter/free";
            }

            activity(
                "[BLENDER PROVIDER] Groq unavailable; using OpenRouter fallback · "
                + openRouterModel
                + " ("
                + Trim(failure.Message, 300)
                + ")"
            );

            return await SendCompletionAsync(
                OpenRouterEndpoint,
                openRouterApiKey,
                openRouterModel,
                messages,
                ResolveMaxCompletionTokens(
                    "OPENROUTER_BLENDER_MAX_TOKENS",
                    DefaultOpenRouterMaxCompletionTokens
                )
            );
        }

        private static JsonElement ReadAssistantMessage(JsonDocument response)
        {
            JsonElement root = response.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out JsonElement message))
            {
                string details = root.TryGetProperty("error", out JsonElement error)
                    ? error.GetRawText()
                    : root.GetRawText();
                throw new InvalidOperationException(
                    "Provider response has no usable choices/message: "
                    + Trim(details, 1200)
                );
            }

            return message;
        }

        private async Task<JsonDocument> SendCompletionAsync(
            string endpoint,
            string apiKey,
            string model,
            List<object> messages,
            int maxCompletionTokens
        )
        {
            List<object> groqTools = tools.Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = Trim(tool.Description ?? "Blender MCP tool", 300),
                    parameters = tool.InputSchema.ValueKind == JsonValueKind.Undefined
                        ? JsonSerializer.SerializeToElement(new { type = "object" })
                        : CompactSchema(tool.InputSchema)
                }
            }).Cast<object>().ToList();

            Dictionary<string, object?> body = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["messages"] = messages,
                ["tools"] = groqTools,
                ["tool_choice"] = "auto",
                ["temperature"] = 0.1,
                ["max_tokens"] = maxCompletionTokens
            };

            using HttpRequestMessage request = new HttpRequestMessage(
                HttpMethod.Post,
                endpoint
            );
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            if (endpoint == OpenRouterEndpoint)
            {
                request.Headers.TryAddWithoutValidation(
                    "HTTP-Referer",
                    "https://github.com/sulejmanbesic03-stack/AI-Assistant"
                );
                request.Headers.TryAddWithoutValidation(
                    "X-Title",
                    "AI Assistant Blender MCP"
                );
            }
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"
            );

            using HttpResponseMessage response = await httpClient.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "Provider HTTP " + (int)response.StatusCode + ": " + Trim(text, 2000)
                );
            }

            JsonDocument document = JsonDocument.Parse(text);
            // Validate the OpenAI-compatible envelope here so a malformed
            // Groq response can enter the OpenRouter fallback path instead of
            // failing later with an opaque dictionary lookup exception.
            try
            {
                _ = ReadAssistantMessage(document);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }

        private static int ResolveMaxCompletionTokens(string variableName, int defaultValue)
        {
            string? configured = Environment.GetEnvironmentVariable(variableName);
            if (int.TryParse(configured, out int value))
            {
                return Math.Clamp(value, 512, 3000);
            }

            return defaultValue;
        }

        private static JsonElement CompactSchema(JsonElement schema)
        {
            return JsonSerializer.SerializeToElement(CompactSchemaValue(schema));
        }

        private static object? CompactSchemaValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<string, object?> compact = new Dictionary<string, object?>();
                    foreach (JsonProperty property in value.EnumerateObject())
                    {
                        if (property.NameEquals("description")
                            || property.NameEquals("title")
                            || property.NameEquals("$schema")
                            || property.NameEquals("examples"))
                        {
                            continue;
                        }

                        compact[property.Name] = CompactSchemaValue(property.Value);
                    }
                    return compact;

                case JsonValueKind.Array:
                    return value.EnumerateArray()
                        .Select(CompactSchemaValue)
                        .ToList();

                case JsonValueKind.String:
                    return value.GetString();

                case JsonValueKind.Number:
                    return value.TryGetInt64(out long integer)
                        ? integer
                        : value.GetDouble();

                case JsonValueKind.True:
                    return true;

                case JsonValueKind.False:
                    return false;

                default:
                    return null;
            }
        }

        private async Task<JsonElement> SendRequestAsync(string method, object? parameters)
        {
            if (mcpInput == null || mcpOutput == null)
            {
                throw new InvalidOperationException("Blender MCP stdio nije otvoren.");
            }

            int id = Interlocked.Increment(ref rpcId);
            Dictionary<string, object?> request = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            };

            await mcpInput.WriteLineAsync(JsonSerializer.Serialize(request));
            await mcpInput.FlushAsync();

            using CancellationTokenSource timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(RequestTimeoutSeconds)
            );

            while (true)
            {
                string? line = await mcpOutput.ReadLineAsync(timeout.Token);
                if (line == null)
                {
                    throw new InvalidOperationException(
                        "Blender MCP server je zatvorio stdio kanal."
                    );
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;

                if (!root.TryGetProperty("id", out JsonElement responseId)
                    || responseId.ValueKind != JsonValueKind.Number
                    || responseId.GetInt32() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out JsonElement error))
                {
                    throw new InvalidOperationException("MCP " + method + ": " + error.GetRawText());
                }

                return root.GetProperty("result").Clone();
            }
        }

        private async Task SendNotificationAsync(string method, object? parameters)
        {
            if (mcpInput == null)
            {
                throw new InvalidOperationException("Blender MCP stdio nije otvoren.");
            }

            Dictionary<string, object?> notification = new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method
            };

            if (parameters != null)
            {
                notification["params"] = parameters;
            }

            await mcpInput.WriteLineAsync(JsonSerializer.Serialize(notification));
            await mcpInput.FlushAsync();
        }

        private async Task ReadErrorsAsync(StreamReader reader)
        {
            try
            {
                while (true)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null)
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        activity("[BLENDER MCP SERVER] " + Trim(line, 1000));
                    }
                }
            }
            catch
            {
            }
        }

        private static JsonElement ParseObject(string json)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(json) ? "{}" : json
                );
                return document.RootElement.Clone();
            }
            catch
            {
                return JsonSerializer.SerializeToElement(new { });
            }
        }

        private static string Trim(string value, int max)
        {
            return value.Length <= max
                ? value
                : value.Substring(0, max) + "…";
        }

        public void Reset()
        {
            gate.Wait();
            try
            {
                DisposeProcess();
            }
            finally
            {
                gate.Release();
            }
        }

        public void Dispose()
        {
            gate.Wait();
            try
            {
                DisposeProcess();
                httpClient.Dispose();
            }
            finally
            {
                gate.Release();
                gate.Dispose();
            }
        }

        private void DisposeProcess()
        {
            initialized = false;
            tools = new List<McpTool>();
            mcpInput = null;
            mcpOutput = null;

            if (mcpProcess == null)
            {
                return;
            }

            try
            {
                if (!mcpProcess.HasExited)
                {
                    mcpProcess.Kill(true);
                }
            }
            catch
            {
            }

            mcpProcess.Dispose();
            mcpProcess = null;
        }

        private sealed class McpTool
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("description")]
            public string? Description { get; set; }

            [JsonPropertyName("inputSchema")]
            public JsonElement InputSchema { get; set; }
        }

        private sealed class GroqToolCall
        {
            [JsonPropertyName("id")]
            public string Id { get; set; } = "";

            [JsonPropertyName("function")]
            public GroqFunction? Function { get; set; }
        }

        private sealed class GroqFunction
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "";

            [JsonPropertyName("arguments")]
            public string Arguments { get; set; } = "{}";
        }
    }
}
