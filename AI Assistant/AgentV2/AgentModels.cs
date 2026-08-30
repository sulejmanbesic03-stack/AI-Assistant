using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AI_Assistant.AgentV2
{
    public enum AgentModeV2
    {
        Agent,
        Plan
    }

    public enum AgentTaskPhaseV2
    {
        Created,
        Inspecting,
        Designing,
        Executing,
        Observing,
        Correcting,
        Repairing,
        Verifying,
        Completed,
        Failed
    }

    public sealed class AgentTaskStateV2
    {
        public string TaskId { get; set; } =
            Guid.NewGuid().ToString("N")[..10];

        public string Goal { get; set; } = "";

        public AgentTaskPhaseV2 Phase { get; set; } =
            AgentTaskPhaseV2.Created;

        public string ActiveProvider { get; set; } = "";
        public int ModelCalls { get; set; }
        public int ExecutionAttempts { get; set; }
        public bool Completed { get; set; }

        public List<string> CompletedSteps { get; } =
            new List<string>();

        public List<string> FilesChanged { get; } =
            new List<string>();

        public List<string> AttemptFingerprints { get; } =
            new List<string>();

        public string LastSummary { get; set; } = "";
        public string LastObservation { get; set; } = "";

        public DateTime CreatedUtc { get; } = DateTime.UtcNow;

        public void Advance(
            AgentTaskPhaseV2 phase,
            string? completedStep = null
        )
        {
            Phase = phase;

            if (!string.IsNullOrWhiteSpace(completedStep))
            {
                CompletedSteps.Add(completedStep);
            }
        }
    }

    public sealed class UnityProjectSnapshotV2
    {
        public string ProjectSettings { get; set; } = "";
        public string SceneHierarchy { get; set; } = "";
        public string ConsoleErrors { get; set; } = "";
        public string ScriptIndex { get; set; } = "";

        public Dictionary<string, string> RelevantScripts { get; } =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase
            );

        public string Documentation { get; set; } = "";
    }

    public sealed class AgentImplementationV2
    {
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = "";

        [JsonPropertyName("needs_documentation")]
        public bool NeedsDocumentation { get; set; }

        [JsonPropertyName("documentation_query")]
        public string DocumentationQuery { get; set; } = "";

        [JsonPropertyName("script_changes")]
        public List<ScriptChangeV2> ScriptChanges { get; set; } =
            new List<ScriptChangeV2>();

        [JsonPropertyName("scene_actions")]
        public List<SceneActionV2> SceneActions { get; set; } =
            new List<SceneActionV2>();

        [JsonPropertyName("capability_call")]
        public ReusableCapabilityCallV2? CapabilityCall { get; set; }

        [JsonPropertyName("temporary_capability")]
        public TempCapabilitySpecV2? TemporaryCapability { get; set; }

        [JsonPropertyName("runtime_object_paths")]
        public List<string> RuntimeObjectPaths { get; set; } =
            new List<string>();

        [JsonPropertyName("notes")]
        public List<string> Notes { get; set; } =
            new List<string>();

        public bool HasConcreteWork()
        {
            return
                ScriptChanges.Count > 0
                || SceneActions.Count > 0
                || CapabilityCall != null
                || TemporaryCapability != null;
        }
    }

    public sealed class ScriptChangeV2
    {
        [JsonPropertyName("asset_path")]
        public string AssetPath { get; set; } = "";

        [JsonPropertyName("class_name")]
        public string ClassName { get; set; } = "";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("overwrite")]
        public bool Overwrite { get; set; } = true;

        [JsonPropertyName("attach_to")]
        public string AttachTo { get; set; } = "";
    }

    public sealed class SceneActionV2
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        [JsonPropertyName("object_path")]
        public string ObjectPath { get; set; } = "";

        [JsonPropertyName("parent_path")]
        public string ParentPath { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("new_name")]
        public string NewName { get; set; } = "";

        [JsonPropertyName("component_type")]
        public string ComponentType { get; set; } = "";

        [JsonPropertyName("script_type")]
        public string ScriptType { get; set; } = "";

        [JsonPropertyName("primitive_type")]
        public string PrimitiveType { get; set; } = "";

        [JsonPropertyName("asset_path")]
        public string AssetPath { get; set; } = "";

        [JsonPropertyName("material_path")]
        public string MaterialPath { get; set; } = "";

        [JsonPropertyName("shader_name")]
        public string ShaderName { get; set; } = "";

        [JsonPropertyName("x")]
        public float? X { get; set; }

        [JsonPropertyName("y")]
        public float? Y { get; set; }

        [JsonPropertyName("z")]
        public float? Z { get; set; }

        [JsonPropertyName("red")]
        public float? Red { get; set; }

        [JsonPropertyName("green")]
        public float? Green { get; set; }

        [JsonPropertyName("blue")]
        public float? Blue { get; set; }

        [JsonPropertyName("alpha")]
        public float? Alpha { get; set; }

        [JsonPropertyName("mass")]
        public float? Mass { get; set; }

        [JsonPropertyName("use_gravity")]
        public bool? UseGravity { get; set; }

        [JsonPropertyName("is_kinematic")]
        public bool? IsKinematic { get; set; }

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("is_trigger")]
        public bool? IsTrigger { get; set; }

        [JsonPropertyName("active")]
        public bool? Active { get; set; }
    }

    public sealed class ReusableCapabilityCallV2
    {
        [JsonPropertyName("tool_name")]
        public string ToolName { get; set; } = "";

        [JsonPropertyName("arguments_json")]
        public string ArgumentsJson { get; set; } = "{}";
    }

    public sealed class TempCapabilitySpecV2
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("arguments_json")]
        public string ArgumentsJson { get; set; } = "{}";
    }

    public sealed class AgentExecutionReportV2
    {
        public bool Success { get; set; } = true;
        public bool CompileFailed { get; set; }
        public string CompileFailureText { get; set; } = "";
        public string ConsoleResult { get; set; } = "";

        public List<string> Steps { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        public List<string> RuntimeResults { get; } = new List<string>();
        public List<string> FilesChanged { get; } = new List<string>();

        public void Fail(string message)
        {
            Success = false;
            Errors.Add(message);
        }

        public void MergeFrom(AgentExecutionReportV2 other)
        {
            Success = Success && other.Success;
            CompileFailed = CompileFailed || other.CompileFailed;

            if (!string.IsNullOrWhiteSpace(other.CompileFailureText))
            {
                CompileFailureText = other.CompileFailureText;
            }

            if (!string.IsNullOrWhiteSpace(other.ConsoleResult))
            {
                ConsoleResult = other.ConsoleResult;
            }

            Steps.AddRange(other.Steps);
            Errors.AddRange(other.Errors);
            RuntimeResults.AddRange(other.RuntimeResults);
            FilesChanged.AddRange(other.FilesChanged);
        }
    }

    internal static class AgentJsonV2
    {
        private static readonly JsonSerializerOptions Options =
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true
            };

        public static bool TryParseImplementation(
            string text,
            out AgentImplementationV2 implementation,
            out string error
        )
        {
            implementation = new AgentImplementationV2();
            error = "";

            try
            {
                string json = ExtractObject(text);

                AgentImplementationV2? parsed =
                    JsonSerializer.Deserialize<AgentImplementationV2>(
                        json,
                        Options
                    );

                if (parsed == null)
                {
                    error = "Model returned an empty implementation JSON object.";
                    return false;
                }

                parsed.ScriptChanges ??= new List<ScriptChangeV2>();
                parsed.SceneActions ??= new List<SceneActionV2>();
                parsed.RuntimeObjectPaths ??= new List<string>();
                parsed.Notes ??= new List<string>();

                implementation = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        public static string ExtractObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new JsonException("Response is empty.");
            }

            string cleaned = text
                .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                .Replace("```", "", StringComparison.Ordinal)
                .Trim();

            int start = cleaned.IndexOf('{');
            int end = cleaned.LastIndexOf('}');

            if (start < 0 || end <= start)
            {
                throw new JsonException(
                    "No JSON object was found in the model response."
                );
            }

            return cleaned.Substring(start, end - start + 1);
        }

        public static string? FindStringProperty(
            string json,
            string propertyName
        )
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);

                return FindStringProperty(
                    document.RootElement,
                    propertyName
                );
            }
            catch
            {
                return null;
            }
        }

        private static string? FindStringProperty(
            JsonElement element,
            string propertyName
        )
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (
                        string.Equals(
                            property.Name,
                            propertyName,
                            StringComparison.OrdinalIgnoreCase
                        )
                        && property.Value.ValueKind == JsonValueKind.String
                    )
                    {
                        return property.Value.GetString();
                    }

                    string? nested = FindStringProperty(
                        property.Value,
                        propertyName
                    );

                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    string? nested = FindStringProperty(
                        item,
                        propertyName
                    );

                    if (nested != null)
                    {
                        return nested;
                    }
                }
            }

            return null;
        }

        public static bool LooksSuccessful(string result)
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                return false;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(result);

                if (
                    document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty(
                        "success",
                        out JsonElement success
                    )
                    && (
                        success.ValueKind == JsonValueKind.True
                        || success.ValueKind == JsonValueKind.False
                    )
                )
                {
                    return success.GetBoolean();
                }
            }
            catch
            {
                // Some bridge endpoints return plain text on success.
            }

            string normalized = result.ToLowerInvariant();

            return
                !normalized.Contains("error")
                && !normalized.Contains("failed")
                && !normalized.Contains("denied")
                && !normalized.Contains("timeout");
        }

        public static string Compact(
            string? value,
            int maxChars
        )
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            string text = value.Trim();

            if (text.Length <= maxChars)
            {
                return text;
            }

            return text.Substring(0, maxChars)
                + "\n...[truncated by Agent V2]";
        }
    }
}
