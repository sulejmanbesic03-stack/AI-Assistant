using AI_Assistant.AI;
using AI_Assistant.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public class AIIntegration
{
    private readonly HttpClient client;
    private readonly FileSystemTools fileTools;

    private List<ChatMessage> conversationHistory;

    public AIIntegration(string workspacePath)
    {
        client = new HttpClient();

        fileTools = new FileSystemTools(workspacePath);

        conversationHistory = new List<ChatMessage>();
    }

    public async Task<string> Ask(string prompt)
    {
        string? apiKey =
            Environment.GetEnvironmentVariable("GEMINI_API_KEY");

        if (string.IsNullOrEmpty(apiKey))
        {
            return "Gemini API key nije pronađen!";
        }


        // 1. Zapamti novu user poruku
        conversationHistory.Add(
            new ChatMessage("user", prompt)
        );


        // 2. Pretvori naš ChatMessage history
        // u format koji Gemini očekuje
        var contents = conversationHistory
            .Select(message => new
            {
                role = message.Role,

                parts = new[]
                {
                    new
                    {
                        text = message.Message
                    }
                }
            })
            .ToArray();


        // 3. Opisujemo Geminiju koje alate ima
        var requestBody = new
        {
            contents = contents,

            tools = new[]
            {
                new
                {
                    functionDeclarations = new[]
                    {
                        new
                        {
                            name = "create_folder",

                            description =
                                "Creates a new folder inside the allowed workspace.",

                            parameters = new
                            {
                                type = "object",

                                properties = new
                                {
                                    folderName = new
                                    {
                                        type = "string",

                                        description =
                                            "Name of the folder to create."
                                    }
                                },

                                required = new[]
                                {
                                    "folderName"
                                }
                            }
                        }
                    }
                }
            }
        };


        string json =
            JsonSerializer.Serialize(requestBody);


        using StringContent content = new StringContent(
            json,
            Encoding.UTF8,
            "application/json"
        );


        string url =
            $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}";


        // 4. Pošalji request Geminiju
        HttpResponseMessage response =
            await client.PostAsync(url, content);


        string responseText =
            await response.Content.ReadAsStringAsync();


        if (!response.IsSuccessStatusCode)
        {
            return $"Gemini API greška:\n{responseText}";
        }


        using JsonDocument document =
            JsonDocument.Parse(responseText);


        JsonElement parts = document
            .RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts");


        // 5. Provjeri svaki dio Gemini odgovora
        foreach (JsonElement part in parts.EnumerateArray())
        {
            // Je li Gemini zatražio tool?
            if (part.TryGetProperty(
                "functionCall",
                out JsonElement functionCall))
            {
                string functionName =
                    functionCall
                    .GetProperty("name")
                    .GetString() ?? "";


                // Trenutno imamo samo jedan tool
                if (functionName == "create_folder")
                {
                    string folderName =
                        functionCall
                        .GetProperty("args")
                        .GetProperty("folderName")
                        .GetString() ?? "";


                    if (string.IsNullOrWhiteSpace(folderName))
                    {
                        return "Gemini nije poslao ispravno ime foldera.";
                    }


                    string toolResult =
                        fileTools.CreateFolder(folderName);


                    return toolResult;
                }
            }


            // Ako nije function call,
            // možda je običan AI tekst
            if (part.TryGetProperty(
                "text",
                out JsonElement textElement))
            {
                string answer =
                    textElement.GetString() ?? "";


                conversationHistory.Add(
                    new ChatMessage("model", answer)
                );


                return answer;
            }
        }


        return "Gemini nije vratio tekst niti poznati tool call.";
    }
}