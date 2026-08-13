using AI_Assistant.Tools;
using System.Collections;
List<string> allowedRoots = new List<string>
{
    @"C:\AIWorkspace",
    @"C:\BlenderProjects",
    @"C:\SubstanceProjects",
    @"D:\UnityProjects\MyGame"
  
};


AIIntegration ai = new AIIntegration(allowedRoots);



while (true)
{
    Console.Write("Ti: ");

    string? prompt = Console.ReadLine();

    if (prompt == "EXIT")
    {
        break;
    }

    if (string.IsNullOrWhiteSpace(prompt))
    {
        Console.WriteLine("Nisi unio pitanje.");
        continue;
    }

    string odgovor = await ai.Ask(prompt);

    Console.WriteLine($"AI: {odgovor}");
}