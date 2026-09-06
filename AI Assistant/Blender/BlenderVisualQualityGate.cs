using AI_Assistant.AgentV2;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Assistant.Blender
{
    internal sealed class BlenderVisualQualityResult
    {
        public bool Available { get; init; }
        public bool Passed { get; init; }
        public int Score { get; init; }
        public string Feedback { get; init; } = "";
    }

    internal sealed class BlenderVisualQualityGate
    {
        private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
        private readonly Action<string> activity;
        private readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(180) };

        public BlenderVisualQualityGate(Action<string> activity)
        {
            this.activity = activity;
        }

        public async Task<BlenderVisualQualityResult> EvaluateAsync(
            string goal,
            string quality,
            string topology,
            IEnumerable<string> previewPaths,
            CancellationToken token
        )
        {
            string? apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            List<string> images = previewPaths.Where(File.Exists).Take(3).ToList();
            if (string.IsNullOrWhiteSpace(apiKey) || images.Count == 0)
                return new BlenderVisualQualityResult { Available = false, Feedback = "Gemini vision or rendered previews unavailable." };

            activity("[BLENDER VISUAL QA] reviewing three rendered angles");

            List<object> content = new()
            {
                new
                {
                    type = "text",
                    text = "USER GOAL:\n" + goal
                        + "\n\nREQUESTED QUALITY: " + quality
                        + "\n\nTOPOLOGY METRICS:\n" + topology
                        + "\n\nJudge the actual visible result in all attached angles. Return strict JSON only."
                }
            };
            foreach (string image in images)
            {
                string base64 = Convert.ToBase64String(await File.ReadAllBytesAsync(image, token));
                content.Add(new
                {
                    type = "image_url",
                    image_url = new { url = "data:image/png;base64," + base64 }
                });
            }

            object body = new
            {
                model = Environment.GetEnvironmentVariable("GEMINI_VISION_MODEL")
                    ?? Environment.GetEnvironmentVariable("GEMINI_MODEL")
                    ?? "gemini-3.7-flash",
                reasoning_effort = "high",
                max_tokens = 1800,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = "You are a strict senior real-time 3D art lead. Reject grayboxes, disconnected primitive mannequins, floating parts, broken proportions, intersecting assets, unreadable silhouettes, obvious scale/orientation errors, empty surfaces and geometry that merely inflates triangle count. AA means convincing shippable mid-high quality, not AAA photorealism. For characters require a connected anatomical body silhouette, coherent joints, hands/feet/head, clothing or surface detail and believable proportions. For environments require functional composition, grounding, clearance, consistent scale and secondary/tertiary detail. A technically valid FBX is not automatically visually acceptable. Return exactly {\"pass\":bool,\"score\":0-100,\"silhouette\":0-10,\"proportions\":0-10,\"detail\":0-10,\"construction\":0-10,\"composition\":0-10,\"materials\":0-10,\"problems\":[\"...\"],\"repair_instructions\":[\"...\"]}."
                    },
                    new { role = "user", content }
                }
            };

            using HttpRequestMessage request = new(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            try
            {
                using HttpResponseMessage response = await client.SendAsync(request, token);
                string raw = await response.Content.ReadAsStringAsync(token);
                if (!response.IsSuccessStatusCode)
                    return new BlenderVisualQualityResult { Available = false, Feedback = "Vision provider HTTP " + (int)response.StatusCode + "." };

                string? assistant = ReadAssistantContent(raw);
                if (string.IsNullOrWhiteSpace(assistant))
                    return new BlenderVisualQualityResult { Available = false, Feedback = "Vision provider returned no content." };

                using JsonDocument document = JsonDocument.Parse(AgentJsonV2.ExtractObject(assistant));
                JsonElement root = document.RootElement;
                bool modelPass = root.TryGetProperty("pass", out JsonElement pass) && pass.ValueKind == JsonValueKind.True;
                int score = root.TryGetProperty("score", out JsonElement scoreElement) && scoreElement.TryGetInt32(out int parsedScore)
                    ? Math.Clamp(parsedScore, 0, 100)
                    : 0;
                int minimum = quality.Equals("AA", StringComparison.OrdinalIgnoreCase) ? 72 : 62;
                string feedback = JoinFeedback(root, "problems") + " " + JoinFeedback(root, "repair_instructions");
                return new BlenderVisualQualityResult
                {
                    Available = true,
                    Passed = modelPass && score >= minimum,
                    Score = score,
                    Feedback = string.IsNullOrWhiteSpace(feedback) ? "No detailed visual feedback returned." : feedback.Trim()
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new BlenderVisualQualityResult { Available = false, Feedback = ex.GetType().Name + ": " + ex.Message };
            }
        }

        private static string? ReadAssistantContent(string raw)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(raw);
                JsonElement choices = document.RootElement.GetProperty("choices");
                JsonElement content = choices[0].GetProperty("message").GetProperty("content");
                return content.ValueKind == JsonValueKind.String ? content.GetString() : content.GetRawText();
            }
            catch { return null; }
        }

        private static string JoinFeedback(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out JsonElement values) || values.ValueKind != JsonValueKind.Array) return "";
            return string.Join("; ", values.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Take(10));
        }
    }
}
