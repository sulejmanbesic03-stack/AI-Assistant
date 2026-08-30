using AI_Assistant.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AI_Assistant.TempCapabilities
{
    public sealed class UnityBatchBuilder
    {
        private readonly UnityBridgeTools unity;

        private readonly List<Dictionary<string, object?>> operations =
            new List<Dictionary<string, object?>>();

        private readonly Dictionary<string, string> createdObjectPaths =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly HashSet<string> ambiguousCreatedNames =
            new HashSet<string>(StringComparer.Ordinal);

        private string? liveHierarchyJson;
        private Dictionary<string, HashSet<string>>? liveComponentsByPath;

        private bool stopOnFailure = true;
        private bool saveScene;

        public UnityBatchBuilder(
            UnityBridgeTools unity
        )
        {
            this.unity = unity;
        }

        public UnityBatchBuilder StopOnFailure(
            bool value = true
        )
        {
            stopOnFailure = value;
            return this;
        }

        public UnityBatchBuilder SaveScene(
            bool value = true
        )
        {
            saveScene = value;
            return this;
        }

        public UnityBatchBuilder CreateGameObject(
            string name,
            string parentPath = ""
        )
        {
            string resolvedParentPath =
                ResolveObjectPath(parentPath);

            string fullPath = BuildPath(
                name,
                resolvedParentPath
            );

            // Cowork/idempotency rule: a retry must reuse an object that the
            // previous attempt already created instead of stacking duplicates.
            if (!ObjectExistsLive(fullPath))
            {
                operations.Add(
                    new Dictionary<string, object?>
                    {
                        ["operation"] = "create_gameobject",
                        ["name"] = name,
                        ["parentPath"] = resolvedParentPath
                    }
                );
            }

            RememberCreatedObject(
                name,
                resolvedParentPath
            );

            return this;
        }

        public UnityBatchBuilder CreatePrimitive(
            string primitiveType,
            string name,
            string parentPath = ""
        )
        {
            string resolvedParentPath =
                ResolveObjectPath(parentPath);

            string fullPath = BuildPath(
                name,
                resolvedParentPath
            );

            if (!ObjectExistsLive(fullPath))
            {
                operations.Add(
                    new Dictionary<string, object?>
                    {
                        ["operation"] = "create_primitive",
                        ["primitiveType"] = primitiveType,
                        ["name"] = name,
                        ["parentPath"] = resolvedParentPath
                    }
                );
            }

            RememberCreatedObject(
                name,
                resolvedParentPath
            );

            return this;
        }

        public UnityBatchBuilder DeleteGameObject(
            string objectPath
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "delete_gameobject",
                    ["objectPath"] = ResolveObjectPath(objectPath)
                }
            );

            return this;
        }

        public UnityBatchBuilder RenameGameObject(
            string objectPath,
            string newName
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "rename_gameobject",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["newName"] = newName
                }
            );

            return this;
        }

        public UnityBatchBuilder SetParent(
            string objectPath,
            string parentPath
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "set_parent",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["parentPath"] = ResolveObjectPath(parentPath)
                }
            );

            return this;
        }

        public UnityBatchBuilder SetActive(
            string objectPath,
            bool active
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "set_active",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["boolValue"] = active
                }
            );

            return this;
        }

        public UnityBatchBuilder SetPosition(
            string objectPath,
            float x,
            float y,
            float z
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "set_position",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["x"] = x,
                    ["y"] = y,
                    ["z"] = z
                }
            );

            return this;
        }

        public UnityBatchBuilder SetRotation(
            string objectPath,
            float x,
            float y,
            float z
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "set_rotation",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["x"] = x,
                    ["y"] = y,
                    ["z"] = z
                }
            );

            return this;
        }

        public UnityBatchBuilder SetScale(
            string objectPath,
            float x,
            float y,
            float z
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "set_scale",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["x"] = x,
                    ["y"] = y,
                    ["z"] = z
                }
            );

            return this;
        }

        public UnityBatchBuilder AddComponent(
            string objectPath,
            string componentType
        )
        {
            string resolvedPath = ResolveObjectPath(objectPath);

            if (!ComponentExistsLive(resolvedPath, componentType))
            {
                operations.Add(
                    new Dictionary<string, object?>
                    {
                        ["operation"] = "add_component",
                        ["objectPath"] = resolvedPath,
                        ["componentType"] = componentType
                    }
                );
            }

            return this;
        }

        public UnityBatchBuilder RemoveComponent(
            string objectPath,
            string componentType
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "remove_component",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["componentType"] = componentType
                }
            );

            return this;
        }

        public UnityBatchBuilder SetInt(
            string objectPath,
            string componentType,
            string propertyName,
            int value
        )
        {
            return AddProperty(
                objectPath,
                componentType,
                propertyName,
                "int",
                "intValue",
                value
            );
        }

        public UnityBatchBuilder SetFloat(
            string objectPath,
            string componentType,
            string propertyName,
            float value
        )
        {
            return AddProperty(
                objectPath,
                componentType,
                propertyName,
                "float",
                "floatValue",
                value
            );
        }

        public UnityBatchBuilder SetBool(
            string objectPath,
            string componentType,
            string propertyName,
            bool value
        )
        {
            return AddProperty(
                objectPath,
                componentType,
                propertyName,
                "bool",
                "boolValue",
                value
            );
        }

        public UnityBatchBuilder SetString(
            string objectPath,
            string componentType,
            string propertyName,
            string value
        )
        {
            return AddProperty(
                objectPath,
                componentType,
                propertyName,
                "string",
                "stringValue",
                value
            );
        }

        public UnityBatchBuilder SetVector2(
            string objectPath,
            string componentType,
            string propertyName,
            float x,
            float y
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "set_component_property",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["componentType"] = componentType,
                    ["propertyName"] = propertyName,
                    ["valueType"] = "vector2",
                    ["x"] = x,
                    ["y"] = y
                }
            );

            return this;
        }

        public UnityBatchBuilder SetVector3(
            string objectPath,
            string componentType,
            string propertyName,
            float x,
            float y,
            float z
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "set_component_property",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["componentType"] = componentType,
                    ["propertyName"] = propertyName,
                    ["valueType"] = "vector3",
                    ["x"] = x,
                    ["y"] = y,
                    ["z"] = z
                }
            );

            return this;
        }

        public UnityBatchBuilder SetColor(
            string objectPath,
            string componentType,
            string propertyName,
            float red,
            float green,
            float blue,
            float alpha = 1f
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "set_component_property",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["componentType"] = componentType,
                    ["propertyName"] = propertyName,
                    ["valueType"] = "color",
                    ["r"] = red,
                    ["g"] = green,
                    ["b"] = blue,
                    ["a"] = alpha
                }
            );

            return this;
        }

        private UnityBatchBuilder AddProperty(
            string objectPath,
            string componentType,
            string propertyName,
            string valueType,
            string valueKey,
            object value
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "set_component_property",
                    ["objectPath"] = ResolveObjectPath(objectPath),
                    ["componentType"] = componentType,
                    ["propertyName"] = propertyName,
                    ["valueType"] = valueType,
                    [valueKey] = value
                }
            );

            return this;
        }

        private string ResolveObjectPath(
            string objectPath
        )
        {
            if (
                string.IsNullOrWhiteSpace(objectPath) ||
                objectPath.Contains('/') ||
                ambiguousCreatedNames.Contains(objectPath)
            )
            {
                return objectPath;
            }

            return
                createdObjectPaths.TryGetValue(
                    objectPath,
                    out string? fullPath
                )
                    ? fullPath
                    : objectPath;
        }

        private void RememberCreatedObject(
            string name,
            string parentPath
        )
        {
            if (
                string.IsNullOrWhiteSpace(name) ||
                ambiguousCreatedNames.Contains(name)
            )
            {
                return;
            }

            string fullPath = BuildPath(name, parentPath);

            if (
                createdObjectPaths.TryGetValue(
                    name,
                    out string? existingPath
                ) &&
                !string.Equals(
                    existingPath,
                    fullPath,
                    StringComparison.Ordinal
                )
            )
            {
                createdObjectPaths.Remove(name);
                ambiguousCreatedNames.Add(name);
                return;
            }

            createdObjectPaths[name] = fullPath;
        }

        private static string BuildPath(
            string name,
            string parentPath
        )
        {
            return string.IsNullOrWhiteSpace(parentPath)
                ? name
                : parentPath.TrimEnd('/') + "/" + name;
        }

        private bool ObjectExistsLive(string fullPath)
        {
            EnsureLiveHierarchyIndex();

            return liveComponentsByPath != null
                && liveComponentsByPath.ContainsKey(fullPath);
        }

        private bool ComponentExistsLive(
            string objectPath,
            string componentType
        )
        {
            EnsureLiveHierarchyIndex();

            if (
                liveComponentsByPath == null
                || !liveComponentsByPath.TryGetValue(
                    objectPath,
                    out HashSet<string>? components
                )
            )
            {
                return false;
            }

            string requested = (componentType ?? "").Trim();

            return components.Any(actual =>
                string.Equals(
                    actual,
                    requested,
                    StringComparison.OrdinalIgnoreCase
                )
                || actual.EndsWith(
                    "." + requested,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        }

        private void EnsureLiveHierarchyIndex()
        {
            if (liveComponentsByPath != null)
            {
                return;
            }

            liveComponentsByPath =
                new Dictionary<string, HashSet<string>>(
                    StringComparer.OrdinalIgnoreCase
                );

            try
            {
                liveHierarchyJson = unity.GetSceneHierarchy();

                using JsonDocument document =
                    JsonDocument.Parse(liveHierarchyJson);

                if (
                    document.RootElement.TryGetProperty(
                        "roots",
                        out JsonElement roots
                    )
                    && roots.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (JsonElement root in roots.EnumerateArray())
                    {
                        IndexHierarchyNode(root);
                    }
                }
            }
            catch
            {
                // If inspection is temporarily unavailable, do not block the
                // batch. The Unity server still performs its normal validation.
            }
        }

        private void IndexHierarchyNode(JsonElement node)
        {
            if (liveComponentsByPath == null)
            {
                return;
            }

            string path =
                node.TryGetProperty(
                    "hierarchyPath",
                    out JsonElement pathElement
                )
                && pathElement.ValueKind == JsonValueKind.String
                    ? pathElement.GetString() ?? ""
                    : "";

            if (!string.IsNullOrWhiteSpace(path))
            {
                HashSet<string> components =
                    new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase
                    );

                if (
                    node.TryGetProperty(
                        "components",
                        out JsonElement componentArray
                    )
                    && componentArray.ValueKind == JsonValueKind.Array
                )
                {
                    foreach (
                        JsonElement component
                        in componentArray.EnumerateArray()
                    )
                    {
                        if (component.ValueKind == JsonValueKind.String)
                        {
                            string? value = component.GetString();

                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                components.Add(value);
                            }
                        }
                    }
                }

                // Duplicate hierarchy names are possible in Unity. For the
                // idempotency guard it is enough to know that the path exists.
                if (!liveComponentsByPath.ContainsKey(path))
                {
                    liveComponentsByPath[path] = components;
                }
            }

            if (
                node.TryGetProperty(
                    "children",
                    out JsonElement children
                )
                && children.ValueKind == JsonValueKind.Array
            )
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    IndexHierarchyNode(child);
                }
            }
        }

        public UnityBatchBuilder CreateScript(
            string assetPath,
            string content
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] = "create_script",
                    ["assetPath"] = assetPath,
                    ["content"] = content
                }
            );

            return this;
        }

        public string Execute()
        {
            if (operations.Count == 0)
            {
                return
                    "{\"success\":true,\"message\":\"Batch already satisfied by live Unity state.\",\"results\":[]}";
            }

            string json =
                JsonSerializer.Serialize(
                    new
                    {
                        stopOnFailure,
                        saveScene,
                        operations
                    }
                );

            return unity.ExecuteBatch(json);
        }
    }
}
