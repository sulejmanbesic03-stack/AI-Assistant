using System;
using System.ComponentModel;
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
using AI_Assistant.Runtime;

namespace AI_Assistant.AI
{
    /// <summary>
    /// Small MCP client for the official Blender Lab server.
    /// The MCP server is launched through uvx and talks to Blender's addon on localhost:9876.
    /// Groq is used for Blender MCP with a direct Groq model fallback.
    /// </summary>
    public sealed class BlenderMcpAgent : IDisposable
    {
        private const string GroqEndpoint =
            "https://api.groq.com/openai/v1/chat/completions";

        private const string DefaultGroqModel =
            "qwen/qwen3.6-27b";

        private const string DefaultGroqFallbackModel =
            "openai/gpt-oss-120b";

        private const string OfficialMcpSource =
            "git+https://projects.blender.org/lab/blender_mcp.git@4309a39646e644261624bfcd2bca669b343b7621#subdirectory=mcp";

        private const int MaxToolCycles = 8;
        private const int RequestTimeoutSeconds = 120;
        private const int MaxToolResultChars = 4000;
        private const int DefaultGroqMaxCompletionTokens = 2200;
        private const int DefaultGroqFallbackMaxCompletionTokens = 2600;

        // The official server exposes useful inspection and documentation tools,
        // but sending every schema on every Groq turn needlessly consumes the
        // free-tier input budget. execute_blender_code remains the general
        // official escape hatch; the other entries are small inspection tools.
        private static readonly string[] ModelToolNames =
        {
            "execute_blender_code",
            "get_objects_summary",
            "get_object_detail_summary",
            "get_blendfile_summary_datablocks",
            "get_blendfile_summary_path_info"
        };

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
        private List<McpTool> modelTools = new List<McpTool>();

        public BlenderMcpAgent(Action<string> activity)
        {
            this.activity = activity;
        }

        public async Task<string> AskAsync(string prompt)
        {
            await gate.WaitAsync(AgentCancellationHub.Token);

            try
            {
                await EnsureConnectedAsync();

                string? apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return "GROQ_API_KEY nije pronađen.";
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
                            + "Keep reasoning concise, emit only the required tool arguments, and keep the final confirmation short. "
                            + "For large Blender edits, split execute_blender_code into several short calls. "
                            + "Every tool arguments value must be valid JSON with escaped newlines; never emit raw newlines "
                            + "inside a JSON string and never cut a code argument off mid-script."
                    },
                    new
                    {
                        role = "user",
                        content = prompt
                    }
                };

                for (int cycle = 0; cycle < MaxToolCycles; cycle++)
                {
                    using JsonDocument response = await SendWithGroqFallbackAsync(apiKey, model, messages);
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

                    // Preserve the complete provider message. In particular,
                    // reasoning-capable Groq models can attach fields beyond
                    // role/content/tool_calls; rebuilding the object by hand
                    // can make the next tool-loop request invalid.
                    messages.Add(message.Clone());

                    foreach (GroqToolCall call in calls)
                    {
                        string toolName = call.Function?.Name ?? "";
                        if (string.IsNullOrWhiteSpace(toolName))
                        {
                            throw new InvalidOperationException(
                                "Groq je vratio MCP poziv bez imena alata."
                            );
                        }

                        if (!tools.Any(tool => string.Equals(
                                tool.Name,
                                toolName,
                                StringComparison.Ordinal
                            )))
                        {
                            throw new InvalidOperationException(
                                "Groq je zatražio nepoznati Blender MCP alat: " + toolName
                            );
                        }

                        string arguments = NormalizeToolArguments(call.Function?.Arguments);
                        activity("[BLENDER MCP] " + toolName);

                        string toolResult = await CallMcpToolAsync(toolName, arguments);
                        messages.Add(new
                        {
                            role = "tool",
                            tool_call_id = call.Id,
                            name = toolName,
                            content = Trim(toolResult, MaxToolResultChars)
                        });
                    }
                }

                return "Blender agent je dostigao maksimalan broj MCP ciklusa.";
            }
            catch (OperationCanceledException) when (AgentCancellationHub.IsCancellationRequested)
            {
                activity("[BLENDER MCP] cancelled");
                return "Blender MCP zadatak je otkazan.";
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
            startInfo.ArgumentList.Add("mcp>=1.2,<2");
            startInfo.ArgumentList.Add("--from");
            startInfo.ArgumentList.Add(OfficialMcpSource);
            startInfo.ArgumentList.Add("blender-mcp");

            try
            {
                mcpProcess = Process.Start(startInfo)
                    ?? throw new InvalidOperationException(
                        "Nisam mogao pokrenuti Blender MCP server. Provjeri da je uvx instaliran ili postavi BLENDER_MCP_COMMAND."
                    );
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "Blender MCP nije pokrenut jer komanda nije pronađena: "
                    + command
                    + ". Dodaj uv u PATH ili postavi BLENDER_MCP_COMMAND na puni put do uvx.exe.",
                    ex
                );
            }

