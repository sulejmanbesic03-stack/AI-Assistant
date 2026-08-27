using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AI_Assistant.Tools
{
    public class UnityBridgeTools
    {
        private const string ReadBaseUrl =
            "http://127.0.0.1:47821";

        private const string ActionBaseUrl =
            "http://127.0.0.1:47822";

        private const string SafeActionBaseUrl =
            "http://127.0.0.1:47823";

        private const string PersistentScriptBaseUrl =
            "http://127.0.0.1:47826";

        private const string CodeIntelligenceBaseUrl =
            "http://127.0.0.1:47827";

        private const string BridgeHeaderValue =
            "AI-Assistant-Local";

        private readonly HttpClient client;

        public UnityBridgeTools()
        {
            client =
                new HttpClient();

            client.Timeout =
                TimeSpan.FromSeconds(
                    5
                );

            client.DefaultRequestHeaders.Add(
                "X-AI-Bridge",
                BridgeHeaderValue
            );
        }

        // ============================================
        // READ-ONLY UNITY TOOLS
        // ============================================

        public string GetActiveScene()
        {
            return SendGetRequest(
                "/active-scene"
            );
        }

        public string GetSceneHierarchy()
        {
            return SendGetRequest(
                "/scene-hierarchy"
            );
        }

        public string GetConsoleErrors()
        {
            return SendGetRequest(
                "/console-errors"
            );
        }

        // ============================================
        // ORIGINAL ACTION SERVER
        // ============================================

        public string CreateGameObject(
            string name,
            string parentPath
        )
        {
            string json =
                JsonSerializer.Serialize(
                    new
                    {
                        name,
                        parentPath
                    }
                );

            return SendPostRequest(
                "/create-gameobject",
                json
            );
        }

        public string SetTransform(
            string objectPath,
            float positionX,
            float positionY,
            float positionZ,
            float rotationX,
            float rotationY,
            float rotationZ,
            float scaleX,
            float scaleY,
            float scaleZ
        )
        {
            string json =
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        positionX,
                        positionY,
                        positionZ,
                        rotationX,
                        rotationY,
                        rotationZ,
                        scaleX,
                        scaleY,
                        scaleZ
                    }
                );

            return SendPostRequest(
                "/set-transform",
                json
            );
        }

        // ============================================
        // SAFE ACTION SERVER
        // ============================================

        public string AddComponent(
            string objectPath,
            string componentType
        )
        {
            return SendSafePostRequest(
                "/add-component",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        componentType
                    }
                )
            );
        }

        public string AttachScript(
            string objectPath,
            string scriptType
        )
        {
            return SendSafePostRequest(
                "/attach-script",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        componentType = scriptType
                    }
                )
            );
        }


        public string CreatePersistentScript(
            string assetPath,
            string className,
            string source,
            bool overwrite
        )
        {
            return SendPersistentPostRequest(
                "/create-script",
                JsonSerializer.Serialize(
                    new
                    {
                        assetPath,
                        className,
                        source,
                        overwrite
                    }
                )
            );
        }


        public string WaitForPersistentScript(
            string jobId
        )
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return CreateFailure(
                    "VALIDATION_FAILED",
                    "jobId is required."
                );
            }

            DateTime deadline =
                DateTime.UtcNow.AddSeconds(90);

            string lastResult =
                "";

            while (DateTime.UtcNow < deadline)
            {
                lastResult =
                    SendPersistentGetRequest(
                        "/script-status?jobId="
                        + Uri.EscapeDataString(jobId)
                    );

                try
                {
                    using JsonDocument document =
                        JsonDocument.Parse(lastResult);

                    JsonElement root =
                        document.RootElement;

                    string state =
                        root.TryGetProperty(
                            "state",
                            out JsonElement stateElement
                        )
                        && stateElement.ValueKind ==
                            JsonValueKind.String
                            ? stateElement.GetString() ?? ""
                            : "";

                    if (
                        state == "compiled"
                        || state == "failed"
                    )
                    {
                        return lastResult;
                    }
                }
                catch
                {
                    // Unity may be briefly offline during domain reload.
                }

                Thread.Sleep(500);
            }

            return JsonSerializer.Serialize(
                new
                {
                    success = false,
                    phase = "timeout",
                    state = "failed",
                    jobId,
                    message =
                        "Timed out waiting for Unity persistent-script compilation.",
                    lastResult
                }
            );
        }


        // ============================================
        // UNITY CODE INTELLIGENCE
        // ============================================

        public string GetUnityProjectSettings()
        {
            return SendCodeIntelligenceGetRequest(
                "/project-settings"
            );
        }


        public string FindUnityScripts(
            string searchText
        )
        {
            return SendCodeIntelligenceGetRequest(
                "/list-scripts?searchText="
                + Uri.EscapeDataString(
                    searchText ?? ""
                )
            );
        }


        public string ReadUnityScript(
            string assetPath,
            int startLine,
            int endLine
        )
        {
            return SendCodeIntelligenceGetRequest(
                "/read-script?assetPath="
                + Uri.EscapeDataString(assetPath ?? "")
                + "&startLine="
                + startLine
                + "&endLine="
                + endLine
            );
        }


        public string ReviewUnityScript(
            string assetPath
        )
        {
            return SendCodeIntelligencePostRequest(
                "/review-script",
                JsonSerializer.Serialize(
                    new
                    {
                        assetPath
                    }
                )
            );
        }


        public string GetUnityRuntimeState(
            string objectPath
        )
        {
            return SendCodeIntelligenceGetRequest(
                "/runtime-state?objectPath="
                + Uri.EscapeDataString(objectPath ?? "")
            );
        }


        public string SetUnityPlayMode(
            string action
        )
        {
            string result =
                SendCodeIntelligencePostRequest(
                    "/play-mode",
                    JsonSerializer.Serialize(
                        new
                        {
                            action
                        }
                    )
                );

            bool expectedPlaying =
                string.Equals(
                    action,
                    "enter",
                    StringComparison.OrdinalIgnoreCase
                );

            if (
                !expectedPlaying
                && !string.Equals(
                    action,
                    "exit",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return result;
            }

            try
            {
                using JsonDocument initial =
                    JsonDocument.Parse(result);

                if (
                    initial.RootElement.TryGetProperty(
                        "success",
                        out JsonElement success
                    )
                    && !success.GetBoolean()
                )
                {
                    return result;
                }
            }
            catch
            {
                return result;
            }

            DateTime deadline =
                DateTime.UtcNow.AddSeconds(20);

            string lastState =
                result;

            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(300);

                lastState =
                    GetUnityProjectSettings();

                try
                {
                    using JsonDocument state =
                        JsonDocument.Parse(lastState);

                    JsonElement root =
                        state.RootElement;

                    if (
                        root.TryGetProperty(
                            "success",
                            out JsonElement stateSuccess
                        )
                        && stateSuccess.GetBoolean()
                        && root.TryGetProperty(
                            "isPlaying",
                            out JsonElement isPlaying
                        )
                        && isPlaying.GetBoolean() == expectedPlaying
                    )
                    {
                        return JsonSerializer.Serialize(
                            new
                            {
                                success = true,
                                phase = "play-mode",
                                action,
                                isPlaying = expectedPlaying,
                                message = expectedPlaying
                                    ? "Unity entered Play Mode."
                                    : "Unity exited Play Mode."
                            }
                        );
                    }
                }
                catch
                {
                    // Domain reload may briefly restart the bridge.
                }
            }

            return JsonSerializer.Serialize(
                new
                {
                    success = false,
                    phase = "play-mode-timeout",
                    action,
                    message =
                        "Unity did not confirm the requested Play Mode state within 20 seconds. Do not repeat automatically.",
                    lastState
                }
            );
        }

        public string SaveScene()
        {
            return SendSafePostRequest(
                "/save-scene",
                "{}"
            );
        }

        public string CreatePrimitive(
            string primitiveType,
            string name,
            string parentPath
        )
        {
            return SendSafePostRequest(
                "/create-primitive",
                JsonSerializer.Serialize(
                    new
                    {
                        primitiveType,
                        name,
                        parentPath
                    }
                )
            );
        }

        public string RenameGameObject(
            string objectPath,
            string newName
        )
        {
            return SendSafePostRequest(
                "/rename-gameobject",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        newName
                    }
                )
            );
        }

        public string SetParent(
            string objectPath,
            string parentPath
        )
        {
            return SendSafePostRequest(
                "/set-parent",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        parentPath
                    }
                )
            );
        }

        public string SetActive(
            string objectPath,
            bool active
        )
        {
            return SendSafePostRequest(
                "/set-active",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        active
                    }
                )
            );
        }

        public string FindAssets(
            string filter,
            string searchFolder
        )
        {
            return SendSafePostRequest(
                "/find-assets",
                JsonSerializer.Serialize(
                    new
                    {
                        filter,
                        searchFolder
                    }
                )
            );
        }

        public string GetAssetInfo(
            string assetPath
        )
        {
            return SendSafePostRequest(
                "/get-asset-info",
                JsonSerializer.Serialize(
                    new
                    {
                        assetPath
                    }
                )
            );
        }

        public string CreateMaterial(
            string assetPath,
            string shaderName
        )
        {
            return SendSafePostRequest(
                "/create-material",
                JsonSerializer.Serialize(
                    new
                    {
                        assetPath,
                        shaderName
                    }
                )
            );
        }

        public string SetMaterialColor(
            string materialPath,
            float red,
            float green,
            float blue,
            float alpha
        )
        {
            return SendSafePostRequest(
                "/set-material-color",
                JsonSerializer.Serialize(
                    new
                    {
                        materialPath,
                        red,
                        green,
                        blue,
                        alpha
                    }
                )
            );
        }

        public string AssignMaterial(
            string objectPath,
            string materialPath
        )
        {
            return SendSafePostRequest(
                "/assign-material",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        materialPath
                    }
                )
            );
        }

        public string ImportAsset(
            string assetPath
        )
        {
            return SendSafePostRequest(
                "/import-asset",
                JsonSerializer.Serialize(
                    new
                    {
                        assetPath
                    }
                )
            );
        }

        public string SetPosition(
            string objectPath,
            float x,
            float y,
            float z
        )
        {
            return SendSafePostRequest(
                "/set-position",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        x,
                        y,
                        z
                    }
                )
            );
        }

        public string SetRotation(
            string objectPath,
            float x,
            float y,
            float z
        )
        {
            return SendSafePostRequest(
                "/set-rotation",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        x,
                        y,
                        z
                    }
                )
            );
        }

        public string SetScale(
            string objectPath,
            float x,
            float y,
            float z
        )
        {
            return SendSafePostRequest(
                "/set-scale",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        x,
                        y,
                        z
                    }
                )
            );
        }

        // ============================================
        // DUPLICATE AND PHYSICS
        // ============================================

        public string DuplicateGameObject(
            string objectPath,
            string newName,
            string parentPath
        )
        {
            return SendSafePostRequest(
                "/duplicate-gameobject",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        newName,
                        parentPath
                    }
                )
            );
        }

        public string ConfigureRigidbody(
            string objectPath,
            float mass,
            bool useGravity,
            bool isKinematic
        )
        {
            return SendSafePostRequest(
                "/configure-rigidbody",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        mass,
                        useGravity,
                        isKinematic
                    }
                )
            );
        }

        public string ConfigureCollider(
            string objectPath,
            bool enabled,
            bool isTrigger
        )
        {
            return SendSafePostRequest(
                "/configure-collider",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        enabled,
                        isTrigger
                    }
                )
            );
        }

        // ============================================
        // PREFABS
        // ============================================

        public string CreatePrefab(
            string objectPath,
            string assetPath
        )
        {
            return SendSafePostRequest(
                "/create-prefab",
                JsonSerializer.Serialize(
                    new
                    {
                        objectPath,
                        assetPath
                    }
                )
            );
        }

        public string InstantiatePrefab(
            string assetPath,
            string name,
            string parentPath
        )
        {
            return SendSafePostRequest(
                "/instantiate-prefab",
                JsonSerializer.Serialize(
                    new
                    {
                        assetPath,
                        name,
                        parentPath
                    }
                )
            );
        }

        // ============================================
        // SEND GET
        // ============================================

        private string SendGetRequest(
            string endpoint
        )
        {
            try
            {
                using HttpResponseMessage response =
                    client
                        .GetAsync(
                            ReadBaseUrl + endpoint
                        )
                        .GetAwaiter()
                        .GetResult();

                return ReadResponse(
                    response
                );
            }
            catch (Exception ex)
            {
                return FormatConnectionError(
                    ex
                );
            }
        }

        // ============================================
        // SEND POST - ORIGINAL ACTION SERVER
        // ============================================

        private string SendPostRequest(
            string endpoint,
            string json
        )
        {
            try
            {
                using StringContent content =
                    new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"
                    );

                using HttpResponseMessage response =
                    client
                        .PostAsync(
                            ActionBaseUrl + endpoint,
                            content
                        )
                        .GetAwaiter()
                        .GetResult();

                return ReadResponse(
                    response
                );
            }
            catch (Exception ex)
            {
                return FormatConnectionError(
                    ex
                );
            }
        }

        // ============================================
        // SEND POST - SAFE ACTION SERVER
        // ============================================

        private string SendSafePostRequest(
            string endpoint,
            string json
        )
        {
            try
            {
                using StringContent content =
                    new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"
                    );

                using HttpResponseMessage response =
                    client
                        .PostAsync(
                            SafeActionBaseUrl + endpoint,
                            content
                        )
                        .GetAwaiter()
                        .GetResult();

                return ReadResponse(
                    response
                );
            }
            catch (Exception ex)
            {
                return FormatConnectionError(
                    ex
                );
            }
        }


        private string SendPersistentPostRequest(
            string endpoint,
            string json
        )
        {
            try
            {
                using StringContent content =
                    new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"
                    );

                using HttpResponseMessage response =
                    client
                        .PostAsync(
                            PersistentScriptBaseUrl + endpoint,
                            content
                        )
                        .GetAwaiter()
                        .GetResult();

                return ReadResponse(response);
            }
            catch (Exception ex)
            {
                return FormatConnectionError(ex);
            }
        }


        private string SendPersistentGetRequest(
            string endpoint
        )
        {
            try
            {
                using HttpResponseMessage response =
                    client
                        .GetAsync(
                            PersistentScriptBaseUrl + endpoint
                        )
                        .GetAwaiter()
                        .GetResult();

                return ReadResponse(response);
            }
            catch (Exception ex)
            {
                return FormatConnectionError(ex);
            }
        }


        private string SendCodeIntelligenceGetRequest(
            string endpoint
        )
        {
            try
            {
                using HttpResponseMessage response =
                    client
                        .GetAsync(
                            CodeIntelligenceBaseUrl + endpoint
                        )
                        .GetAwaiter()
                        .GetResult();

                return ReadResponse(response);
            }
            catch (Exception ex)
            {
                return FormatConnectionError(ex);
            }
        }


        private string SendCodeIntelligencePostRequest(
            string endpoint,
            string json
        )
        {
            try
            {
                using StringContent content =
                    new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"
                    );

                using HttpResponseMessage response =
                    client
                        .PostAsync(
                            CodeIntelligenceBaseUrl + endpoint,
                            content
                        )
                        .GetAwaiter()
                        .GetResult();

                return ReadResponse(response);
            }
            catch (Exception ex)
            {
                return FormatConnectionError(ex);
            }
        }

        // ============================================
        // READ RESPONSE
        // ============================================

        private string ReadResponse(
            HttpResponseMessage response
        )
        {
            string responseBody =
                response.Content
                    .ReadAsStringAsync()
                    .GetAwaiter()
                    .GetResult();

            if (!response.IsSuccessStatusCode)
            {
                string message =
                    ExtractErrorMessage(
                        responseBody
                    );

                string errorCode =
                    ClassifyError(
                        (int)response.StatusCode,
                        message
                    );

                if (errorCode == "ALREADY_EXISTS")
                {
                    return JsonSerializer.Serialize(
                        new
                        {
                            success = true,
                            status = "already_exists",
                            message
                        }
                    );
                }

                return JsonSerializer.Serialize(
                    new
                    {
                        success = false,
                        errorCode,
                        statusCode = (int)response.StatusCode,
                        message
                    }
                );
            }

            return responseBody;
        }

        // ============================================
        // CONNECTION ERRORS
        // ============================================

        private string FormatConnectionError(
            Exception ex
        )
        {
            if (ex is TaskCanceledException)
            {
                return CreateFailure(
                    "TIMEOUT",
                    "Unity nije odgovorio u roku od 5 sekundi."
                );
            }

            if (ex is HttpRequestException)
            {
                return CreateFailure(
                    "OFFLINE",
                    "Provjeri da li je Unity Editor otvoren i bridge pokrenut. " +
                    ex.Message
                );
            }

            return CreateFailure(
                "CONNECTION_ERROR",
                ex.Message
            );
        }

        private string ExtractErrorMessage(
            string responseBody
        )
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(
                        responseBody
                    );

                JsonElement root =
                    document.RootElement;

                if (
                    root.TryGetProperty(
                        "error",
                        out JsonElement error
                    )
                )
                {
                    return error.GetString()
                        ?? responseBody;
                }

                if (
                    root.TryGetProperty(
                        "message",
                        out JsonElement message
                    )
                )
                {
                    return message.GetString()
                        ?? responseBody;
                }
            }
            catch
            {
                // Bridge je vratio tekst koji nije JSON.
            }

            return responseBody;
        }

        private string ClassifyError(
            int statusCode,
            string message
        )
        {
            if (
                message.Contains(
                    "already exists",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return "ALREADY_EXISTS";
            }

            if (
                message.Contains(
                    "Unknown endpoint",
                    StringComparison.OrdinalIgnoreCase
                )
                ||
                message.Contains(
                    "Unknown action endpoint",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return "ENDPOINT_NOT_FOUND";
            }

            if (
                message.Contains(
                    "GameObject not found",
                    StringComparison.OrdinalIgnoreCase
                )
                ||
                message.Contains(
                    "GameObject nije pronađen",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return "GAMEOBJECT_NOT_FOUND";
            }

            if (
                message.Contains(
                    "Parent not found",
                    StringComparison.OrdinalIgnoreCase
                )
                ||
                message.Contains(
                    "Parent GameObject nije pronađen",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return "PARENT_NOT_FOUND";
            }

            if (
                message.Contains(
                    "Asset not found",
                    StringComparison.OrdinalIgnoreCase
                )
                ||
                message.Contains(
                    "Material not found",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return "ASSET_NOT_FOUND";
            }

            if (statusCode == 403)
            {
                return "UNAUTHORIZED";
            }

            if (statusCode == 405)
            {
                return "METHOD_NOT_ALLOWED";
            }

            if (statusCode == 400)
            {
                return "VALIDATION_FAILED";
            }

            if (statusCode == 404)
            {
                return "RESOURCE_NOT_FOUND";
            }

            return "UNITY_OPERATION_FAILED";
        }

        private string CreateFailure(
            string errorCode,
            string message
        )
        {
            return JsonSerializer.Serialize(
                new
                {
                    success = false,
                    errorCode,
                    message
                }
            );
        }
    }
}
