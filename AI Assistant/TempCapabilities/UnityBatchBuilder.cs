using AI_Assistant.Tools;

using System.Text.Json;


namespace AI_Assistant.TempCapabilities
{
    public sealed class UnityBatchBuilder
    {
        private readonly UnityBridgeTools unity;

        private readonly List<Dictionary<string, object?>> operations =
            new List<Dictionary<string, object?>>();


        private bool stopOnFailure =
            true;


        private bool saveScene;


        public UnityBatchBuilder(
            UnityBridgeTools unity
        )
        {
            this.unity =
                unity;
        }


        // ============================================================
        // SETTINGS
        // ============================================================

        public UnityBatchBuilder StopOnFailure(
            bool value = true
        )
        {
            stopOnFailure =
                value;


            return this;
        }


        public UnityBatchBuilder SaveScene(
            bool value = true
        )
        {
            saveScene =
                value;


            return this;
        }


        // ============================================================
        // GAMEOBJECT
        // ============================================================

        public UnityBatchBuilder CreateGameObject(
            string name,
            string parentPath = ""
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] =
                        "create_gameobject",

                    ["name"] =
                        name,

                    ["parentPath"] =
                        parentPath
                }
            );


            return this;
        }


        public UnityBatchBuilder CreatePrimitive(
            string primitiveType,
            string name,
            string parentPath = ""
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] =
                        "create_primitive",

                    ["primitiveType"] =
                        primitiveType,

                    ["name"] =
                        name,

                    ["parentPath"] =
                        parentPath
                }
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
                    ["operation"] =
                        "delete_gameobject",

                    ["objectPath"] =
                        objectPath
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
                    ["operation"] =
                        "rename_gameobject",

                    ["objectPath"] =
                        objectPath,

                    ["newName"] =
                        newName
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
                    ["operation"] =
                        "set_parent",

                    ["objectPath"] =
                        objectPath,

                    ["parentPath"] =
                        parentPath
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
                    ["operation"] =
                        "set_active",

                    ["objectPath"] =
                        objectPath,

                    ["boolValue"] =
                        active
                }
            );


            return this;
        }


        // ============================================================
        // TRANSFORM
        // ============================================================

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
                    ["operation"] =
                        "set_position",

                    ["objectPath"] =
                        objectPath,

                    ["x"] =
                        x,

                    ["y"] =
                        y,

                    ["z"] =
                        z
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
                    ["operation"] =
                        "set_rotation",

                    ["objectPath"] =
                        objectPath,

                    ["x"] =
                        x,

                    ["y"] =
                        y,

                    ["z"] =
                        z
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
                    ["operation"] =
                        "set_scale",

                    ["objectPath"] =
                        objectPath,

                    ["x"] =
                        x,

                    ["y"] =
                        y,

                    ["z"] =
                        z
                }
            );


            return this;
        }


        // ============================================================
        // COMPONENTS
        // ============================================================

        public UnityBatchBuilder AddComponent(
            string objectPath,
            string componentType
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] =
                        "add_component",

                    ["objectPath"] =
                        objectPath,

                    ["componentType"] =
                        componentType
                }
            );


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
                    ["operation"] =
                        "remove_component",

                    ["objectPath"] =
                        objectPath,

                    ["componentType"] =
                        componentType
                }
            );


            return this;
        }


        // ============================================================
        // SERIALIZED PROPERTIES
        // ============================================================

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
                    ["operation"] =
                        "set_component_property",

                    ["objectPath"] =
                        objectPath,

                    ["componentType"] =
                        componentType,

                    ["propertyName"] =
                        propertyName,

                    ["valueType"] =
                        "vector2",

                    ["x"] =
                        x,

                    ["y"] =
                        y
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
                    ["operation"] =
                        "set_component_property",

                    ["objectPath"] =
                        objectPath,

                    ["componentType"] =
                        componentType,

                    ["propertyName"] =
                        propertyName,

                    ["valueType"] =
                        "vector3",

                    ["x"] =
                        x,

                    ["y"] =
                        y,

                    ["z"] =
                        z
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
                    ["operation"] =
                        "set_component_property",

                    ["objectPath"] =
                        objectPath,

                    ["componentType"] =
                        componentType,

                    ["propertyName"] =
                        propertyName,

                    ["valueType"] =
                        "color",

                    ["r"] =
                        red,

                    ["g"] =
                        green,

                    ["b"] =
                        blue,

                    ["a"] =
                        alpha
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
                    ["operation"] =
                        "set_component_property",

                    ["objectPath"] =
                        objectPath,

                    ["componentType"] =
                        componentType,

                    ["propertyName"] =
                        propertyName,

                    ["valueType"] =
                        valueType,

                    [valueKey] =
                        value
                }
            );


            return this;
        }


        // ============================================================
        // SCRIPT
        // ============================================================

        public UnityBatchBuilder CreateScript(
            string assetPath,
            string content
        )
        {
            operations.Add(
                new Dictionary<string, object?>
                {
                    ["operation"] =
                        "create_script",

                    ["assetPath"] =
                        assetPath,

                    ["content"] =
                        content
                }
            );


            return this;
        }


        // ============================================================
        // EXECUTE
        // ============================================================

        public string Execute()
        {
            if (
                operations.Count ==
                0
            )
            {
                return
                    "{\"success\":false,\"message\":\"Batch contains no operations.\"}";
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


            return
                unity.ExecuteBatch(
                    json
                );
        }
    }
}