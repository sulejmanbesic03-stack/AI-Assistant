using AI_Assistant.Runtime;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Assistant.AI
{
    /// <summary>
    /// Creates an internal visual reference for an asset request. This is
    /// intentionally separate from Blender MCP: the image is feedback and a
    /// design reference, not a Blender tool call or a chat message.
    /// </summary>
    internal sealed class VisualPreviewService : IDisposable
    {
        private const string GeminiEndpoint =
            "https://generativelanguage.googleapis.com/v1beta/interactions";

        private const string DefaultImageModel = "gemini-3.1-flash-image";
        private const int RequestTimeoutSeconds = 120;

        private readonly HttpClient client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds)
        };

        private readonly Action<string> activity;
        private readonly string previewDirectory;

        public string? LastPreviewPath { get; private set; }

        public VisualPreviewService(Action<string> activity)
        {
            this.activity = activity;
            previewDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AI Assistant",
                "Previews"
            );
        }

        public async Task<string?> GenerateAsync(
            string userPrompt,
            CancellationToken cancellationToken = default
        )
        {
            // Never let a failed request accidentally reuse the previous
            // asset's reference image for a new Blender task.
            LastPreviewPath = null;

            if (string.Equals(Environment.GetEnvironmentVariable("AI_PREVIEW_ENABLED"), "0", StringComparison.OrdinalIgnoreCase))
            {
                activity("[PREVIEW] disabled by AI_PREVIEW_ENABLED");
                return null;
            }

            string? apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                activity("[PREVIEW] skipped · GEMINI_API_KEY nije konfigurisan");
                return null;
            }

            string model = Environment.GetEnvironmentVariable("GEMINI_IMAGE_MODEL") ?? "";
            if (string.IsNullOrWhiteSpace(model))
            {
                model = DefaultImageModel;
            }

            string prompt = BuildPreviewPrompt(userPrompt);
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                ["model"] = model,
                ["input"] = new object[]
                {
                    new { type = "text", text = prompt }
                }
            };

            try
            {
                activity("[PREVIEW] generating internal reference · Gemini " + model);

                using HttpRequestMessage request = new HttpRequestMessage(
                    HttpMethod.Post,
                    GeminiEndpoint
                );
                request.Headers.Add("x-goog-api-key", apiKey);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
                    activity("[PREVIEW] Gemini HTTP " + (int)response.StatusCode + " · " + Compact(responseText, 500));
                    return null;
                }

                string? encodedImage = FindImageData(responseText);
                if (string.IsNullOrWhiteSpace(encodedImage))
                {
                    activity("[PREVIEW] Gemini nije vratio image output");
                    return null;
                }

                byte[] imageBytes;
                try
                {
                    imageBytes = Convert.FromBase64String(encodedImage);
                }
                catch (FormatException)
                {
                    activity("[PREVIEW] Gemini je vratio neispravan image payload");
                    return null;
                }

                if (imageBytes.Length < 64)
                {
                    activity("[PREVIEW] image payload je prazan ili premalen");
                    return null;
                }

                Directory.CreateDirectory(previewDirectory);
                string temporaryPath = Path.Combine(
                    previewDirectory,
                    "preview-" + Guid.NewGuid().ToString("N") + ".tmp"
                );
                string latestPath = Path.Combine(previewDirectory, "latest.png");

                await File.WriteAllBytesAsync(temporaryPath, imageBytes, cancellationToken);
                File.Move(temporaryPath, latestPath, true);
                LastPreviewPath = latestPath;
                activity("[PREVIEW] ready · " + latestPath);
                return latestPath;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                activity("[PREVIEW] cancelled");
                return null;
            }
            catch (Exception ex)
            {
                // A preview is optional feedback. Never turn an image-provider
                // failure into a failed Blender or Unity task.
                activity("[PREVIEW] failed · " + ex.GetType().Name + ": " + Compact(ex.Message, 500));
                return null;
            }
        }

        private static string BuildPreviewPrompt(string userPrompt)
        {
            return
                "Create an internal visual concept reference for this 3D asset request: "
                + userPrompt
                + ". Show the complete subject clearly in a neutral studio, three-quarter view, "
                + "full body when it is a character, plain background, readable silhouette, "
                + "consistent proportions, practical game-asset design, no text, no logo, no UI, "
                + "no watermark-like labels, no extra characters. This image is only a visual "
                + "reference for a later Blender/3D workflow; prioritize the requested subject and details.";
        }

        private static string? FindImageData(string responseText)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(responseText);
                return FindImageData(document.RootElement, 0);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? FindImageData(JsonElement element, int depth)
        {
            if (depth > 8
                || (element.ValueKind != JsonValueKind.Object
                    && element.ValueKind != JsonValueKind.Array))
            {
                return null;
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                bool imageLike = element.TryGetProperty("type", out JsonElement type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "image", StringComparison.OrdinalIgnoreCase);

                if (imageLike && element.TryGetProperty("data", out JsonElement imageData)
                    && imageData.ValueKind == JsonValueKind.String)
                {
                    return imageData.GetString();
                }

                if (element.TryGetProperty("output_image", out JsonElement outputImage))
                {
                    string? direct = FindBase64Data(outputImage);
                    if (!string.IsNullOrWhiteSpace(direct))
                    {
                        return direct;
                    }
                }

                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string? nested = FindImageData(property.Value, depth + 1);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }
            else
            {
                foreach (JsonElement child in element.EnumerateArray())
                {
                    string? nested = FindImageData(child, depth + 1);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }

            return null;
        }

        private static string? FindBase64Data(JsonElement element)
        {
            return element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.String
                ? data.GetString()
                : null;
        }

        private static string Compact(string? value, int max)
        {
            string text = value ?? "";
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        public void Dispose()
        {
            client.Dispose();
        }
    }
}
