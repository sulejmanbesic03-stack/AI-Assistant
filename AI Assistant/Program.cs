using AI_Assistant.AI;

using System;
using System.Collections.Generic;
using System.IO;


// ============================================
// FIND MAIN PROJECT
// ============================================

string projectFile =
    FindProjectFileUpwards(
        AppContext.BaseDirectory,
        "AI Assistant.csproj"
    )
    ??
    throw new FileNotFoundException(
        "AI Assistant.csproj nije pronađen."
    );


string sourceRoot =
    Path.GetDirectoryName(
        projectFile
    )
    ??
    throw new DirectoryNotFoundException(
        "Source root nije pronađen."
    );


// ============================================
// FIND SOLUTION / UPDATER
// ============================================

string solutionRoot =
    Directory.GetParent(
        sourceRoot
    )?.FullName
    ??
    throw new DirectoryNotFoundException(
        "Solution root nije pronađen."
    );


string updaterProject =
    Path.Combine(
        solutionRoot,
        "AI Assistant Updater",
        "AI Assistant Updater.csproj"
    );


if (!File.Exists(updaterProject))
{
    throw new FileNotFoundException(
        $"Updater project nije pronađen:\n{updaterProject}"
    );
}


// ============================================
// ALLOWED FILESYSTEM ROOTS
// ============================================

List<string> allowedRoots =
    new List<string>();


string[] optionalRoots =
{
    @"C:\AIWorkspace",
    @"C:\BlenderProjects",
    @"C:\SubstanceProjects"
};


foreach (string root in optionalRoots)
{
    if (Directory.Exists(root))
    {
        allowedRoots.Add(
            root
        );
    }
}


// Agent mora moći čitati svoj source.
// Pisanje u source je dodatno zaštićeno
// kroz SelfDevelopmentTools.
allowedRoots.Add(
    sourceRoot
);


// ============================================
// START AGENT
// ============================================

AIIntegration ai =
    new AIIntegration(
        allowedRoots,
        projectFile,
        sourceRoot,
        updaterProject
    );


Console.WriteLine(
    "AI Assistant pokrenut."
);


Console.WriteLine(
    "Upiši EXIT za izlaz."
);


Console.WriteLine();


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
            StringComparison.OrdinalIgnoreCase
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


// ============================================
// FIND CSPROJ UPWARDS
// ============================================

static string? FindProjectFileUpwards(
    string startDirectory,
    string projectFileName
)
{
    DirectoryInfo? directory =
        new DirectoryInfo(
            startDirectory
        );


    while (directory != null)
    {
        string candidate =
            Path.Combine(
                directory.FullName,
                projectFileName
            );


        if (File.Exists(candidate))
        {
            return
                candidate;
        }


        directory =
            directory.Parent;
    }


    return null;
}