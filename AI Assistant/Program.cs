using AI_Assistant.Tools;

AIIntegration ai = new AIIntegration(
    @"C:\AIWorkspace"
);

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