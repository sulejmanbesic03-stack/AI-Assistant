using AI_Assistant.Runtime;

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
        public int RetryAfterSeconds { get; init; }
    }

    internal interface IAIProviderV2
    {
        string Name { get; }
        bool IsConfigured { get; }
        string ModelName { get; }

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
        public string ModelName => Model;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable)
            );

        public OpenAiCompatibleProviderV2(
            string name,
            string endpoint,
            string model,
            string apiKeyEnvironmentVariable,
            int timeoutSeconds = 180
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

        protected virtual void ConfigureHeaders(HttpRequestMessage request, string apiKey)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        protected async Task<ProviderReplyV2> SendAsync(
            Dictionary<string, object?> requestBody,
            string requestedModel,
            CancellationToken cancellationToken
        )
        {
            string? apiKey = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
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

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            ConfigureHeaders(request, apiKey);
            request.Content = new StringContent(
                JsonSerializer.Serialize(requestBody),
                Encoding.UTF8,
                "application/json"
            );

            try
            {
                using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
                string responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                string responseModel = TryReadModel(responseText) ?? requestedModel;
                int retryAfterSeconds = GetRetryAfterSeconds(response);

                if (!response.IsSuccessStatusCode)
                {
                    return new ProviderReplyV2
                    {
                        Success = false,
                        Provider = Name,
                        Model = responseModel,
                        StatusCode = (int)response.StatusCode,
                        RetryAfterSeconds = retryAfterSeconds,
                        Error = ReadError(responseText, response.ReasonPhrase)
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
                        Error = "Provider returned no assistant content. Raw response: "
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
                bool cancelled = cancellationToken.IsCancellationRequested;
                return new ProviderReplyV2
                {
                    Success = false,
                    Provider = Name,
                    Model = requestedModel,
                    StatusCode = cancelled ? 499 : (int)HttpStatusCode.RequestTimeout,
                    Error = cancelled
                        ? "Provider request was cancelled by the user."
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

        private static string ReadError(string responseText, string? reason)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(responseText);
                if (doc.RootElement.TryGetProperty("error", out JsonElement error))
                {
                    if (error.ValueKind == JsonValueKind.String)
                    {
                        return error.GetString() ?? "Provider error.";
                    }
                    if (error.ValueKind == JsonValueKind.Object
                        && error.TryGetProperty("message", out JsonElement message)
                        && message.ValueKind == JsonValueKind.String)
                    {
                        return message.GetString() ?? error.GetRawText();
                    }
                    return error.GetRawText();
                }
            }
            catch
            {
            }

            return string.IsNullOrWhiteSpace(responseText)
                ? reason ?? "Provider error."
                : AgentJsonV2.Compact(responseText, 1600);
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
                return Math.Max(1, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
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

        private static string? TryReadContent(string responseText)
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

                JsonElement first = choices[0];
                if (!first.TryGetProperty("message", out JsonElement message))
                {
                    return null;
                }

                if (message.TryGetProperty("content", out JsonElement content))
                {
                    return content.ValueKind == JsonValueKind.String
                        ? content.GetString()
                        : content.GetRawText();
                }

                return null;
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
                if (document.RootElement.TryGetProperty("model", out JsonElement model)
                    && model.ValueKind == JsonValueKind.String)
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

    internal sealed class OpenRouterFreeProviderV2 : OpenAiCompatibleProviderV2
    {
        public OpenRouterFreeProviderV2()
            : base(
                "OpenRouter-Free",
                "https://openrouter.ai/api/v1/chat/completions",
                Environment.GetEnvironmentVariable("OPENROUTER_MODEL") ?? "openrouter/free",
                "OPENROUTER_API_KEY",
                180
            )
        {
        }

        protected override Dictionary<string, object?> BuildRequestBody(string systemPrompt, string userPrompt)
        {
            Dictionary<string, object?> body = base.BuildRequestBody(systemPrompt, userPrompt);
            body.Remove("temperature");
            return body;
        }

        protected override void ConfigureHeaders(HttpRequestMessage request, string apiKey)
        {
            base.ConfigureHeaders(request, apiKey);
            request.Headers.TryAddWithoutValidation(
                "X-OpenRouter-Title",
                "AI Assistant Cowork Beta"
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
                Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-3.7-flash",
                "GEMINI_API_KEY",
                180
            )
        {
            reasoningEffort = NormalizeReasoningEffort(
                Environment.GetEnvironmentVariable("GEMINI_REASONING_EFFORT") ?? "high"
            );
        }

        protected override Dictionary<string, object?> BuildRequestBody(string systemPrompt, string userPrompt)
        {
            Dictionary<string, object?> body = base.BuildRequestBody(systemPrompt, userPrompt);
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
        private readonly IAIProviderV2 openRouter;
        private readonly IAIProviderV2 gemini;
        private readonly IAIProviderV2 groq;
        private readonly Action<string> activity;

        private readonly Dictionary<string, DateTime> cooldownUntil =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, ProviderScore> scores =
            new Dictionary<string, ProviderScore>(StringComparer.OrdinalIgnoreCase);

        public ProviderRouterV2(Action<string> activity)
        {
            this.activity = activity;

            openRouter = new OpenRouterFreeProviderV2();
            gemini = new GeminiProviderV2();
            groq = new OpenAiCompatibleProviderV2(
                "Groq",
                "https://api.groq.com/openai/v1/chat/completions",
                Environment.GetEnvironmentVariable("GROQ_MODEL") ?? "openai/gpt-oss-120b",
                "GROQ_API_KEY",
                180
            );
        }

        public async Task<ProviderReplyV2> CompleteAsync(
            AgentTaskStateV2 task,
            string systemPrompt,
            string userPrompt,
            CancellationToken cancellationToken = default
        )
        {
            if (AgentCancellationHub.IsCancellationRequested)
            {
                return CancelledReply();
            }

            using CancellationTokenSource linkedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    AgentCancellationHub.Token
                );

            CancellationToken effectiveToken = linkedCancellation.Token;
            List<IAIProviderV2> candidates = BuildCandidateOrder(task);
            if (candidates.Count == 0)
            {
                return BuildUnavailableReply();
            }

            ProviderReplyV2 last = new ProviderReplyV2
            {
                Success = false,
                Error = "No provider attempt was made."
            };

            foreach (IAIProviderV2 provider in candidates)
            {
                if (effectiveToken.IsCancellationRequested)
                {
                    return CancelledReply();
                }

                last = await CallProvider(
                    task,
                    provider,
                    systemPrompt,
                    userPrompt,
                    effectiveToken
                );

                UpdateProviderState(last);

                if (last.Success)
                {
                    return last;
                }

                if (last.StatusCode == 499 || effectiveToken.IsCancellationRequested)
                {
                    return CancelledReply(provider.Name, provider.ModelName);
                }

                if (!ShouldFallback(last.StatusCode))
                {
                    return last;
                }

                activity(
                    "[V2 PROVIDER] " + provider.Name
                    + " failed HTTP " + last.StatusCode
                    + "; trying next free provider"
                );
            }

            return last;
        }

        private List<IAIProviderV2> BuildCandidateOrder(AgentTaskStateV2 task)
        {
            IEnumerable<IAIProviderV2> baseOrder;

            if (task.Phase == AgentTaskPhaseV2.Correcting)
            {
                baseOrder = new[] { groq, gemini, openRouter };
            }
            else
            {
                baseOrder = new[] { openRouter, gemini, groq };
            }

            List<IAIProviderV2> available = baseOrder
                .Where(p => p.IsConfigured && !IsCoolingDown(p.Name))
                .ToList();

            if (task.Phase != AgentTaskPhaseV2.Correcting
                && !string.IsNullOrWhiteSpace(task.ActiveProvider))
            {
                IAIProviderV2? sticky = available.FirstOrDefault(
                    p => p.Name.Equals(task.ActiveProvider, StringComparison.OrdinalIgnoreCase)
                );
                if (sticky != null)
                {
                    available.Remove(sticky);
                    available.Insert(0, sticky);
                }
            }

            return available;
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

            int approxInputTokens = Math.Max(1, (systemPrompt.Length + userPrompt.Length + 3) / 4);
            ProviderScore score = GetScore(provider.Name);

            activity(
                "[V2 MODEL] " + provider.Name
                + " / " + provider.ModelName
                + " call " + task.ModelCalls
                + " success-rate=" + score.SuccessRate.ToString("P0")
            );
            activity(
                "[V2 TOKENS] approx input " + approxInputTokens
                + " · free-first route"
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

        private void UpdateProviderState(ProviderReplyV2 reply)
        {
            if (string.IsNullOrWhiteSpace(reply.Provider)
                || reply.StatusCode == 499)
            {
                return;
            }

            ProviderScore score = GetScore(reply.Provider);
            score.Attempts++;

            if (reply.Success)
            {
                score.Successes++;
                cooldownUntil.Remove(reply.Provider);
                return;
            }

            score.Failures++;

            if (reply.StatusCode == 429)
            {
                int seconds = reply.RetryAfterSeconds > 0
                    ? Math.Clamp(reply.RetryAfterSeconds, 1, 86400)
                    : 90;
                cooldownUntil[reply.Provider] = DateTime.UtcNow.AddSeconds(seconds);
                activity(
                    "[V2 RATE LIMIT] " + reply.Provider
                    + " cooldown " + seconds + "s; no blind retries"
                );
            }
        }

        private ProviderScore GetScore(string provider)
        {
            if (!scores.TryGetValue(provider, out ProviderScore? score) || score == null)
            {
                score = new ProviderScore();
                scores[provider] = score;
            }
            return score;
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
            bool configured = new[] { openRouter, gemini, groq }.Any(p => p.IsConfigured);
            if (!configured)
            {
                return new ProviderReplyV2
                {
                    Success = false,
                    Error = "No Agent V2 provider is configured. Set OPENROUTER_API_KEY, GEMINI_API_KEY or GROQ_API_KEY."
                };
            }

            int retry = 90;
            if (cooldownUntil.Count > 0)
            {
                TimeSpan remaining = cooldownUntil.Values.Min() - DateTime.UtcNow;
                retry = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
            }

            return new ProviderReplyV2
            {
                Success = false,
                Provider = "rate-limit-guard",
                StatusCode = 429,
                RetryAfterSeconds = retry,
                Error = "All configured free providers are cooling down. Retry in about " + retry + " seconds."
            };
        }

        private static ProviderReplyV2 CancelledReply(
            string provider = "cancelled",
            string model = ""
        )
        {
            return new ProviderReplyV2
            {
                Success = false,
                Provider = provider,
                Model = model,
                StatusCode = 499,
                Error = "Agent work was cancelled by the user."
            };
        }

        private static bool ShouldFallback(int statusCode)
        {
            return statusCode == 0
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

        private sealed class ProviderScore
        {
            public int Attempts { get; set; }
            public int Successes { get; set; }
            public int Failures { get; set; }
            public double SuccessRate => Attempts == 0 ? 0.5 : (double)Successes / Attempts;
        }
    }
}
