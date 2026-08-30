using AI_Assistant.Tools;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AI_Assistant.AgentV2
{
    internal sealed class UnityContextServiceV2
    {
        private const int MaxProjectSettingsChars = 1200;
        private const int MaxHierarchyChars = 3500;
        private const int MaxConsoleChars = 1000;
        private const int MaxScriptIndexChars = 2200;
        private const int MaxScriptChars = 3200;
        private const int MaxRelevantScripts = 2;

        private readonly UnityBridgeTools unity;
        private readonly UnityDocumentationTools docs;
        private readonly Action<string> activity;

        public UnityContextServiceV2(
            UnityBridgeTools unity,
            Action<string> activity
        )
        {
            this.unity = unity;
            this.activity = activity;
            docs = new UnityDocumentationTools();
        }

        public Task<UnityProjectSnapshotV2> CaptureAsync(string goal)
        {
            return Task.Run(() => Capture(goal));
        }

        private UnityProjectSnapshotV2 Capture(string goal)
        {
            activity("[V2 INSPECT] compact project snapshot");

            UnityProjectSnapshotV2 snapshot =
                new UnityProjectSnapshotV2
                {
                    ProjectSettings = AgentJsonV2.Compact(
                        unity.GetUnityProjectSettings(),
                        MaxProjectSettingsChars
                    ),
                    SceneHierarchy = AgentJsonV2.Compact(
                        unity.GetSceneHierarchy(),
                        MaxHierarchyChars
                    ),
                    ConsoleErrors = CompactConsole(
                        unity.GetConsoleErrors()
                    )
                };

            List<string> searchTerms = BuildSearchTerms(goal);
            StringBuilder indexBuilder = new StringBuilder();
            HashSet<string> scriptPaths =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string term in searchTerms.Take(3))
            {
                string result = unity.FindUnityScripts(term);

                indexBuilder.Append("SEARCH: ");
                indexBuilder.AppendLine(term);
                indexBuilder.AppendLine(
                    AgentJsonV2.Compact(result, 900)
                );

                foreach (string path in ExtractAssetPaths(result))
                {
                    scriptPaths.Add(path);
                }
            }

            snapshot.ScriptIndex = AgentJsonV2.Compact(
                indexBuilder.ToString(),
                MaxScriptIndexChars
            );

            foreach (
                string scriptPath
                in scriptPaths.Take(MaxRelevantScripts)
            )
            {
                activity("[V2 INSPECT] read " + scriptPath);

                string source = unity.ReadUnityScript(
                    scriptPath,
                    1,
                    360
                );

                snapshot.RelevantScripts[scriptPath] =
                    AgentJsonV2.Compact(source, MaxScriptChars);
            }

            return snapshot;
        }

        public Task<string> GetDocumentationAsync(string query)
        {
            return Task.Run(() => GetDocumentation(query));
        }

        private string GetDocumentation(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "";
            }

            activity(
                "[V2 DOCS] "
                + AgentJsonV2.Compact(query, 120)
            );

            string search = docs.SearchUnityDocs(query);
            string? firstUrl = ExtractFirstDocsUrl(search);

            if (string.IsNullOrWhiteSpace(firstUrl))
            {
                return AgentJsonV2.Compact(search, 2200);
            }

            string document = docs.ReadUnityDoc(firstUrl);

            return AgentJsonV2.Compact(
                search + "\n\n" + document,
                4200
            );
        }

        public static string FormatForModel(
            UnityProjectSnapshotV2 snapshot
        )
        {
            StringBuilder builder = new StringBuilder();

            AppendSection(
                builder,
                "UNITY PROJECT SETTINGS",
                snapshot.ProjectSettings
            );
            AppendSection(
                builder,
                "SCENE HIERARCHY",
                snapshot.SceneHierarchy
            );
            AppendSection(
                builder,
                "CURRENT CONSOLE",
                snapshot.ConsoleErrors
            );
            AppendSection(
                builder,
                "RELEVANT SCRIPT INDEX",
                snapshot.ScriptIndex
            );

            foreach (
                KeyValuePair<string, string> script
                in snapshot.RelevantScripts.Take(MaxRelevantScripts)
            )
            {
                AppendSection(
                    builder,
                    "SCRIPT: " + script.Key,
                    AgentJsonV2.Compact(script.Value, MaxScriptChars)
                );
            }

            if (!string.IsNullOrWhiteSpace(snapshot.Documentation))
            {
                AppendSection(
                    builder,
                    "OFFICIAL UNITY DOCUMENTATION",
                    AgentJsonV2.Compact(snapshot.Documentation, 4200)
                );
            }

            return AgentJsonV2.Compact(builder.ToString(), 14500);
        }

        private static void AppendSection(
            StringBuilder builder,
            string title,
            string content
        )
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            builder.Append("=== ");
            builder.Append(title);
            builder.AppendLine(" ===");
            builder.AppendLine(content.Trim());
            builder.AppendLine();
        }

        private static string CompactConsole(string result)
        {
            if (string.IsNullOrWhiteSpace(result))
            {
                return "No Unity console output.";
            }

            string normalized = result
                .Replace(" ", "", StringComparison.Ordinal)
                .Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", "", StringComparison.Ordinal)
                .ToLowerInvariant();

            bool success = normalized.Contains("\"success\":true");
            bool emptyErrors =
                normalized.Contains("\"errors\":[]")
                || normalized.Contains("\"items\":[]")
                || normalized.Contains("\"messages\":[]");

            if (success && emptyErrors)
            {
                return "No Unity console errors.";
            }

            return AgentJsonV2.Compact(result, MaxConsoleChars);
        }

        private static List<string> BuildSearchTerms(string goal)
        {
            string text = goal.ToLowerInvariant();
            List<string> terms = new List<string>();

            void Add(params string[] values)
            {
                foreach (string value in values)
                {
                    if (!terms.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        terms.Add(value);
                    }
                }
            }

            if (
                ContainsAny(
                    text,
                    "player",
                    "movement",
                    "controller",
                    "igrac",
                    "igrač"
                )
            )
            {
                Add("Player", "Controller", "Movement");
            }

            if (
                ContainsAny(
                    text,
                    "camera",
                    "kamera",
                    "mouse look",
                    "mouselook"
                )
            )
            {
                Add("Camera", "Look");
            }

            if (
                ContainsAny(
                    text,
                    "enemy",
                    "navmesh",
                    "navigation",
                    "patrol",
                    "chase",
                    "neprijatelj"
                )
            )
            {
                Add("Enemy", "AI", "Nav");
            }

            if (
                ContainsAny(
                    text,
                    "inventory",
                    "weapon",
                    "interaction",
                    "interact"
                )
            )
            {
                Add("Inventory", "Weapon", "Interaction");
            }

            foreach (
                Match match
                in Regex.Matches(
                    goal,
                    @"\b[A-Z][A-Za-z0-9_]{2,}\b"
                )
            )
            {
                Add(match.Value);
            }

            if (terms.Count == 0)
            {
                Add("Controller", "Manager", "AI");
            }

            return terms;
        }

        private static IEnumerable<string> ExtractAssetPaths(
            string text
        )
        {
            HashSet<string> result =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                CollectAssetPaths(document.RootElement, result);
            }
            catch
            {
            }

            foreach (
                Match match
                in Regex.Matches(
                    text,
                    @"Assets/[A-Za-z0-9_ ./\-]+?\.cs",
                    RegexOptions.IgnoreCase
                )
            )
            {
                result.Add(match.Value.Trim());
            }

            return result;
        }

        private static void CollectAssetPaths(
            JsonElement element,
            HashSet<string> result
        )
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (
                        JsonProperty property
                        in element.EnumerateObject()
                    )
                    {
                        CollectAssetPaths(property.Value, result);
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        CollectAssetPaths(item, result);
                    }
                    break;

                case JsonValueKind.String:
                    string? value = element.GetString();

                    if (
                        !string.IsNullOrWhiteSpace(value)
                        && value.StartsWith(
                            "Assets/",
                            StringComparison.OrdinalIgnoreCase
                        )
                        && value.EndsWith(
                            ".cs",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    {
                        result.Add(value);
                    }
                    break;
            }
        }

        private static string? ExtractFirstDocsUrl(string text)
        {
            Match match = Regex.Match(
                text,
                @"https://docs\.unity3d\.com/[^\s\)\]\>]+",
                RegexOptions.IgnoreCase
            );

            return match.Success
                ? match.Value.TrimEnd('.', ',', ';')
                : null;
        }

        private static bool ContainsAny(
            string text,
            params string[] values
        )
        {
            foreach (string value in values)
            {
                if (
                    text.Contains(
                        value,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return true;
                }
            }

            return false;
        }
    }
}
