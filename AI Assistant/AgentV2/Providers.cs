using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Assistant.AgentV2
{
    public sealed class ProviderReplyV2
    {
        public bool Success { get; init; }
        public string Provider { get; init; } = "";
        public string Content { get; init; } = "";
        public string Error { get; init; } = "";
        public int StatusCode { get; init; }
    }

    internal interface IAIProviderV2
    {
        string Name { get; }

        bool IsConfigured { get; }

        Task<ProviderReplyV2> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default
        );
    }

    internal sealed class OpenAiCompatibleProviderV2
        : IAIProviderV2
    {
        private readonly string endpoint;
        private readonly string model;
        private readonly string apiKeyEnvironmentVariable;
        private readonly HttpClient client;

        public string Name { get; }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    apiKeyEnvironmentVariable
                )
            );

        public OpenAiCompatibleProviderV2(
            string name,
            string endpoint,
            string model,
            string apiKeyEnvironmentVariable,
            int timeoutSeconds = 90
        )
        {
            Name = name;
            this.endpoint = endpoint;
            this.model = model;
            this.apiKeyEnvironmentVariable =
                apiKeyEnvironmentVariable;

            client =
                new HttpClient
                {
                    Timeout =
                        TimeSpan.FromSeconds(
                            timeoutSeconds
                        )
                };
        }

        public async Task<ProviderReplyV2> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default
        )
        {
            string? apiKey =
                Environment.GetEnvironmentVariable(
                    apiKeyEnvironmentVariable
                );

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new ProviderReplyV2
                {
                    Success = false,
                    Provider = Name,
                    Error =
                        apiKeyEnvironmentVariable
                        + " is not configured."
                };
            }

            object requestBody =
                new
                {
                    model,
                    messages =
                        new object[]
                        {
                            new
                            {
                                role = "system",
                                content = systemPrompt
                            },
                            new
                            {
                                role = "user",
                                content = userPrompt
                            }
                        },
                    temperature = 0.15
                };

            string requestJson =
                JsonSerializer.Serialize(
                    requestBody
                );

            using HttpRequestMessage request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    endpoint
                );

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    apiKey
                );

            request.Content =
                new StringContent(
                    requestJson,
                    Encoding.UTF8,
                    "application/json"
                );

            try
            {
                using HttpResponseMessage response =
                    await client.SendAsync(
                        request,
                        cancellationToken
                    );

                string responseText =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken
                    );

                if (!response.IsSuccessStatusCode)
                {
                    return new ProviderReplyV2
                    {
                        Success = false,
                        Provider = Name,
                        StatusCode = (int)response.StatusCode,
                        Error =
                            string.IsNullOrWhiteSpace(responseText)
                                ? response.ReasonPhrase ?? "Provider error."
                                : responseText
                    };
                }

                string? content =
                    TryReadContent(
                        responseText
                    );

                if (string.IsNullOrWhiteSpace(content))
                {
                    return new ProviderReplyV2
                    {
                        Success = false,
                        Provider = Name,
                        StatusCode = (int)response.StatusCode,
                        Error =
                            "Provider returned no assistant content. Raw response: "
                            + AgentJsonV2.Compact(
                                responseText,
                                1200
                            )
                    };
                }

                return new ProviderReplyV2
                {
                    Success = true,
                    Provider = Name,
                    StatusCode = (int)response.StatusCode,
                    Content = content
                };
            }
            catch (TaskCanceledException ex)
            {
                return new ProviderReplyV2
                {
                    Success = false,
                    Provider = Name,
                    StatusCode = (int)HttpStatusCode.RequestTimeout,
                    Error =
                        cancellationToken.IsCancellationRequested
                            ? "Provider request was cancelled."
                            : "Provider request timed out: " + ex.Message
                };
            }
            catch (Exception ex)
            {
                return new ProviderReplyV2
                {
                    Success = false,
                    Provider = Name,
                    Error =
                        ex.GetType().Name
                        + ": "
                        + ex.Message
                };
            }
        }

        private static string? TryReadContent(string responseText)
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(
                        responseText
                    );

                JsonElement root =
                    document.RootElement;

                if (
                    !root.TryGetProperty(
                        "choices",
                        out JsonElement choices
                    )
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0
                )
                {
                    return null;
                }

                JsonElement first = choices[0];

                if (
                    !first.TryGetProperty(
                        "message",
                        out JsonElement message
                    )
                    || !message.TryGetProperty(
                        "content",
                        out JsonElement content
                    )
                )
                {
                    return null;
                }

                if (content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }

                return content.GetRawText();
            }
            catch
            {
                return null;
            }
        }
    }

    internal sealed class ProviderRouterV2
    {
        private readonly IAIProviderV2 gemini;
        private readonly IAIProviderV2 groq;
        private readonly Action<string> activity;

        public ProviderRouterV2(
            Action<string> activity
        )
        {
            this.activity = activity;

            string geminiModel =
                Environment.GetEnvironmentVariable(
                    "GEMINI_MODEL"
                )
                ?? "gemini-3.6-flash";

            string groqModel =
                Environment.GetEnvironmentVariable(
                    "GROQ_MODEL"
                )
                ?? "openai/gpt-oss-120b";

            gemini =
                new OpenAiCompatibleProviderV2(
                    "Gemini",
                    "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                    geminiModel,
                    "GEMINI_API_KEY"
                );

            groq =
                new OpenAiCompatibleProviderV2(
                    "Groq",
                    "https://api.groq.com/openai/v1/chat/completions",
                    groqModel,
                    "GROQ_API_KEY"
                );
        }

        public async Task<ProviderReplyV2> CompleteAsync(
            AgentTaskStateV2 task,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default
        )
        {
            IAIProviderV2? selected =
                ResolveProvider(
                    task.ActiveProvider
                );

            if (selected == null)
            {
                selected =
                    gemini.IsConfigured
                        ? gemini
                        : groq.IsConfigured
                            ? groq
                            : null;
            }

            if (selected == null)
            {
                return new ProviderReplyV2
                {
                    Success = false,
                    Error =
                        "Neither GEMINI_API_KEY nor GROQ_API_KEY is configured."
                };
            }

            task.ActiveProvider = selected.Name;
            task.ModelCalls++;

            activity(
                "[V2 MODEL] "
                + selected.Name
                + " call "
                + task.ModelCalls
            );

            ProviderReplyV2 primaryReply =
                await selected.CompleteAsync(
                    systemPrompt,
                    userPrompt,
                    cancellationToken
                );

            if (primaryReply.Success)
            {
                return primaryReply;
            }

            IAIProviderV2? fallback =
                selected.Name.Equals(
                    "Gemini",
                    StringComparison.OrdinalIgnoreCase
                )
                && groq.IsConfigured
                    ? groq
                    : null;

            if (
                fallback == null
                || !ShouldFallback(
                    primaryReply.StatusCode
                )
            )
            {
                return primaryReply;
            }

            // Sticky fallback for the rest of this task. Agent V2 sends
            // no provider-specific tool history, so provider switching is
            // cheap and cannot create Gemini thought_signature chains.
            task.ActiveProvider = fallback.Name;
            task.ModelCalls++;

            activity(
                "[V2 FALLBACK] "
                + selected.Name
                + " -> "
                + fallback.Name
                + " (HTTP "
                + primaryReply.StatusCode
                + ")"
            );

            return
                await fallback.CompleteAsync(
                    systemPrompt,
                    userPrompt,
                    cancellationToken
                );
        }

        private IAIProviderV2? ResolveProvider(string name)
        {
            if (
                name.Equals(
                    "Gemini",
                    StringComparison.OrdinalIgnoreCase
                )
                && gemini.IsConfigured
            )
            {
                return gemini;
            }

            if (
                name.Equals(
                    "Groq",
                    StringComparison.OrdinalIgnoreCase
                )
                && groq.IsConfigured
            )
            {
                return groq;
            }

            return null;
        }

        private static bool ShouldFallback(int statusCode)
        {
            return
                statusCode == 0
                || statusCode == 408
                || statusCode == 429
                || statusCode == 500
                || statusCode == 502
                || statusCode == 503
                || statusCode == 504;
        }
    }
}
