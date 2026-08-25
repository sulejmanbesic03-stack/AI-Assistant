using System;
using System.Net.Http;
using System.Text;


namespace AI_Assistant.Tools
{
    public static class UnityBatchExtensions
    {
        private const string BatchUrl =
            "http://127.0.0.1:47824/execute-batch";


        private const string BridgeHeaderValue =
            "AI-Assistant-Local";


        private static readonly HttpClient client =
            CreateClient();


        // ============================================================
        // EXECUTE BATCH
        // ============================================================

        public static string ExecuteBatch(
            this UnityBridgeTools unity,
            string operationsJson
        )
        {
            try
            {
                if (
                    string.IsNullOrWhiteSpace(
                        operationsJson
                    )
                )
                {
                    return
                        "{\"success\":false,\"message\":\"Batch JSON is empty.\"}";
                }


                using StringContent content =
                    new StringContent(
                        operationsJson,
                        Encoding.UTF8,
                        "application/json"
                    );


                using HttpResponseMessage response =
                    client
                        .PostAsync(
                            BatchUrl,
                            content
                        )
                        .GetAwaiter()
                        .GetResult();


                string body =
                    response
                        .Content
                        .ReadAsStringAsync()
                        .GetAwaiter()
                        .GetResult();


                return
                    body;
            }
            catch (
                TaskCanceledException
            )
            {
                return
                    "{\"success\":false,\"message\":\"Unity batch bridge timeout.\"}";
            }
            catch (
                HttpRequestException ex
            )
            {
                return
                    "{\"success\":false,\"message\":\"Unity batch bridge offline: "
                    +
                    EscapeJson(
                        ex.Message
                    )
                    +
                    "\"}";
            }
            catch (
                Exception ex
            )
            {
                return
                    "{\"success\":false,\"message\":\"Unity batch bridge error: "
                    +
                    EscapeJson(
                        ex.Message
                    )
                    +
                    "\"}";
            }
        }


        // ============================================================
        // CLIENT
        // ============================================================

        private static HttpClient CreateClient()
        {
            HttpClient result =
                new HttpClient();


            // Batch may create scripts/assets and therefore
            // can reasonably take longer than tiny bridge actions.
            result.Timeout =
                TimeSpan.FromSeconds(
                    60
                );


            result.DefaultRequestHeaders.Add(
                "X-AI-Bridge",
                BridgeHeaderValue
            );


            return
                result;
        }


        // ============================================================
        // ESCAPE
        // ============================================================

        private static string EscapeJson(
            string text
        )
        {
            return
                text
                    .Replace(
                        "\\",
                        "\\\\"
                    )
                    .Replace(
                        "\"",
                        "\\\""
                    )
                    .Replace(
                        "\r",
                        " "
                    )
                    .Replace(
                        "\n",
                        " "
                    );
        }
    }
}