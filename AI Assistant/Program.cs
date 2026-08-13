using System.Collections.Generic;


string sourceRoot =
    @"C:\Users\sulja\source\repos\AI Assistant\AI Assistant";


string projectFile =
    @"C:\Users\sulja\source\repos\AI Assistant\AI Assistant\AI Assistant.csproj";


string updaterProject =
    @"C:\Users\sulja\source\repos\AI Assistant\AI Assistant Updater\AI Assistant Updater.csproj";


List<string> allowedRoots =
    new List<string>
    {
        @"C:\AIWorkspace",

        @"C:\BlenderProjects",

        @"C:\SubstanceProjects",

        sourceRoot

        // Kasnije:
        // @"C:\UnityProjects\MyGame"
    };


AIIntegration ai =
    new AIIntegration(
        allowedRoots,
        projectFile,
        sourceRoot,
        updaterProject
    );


while (true)
{
    Console.Write(
        "Ti: "
    );


    string? prompt =
        Console.ReadLine();


    if (
        prompt?.Equals(
            "EXIT",
            System.StringComparison.OrdinalIgnoreCase
        )
        == true
    )
    {
        break;
    }


    if (
        string.IsNullOrWhiteSpace(
            prompt
        )
    )
    {
        Console.WriteLine(
            "Nisi unio pitanje."
        );

        continue;
    }


    string answer =
        await ai.Ask(
            prompt
        );


    Console.WriteLine(
        $"AI: {answer}"
    );
}