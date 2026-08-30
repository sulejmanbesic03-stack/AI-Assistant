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
        private const int MaxProjectSettingsChars = 3000;
        private const int MaxHierarchyChars = 7000;
        private const int MaxConsoleChars = 2500;
        private const int MaxScriptIndexChars = 5000;
        private const int MaxScriptChars = 7000;
        private const int MaxRelevantScripts = 4;

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

        public Task<UnityProjectSnapshotV2> CaptureAsync(
            string goal
        )
        {
            return Task.Run(
                () => Capture(goal)
            );
        }

        private UnityProjectSnapshotV2 Capture(string goal)
        {
            activity("[V2 INSPECT] project snapshot");

            UnityProjectSnapshotV2 snapshot =
                new UnityProjectSnapshotV2
                {
                    ProjectSettings =
                        AgentJsonV2.Compact(
                            unity.GetUnityProjectSettings(),
                            MaxProjectSettingsChars
                        ),

                    SceneHierarchy =
                        AgentJsonV2.Compact(
                            unity.GetSceneHierarchy(),
                            MaxHierarchyChars
                        ),

                    ConsoleErrors =
                        AgentJsonV2.Compact(
                            unity.GetConsoleErrors(),
                            MaxConsoleChars
                        )
                };

            List<string> searchTerms =
                BuildSearchTerms(goal);

            StringBuilder indexBuilder =
                new StringBuilder();

            HashSet<string> scriptPaths =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase
                );

            foreach (string term in searchTerms.Take(4))
            {
                string result =
                    unity.FindUnityScripts(term);

                indexBuilder.AppendLine(
                    "SEARCH: " + term
                );

                indexBuilder.AppendLine(
                    AgentJsonV2.Compact(
                        result,
                        2200
                    )
                );

                foreach (
                    string path
                    in ExtractAssetPaths(result)
                )
                {
                    scriptPaths.Add(path);
                }
            }

            snapshot.ScriptIndex =
                AgentJsonV2.Compact(
                    indexBuilder.ToString(),
                    MaxScriptIndexChars
                );

            foreach (
                string scriptPath
                in scriptPaths.Take(MaxRelevantScripts)
            )
            {
                activity(
                    "[V2 INSPECT] read "
                    + scriptPath
                );

                string source =
                    unity.ReadUnityScript(
                        scriptPath,
                        1,
                        700
                    );

                snapshot.RelevantScripts[scriptPath] =
                    AgentJsonV2.Compact(
                        source,
                        MaxScriptChars
                    );
            }

            return snapshot;
        }

        public Task<string> GetDocumentationAsync(
            string query
        )
        {
            return Task.Run(
                () => GetDocumentation(query)
            );
        }

        private string GetDocumentation(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return "";
            }

            activity(
                "[V2 DOCS] "
                + AgentJsonV2.Compact(
                    query,
                    120
                )
            );

            string search =
                docs.SearchUnityDocs(query);

            string? firstUrl =
                ExtractFirstDocsUrl(search);

            if (string.IsNullOrWhiteSpace(firstUrl))
            {
                return AgentJsonV2.Compact(
                    search,
                    3500
                );
            }

            string document =
                docs.ReadUnityDoc(firstUrl);

            return AgentJsonV2.Compact(
                search
                + "\n\n"
                + document,
                7500
            );
        }

        public static string FormatForModel(
            UnityProjectSnapshotV2 snapshot
        )
        {
            StringBuilder builder =
                new StringBuilder();

            builder.AppendLine("=== UNITY PROJECT SETTINGS ===");
            builder.AppendLine(snapshot.ProjectSettings);
            builder.AppendLine();

            builder.AppendLine("=== SCENE HIERARCHY ===");
            builder.AppendLine(snapshot.SceneHierarchy);
            builder.AppendLine();

            builder.AppendLine("=== CURRENT CONSOLE ERRORS ===");
            builder.AppendLine(snapshot.ConsoleErrors);
            builder.AppendLine();

            builder.AppendLine("=== RELEVANT SCRIPT INDEX ===");
            builder.AppendLine(snapshot.ScriptIndex);
            builder.AppendLine();

            foreach (
                KeyValuePair<string, string> script
                in snapshot.RelevantScripts
            )
            {
                builder.AppendLine(
                    "=== SCRIPT: "
                    + script.Key
                    + " ==="
                );

                builder.AppendLine(script.Value);
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(snapshot.Documentation))
            {
                builder.AppendLine("=== OFFICIAL UNITY DOCUMENTATION ===");
                builder.AppendLine(snapshot.Documentation);
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static List<string> BuildSearchTerms(string goal)
        {
            string text =
                goal.ToLowerInvariant();

            List<string> terms =
                new List<string>();

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
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase
                );

            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(text);

                CollectAssetPaths(
                    document.RootElement,
                    result
                );
            }
            catch
            {
                // Fall through to regex extraction below.
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
                result.Add(
                    match.Value.Trim()
                );
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
                        CollectAssetPaths(
                            property.Value,
                            result
                        );
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (
                        JsonElement item
                        in element.EnumerateArray()
                    )
                    {
                        CollectAssetPaths(
                            item,
                            result
                        );
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
            Match match =
                Regex.Match(
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