            mcpInput = mcpProcess.StandardInput;
            mcpOutput = mcpProcess.StandardOutput;
            _ = ReadErrorsAsync(mcpProcess.StandardError);

            _ = await SendRequestAsync(
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

            // tools/list is paginated by MCP. Most Blender installations fit
            // in one page, but ignoring nextCursor would silently hide tools
            // on a larger/future official server.
            List<McpTool> discoveredTools = new List<McpTool>();
            string? cursor = null;
            HashSet<string> seenCursors = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                if (!string.IsNullOrWhiteSpace(cursor) && !seenCursors.Add(cursor))
                {
                    throw new InvalidOperationException(
                        "Blender MCP tools/list je vratio ponovljeni pagination cursor."
                    );
                }

                object parameters;
                if (string.IsNullOrWhiteSpace(cursor))
                {
                    parameters = new { };
                }
                else
                {
                    parameters = new { cursor };
                }
                JsonElement toolsResult = await SendRequestAsync("tools/list", parameters);

                if (!toolsResult.TryGetProperty("tools", out JsonElement toolArray)
                    || toolArray.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException("Blender MCP nije vratio tools/list.");
                }

                List<McpTool> page = JsonSerializer.Deserialize<List<McpTool>>(
                    toolArray.GetRawText(),
                    JsonOptions
                ) ?? new List<McpTool>();
                discoveredTools.AddRange(page);

                cursor = toolsResult.TryGetProperty("nextCursor", out JsonElement nextCursor)
                    && nextCursor.ValueKind == JsonValueKind.String
                    ? nextCursor.GetString()
                    : null;
            }
            while (!string.IsNullOrWhiteSpace(cursor));

