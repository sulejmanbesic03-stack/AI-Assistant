using System;
using System.Diagnostics;
using System.IO;
using System.Threading;


if (args.Length < 4)
{
    Console.WriteLine(
        "Updater nije dobio dovoljno argumenata."
    );

    return;
}


if (
    !int.TryParse(
        args[0],
        out int parentPid
    )
)
{
    Console.WriteLine(
        "Neispravan PID."
    );

    return;
}


string newBuildDirectory =
    Path.GetFullPath(
        args[1]
    );


string targetDirectory =
    Path.GetFullPath(
        args[2]
    );


string exeName =
    args[3];


Console.WriteLine(
    "AI Assistant Updater pokrenut."
);


Console.WriteLine(
    $"Čekam PID {parentPid}..."
);


// ============================================
// WAIT FOR OLD AGENT
// ============================================

try
{
    Process oldProcess =
        Process.GetProcessById(
            parentPid
        );


    oldProcess.WaitForExit();
}
catch
{
    // Stari proces možda već ne postoji.
}


Thread.Sleep(
    1000
);


// ============================================
// VALIDATE
// ============================================

if (!Directory.Exists(newBuildDirectory))
{
    Console.WriteLine(
        $"Staging build ne postoji:\n{newBuildDirectory}"
    );

    return;
}


Directory.CreateDirectory(
    targetDirectory
);


// ============================================
// INSTALL
// ============================================

Console.WriteLine(
    "Instaliram novu verziju..."
);


CopyDirectory(
    newBuildDirectory,
    targetDirectory
);


// ============================================
// RESTART
// ============================================

string newExePath =
    Path.Combine(
        targetDirectory,
        exeName
    );


if (!File.Exists(newExePath))
{
    Console.WriteLine(
        $"Executable nije pronađen:\n{newExePath}"
    );

    return;
}


ProcessStartInfo startInfo =
    new ProcessStartInfo
    {
        FileName =
            newExePath,

        WorkingDirectory =
            targetDirectory,

        UseShellExecute =
            true
    };


Process.Start(
    startInfo
);


Console.WriteLine(
    "Nova verzija pokrenuta."
);


// ============================================
// COPY DIRECTORY
// ============================================

static void CopyDirectory(
    string source,
    string destination
)
{
    Directory.CreateDirectory(
        destination
    );


    foreach (
        string file
        in Directory.GetFiles(source)
    )
    {
        string destinationFile =
            Path.Combine(
                destination,
                Path.GetFileName(file)
            );


        File.Copy(
            file,
            destinationFile,
            true
        );
    }


    foreach (
        string directory
        in Directory.GetDirectories(source)
    )
    {
        string destinationDirectory =
            Path.Combine(
                destination,
                Path.GetFileName(directory)
            );


        CopyDirectory(
            directory,
            destinationDirectory
        );
    }
}