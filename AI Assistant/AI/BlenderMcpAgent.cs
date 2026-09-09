using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
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
    /// Blender MCP uses Groq first, then configured direct provider fallbacks.
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

        private const int MaxToolCycles = 10;
        private const int DefaultProviderRequestTimeoutSeconds = 180;
        private const int DefaultMcpRequestTimeoutSeconds = 240;
        private const int DefaultVisionRequestTimeoutSeconds = 90;
        private const int MaxProviderAttempts = 2;
        private const int MaxToolResultChars = 4000;
        private const int DefaultGroqMaxCompletionTokens = 2200;
        private const int DefaultGroqFallbackMaxCompletionTokens = 2600;
        private const string ViewportScreenshotToolName = "get_viewport_screenshot";
        private const int MaxViewportReviews = 3;

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
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
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

        public async Task<string> AskAsync(
            string prompt,
            string? referenceImagePath = null,
            bool requireViewportReview = false
        )
        {
            await gate.WaitAsync(AgentCancellationHub.Token);

            try
            {
                await EnsureConnectedAsync();

                string? apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
                bool hasMiniMax = !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("MINIMAX_API_KEY")
                );
                bool hasOpenRouter = !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                );
                bool hasInclusionAi = !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("INCLUSIONAI_API_KEY")
                ) && !string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable("INCLUSIONAI_BASE_URL")
                );
                if (string.IsNullOrWhiteSpace(apiKey) && !hasOpenRouter && !hasMiniMax && !hasInclusionAi)
                {
                    return "Nijedan Blender LLM provider nije konfigurisan. Postavi GROQ_API_KEY, OPENROUTER_API_KEY, MINIMAX_API_KEY ili INCLUSIONAI_API_KEY + INCLUSIONAI_BASE_URL.";
                }

                string model = Environment.GetEnvironmentVariable("GROQ_BLENDER_MODEL") ?? "";
                if (string.IsNullOrWhiteSpace(model))
                {
                    model = DefaultGroqModel;
                }

                bool hasReferenceImage = HasUsableImage(referenceImagePath);
                bool visualReviewRequired = requireViewportReview || hasReferenceImage;
                int viewportReviewCount = 0;
                bool mutationSinceReview = false;
                bool viewportReviewCompleted = false;
                bool viewportReviewPassed = false;

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
                            + "When viewport review is required, a separate vision reviewer compares the internal reference "
                            + "when available, or the original request otherwise, with the real Blender viewport after geometry mutations. Use that review feedback to repair visible "
                            + "mismatches before continuing. Never claim that "
                            + "an asset is complete from text or object names alone, and never export before the visual "
                            + "review passes. "
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
                        if (visualReviewRequired
                            && (!viewportReviewCompleted || !viewportReviewPassed))
                        {
                            return "Blender agent nije završio obaveznu vizuelnu provjeru viewporta; export nije dozvoljen.";
                        }

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

                        if (visualReviewRequired
                            && !viewportReviewPassed
                            && ContainsExportOperation(toolName, arguments))
                        {
                            activity("[BLENDER REVIEW] export blocked until viewport passes");
                            messages.Add(new
                            {
                                role = "tool",
                                tool_call_id = call.Id,
                                name = toolName,
                                content = "{\"error\":\"Export is blocked until the real Blender viewport review returns pass=true. Finish modeling first; do not export in this call.\"}"
                            });
                            continue;
                        }

                        string toolResult = await CallMcpToolAsync(toolName, arguments);
                        messages.Add(new
                        {
                            role = "tool",
                            tool_call_id = call.Id,
                            name = toolName,
                            content = Trim(toolResult, MaxToolResultChars)
                        });

                        if (IsMutationTool(toolName))
                        {
                            mutationSinceReview = true;
                            viewportReviewCompleted = false;
                        }
                    }

                    if (visualReviewRequired
                        && mutationSinceReview
                        && viewportReviewCount < MaxViewportReviews)
                    {
                        ViewportReviewResult? review = await AttachViewportReviewAsync(
                            messages,
                            prompt,
                            referenceImagePath,
                            ++viewportReviewCount
                        );
                        mutationSinceReview = false;
                        viewportReviewCompleted = review?.Succeeded == true;
                        viewportReviewPassed = review?.Passed == true;

                        if (review == null || !review.Succeeded)
                        {
                            return "Blender agent nije mogao dobiti stvarni viewport screenshot; export nije dozvoljen.";
                        }
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

            string command = Environment.GetEnvironmentVariable("BLENDER_MCP_COMMAND") ?? "";
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
                + (tools.Any(tool => string.Equals(
                        tool.Name,
                        ViewportScreenshotToolName,
                        StringComparison.Ordinal))
                    ? " · viewport screenshot available"
                    : " · viewport screenshot unavailable")
            );
        }

        private async Task<ViewportReviewResult?> AttachViewportReviewAsync(
            List<object> messages,
            string prompt,
            string? referenceImagePath,
            int reviewNumber
        )
        {
            if (!tools.Any(tool => string.Equals(
                    tool.Name,
                    ViewportScreenshotToolName,
                    StringComparison.Ordinal)))
            {
                activity("[BLENDER REVIEW] get_viewport_screenshot nije objavljen od MCP servera");
                return null;
            }

            try
            {
                activity("[BLENDER REVIEW] capturing viewport · review " + reviewNumber);
                string rawResult = await CallMcpToolAsync(
                    ViewportScreenshotToolName,
                    "{}"
                );
                McpImagePayload? image = FindMcpImage(rawResult);
                if (image == null || string.IsNullOrWhiteSpace(image.Data))
                {
                    activity("[BLENDER REVIEW] MCP nije vratio image content");
                    return null;
                }

                ViewportReviewResult review = await SendVisionReviewAsync(
                    prompt,
                    referenceImagePath,
                    image,
                    reviewNumber
                );
                messages.Add(new
                {
                    role = "user",
                    content =
                        "VISUAL REVIEW #"
                        + reviewNumber
                        + " from the real Blender viewport: "
                        + review.Feedback
                        + (review.Passed
                            ? " The visual target passes; continue only with remaining required steps."
                            : " The visual target does not pass. Repair the listed issues with execute_blender_code, then wait for another viewport review. Do not export yet.")
                });
                activity(
                    "[BLENDER REVIEW] "
                    + (review.Passed ? "passed" : "mismatch found")
                    + " · vision feedback returned"
                );
                return review;
            }
            catch (Exception ex)
            {
                activity("[BLENDER REVIEW] screenshot failed · " + Trim(ex.Message, 500));
                return null;
            }
        }

        private static bool IsMutationTool(string toolName)
        {
            // The model-facing allow-list currently contains execute_blender_code
            // plus read-only summaries. Keep this explicit so a future read tool
            // cannot trigger unnecessary screenshots.
            return string.Equals(
                toolName,
                "execute_blender_code",
                StringComparison.Ordinal
            );
        }

        private static bool ContainsExportOperation(string toolName, string arguments)
        {
            if (!string.Equals(toolName, "execute_blender_code", StringComparison.Ordinal))
            {
                return false;
            }

            string code = (arguments ?? "").ToLowerInvariant();
            string[] exportOperations =
            {
                "export_scene.",
                "export_mesh.",
                "wm.fbx_export",
                "wm.obj_export",
                "wm.ply_export",
                "wm.stl_export",
                "wm.usd_export",
                "wm.alembic_export",
                "wm.collada_export"
            };
            return exportOperations.Any(operation =>
                code.Contains(operation, StringComparison.Ordinal));
        }

        private static bool HasUsableImage(string? path)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(path)
                    && File.Exists(path)
                    && new FileInfo(path).Length >= 64;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private async Task<ViewportReviewResult> SendVisionReviewAsync(
            string prompt,
            string? referenceImagePath,
            McpImagePayload viewportImage,
            int reviewNumber
        )
        {
            string visionModel = Environment.GetEnvironmentVariable(
                "GROQ_BLENDER_VISION_MODEL"
            ) ?? "";
            if (string.IsNullOrWhiteSpace(visionModel))
            {
                visionModel = DefaultGroqModel;
            }

            bool hasReference = TryReadImageBase64(referenceImagePath, out string? referenceBase64);
            string comparisonInstruction = hasReference
                ? "Compare the design reference image (first image) with the real current Blender viewport (last image)."
                : "There is no design reference image; judge the real current Blender viewport directly against the original request.";

            List<object> imageParts = new List<object>
            {
                new
                {
                    type = "text",
                    text =
                        "You are the strict visual QA reviewer for a Blender asset. "
                        + comparisonInstruction
                        + " Original request: "
                        + prompt
                        + ". Check whether the requested subject is actually present, whether all essential parts "
                        + "exist, and whether the silhouette and proportions are usable as a game asset. Ignore UI, "
                        + "grid and camera framing. Return ONLY a compact JSON object with this exact shape: "
                        + "{\"pass\":true_or_false,\"issues\":[\"short issue\"],\"repair\":\"short concrete repair instruction\"}. "
                        + "Set pass=false if the object is merely a placeholder, missing a major part, or visibly "
                        + "does not match the requested subject. This is visual review #"
                        + reviewNumber
                        + "."
                }
            };

            if (hasReference)
            {
                imageParts.Add(new
                {
                    type = "image_url",
                    image_url = new
                    {
                        url = "data:image/png;base64," + referenceBase64
                    }
                });
            }

            imageParts.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = "data:"
                        + (string.IsNullOrWhiteSpace(viewportImage.MimeType)
                            ? "image/png"
                            : viewportImage.MimeType)
                        + ";base64,"
                        + viewportImage.Data
                }
            });

            Dictionary<string, object?> body = new Dictionary<string, object?>
            {
                ["messages"] = new object[]
                {
                    new
                    {
                        role = "system",
                        content = comparisonInstruction + " Never infer missing geometry from the text report."
                    },
                    new
                    {
                        role = "user",
                        content = imageParts.ToArray()
                    }
                },
                ["temperature"] = 0.0
            };

            List<CompletionProvider> providers = BuildVisionProviders(visionModel);
            if (providers.Count == 0)
            {
                throw new InvalidOperationException(
                    "Nijedan vision provider nije konfigurisan. Postavi GROQ_API_KEY ili OPENROUTER_API_KEY; za MiniMax postavi MINIMAX_API_KEY."
                );
            }

            Exception? lastFailure = null;
            foreach (CompletionProvider provider in providers)
            {
                for (int attempt = 1; attempt <= MaxProviderAttempts; attempt++)
                {
                    try
                    {
                        body["model"] = provider.Model;
                        if (provider.IsGroq)
                        {
                            body.Remove("max_tokens");
                            body["max_completion_tokens"] = 512;
                        }
                        else
                        {
                            body.Remove("max_completion_tokens");
                            body["max_tokens"] = 512;
                        }
                        using HttpRequestMessage request = new HttpRequestMessage(
                            HttpMethod.Post,
                            provider.Endpoint
                        );
                        request.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue(
                                "Bearer",
                                provider.ApiKey
                            );
                        AddProviderHeaders(request, provider);
                        request.Content = new StringContent(
                            JsonSerializer.Serialize(body),
                            Encoding.UTF8,
                            "application/json"
                        );

                        activity(
                            "[BLENDER REVIEW] "
                            + provider.Name
                            + " vision · "
                            + provider.Model
                        );
                        using CancellationTokenSource visionTimeout = new CancellationTokenSource(
                            TimeSpan.FromSeconds(ResolveTimeoutSeconds(
                                provider.IsGroq
                                    ? "GROQ_BLENDER_VISION_TIMEOUT_SECONDS"
                                    : "BLENDER_VISION_FALLBACK_TIMEOUT_SECONDS",
                                DefaultVisionRequestTimeoutSeconds
                            ))
                        );
                        using CancellationTokenSource visionLinked = CancellationTokenSource.CreateLinkedTokenSource(
                            visionTimeout.Token,
                            AgentCancellationHub.Token
                        );
                        using HttpResponseMessage response = await httpClient.SendAsync(
                            request,
                            visionLinked.Token
                        );
                        string responseText = await response.Content.ReadAsStringAsync(
                            visionLinked.Token
                        );
                        if (!response.IsSuccessStatusCode)
                        {
                            string message =
                                "Vision provider HTTP "
                                + (int)response.StatusCode
                                + ": "
                                + Trim(responseText, 1200);
                            if (IsTransientStatus(response.StatusCode)
                                && attempt < MaxProviderAttempts)
                            {
                                lastFailure = new InvalidOperationException(message);
                                await DelayProviderRetryAsync(provider.Name + " vision", attempt);
                                continue;
                            }

                            throw new InvalidOperationException(message);
                        }

                        using JsonDocument document = JsonDocument.Parse(responseText);
                        string feedback = ReadTextContent(document.RootElement);
                        if (!TryReadReviewPass(feedback, out bool parsedPass))
                        {
                            throw new InvalidOperationException(
                                "Vision provider nije vratio validan review JSON: "
                                + Trim(feedback, 800)
                            );
                        }

                        return new ViewportReviewResult(
                            true,
                            parsedPass,
                            Trim(feedback, 1800)
                        );
                    }
                    catch (OperationCanceledException) when (AgentCancellationHub.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException ex)
                    {
                        lastFailure = new InvalidOperationException(
                            provider.Name + " vision timeout/abort: " + ex.Message,
                            ex
                        );
                        if (attempt < MaxProviderAttempts)
                        {
                            await DelayProviderRetryAsync(provider.Name + " vision", attempt);
                            continue;
                        }

                        break;
                    }
                    catch (Exception ex)
                    {
                        lastFailure = ex;
                        activity(
                            "[BLENDER REVIEW] "
                            + provider.Name
                            + " failed · "
                            + Trim(ex.Message, 500)
                        );
                        break;
                    }
                }
            }

            throw new InvalidOperationException(
                "Svi Blender vision fallback provideri su nedostupni. Posljednja greška: "
                + Trim(lastFailure?.Message ?? "nepoznata greška", 1000),
                lastFailure
            );
        }

        private static bool TryReadImageBase64(string? path, out string? base64)
        {
            base64 = null;
            if (!HasUsableImage(path))
            {
                return false;
            }

            try
            {
                base64 = Convert.ToBase64String(File.ReadAllBytes(path!));
                return !string.IsNullOrWhiteSpace(base64);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static string ReadTextContent(JsonElement root)
        {
            if (root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out JsonElement message)
                && message.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? "";
            }

            return "";
        }

        private static bool TryReadReviewPass(string text, out bool pass)
        {
            pass = false;
            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return false;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    text.Substring(start, end - start + 1)
                );
                if (!document.RootElement.TryGetProperty("pass", out JsonElement value)
                    || (value.ValueKind != JsonValueKind.True
                        && value.ValueKind != JsonValueKind.False))
                {
                    return false;
                }

                pass = value.GetBoolean();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static McpImagePayload? FindMcpImage(string rawResult)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(rawResult);
                return FindMcpImage(document.RootElement, 0);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static McpImagePayload? FindMcpImage(
            JsonElement element,
            int depth
        )
        {
            if (depth > 10)
            {
                return null;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                bool isImage = element.TryGetProperty("type", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "image", StringComparison.OrdinalIgnoreCase);
                if (isImage
                    && element.TryGetProperty("data", out JsonElement data)
                    && data.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(data.GetString()))
                {
                    string mimeType = element.TryGetProperty("mimeType", out JsonElement mime)
                        && mime.ValueKind == JsonValueKind.String
                        ? mime.GetString() ?? "image/png"
                        : "image/png";
                    return new McpImagePayload(data.GetString() ?? "", mimeType);
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    McpImagePayload? nested = FindMcpImage(property.Value, depth + 1);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    McpImagePayload? nested = FindMcpImage(child, depth + 1);
                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }

            return null;
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
            List<CompletionProvider> providers = BuildCompletionProviders(
                groqApiKey,
                groqModel
            );
            if (providers.Count == 0)
            {
                throw new InvalidOperationException(
                    "Nijedan Blender LLM provider nije konfigurisan."
                );
            }

            Exception? lastFailure = null;
            List<object> providerMessages = messages;
            bool skipSameGroqFallback = false;
            foreach (CompletionProvider provider in providers)
            {
                if (skipSameGroqFallback
                    && string.Equals(provider.Name, "Groq direct fallback", StringComparison.Ordinal))
                {
                    activity(
                        "[BLENDER PROVIDER] skipping Groq direct fallback after Groq transport/rate-limit failure"
                    );
                    continue;
                }

                try
                {
                    activity(
                        "[BLENDER PROVIDER] trying "
                        + provider.Name
                        + " · "
                        + provider.Model
                    );
                    return await SendCompletionAsync(
                        provider,
                        providerMessages,
                        ResolveMaxCompletionTokens(
                            provider.IsGroq
                                ? (string.Equals(
                                    provider.Model,
                                    groqModel,
                                    StringComparison.OrdinalIgnoreCase
                                )
                                    ? "GROQ_BLENDER_MAX_TOKENS"
                                    : "GROQ_BLENDER_FALLBACK_MAX_TOKENS")
                                : "BLENDER_FALLBACK_MAX_TOKENS",
                            provider.IsGroq
                                ? (string.Equals(
                                    provider.Model,
                                    groqModel,
                                    StringComparison.OrdinalIgnoreCase
                                )
                                    ? DefaultGroqMaxCompletionTokens
                                    : DefaultGroqFallbackMaxCompletionTokens)
                                : DefaultGroqFallbackMaxCompletionTokens
                        )
                    );
                }
                catch (OperationCanceledException) when (AgentCancellationHub.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    if (provider.IsGroq
                        && !IsToolCallFormatFailure(ex)
                        && IsProviderTransportFailure(ex))
                    {
                        skipSameGroqFallback = true;
                    }
                    activity(
                        "[BLENDER PROVIDER] "
                        + provider.Name
                        + " failed · "
                        + Trim(ex.Message, 500)
                    );
                    if (IsToolCallFormatFailure(ex))
                    {
                        providerMessages = AddToolCallRepairHint(messages);
                    }
                }
            }

            throw new InvalidOperationException(
                "Svi Blender LLM fallback provideri su nedostupni. Posljednja greška: "
                + Trim(lastFailure?.Message ?? "nepoznata greška", 1200),
                lastFailure
            );
        }

        private static List<CompletionProvider> BuildCompletionProviders(
            string? groqApiKey,
            string groqModel
        )
        {
            List<CompletionProvider> providers = new List<CompletionProvider>();
            if (!string.IsNullOrWhiteSpace(groqApiKey))
            {
                providers.Add(
                    new CompletionProvider(
                        "Groq",
                        GroqEndpoint,
                        groqApiKey,
                        groqModel,
                        true
                    )
                );

                string fallbackModel =
                    Environment.GetEnvironmentVariable("GROQ_BLENDER_FALLBACK_MODEL")
                    ?? Environment.GetEnvironmentVariable("GROQ_MODEL")
                    ?? DefaultGroqFallbackModel;
                if (string.IsNullOrWhiteSpace(fallbackModel)
                    || string.Equals(fallbackModel, groqModel, StringComparison.OrdinalIgnoreCase))
                {
                    fallbackModel = DefaultGroqFallbackModel;
                }

                providers.Add(
                    new CompletionProvider(
                        "Groq direct fallback",
                        GroqEndpoint,
                        groqApiKey,
                        fallbackModel,
                        true
                    )
                );
            }

            string? openRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
            if (!string.IsNullOrWhiteSpace(openRouterKey))
            {
                providers.Add(
                    new CompletionProvider(
                        "OpenRouter",
                        "https://openrouter.ai/api/v1/chat/completions",
                        openRouterKey,
                        Environment.GetEnvironmentVariable("BLENDER_OPENROUTER_MODEL")
                            ?? Environment.GetEnvironmentVariable("OPENROUTER_MODEL")
                            ?? "nex-agi/nex-n2.5-pro:free",
                        false
                    )
                );
            }

            string? minimaxKey = Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
            if (!string.IsNullOrWhiteSpace(minimaxKey))
            {
                string minimaxBase = Environment.GetEnvironmentVariable("MINIMAX_BASE_URL")
                    ?? "https://api.minimax.io/v1";
                providers.Add(
                    new CompletionProvider(
                        "MiniMax",
                        ToCompletionEndpoint(minimaxBase),
                        minimaxKey,
                        Environment.GetEnvironmentVariable("MINIMAX_MODEL") ?? "MiniMax-M2.7",
                        false
                    )
                );
            }

            string? inclusionKey = Environment.GetEnvironmentVariable("INCLUSIONAI_API_KEY");
            string? inclusionBase = Environment.GetEnvironmentVariable("INCLUSIONAI_BASE_URL");
            if (!string.IsNullOrWhiteSpace(inclusionKey)
                && !string.IsNullOrWhiteSpace(inclusionBase))
            {
                providers.Add(
                    new CompletionProvider(
                        "InclusionAI",
                        ToCompletionEndpoint(inclusionBase),
                        inclusionKey,
                        Environment.GetEnvironmentVariable("INCLUSIONAI_MODEL")
                            ?? "inclusionai/ling-3.0-flash",
                        false
                    )
                );
            }

            return providers;
        }

        private static List<CompletionProvider> BuildVisionProviders(string groqVisionModel)
        {
            List<CompletionProvider> providers = new List<CompletionProvider>();
            string? groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
            if (!string.IsNullOrWhiteSpace(groqKey))
            {
                providers.Add(new CompletionProvider(
                    "Groq",
                    GroqEndpoint,
                    groqKey,
                    groqVisionModel,
                    true
                ));
            }

            string? openRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
            if (!string.IsNullOrWhiteSpace(openRouterKey))
            {
                providers.Add(new CompletionProvider(
                    "OpenRouter",
                    "https://openrouter.ai/api/v1/chat/completions",
                    openRouterKey,
                    Environment.GetEnvironmentVariable("BLENDER_OPENROUTER_VISION_MODEL")
                        ?? Environment.GetEnvironmentVariable("OPENROUTER_VISION_MODEL")
                        ?? Environment.GetEnvironmentVariable("BLENDER_OPENROUTER_MODEL")
                        ?? Environment.GetEnvironmentVariable("OPENROUTER_MODEL")
                        ?? "nex-agi/nex-n2.5-pro:free",
                    false
                ));
            }

            string? minimaxKey = Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
            if (!string.IsNullOrWhiteSpace(minimaxKey))
            {
                providers.Add(new CompletionProvider(
                    "MiniMax",
                    ToCompletionEndpoint(Environment.GetEnvironmentVariable("MINIMAX_BASE_URL")
                        ?? "https://api.minimax.io/v1"),
                    minimaxKey,
                    Environment.GetEnvironmentVariable("MINIMAX_VISION_MODEL") ?? "MiniMax-M3",
                    false
                ));
            }

            string? inclusionKey = Environment.GetEnvironmentVariable("INCLUSIONAI_API_KEY");
            string? inclusionBase = Environment.GetEnvironmentVariable("INCLUSIONAI_BASE_URL");
            string? inclusionVisionModel = Environment.GetEnvironmentVariable("INCLUSIONAI_VISION_MODEL");
            if (!string.IsNullOrWhiteSpace(inclusionKey)
                && !string.IsNullOrWhiteSpace(inclusionBase)
                && !string.IsNullOrWhiteSpace(inclusionVisionModel))
            {
                providers.Add(new CompletionProvider(
                    "InclusionAI",
                    ToCompletionEndpoint(inclusionBase),
                    inclusionKey,
                    inclusionVisionModel,
                    false
                ));
            }

            return providers;
        }

        private static string ToCompletionEndpoint(string baseUrl)
        {
            string value = baseUrl.Trim().TrimEnd('/');
            return value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
                ? value
                : value + "/chat/completions";
        }

        private static void AddProviderHeaders(
            HttpRequestMessage request,
            CompletionProvider provider
        )
        {
            if (!provider.Endpoint.Contains("openrouter.ai", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            request.Headers.TryAddWithoutValidation(
                "HTTP-Referer",
                "https://github.com/sulejmanbesic03-stack/AI-Assistant"
            );
            request.Headers.TryAddWithoutValidation(
                "X-Title",
                "AI Assistant Cowork Beta"
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

        private static bool IsProviderTransportFailure(Exception failure)
        {
            if (failure is HttpRequestException)
            {
                return true;
            }

            string message = failure.ToString();
            return message.Contains("timeout/abort", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Provider HTTP 408", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Provider HTTP 429", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Provider HTTP 499", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Provider HTTP 500", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Provider HTTP 502", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Provider HTTP 503", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Provider HTTP 504", StringComparison.OrdinalIgnoreCase);
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
            CompletionProvider provider,
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
                ["model"] = provider.Model,
                ["messages"] = messages,
                ["tools"] = groqTools,
                ["tool_choice"] = "auto",
                ["temperature"] = 0.1,
            };

            if (provider.IsGroq)
            {
                body["max_completion_tokens"] = maxCompletionTokens;
                body["parallel_tool_calls"] = false;
                body["reasoning_effort"] = provider.Model.StartsWith(
                    "qwen/",
                    StringComparison.OrdinalIgnoreCase
                ) ? "none" : "low";
            }
            else
            {
                body["max_tokens"] = maxCompletionTokens;
            }

            string serializedBody = JsonSerializer.Serialize(body);
            Exception? lastFailure = null;
            for (int attempt = 1; attempt <= MaxProviderAttempts; attempt++)
            {
                try
                {
                    using HttpRequestMessage request = new HttpRequestMessage(
                        HttpMethod.Post,
                        provider.Endpoint
                    );
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue(
                            "Bearer",
                            provider.ApiKey
                        );
                    AddProviderHeaders(request, provider);
                    request.Content = new StringContent(
                        serializedBody,
                        Encoding.UTF8,
                        "application/json"
                    );

                    using CancellationTokenSource timeout = new CancellationTokenSource(
                        TimeSpan.FromSeconds(ResolveTimeoutSeconds(
                            provider.IsGroq
                                ? "GROQ_BLENDER_REQUEST_TIMEOUT_SECONDS"
                                : "BLENDER_FALLBACK_REQUEST_TIMEOUT_SECONDS",
                            DefaultProviderRequestTimeoutSeconds
                        ))
                    );
                    using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                        timeout.Token,
                        AgentCancellationHub.Token
                    );

                    using HttpResponseMessage response = await httpClient.SendAsync(
                        request,
                        linked.Token
                    );
                    string text = await response.Content.ReadAsStringAsync(linked.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        string message =
                            "Provider HTTP "
                            + (int)response.StatusCode
                            + ": "
                            + Trim(text, 2000);
                        if (IsTransientStatus(response.StatusCode)
                            && attempt < MaxProviderAttempts)
                        {
                            lastFailure = new InvalidOperationException(message);
                            await DelayProviderRetryAsync(provider.Name, attempt);
                            continue;
                        }

                        throw new InvalidOperationException(message);
                    }

                    JsonDocument document = JsonDocument.Parse(text);
                    // Validate the OpenAI-compatible envelope here so a malformed
                    // provider response can enter the next fallback instead of
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
                catch (OperationCanceledException) when (AgentCancellationHub.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException ex)
                {
                    lastFailure = new InvalidOperationException(
                        provider.Name + " request timeout/abort: " + ex.Message,
                        ex
                    );
                    if (attempt >= MaxProviderAttempts)
                    {
                        throw lastFailure;
                    }

                    await DelayProviderRetryAsync(provider.Name, attempt);
                }
                catch (HttpRequestException ex)
                {
                    lastFailure = ex;
                    if (attempt >= MaxProviderAttempts)
                    {
                        throw;
                    }

                    await DelayProviderRetryAsync(provider.Name, attempt);
                }
            }

            throw lastFailure
                ?? new InvalidOperationException(provider.Name + " nije vratio odgovor.");
        }

        private async Task DelayProviderRetryAsync(string providerName, int attempt)
        {
            int delaySeconds = Math.Min(6, Math.Max(1, attempt * 2));
            activity(
                "[BLENDER PROVIDER] "
                + providerName
                + " transient failure; retrying in "
                + delaySeconds
                + "s"
            );
            await Task.Delay(
                TimeSpan.FromSeconds(delaySeconds),
                AgentCancellationHub.Token
            );
        }

        private static bool IsTransientStatus(HttpStatusCode statusCode)
        {
            return statusCode == HttpStatusCode.RequestTimeout
                || statusCode == HttpStatusCode.InternalServerError
                || statusCode == HttpStatusCode.BadGateway
                || statusCode == HttpStatusCode.ServiceUnavailable
                || statusCode == HttpStatusCode.GatewayTimeout;
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

        private static int ResolveTimeoutSeconds(string variableName, int defaultValue)
        {
            string? configured = Environment.GetEnvironmentVariable(variableName);
            return int.TryParse(configured, out int value)
                ? Math.Clamp(value, 15, 600)
                : defaultValue;
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
                TimeSpan.FromSeconds(ResolveTimeoutSeconds(
                    "BLENDER_MCP_REQUEST_TIMEOUT_SECONDS",
                    DefaultMcpRequestTimeoutSeconds
                ))
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

        private sealed class McpImagePayload
        {
            public string Data { get; }
            public string MimeType { get; }

            public McpImagePayload(string data, string mimeType)
            {
                Data = data;
                MimeType = mimeType;
            }
        }

        private sealed class ViewportReviewResult
        {
            public bool Succeeded { get; }
            public bool Passed { get; }
            public string Feedback { get; }

            public ViewportReviewResult(bool succeeded, bool passed, string feedback)
            {
                Succeeded = succeeded;
                Passed = passed;
                Feedback = feedback;
            }
        }

        private sealed class CompletionProvider
        {
            public string Name { get; }
            public string Endpoint { get; }
            public string ApiKey { get; }
            public string Model { get; }
            public bool IsGroq { get; }

            public CompletionProvider(
                string name,
                string endpoint,
                string apiKey,
                string model,
                bool isGroq
            )
            {
                Name = name;
                Endpoint = endpoint;
                ApiKey = apiKey;
                Model = model;
                IsGroq = isGroq;
            }
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