            tools = discoveredTools
                .Where(tool => !string.IsNullOrWhiteSpace(tool.Name))
                .GroupBy(tool => tool.Name, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();

            if (tools.Count == 0)
            {
                throw new InvalidOperationException(
                    "Blender MCP server je pokrenut, ali nije objavio nijedan alat."
                );
            }

            modelTools = ModelToolNames
                .Select(name => tools.FirstOrDefault(tool =>
                    string.Equals(tool.Name, name, StringComparison.Ordinal)))
                .Where(tool => tool != null)
                .Cast<McpTool>()
                .ToList();

            if (modelTools.Count == 0)
            {
                throw new InvalidOperationException(
                    "Blender MCP nije objavio očekivani execute_blender_code alat."
                );
            }

            initialized = true;
            activity(
                "[BLENDER MCP] connected · "
                + tools.Count
                + " official tools, "
                + modelTools.Count
                + " sent to Groq"
            );
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

        private async Task<JsonDocument> SendWithGroqFallbackAsync(
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
                catch (OperationCanceledException) when (AgentCancellationHub.IsCancellationRequested)
                {
                    throw;
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

            Exception failure = groqError
                ?? new InvalidOperationException("Groq provider nije vratio odgovor.");

            List<object> fallbackMessages = IsToolCallFormatFailure(failure)
                ? AddToolCallRepairHint(messages)
                : messages;

            string fallbackModel =
                Environment.GetEnvironmentVariable("GROQ_BLENDER_FALLBACK_MODEL")
                ?? Environment.GetEnvironmentVariable("GROQ_MODEL")
                ?? DefaultGroqFallbackModel;
            if (string.IsNullOrWhiteSpace(fallbackModel)
                || string.Equals(fallbackModel, groqModel, StringComparison.OrdinalIgnoreCase))
            {
                fallbackModel = DefaultGroqFallbackModel;
            }

            activity(
                "[BLENDER PROVIDER] Groq primary unavailable; using direct Groq fallback · "
                + fallbackModel
                + " ("
                + Trim(failure.Message, 300)
                + ")"
            );

            return await SendCompletionAsync(
                GroqEndpoint,
                groqApiKey ?? "",
                fallbackModel,
                fallbackMessages,
                ResolveMaxCompletionTokens(
                    "GROQ_BLENDER_FALLBACK_MAX_TOKENS",
                    DefaultGroqFallbackMaxCompletionTokens
                )
            );
        }

        private static bool IsToolCallFormatFailure(Exception failure)
        {
            string message = failure.ToString();
            return message.Contains("tool_use_failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("parse tool call arguments", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unexpected end of JSON", StringComparison.OrdinalIgnoreCase)
                || message.Contains("neispravan Blender MCP tool call", StringComparison.OrdinalIgnoreCase);
        }

        private static List<object> AddToolCallRepairHint(List<object> messages)
        {
            List<object> repaired = new List<object>(messages)
            {
                new
                {
                    role = "user",
                    content =
                        "The previous provider could not parse a tool call. Retry the Blender operation using "
                        + "short, valid MCP tool calls. If using execute_blender_code, keep the Python snippet small "
                        + "and ensure its JSON arguments are complete and correctly escaped. Do not send one giant script."
                }
            };
            return repaired;
        }

        private JsonElement ReadAssistantMessage(JsonDocument response)
        {
            JsonElement root = response.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out JsonElement message)
                || message.ValueKind != JsonValueKind.Object)
            {
                string details = root.TryGetProperty("error", out JsonElement error)
                    ? error.GetRawText()
                    : root.GetRawText();
                throw new InvalidOperationException(
                    "Provider response has no usable choices/message: "
                    + Trim(details, 1200)
                );
            }

            bool hasContent = message.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(content.GetString());
            bool hasToolCalls = message.TryGetProperty("tool_calls", out JsonElement toolCalls)
                && toolCalls.ValueKind == JsonValueKind.Array
                && toolCalls.GetArrayLength() > 0;
            if (!hasContent && !hasToolCalls)
            {
                throw new InvalidOperationException(
                    "Provider je vratio praznu assistant poruku bez teksta ili MCP poziva."
                );
            }

            if (hasToolCalls)
            {
                try
                {
                    List<GroqToolCall>? parsedCalls = JsonSerializer.Deserialize<List<GroqToolCall>>(
                        toolCalls.GetRawText(),
                        JsonOptions
                    );
                    if (parsedCalls == null || parsedCalls.Count == 0)
                    {
                        throw new InvalidOperationException("tool_calls niz je prazan.");
                    }

                    foreach (GroqToolCall call in parsedCalls)
                    {
                        if (string.IsNullOrWhiteSpace(call.Id))
                        {
                            throw new InvalidOperationException("MCP poziv nema id.");
                        }

                        if (call.Function == null || string.IsNullOrWhiteSpace(call.Function.Name))
                        {
                            throw new InvalidOperationException("MCP poziv nema ime alata.");
                        }

                        if (!modelTools.Any(tool => string.Equals(
                                tool.Name,
                                call.Function.Name,
                                StringComparison.Ordinal
                            )))
                        {
                            throw new InvalidOperationException(
                                "nepoznati Blender MCP alat: " + call.Function.Name
                            );
                        }

                        if (!string.IsNullOrWhiteSpace(call.Function.Arguments))
                        {
                            using JsonDocument arguments = JsonDocument.Parse(call.Function.Arguments);
                            if (arguments.RootElement.ValueKind != JsonValueKind.Object)
                            {
                                throw new InvalidOperationException("argumenti alata nisu JSON objekat.");
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException || ex is InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        "Provider je vratio neispravan Blender MCP tool call: " + ex.Message,
                        ex
                    );
                }
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
            List<object> groqTools = modelTools.Select(tool => new
            {
                type = "function",
                function = new
                {
                    name = tool.Name,
                    description = Trim(tool.Description ?? "Blender MCP tool", 300),
                    parameters = tool.InputSchema.ValueKind != JsonValueKind.Object
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
                ["parallel_tool_calls"] = false,
                ["temperature"] = 0.1,
                ["max_completion_tokens"] = maxCompletionTokens,
                ["reasoning_effort"] = model.StartsWith(
                    "qwen/",
                    StringComparison.OrdinalIgnoreCase
                ) ? "none" : "low"
            };

            using HttpRequestMessage request = new HttpRequestMessage(
                HttpMethod.Post,
                endpoint
            );
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"
            );

            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                AgentCancellationHub.Token
            );
            string text = await response.Content.ReadAsStringAsync(AgentCancellationHub.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "Provider HTTP " + (int)response.StatusCode + ": " + Trim(text, 2000)
                );
            }

            JsonDocument document = JsonDocument.Parse(text);
            // Validate the OpenAI-compatible envelope here so a malformed
            // Groq response can enter the direct Groq model fallback instead
            // of failing later with an opaque dictionary lookup exception.
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

        private static string NormalizeToolArguments(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return "{}";
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(arguments);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? document.RootElement.GetRawText()
                    : "{}";
            }
            catch
            {
                // Providers can truncate a tool call at the output boundary.
                // Never forward malformed arguments into another provider's
                // message history.
                return "{}";
            }
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
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                AgentCancellationHub.Token
            );

            while (true)
            {
                string? line = await mcpOutput.ReadLineAsync(linked.Token);
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

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // A server-side log line must not desynchronize the
                    // JSON-RPC reader. Official MCP keeps logs on stderr, but
                    // tolerating one stray stdout line makes recovery safer.
                    continue;
                }

                using (document)
                {
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

                    if (!root.TryGetProperty("result", out JsonElement result))
                    {
                        throw new InvalidOperationException(
                            "MCP " + method + " odgovor nema result polje."
                        );
                    }

                    return result.Clone();
                }
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
            modelTools = new List<McpTool>();

            try
            {
                mcpInput?.Dispose();
                mcpOutput?.Dispose();
            }
            catch
            {
            }

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
