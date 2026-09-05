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
        protected readonly int MaxCompletionTokens;

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
            int timeoutSeconds = 180,
            int maxCompletionTokens = 12000
        )
        {
            Name = name;
            Endpoint = endpoint;
            Model = model;
            ApiKeyEnvironmentVariable = apiKeyEnvironmentVariable;
            MaxCompletionTokens = Math.Max(0, maxCompletionTokens);
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
            Dictionary<string, object?> body = new Dictionary<string, object?>
            {
                ["model"] = Model,
                ["messages"] = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                ["temperature"] = 0.1
            };

            if (MaxCompletionTokens > 0)
            {
                body["max_tokens"] = MaxCompletionTokens;
            }

            return body;
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
                Error = "No configured AI provider completed the request."
            };

            foreach (IAIProviderV2 provider in candidates)
            {
                if (effectiveToken.IsCancellationRequested)
                {
                    return CancelledReply();
                }

                if (!provider.IsConfigured || IsCoolingDown(provider.Name))
                {
                    continue;
                }

                task.ActiveProvider = provider.Name;
                task.ModelCalls++;
                activity("[V2 MODEL] " + provider.Name + " / " + provider.ModelName + " call " + task.ModelCalls);

                ProviderReplyV2 reply = await provider.CompleteAsync(
                    systemPrompt,
                    userPrompt,
                    effectiveToken
                );
                last = reply;

                if (reply.Success)
                {
                    RecordSuccess(provider.Name);
                    return reply;
                }

                RecordFailure(provider.Name);
                if (reply.StatusCode == 429)
                {
                    int cooldown = Math.Max(15, reply.RetryAfterSeconds);
                    cooldownUntil[provider.Name] = DateTime.UtcNow.AddSeconds(cooldown);
                    activity("[V2 PROVIDER] " + provider.Name + " cooling down for " + cooldown + "s");
                    continue;
                }

                if (reply.StatusCode == 499)
                {
                    return reply;
                }
            }

            return last;
        }

        private List<IAIProviderV2> BuildCandidateOrder(AgentTaskStateV2 task)
        {
            List<IAIProviderV2> baseOrder = task.Phase == AgentTaskPhaseV2.Correcting
                ? new List<IAIProviderV2> { groq, gemini, openRouter }
                : new List<IAIProviderV2> { openRouter, gemini, groq };

            return baseOrder
                .Where(provider => provider.IsConfigured)
                .OrderByDescending(provider => Score(provider.Name))
                .ThenBy(provider => baseOrder.IndexOf(provider))
                .ToList();
        }

        private bool IsCoolingDown(string provider)
        {
            return cooldownUntil.TryGetValue(provider, out DateTime until)
                && until > DateTime.UtcNow;
        }

        private double Score(string provider)
        {
            if (!scores.TryGetValue(provider, out ProviderScore? score))
            {
                return 0;
            }
            return score.Successes - (score.Failures * 0.65);
        }

        private void RecordSuccess(string provider)
        {
            ProviderScore score = GetScore(provider);
            score.Successes++;
        }

        private void RecordFailure(string provider)
        {
            ProviderScore score = GetScore(provider);
            score.Failures++;
        }

        private ProviderScore GetScore(string provider)
        {
            if (!scores.TryGetValue(provider, out ProviderScore? score))
            {
                score = new ProviderScore();
                scores[provider] = score;
            }
            return score;
        }

        private static ProviderReplyV2 CancelledReply()
        {
            return new ProviderReplyV2
            {
                Success = false,
                StatusCode = 499,
                Error = "Provider request was cancelled by the user."
            };
        }

        private static ProviderReplyV2 BuildUnavailableReply()
        {
            return new ProviderReplyV2
            {
                Success = false,
                Error = "No configured free provider is currently available."
            };
        }

        private sealed class ProviderScore
        {
            public int Successes;
            public int Failures;
        }
    }
}
