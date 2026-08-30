using System;
using System.Collections.Generic;
using System.Linq;
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
        public string Model { get; init; } = "";
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

    internal class OpenAiCompatibleProviderV2 : IAIProviderV2
    {
        protected readonly string Endpoint;
        protected readonly string Model;
        protected readonly string ApiKeyEnvironmentVariable;
        protected readonly HttpClient Client;

        public string Name { get; }

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(
                    ApiKeyEnvironmentVariable
                )
            );

        public OpenAiCompatibleProviderV2(
            string name,
            string endpoint,
            string model,
            string apiKeyEnvironmentVariable,
            int timeoutSeconds = 120
        )
        {
            Name = name;
            Endpoint = endpoint;
            Model = model;
            ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable;

            Client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds)
            };
        }

        public virtual Task<ProviderReplyV2> CompleteAsync(
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default
        )
        {
            Dictionary<string, object?> body =
                BuildRequestBody(systemPrompt, userPrompt);

            return SendAsync(
                body,
                Model,
                cancellationToken
            );
        }

        protected virtual Dictionary<string, object?> BuildRequestBody(
            string systemPrompt,
            string userPrompt
        )
        {
            return new Dictionary<string, object?>
            {
                ["model"] = Model,
                ["messages"] = new object[]
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
                ["temperature"] = 0.1
            };
        }

        protected virtual void ConfigureHeaders(
            HttpRequestMessage request,
            string apiKey
        )
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    apiKey
                );
        }

        protected async Task<ProviderReplyV2> SendAsync(
            Dictionary<string, object?> requestBody,
            string requestedModel,
            CancellationToken cancellationToken
        )
        {
            string? apiKey =
                Environment.GetEnvironmentVariable(
                    ApiKeyEnvironmentVariable
                );

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new ProviderReplyV2
                {
                    Success = false,
                    Provider = Name,
                    Model = requestedModel,
                    Error = ApiKeyEnvironmentVariable + " is not configured."
                };
            }

            string requestJson = JsonSerializer.Serialize(requestBody);

            using HttpRequestMessage request =
                new HttpRequestMessage(
                    HttpMethod.Post,
                    Endpoint
                );

            ConfigureHeaders(request, apiKey);

            request.Content = new StringContent(
                requestJson,
                Encoding.UTF8,
                "application/json"
            );

            try
            {
                using HttpResponseMessage response =
                    await Client.SendAsync(
                        request,
                        cancellationToken
                    );

                string responseText =
                    await response.Content.ReadAsStringAsync(
                        cancellationToken
                    );

                string responseModel =
                    TryReadModel(responseText)
                    ?? requestedModel;

                if (
                    TryReadProviderError(
                        responseText,
                        out int embeddedStatus,
                        out string embeddedError
                    )
                )
                {
                    return new ProviderReplyV2
                    {
                        Success = false,
                        Provider = Name,
                        Model = responseModel,
                        StatusCode = embeddedStatus > 0
                            ? embeddedStatus
                            : (int)HttpStatusCode.BadGateway,
                        Error = embeddedError
                    };
                }

                if (!response.IsSuccessStatusCode)
                {
                    return new ProviderReplyV2
                    {
                        Success = false,
                        Provider = Name,
                        Model = responseModel,
                        StatusCode = (int)response.StatusCode,
                        Error = string.IsNullOrWhiteSpace(responseText)
                            ? response.ReasonPhrase ?? "Provider error."
                            : responseText
                    };
                }

                string? content = TryReadContent(responseText);

                if (string.IsNullOrWhiteSpace(content))
                {
                    return new ProviderReplyV2
                    {
                        Success = false,
                        Provider = Name,
                        Model = responseModel,
                        StatusCode = (int)HttpStatusCode.BadGateway,
                        Error =
                            "Provider returned no assistant content. Raw response: "
                            + AgentJsonV2.Compact(responseText, 1600)
                    };
                }

                return new ProviderReplyV2
                {
                    Success = true,
                    Provider = Name,
                    Model = responseModel,
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
                    Model = requestedModel,
                    StatusCode = (int)HttpStatusCode.RequestTimeout,
                    Error = cancellationToken.IsCancellationRequested
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
                    Model = requestedModel,
                    StatusCode = (int)HttpStatusCode.BadGateway,
                    Error = ex.GetType().Name + ": " + ex.Message
                };
            }
        }

        private static bool TryReadProviderError(
            string responseText,
            out int statusCode,
            out string errorText
        )
        {
            statusCode = 0;
            errorText = "";

            try
            {
                using JsonDocument document = JsonDocument.Parse(responseText);
                JsonElement root = document.RootElement;

                if (
                    !root.TryGetProperty("error", out JsonElement error)
                    || error.ValueKind == JsonValueKind.Null
                    || error.ValueKind == JsonValueKind.Undefined
                )
                {
                    return false;
                }

                if (error.ValueKind == JsonValueKind.String)
                {
                    errorText = error.GetString() ?? "Provider error.";
                    return true;
                }

                if (error.ValueKind != JsonValueKind.Object)
                {
                    errorText = error.GetRawText();
                    return true;
                }

                if (error.TryGetProperty("code", out JsonElement code))
                {
                    if (code.ValueKind == JsonValueKind.Number)
                    {
                        code.TryGetInt32(out statusCode);
                    }
                    else if (
                        code.ValueKind == JsonValueKind.String
                        && int.TryParse(code.GetString(), out int parsed)
                    )
                    {
                        statusCode = parsed;
                    }
                }

                if (
                    error.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String
                )
                {
                    errorText = message.GetString() ?? error.GetRawText();
                }
                else
                {
                    errorText = error.GetRawText();
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string? TryReadContent(string responseText)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseText);
                JsonElement root = document.RootElement;

                if (
                    !root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0
                )
                {
                    return null;
                }

                JsonElement first = choices[0];

                if (
                    !first.TryGetProperty("message", out JsonElement message)
                    || !message.TryGetProperty("content", out JsonElement content)
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

        private static string? TryReadModel(string responseText)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseText);

                if (
                    document.RootElement.TryGetProperty(
                        "model",
                        out JsonElement model
                    )
                    && model.ValueKind == JsonValueKind.String
                )
                {
                    return model.GetString();
                }
            }
            catch
            {
            }

            return null;
        }
    }

    internal sealed class GeminiProviderV2
        : OpenAiCompatibleProviderV2
    {
        private readonly string reasoningEffort;

        public GeminiProviderV2()
            : base(
                "Gemini",
                "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                "gemini-3.7-flash",
                "GEMINI_API_KEY",
                180
            )
        {
            reasoningEffort = NormalizeReasoningEffort(
                Environment.GetEnvironmentVariable("GEMINI_REASONING_EFFORT")
                ?? "high"
            );
        }

        protected override Dictionary<string, object?> BuildRequestBody(
            string systemPrompt,
            string userPrompt
        )
        {
            Dictionary<string, object?> body =
                base.BuildRequestBody(systemPrompt, userPrompt);

            body.Remove("temperature");
            body["reasoning_effort"] = reasoningEffort;

            return body;
        }

        private static string NormalizeReasoningEffort(string value)
        {
            string normalized = (value ?? "")
                .Trim()
                .ToLowerInvariant();

            return normalized switch
            {
                "low" => "low",
                "medium" => "medium",
                _ => "high"
            };
        }
    }

    internal sealed class OpenRouterProviderV2
        : OpenAiCompatibleProviderV2
    {
        private const string DefaultPrimaryModel =
            "z-ai/glm-5.2:free";

        private static readonly string[] DefaultFallbackModels =
        {
            "minimax/minimax-m3:free",
            "nvidia/nemotron-3-ultra-550b-a55b:free"
        };

        private readonly string[] fallbackModels;
        private readonly string reasoningEffort;

        public OpenRouterProviderV2()
            : base(
                "OpenRouter",
                "https://openrouter.ai/api/v1/chat/completions",
                ResolveFreeModel(
                    Environment.GetEnvironmentVariable("OPENROUTER_MODEL"),
                    DefaultPrimaryModel
                ),
                "OPENROUTER_API_KEY",
                180
            )
        {
            reasoningEffort =
                NormalizeReasoningEffort(
                    Environment.GetEnvironmentVariable(
                        "OPENROUTER_REASONING_EFFORT"
                    )
                    ?? "high"
                );

            string? configuredFallbacks =
                Environment.GetEnvironmentVariable(
                    "OPENROUTER_FALLBACK_MODELS"
                );

            string[] configuredFreeFallbacks =
                ParseFallbackModels(configuredFallbacks);

            fallbackModels = configuredFreeFallbacks.Length > 0
                ? configuredFreeFallbacks
                : DefaultFallbackModels;
        }

        protected override Dictionary<string, object?> BuildRequestBody(
            string systemPrompt,
            string userPrompt
        )
        {
            Dictionary<string, object?> body =
                base.BuildRequestBody(systemPrompt, userPrompt);

            body.Remove("temperature");

            if (fallbackModels.Length > 0)
            {
                body["models"] = fallbackModels;
            }

            body["reasoning"] = new
            {
                effort = reasoningEffort
            };

            return body;
        }

        protected override void ConfigureHeaders(
            HttpRequestMessage request,
            string apiKey
        )
        {
            base.ConfigureHeaders(request, apiKey);

            request.Headers.TryAddWithoutValidation(
                "X-OpenRouter-Title",
                "AI Assistant Unity Cowork Agent V2"
            );
        }

        private static string ResolveFreeModel(
            string? configuredModel,
            string fallbackModel
        )
        {
            string value = (configuredModel ?? "").Trim();

            return IsFreeOpenRouterModel(value)
                ? value
                : fallbackModel;
        }

        private static bool IsFreeOpenRouterModel(string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                return false;
            }

            string value = model.Trim();

            return value.EndsWith(
                    ":free",
                    StringComparison.OrdinalIgnoreCase
                )
                || value.Equals(
                    "openrouter/free",
                    StringComparison.OrdinalIgnoreCase
                );
        }

        private static string NormalizeReasoningEffort(string value)
        {
            string normalized = (value ?? "")
                .Trim()
                .ToLowerInvariant();

            return normalized switch
            {
                "low" => "low",
                "max" => "max",
                "xhigh" => "max",
                _ => "high"
            };
        }

        private static string[] ParseFallbackModels(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Array.Empty<string>();
            }

            return value
                .Split(
                    new[] { ',', ';' },
                    StringSplitOptions.RemoveEmptyEntries
                )
                .Select(item => item.Trim())
                .Where(IsFreeOpenRouterModel)
                .Where(item =>
                    !item.Equals(
                        DefaultPrimaryModel,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();
        }
    }

    internal sealed class ProviderRouterV2
    {
        private readonly IAIProviderV2 gemini;
        private readonly IAIProviderV2 openRouter;
        private readonly IAIProviderV2 groq;
        private readonly Action<string> activity;

        public ProviderRouterV2(Action<string> activity)
        {
            this.activity = activity;

            gemini = new GeminiProviderV2();
            openRouter = new OpenRouterProviderV2();

            groq = new OpenAiCompatibleProviderV2(
                "Groq",
                "https://api.groq.com/openai/v1/chat/completions",
                "openai/gpt-oss-120b",
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
            IAIProviderV2? selected = ResolveProvider(task.ActiveProvider);

            if (selected == null)
            {
                selected = FirstConfigured();
            }

            if (selected == null)
            {
                return new ProviderReplyV2
                {
                    Success = false,
                    Error =
                        "No Agent V2 provider is configured. Set GEMINI_API_KEY, OPENROUTER_API_KEY or GROQ_API_KEY."
                };
            }

            ProviderReplyV2 reply =
                await CallProvider(
                    task,
                    selected,
                    systemPrompt,
                    userPrompt,
                    cancellationToken
                );

            if (reply.Success || !ShouldFallback(reply.StatusCode))
            {
                return reply;
            }

            foreach (IAIProviderV2 fallback in GetFallbackOrder(selected))
            {
                if (!fallback.IsConfigured)
                {
                    continue;
                }

                activity(
                    "[V2 PROVIDER FALLBACK] "
                    + selected.Name
                    + " -> "
                    + fallback.Name
                    + " (HTTP "
                    + reply.StatusCode
                    + ")"
                );

                task.ActiveProvider = fallback.Name;

                reply = await CallProvider(
                    task,
                    fallback,
                    systemPrompt,
                    userPrompt,
                    cancellationToken
                );

                if (reply.Success || !ShouldFallback(reply.StatusCode))
                {
                    return reply;
                }

                selected = fallback;
            }

            return reply;
        }

        private async Task<ProviderReplyV2> CallProvider(
            AgentTaskStateV2 task,
            IAIProviderV2 provider,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken
        )
        {
            task.ActiveProvider = provider.Name;
            task.ModelCalls++;

            activity(
                "[V2 MODEL] "
                + provider.Name
                + " call "
                + task.ModelCalls
            );

            ProviderReplyV2 reply =
                await provider.CompleteAsync(
                    systemPrompt,
                    userPrompt,
                    cancellationToken
                );

            if (reply.Success && !string.IsNullOrWhiteSpace(reply.Model))
            {
                activity(
                    "[V2 MODEL] resolved "
                    + reply.Model
                );
            }

            return reply;
        }

        private IAIProviderV2? FirstConfigured()
        {
            if (gemini.IsConfigured)
            {
                return gemini;
            }

            if (openRouter.IsConfigured)
            {
                return openRouter;
            }

            if (groq.IsConfigured)
            {
                return groq;
            }

            return null;
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
                    "OpenRouter",
                    StringComparison.OrdinalIgnoreCase
                )
                && openRouter.IsConfigured
            )
            {
                return openRouter;
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

        private IEnumerable<IAIProviderV2> GetFallbackOrder(
            IAIProviderV2 selected
        )
        {
            IAIProviderV2[] order =
            {
                gemini,
                openRouter,
                groq
            };

            int selectedIndex = Array.FindIndex(
                order,
                item => item.Name.Equals(
                    selected.Name,
                    StringComparison.OrdinalIgnoreCase
                )
            );

            if (selectedIndex < 0)
            {
                selectedIndex = 0;
            }

            for (int offset = 1; offset < order.Length; offset++)
            {
                yield return order[
                    (selectedIndex + offset) % order.Length
                ];
            }
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
