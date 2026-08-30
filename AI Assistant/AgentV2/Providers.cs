using System;
using System.Collections.Generic;
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
        public int RetryAfterSeconds { get; init; }
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
                Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable)
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
            return SendAsync(
                BuildRequestBody(systemPrompt, userPrompt),
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
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
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
                new AuthenticationHeaderValue("Bearer", apiKey);
        }

        protected async Task<ProviderReplyV2> SendAsync(
            Dictionary<string, object?> requestBody,
            string requestedModel,
            CancellationToken cancellationToken
        )
        {
            string? apiKey =
                Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);

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

            using HttpRequestMessage request =
                new HttpRequestMessage(HttpMethod.Post, Endpoint);

            ConfigureHeaders(request, apiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            try
            {
                using HttpResponseMessage response =
                    await Client.SendAsync(request, cancellationToken);

                string responseText =
                    await response.Content.ReadAsStringAsync(cancellationToken);

                string responseModel =
                    TryReadModel(responseText) ?? requestedModel;

                int retryAfterSeconds = GetRetryAfterSeconds(response);

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
                        RetryAfterSeconds = retryAfterSeconds,
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
                        RetryAfterSeconds = retryAfterSeconds,
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
                            + AgentJsonV2.Compact(responseText, 1200)
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

        private static int GetRetryAfterSeconds(HttpResponseMessage response)
        {
            RetryConditionHeaderValue? retryAfter = response.Headers.RetryAfter;

            if (retryAfter?.Delta is TimeSpan delta)
            {
                return Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds));
            }

            if (retryAfter?.Date is DateTimeOffset date)
            {
                return Math.Max(
                    1,
                    (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds)
                );
            }

            if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values))
            {
                foreach (string value in values)
                {
                    if (int.TryParse(value, out int seconds) && seconds > 0)
                    {
                        return seconds;
                    }
                }
            }

            return 0;
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

                return content.ValueKind == JsonValueKind.String
                    ? content.GetString()
                    : content.GetRawText();
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
                    document.RootElement.TryGetProperty("model", out JsonElement model)
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

    internal sealed class MiniMaxProviderV2 : OpenAiCompatibleProviderV2
    {
        public MiniMaxProviderV2()
            : base(
                "MiniMax",
                "https://openrouter.ai/api/v1/chat/completions",
                "minimax/minimax-m3:free",
                "OPENROUTER_API_KEY",
                180
            )
        {
        }

        protected override Dictionary<string, object?> BuildRequestBody(
            string systemPrompt,
            string userPrompt
        )
        {
            Dictionary<string, object?> body =
                base.BuildRequestBody(systemPrompt, userPrompt);
            body.Remove("temperature");
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
    }

    internal sealed class GeminiProviderV2 : OpenAiCompatibleProviderV2
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
            return (value ?? "").Trim().ToLowerInvariant() switch
            {
                "low" => "low",
                "medium" => "medium",
                _ => "high"
            };
        }
    }

    internal sealed class ProviderRouterV2
    {
        private readonly IAIProviderV2 minimax;
        private readonly IAIProviderV2 gemini;
        private readonly IAIProviderV2 groq;
        private readonly Action<string> activity;

        private readonly Dictionary<string, DateTime> cooldownUntil =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public ProviderRouterV2(Action<string> activity)
        {
            this.activity = activity;

            minimax = new MiniMaxProviderV2();
            gemini = new GeminiProviderV2();
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
            IAIProviderV2? selected =
                ResolveProvider(task.ActiveProvider) ?? FirstAvailable();

            if (selected == null)
            {
                return BuildUnavailableReply();
            }

            ProviderReplyV2 reply = await CallProvider(
                task,
                selected,
                systemPrompt,
                userPrompt,
                cancellationToken
            );

            UpdateCooldown(reply);

            if (reply.Success || !ShouldFallback(reply.StatusCode))
            {
                return reply;
            }

            foreach (IAIProviderV2 fallback in GetFallbackOrder(selected))
            {
                if (!fallback.IsConfigured || IsCoolingDown(fallback.Name))
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

                reply = await CallProvider(
                    task,
                    fallback,
                    systemPrompt,
                    userPrompt,
                    cancellationToken
                );

                UpdateCooldown(reply);

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

            int approxInputTokens =
                Math.Max(1, (systemPrompt.Length + userPrompt.Length + 3) / 4);

            activity(
                "[V2 MODEL] "
                + provider.Name
                + " call "
                + task.ModelCalls
            );
            activity(
                "[V2 TOKENS] "
                + provider.Name
                + " approx input "
                + approxInputTokens
                + " tokens"
            );

            ProviderReplyV2 reply = await provider.CompleteAsync(
                systemPrompt,
                userPrompt,
                cancellationToken
            );

            if (reply.Success && !string.IsNullOrWhiteSpace(reply.Model))
            {
                activity("[V2 MODEL] resolved " + reply.Model);
            }

            return reply;
        }

        private void UpdateCooldown(ProviderReplyV2 reply)
        {
            if (string.IsNullOrWhiteSpace(reply.Provider))
            {
                return;
            }

            if (reply.Success)
            {
                cooldownUntil.Remove(reply.Provider);
                return;
            }

            if (reply.StatusCode != 429)
            {
                return;
            }

            int seconds = reply.RetryAfterSeconds > 0
                ? Math.Clamp(reply.RetryAfterSeconds, 1, 86400)
                : 90;

            cooldownUntil[reply.Provider] =
                DateTime.UtcNow.AddSeconds(seconds);

            activity(
                "[V2 RATE LIMIT] "
                + reply.Provider
                + " cooling down for "
                + seconds
                + "s; no blind retries"
            );
        }

        private IAIProviderV2? FirstAvailable()
        {
            foreach (IAIProviderV2 provider in ProviderOrder())
            {
                if (provider.IsConfigured && !IsCoolingDown(provider.Name))
                {
                    return provider;
                }
            }

            return null;
        }

        private IAIProviderV2? ResolveProvider(string name)
        {
            foreach (IAIProviderV2 provider in ProviderOrder())
            {
                if (
                    provider.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && provider.IsConfigured
                    && !IsCoolingDown(provider.Name)
                )
                {
                    return provider;
                }
            }

            return null;
        }

        private IEnumerable<IAIProviderV2> GetFallbackOrder(
            IAIProviderV2 selected
        )
        {
            IAIProviderV2[] order = ProviderOrder();
            int selectedIndex = -1;

            for (int i = 0; i < order.Length; i++)
            {
                if (
                    order[i].Name.Equals(
                        selected.Name,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    selectedIndex = i;
                    break;
                }
            }

            if (selectedIndex < 0)
            {
                yield break;
            }

            for (int i = selectedIndex + 1; i < order.Length; i++)
            {
                yield return order[i];
            }
        }

        private IAIProviderV2[] ProviderOrder()
        {
            return new[] { minimax, gemini, groq };
        }

        private bool IsCoolingDown(string providerName)
        {
            if (!cooldownUntil.TryGetValue(providerName, out DateTime until))
            {
                return false;
            }

            if (DateTime.UtcNow >= until)
            {
                cooldownUntil.Remove(providerName);
                return false;
            }

            return true;
        }

        private ProviderReplyV2 BuildUnavailableReply()
        {
            bool anyConfigured = false;
            int shortestWait = int.MaxValue;

            foreach (IAIProviderV2 provider in ProviderOrder())
            {
                if (!provider.IsConfigured)
                {
                    continue;
                }

                anyConfigured = true;

                if (cooldownUntil.TryGetValue(provider.Name, out DateTime until))
                {
                    int wait = Math.Max(
                        1,
                        (int)Math.Ceiling((until - DateTime.UtcNow).TotalSeconds)
                    );
                    shortestWait = Math.Min(shortestWait, wait);
                }
            }

            if (!anyConfigured)
            {
                return new ProviderReplyV2
                {
                    Success = false,
                    Error =
                        "No Agent V2 provider is configured. Set OPENROUTER_API_KEY, GEMINI_API_KEY or GROQ_API_KEY."
                };
            }

            int retry = shortestWait == int.MaxValue ? 90 : shortestWait;

            return new ProviderReplyV2
            {
                Success = false,
                Provider = "rate-limit-guard",
                StatusCode = 429,
                RetryAfterSeconds = retry,
                Error =
                    "All configured free providers are temporarily rate-limited. "
                    + "The host will not waste requests during cooldown. Retry in about "
                    + retry
                    + " seconds."
            };
        }

        private static bool ShouldFallback(int statusCode)
        {
            return
                statusCode == 0
                || statusCode == 400
                || statusCode == 404
                || statusCode == 408
                || statusCode == 409
                || statusCode == 422
                || statusCode == 429
                || statusCode == 500
                || statusCode == 502
                || statusCode == 503
                || statusCode == 504;
        }
    }
}
