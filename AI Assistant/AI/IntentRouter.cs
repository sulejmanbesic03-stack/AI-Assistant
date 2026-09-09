using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Assistant.AI
{
    internal enum AssistantIntent
    {
        Unknown,
        Blender,
        BlenderUnity,
        Unity,
        Plan,
        Conversation
    }

    internal sealed class IntentResult
    {
        public AssistantIntent Intent { get; init; }
        public double Confidence { get; init; }
        public string Reason { get; init; } = "";
        public bool UsedModel { get; init; }
    }

    internal sealed class IntentRouter : IDisposable
    {
        private const string Endpoint =
            "https://api.groq.com/openai/v1/chat/completions";

        private const string DefaultModel = "openai/gpt-oss-120b";
        private const int DefaultTimeoutSeconds = 45;
        private const int MaxProviderAttempts = 2;

        private readonly HttpClient client = new HttpClient
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        private readonly Action<string> activity;

        public IntentRouter(Action<string> activity)
        {
            this.activity = activity;
        }

        public async Task<IntentResult> ClassifyAsync(
            string prompt,
            CancellationToken cancellationToken = default
        )
        {
            List<RouterProvider> providers = BuildProviders();
            if (providers.Count == 0)
            {
                return new IntentResult
                {
                    Intent = AssistantIntent.Unknown,
                    Reason = "Nijedan intent-router provider nije konfigurisan.",
                    UsedModel = false
                };
            }

            Exception? lastFailure = null;
            foreach (RouterProvider provider in providers)
            {
                for (int attempt = 1; attempt <= MaxProviderAttempts; attempt++)
                {
                    try
                    {
                        activity("[ROUTER MODEL] trying " + provider.Name + " · " + provider.Model);
                        string responseText = await SendProviderRequestAsync(
                            provider,
                            prompt,
                            cancellationToken
                        );
                        string? content = ReadContent(responseText);
                        if (string.IsNullOrWhiteSpace(content))
                        {
                            throw new InvalidOperationException("Router nije vratio sadržaj.");
                        }

                        return ParseResult(content);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastFailure = ex;
                        activity(
                            "[ROUTER MODEL] "
                            + provider.Name
                            + " failed · "
                            + Trim(ex.Message, 500)
                        );
                        if (IsTransientFailure(ex) && attempt < MaxProviderAttempts)
                        {
                            await Task.Delay(
                                TimeSpan.FromSeconds(Math.Min(4, attempt * 2)),
                                cancellationToken
                            );
                            continue;
                        }

                        break;
                    }
                }
            }

            return new IntentResult
            {
                Intent = AssistantIntent.Unknown,
                Reason = "Intent router fallback provideri su nedostupni: "
                    + Trim(lastFailure?.Message ?? "nepoznata greška", 800),
                UsedModel = true
            };
        }

        private async Task<string> SendProviderRequestAsync(
            RouterProvider provider,
            string prompt,
            CancellationToken cancellationToken
        )
        {
            Dictionary<string, object?> body = new Dictionary<string, object?>
            {
                ["model"] = provider.Model,
                ["messages"] = new object[]
                {
                    new
                    {
                        role = "system",
                        content =
                            "Classify the user's request for a local Unity and Blender automation app. "
                            + "Return one JSON object only with fields intent, confidence, reason. "
                            + "Allowed intents: blender, blender_unity, unity, plan, conversation. "
                            + "Use blender_unity when the user wants a new 3D asset modeled/generated in Blender and then delivered/imported to Unity, even if the word Blender is not in the request. "
                            + "A request to import an already-existing asset into Unity is unity; a request to create the asset and deliver it is blender_unity. "
                            + "Use blender for any Blender scene, object, material, asset, modeling or export task without Unity delivery. "
                            + "Use unity for Unity Editor, GameObject, scene, script, gameplay or project tasks. "
                            + "Use plan only when the user asks for a plan without execution. "
                            + "Do not classify from one keyword alone; infer the requested action and destination."
                    },
                    new
                    {
                        role = "user",
                        content = Trim(prompt, 5000)
                    }
                },
                ["temperature"] = 0,
                ["max_completion_tokens"] = 160
            };
            if (provider.IsGroq)
            {
                body["reasoning_effort"] = "low";
                body["response_format"] = new { type = "json_object" };
            }

            using HttpRequestMessage request = new HttpRequestMessage(
                HttpMethod.Post,
                provider.Endpoint
            );
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json"
            );

            using CancellationTokenSource timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(ResolveTimeoutSeconds(provider.IsGroq))
            );
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                timeout.Token,
                cancellationToken
            );
            using HttpResponseMessage response = await client.SendAsync(
                request,
                linked.Token
            );
            string responseText = await response.Content.ReadAsStringAsync(linked.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "Router HTTP " + (int)response.StatusCode + ": " + Trim(responseText, 800)
                );
            }

            return responseText;
        }

        private static List<RouterProvider> BuildProviders()
        {
            List<RouterProvider> providers = new List<RouterProvider>();
            string? groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
            if (!string.IsNullOrWhiteSpace(groqKey))
            {
                providers.Add(new RouterProvider(
                    "Groq",
                    Endpoint,
                    groqKey,
                    Environment.GetEnvironmentVariable("GROQ_ROUTER_MODEL")
                        ?? Environment.GetEnvironmentVariable("GROQ_MODEL")
                        ?? DefaultModel,
                    true
                ));
            }

            string? minimaxKey = Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
            if (!string.IsNullOrWhiteSpace(minimaxKey))
            {
                providers.Add(new RouterProvider(
                    "MiniMax",
                    ToCompletionEndpoint(Environment.GetEnvironmentVariable("MINIMAX_BASE_URL")
                        ?? "https://api.minimax.io/v1"),
                    minimaxKey,
                    Environment.GetEnvironmentVariable("MINIMAX_MODEL") ?? "MiniMax-M2.7",
                    false
                ));
            }

            string? inclusionKey = Environment.GetEnvironmentVariable("INCLUSIONAI_API_KEY");
            string? inclusionBase = Environment.GetEnvironmentVariable("INCLUSIONAI_BASE_URL");
            if (!string.IsNullOrWhiteSpace(inclusionKey)
                && !string.IsNullOrWhiteSpace(inclusionBase))
            {
                providers.Add(new RouterProvider(
                    "InclusionAI",
                    ToCompletionEndpoint(inclusionBase),
                    inclusionKey,
                    Environment.GetEnvironmentVariable("INCLUSIONAI_MODEL")
                        ?? "inclusionai/ling-3.0-flash",
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

        private static bool IsTransientFailure(Exception failure)
        {
            string message = failure.ToString();
            return failure is HttpRequestException
                || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                || message.Contains("abort", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Router HTTP 408", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Router HTTP 500", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Router HTTP 502", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Router HTTP 503", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Router HTTP 504", StringComparison.OrdinalIgnoreCase);
        }

        private static IntentResult ParseResult(string content)
        {
            string candidate = ExtractJsonObject(content);
            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate);
                JsonElement root = document.RootElement;
                string intentText = root.TryGetProperty("intent", out JsonElement intent)
                    ? intent.GetString() ?? ""
                    : "";

                AssistantIntent parsed = intentText.Trim().ToLowerInvariant() switch
                {
                    "blender" => AssistantIntent.Blender,
                    "blender_unity" => AssistantIntent.BlenderUnity,
                    "unity" => AssistantIntent.Unity,
                    "plan" => AssistantIntent.Plan,
                    "conversation" => AssistantIntent.Conversation,
                    _ => AssistantIntent.Unknown
                };

                double confidence = root.TryGetProperty("confidence", out JsonElement confidenceElement)
                    && confidenceElement.ValueKind == JsonValueKind.Number
                    && confidenceElement.TryGetDouble(out double parsedConfidence)
                    ? Math.Clamp(parsedConfidence, 0, 1)
                    : 0;

                string reason = root.TryGetProperty("reason", out JsonElement reasonElement)
                    ? reasonElement.GetString() ?? ""
                    : "";

                return new IntentResult
                {
                    Intent = parsed,
                    Confidence = confidence,
                    Reason = Trim(reason, 300),
                    UsedModel = true
                };
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Router JSON nije validan: " + ex.Message,
                    ex
                );
            }
        }

        private static int ResolveTimeoutSeconds(bool isGroq)
        {
            string variable = isGroq
                ? "GROQ_ROUTER_TIMEOUT_SECONDS"
                : "BLENDER_FALLBACK_REQUEST_TIMEOUT_SECONDS";
            string? configured = Environment.GetEnvironmentVariable(variable);
            return int.TryParse(configured, out int value)
                ? Math.Clamp(value, 15, 180)
                : DefaultTimeoutSeconds;
        }

        private static string? ReadContent(string responseText)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseText);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    return null;
                }

                JsonElement message = choices[0].GetProperty("message");
                return message.TryGetProperty("content", out JsonElement content)
                    && content.ValueKind == JsonValueKind.String
                    ? content.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractJsonObject(string content)
        {
            string value = (content ?? "").Trim();
            if (value.StartsWith("```", StringComparison.Ordinal))
            {
                int firstLine = value.IndexOf('\n');
                int lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
                if (firstLine >= 0 && lastFence > firstLine)
                {
                    value = value.Substring(firstLine + 1, lastFence - firstLine - 1).Trim();
                }
            }

            int start = value.IndexOf('{');
            int end = value.LastIndexOf('}');
            return start >= 0 && end > start
                ? value.Substring(start, end - start + 1)
                : value;
        }

        private static string Trim(string value, int max)
        {
            return value.Length <= max
                ? value
                : value.Substring(0, max) + "…";
        }

        public void Dispose()
        {
            client.Dispose();
        }

        private sealed class RouterProvider
        {
            public string Name { get; }
            public string Endpoint { get; }
            public string ApiKey { get; }
            public string Model { get; }
            public bool IsGroq { get; }

            public RouterProvider(
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
    }
}
