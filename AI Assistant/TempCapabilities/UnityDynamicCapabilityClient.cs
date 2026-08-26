using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;


namespace AI_Assistant.TempCapabilities
{
    internal sealed class UnityDynamicCapabilityClient
    {
        private const string Endpoint =
            "http://127.0.0.1:47825/execute-capability";

        private const int MaxSourceChars =
            40000;

        private readonly HttpClient client;


        public UnityDynamicCapabilityClient()
        {
            client =
                new HttpClient
                {
                    Timeout =
                        TimeSpan.FromSeconds(120)
                };

            client.DefaultRequestHeaders.Add(
                "X-AI-Bridge",
                "AI-Assistant-Local"
            );

            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/json"
                )
            );
        }


        public string Execute(
            string capabilityName,
            string sourceCode,
            string argumentsJson
        )
        {
            string? validationError =
                ValidateInput(
                    capabilityName,
                    sourceCode
                );

            if (validationError != null)
            {
                return JsonSerializer.Serialize(
                    new
                    {
                        success = false,
                        phase = "validation",
                        message = validationError
                    }
                );
            }

            if (string.IsNullOrWhiteSpace(argumentsJson))
            {
                argumentsJson = "{}";
            }

            try
            {
                using JsonDocument arguments =
                    JsonDocument.Parse(argumentsJson);

                string requestJson =
                    JsonSerializer.Serialize(
                        new
                        {
                            name = capabilityName,
                            source = sourceCode,
                            argumentsJson =
                                arguments.RootElement.GetRawText()
                        }
                    );

                using StringContent content =
                    new StringContent(
                        requestJson,
                        Encoding.UTF8,
                        "application/json"
                    );

                using HttpResponseMessage response =
                    client
                        .PostAsync(
                            Endpoint,
                            content
                        )
                        .GetAwaiter()
                        .GetResult();

                string responseBody =
                    response.Content
                        .ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();

                if (!string.IsNullOrWhiteSpace(responseBody))
                {
                    return responseBody;
                }

                return JsonSerializer.Serialize(
                    new
                    {
                        success = false,
                        phase = "transport",
                        message =
                            "Unity dynamic capability server returned an empty response.",
                        statusCode =
                            (int)response.StatusCode
                    }
                );
            }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(
                    new
                    {
                        success = false,
                        phase = "arguments",
                        message =
                            "Capability arguments are not valid JSON: "
                            + ex.Message
                    }
                );
            }
            catch (TaskCanceledException)
            {
                return JsonSerializer.Serialize(
                    new
                    {
                        success = false,
                        phase = "timeout",
                        message =
                            "Unity dynamic capability timed out after 120 seconds. "
                            + "Do not automatically retry because Unity may have completed part of the execution."
                    }
                );
            }
            catch (HttpRequestException ex)
            {
                return JsonSerializer.Serialize(
                    new
                    {
                        success = false,
                        phase = "offline",
                        message =
                            "Unity dynamic capability server is unavailable on 127.0.0.1:47825: "
                            + ex.Message
                    }
                );
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(
                    new
                    {
                        success = false,
                        phase = "client",
                        message =
                            ex.GetType().Name
                            + ": "
                            + ex.Message
                    }
                );
            }
        }


        private static string? ValidateInput(
            string capabilityName,
            string sourceCode
        )
        {
            if (
                string.IsNullOrWhiteSpace(capabilityName)
                || !Regex.IsMatch(
                    capabilityName,
                    "^[A-Za-z][A-Za-z0-9_]{0,63}$"
                )
            )
            {
                return
                    "Capability name must start with a letter and contain only letters, numbers or underscore.";
            }

            if (string.IsNullOrWhiteSpace(sourceCode))
            {
                return "Capability source is empty.";
            }

            if (sourceCode.Length > MaxSourceChars)
            {
                return
                    "Capability source is too large ("
                    + sourceCode.Length
                    + "/"
                    + MaxSourceChars
                    + " characters).";
            }

            if (
                !sourceCode.Contains(
                    "IUnityDynamicCapability",
                    StringComparison.Ordinal
                )
            )
            {
                return
                    "Source must implement IUnityDynamicCapability.";
            }

            return null;
        }
    }
}
