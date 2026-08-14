using System;
using System.Diagnostics;
using System.IO;
using System.Threading;


// ============================================
// ARGUMENT VALIDATION
// ============================================

if (args.Length != 4)
{
    Console.WriteLine(
        $"Updater očekuje 4 argumenta. Dobijeno: {args.Length}"
    );


    for (int i = 0; i < args.Length; i++)
    {
        Console.WriteLine(
            $"ARG[{i}] = {args[i]}"
        );
    }


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
        "Neispravan parent PID."
    );

    return;
}


string stagingDirectory =
    Path.GetFullPath(
        args[1]
    );


string targetDirectory =
    Path.GetFullPath(
        args[2]
    );


string executableName =
    args[3];


Console.WriteLine(
    "AI Assistant Updater pokrenut."
);


Console.WriteLine(
    $"Parent PID: {parentPid}"
);


Console.WriteLine(
    $"Staging: {stagingDirectory}"
);


Console.WriteLine(
    $"Target: {targetDirectory}"
);


Console.WriteLine(
    $"Executable: {executableName}"
);


// ============================================
// WAIT FOR OLD PROCESS
// ============================================

Console.WriteLine(
    $"Čekam PID {parentPid}..."
);


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
    // Proces je možda već ugašen.
}


Thread.Sleep(
    750
);


// ============================================
// VALIDATE STAGING
// ============================================

if (!Directory.Exists(stagingDirectory))
{
    Console.WriteLine(
        $"UPDATE FAILED: staging ne postoji:\n{stagingDirectory}"
    );

    return;
}


Directory.CreateDirectory(
    targetDirectory
);


// ============================================
// COPY BUILD
// ============================================

Console.WriteLine(
    "Kopiram novu verziju..."
);


try
{
    CopyDirectory(
        stagingDirectory,
        targetDirectory
    );
}
catch (Exception ex)
{
    Console.WriteLine(
        $"UPDATE FAILED:\n{ex}"
    );

    return;
}


// ============================================
// VALIDATE EXECUTABLE
// ============================================

string executablePath =
    Path.Combine(
        targetDirectory,
        executableName
    );


if (!File.Exists(executablePath))
{
    Console.WriteLine(
        $"UPDATE FAILED: executable nije pronađen:\n{executablePath}"
    );

    return;
}


// ============================================
// START NEW VERSION
// ============================================

Console.WriteLine(
    $"Pokrećem novu verziju:\n{executablePath}"
);


ProcessStartInfo startInfo =
    new ProcessStartInfo
    {
        FileName =
            executablePath,

        WorkingDirectory =
            targetDirectory,

        UseShellExecute =
            true
    };


Process? newProcess =
    Process.Start(
        startInfo
    );


if (newProcess == null)
{
    Console.WriteLine(
        "UPDATE FAILED: nova verzija nije pokrenuta."
    );

    return;
}


Console.WriteLine(
    $"Nova verzija pokrenuta. PID: {newProcess.Id}"
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