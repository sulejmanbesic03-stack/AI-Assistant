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
        private const int TimeoutSeconds = 20;

        private readonly HttpClient client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
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
            string? apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new IntentResult
                {
                    Intent = AssistantIntent.Unknown,
                    Reason = "GROQ_API_KEY nije konfigurisan.",
                    UsedModel = false
                };
            }

            string model = Environment.GetEnvironmentVariable("GROQ_ROUTER_MODEL")
                ?? Environment.GetEnvironmentVariable("GROQ_MODEL")
                ?? DefaultModel;

            Dictionary<string, object?> body = new Dictionary<string, object?>
            {
                ["model"] = model,
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
                ["max_completion_tokens"] = 120,
                ["reasoning_effort"] = "low",
                ["response_format"] = new { type = "json_object" }
            };

            try
            {
                activity("[ROUTER MODEL] classifying task intent");

                using HttpRequestMessage request = new HttpRequestMessage(
                    HttpMethod.Post,
                    Endpoint
                );
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(body),
                    Encoding.UTF8,
                    "application/json"
                );

                using HttpResponseMessage response = await client.SendAsync(
                    request,
                    cancellationToken
                );
                string responseText = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return new IntentResult
                    {
                        Intent = AssistantIntent.Unknown,
                        Reason = "Router HTTP " + (int)response.StatusCode + ": " + Trim(responseText, 800),
                        UsedModel = true
                    };
                }

                string? content = ReadContent(responseText);
                if (string.IsNullOrWhiteSpace(content))
                {
                    return new IntentResult
                    {
                        Intent = AssistantIntent.Unknown,
                        Reason = "Router nije vratio sadržaj.",
                        UsedModel = true
                    };
                }

                return ParseResult(content);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new IntentResult
                {
                    Intent = AssistantIntent.Unknown,
                    Reason = ex.GetType().Name + ": " + ex.Message,
                    UsedModel = true
                };
            }
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
                return new IntentResult
                {
                    Intent = AssistantIntent.Unknown,
                    Reason = "Router JSON nije validan: " + ex.Message,
                    UsedModel = true
                };
            }
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
    }
}
