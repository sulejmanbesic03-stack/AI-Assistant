using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;


namespace AI_Assistant.Tools
{
    public class UnityBridgeTools
    {
        private const string ReadBaseUrl =
            "http://127.0.0.1:47821";


        private const string ActionBaseUrl =
            "http://127.0.0.1:47822";


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
            return
                SendGetRequest(
                    "/active-scene"
                );
        }


        public string GetSceneHierarchy()
        {
            return
                SendGetRequest(
                    "/scene-hierarchy"
                );
        }


        public string GetConsoleErrors()
        {
            return
                SendGetRequest(
                    "/console-errors"
                );
        }


        // ============================================
        // CREATE GAMEOBJECT
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
                        name = name,

                        parentPath = parentPath
                    }
                );


            return
                SendPostRequest(
                    "/create-gameobject",
                    json
                );
        }


        // ============================================
        // SET TRANSFORM
        // ============================================

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
                        objectPath = objectPath,

                        positionX = positionX,

                        positionY = positionY,

                        positionZ = positionZ,

                        rotationX = rotationX,

                        rotationY = rotationY,

                        rotationZ = rotationZ,

                        scaleX = scaleX,

                        scaleY = scaleY,

                        scaleZ = scaleZ
                    }
                );


            return
                SendPostRequest(
                    "/set-transform",
                    json
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


                return
                    ReadResponse(
                        response
                    );
            }
            catch (Exception ex)
            {
                return
                    FormatConnectionError(
                        ex
                    );
            }
        }


        // ============================================
        // SEND POST
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


                return
                    ReadResponse(
                        response
                    );
            }
            catch (Exception ex)
            {
                return
                    FormatConnectionError(
                        ex
                    );
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
                return
                    $"UNITY BRIDGE ERROR ({(int)response.StatusCode}):\n" +
                    responseBody;
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
                return
                    "UNITY BRIDGE TIMEOUT: Unity nije odgovorio u roku od 5 sekundi.";
            }


            if (ex is HttpRequestException)
            {
                return
                    "UNITY BRIDGE OFFLINE:\n" +
                    "Provjeri da li je Unity Editor otvoren i bridge pokrenut.\n" +
                    ex.Message;
            }


            return
                $"UNITY BRIDGE ERROR:\n{ex.Message}";
        }
    }
}