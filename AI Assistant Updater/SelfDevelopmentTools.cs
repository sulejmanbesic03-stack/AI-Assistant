using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AI_Assistant.Tools
{
    public class SelfDevelopmentTools
    {
        private readonly string projectFilePath;
        private readonly string sourceRoot;
        private readonly string updaterProjectPath;

        private readonly string backupRoot;
        private readonly string stagingRoot;

        private bool lastBuildSucceeded;


        public SelfDevelopmentTools(
            string projectFilePath,
            string sourceRoot,
            string updaterProjectPath
        )
        {
            this.projectFilePath =
                Path.GetFullPath(
                    projectFilePath
                );


            this.sourceRoot =
                Path.GetFullPath(
                    sourceRoot
                );


            this.updaterProjectPath =
                Path.GetFullPath(
                    updaterProjectPath
                );


            backupRoot =
                Path.Combine(
                    Path.GetTempPath(),
                    "AIAssistantBackups"
                );


            stagingRoot =
                Path.Combine(
                    Path.GetTempPath(),
                    "AIAssistantStaging"
                );


            Directory.CreateDirectory(
                backupRoot
            );
        }


        // ============================================
        // INSPECT SELF STRUCTURE
        // ============================================

        public string InspectSelfStructure()
        {
            if (!Directory.Exists(sourceRoot))
            {
                return
                    $"Source root ne postoji: {sourceRoot}";
            }


            EnumerationOptions options =
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                };


            string[] files =
                Directory
                    .EnumerateFiles(
                        sourceRoot,
                        "*.cs",
                        options
                    )
                    .Where(path =>
                        !path.Contains(
                            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase
                        )
                        &&
                        !path.Contains(
                            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase
                        )
                        &&
                        !path.Contains(
                            $"{Path.DirectorySeparatorChar}.vs{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                    .ToArray();


            StringBuilder result =
                new StringBuilder();


            result.AppendLine(
                $"SOURCE ROOT: {sourceRoot}"
            );


            result.AppendLine(
                $"PROJECT: {projectFilePath}"
            );


            result.AppendLine();


            if (files.Length == 0)
            {
                result.AppendLine(
                    "Nisu pronađeni C# source fajlovi."
                );

                return result.ToString();
            }


            foreach (string file in files)
            {
                int lineCount =
                    File.ReadLines(file)
                        .Count();


                FileInfo info =
                    new FileInfo(file);


                result.AppendLine(
                    $"{file} | {lineCount} lines | {info.Length} bytes"
                );
            }


            return result.ToString();
        }


        // ============================================
        // BACKUP PROJECT
        // ============================================

        public string BackupProject()
        {
            if (!Directory.Exists(sourceRoot))
            {
                return
                    $"BACKUP FAILED: Source root ne postoji:\n{sourceRoot}";
            }


            string timestamp =
                DateTime.Now.ToString(
                    "yyyy-MM-dd_HH-mm-ss"
                );


            string backupPath =
                Path.Combine(
                    backupRoot,
                    timestamp
                );


            Directory.CreateDirectory(
                backupPath
            );


            try
            {
                CopySourceDirectory(
                    sourceRoot,
                    backupPath
                );
            }
            catch (Exception ex)
            {
                return
                    $"BACKUP FAILED:\n{ex.Message}";
            }


            return
                $"BACKUP SUCCESS:\n{backupPath}";
        }


        // ============================================
        // BUILD SELF
        // ============================================

        public string BuildSelf()
        {
            lastBuildSucceeded = false;


            if (!File.Exists(projectFilePath))
            {
                return
                    $"BUILD FAILED: Project nije pronađen:\n{projectFilePath}";
            }


            try
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(
                        stagingRoot,
                        true
                    );
                }


                Directory.CreateDirectory(
                    stagingRoot
                );
            }
            catch (Exception ex)
            {
                return
                    $"BUILD FAILED: staging folder nije moguće pripremiti.\n{ex.Message}";
            }


            ProcessStartInfo startInfo =
                new ProcessStartInfo
                {
                    FileName = "dotnet",

                    Arguments =
                        $"build \"{projectFilePath}\" " +
                        $"-c Release " +
                        $"-o \"{stagingRoot}\" " +
                        $"--nologo " +
                        $"-v:minimal",

                    RedirectStandardOutput = true,
                    RedirectStandardError = true,

                    UseShellExecute = false,
                    CreateNoWindow = true
                };


            using Process? process =
                Process.Start(
                    startInfo
                );


            if (process == null)
            {
                return
                    "BUILD FAILED: dotnet proces nije pokrenut.";
            }


            string output =
                process.StandardOutput
                    .ReadToEnd();


            string errors =
                process.StandardError
                    .ReadToEnd();


            process.WaitForExit();


            string combinedOutput =
                output +
                Environment.NewLine +
                errors;


            if (combinedOutput.Length > 12000)
            {
                combinedOutput =
                    combinedOutput.Substring(
                        combinedOutput.Length - 12000
                    );
            }


            if (process.ExitCode != 0)
            {
                return
                    $"BUILD FAILED:\n{combinedOutput}";
            }


            lastBuildSucceeded = true;


            return
                $"BUILD SUCCESS\n" +
                $"STAGING: {stagingRoot}\n\n" +
                combinedOutput;
        }


        // ============================================
        // RESTART SELF
        // ============================================

        public string RestartSelf()
        {
            if (!lastBuildSucceeded)
            {
                return
                    "RESTART DENIED: build_self mora prvo vratiti BUILD SUCCESS.";
            }


            if (!File.Exists(updaterProjectPath))
            {
                return
                    $"RESTART FAILED: Updater project nije pronađen:\n{updaterProjectPath}";
            }


            string updaterBuildResult =
                BuildUpdater();


            if (
                !updaterBuildResult.StartsWith(
                    "UPDATER BUILD SUCCESS",
                    StringComparison.Ordinal
                )
            )
            {
                return updaterBuildResult;
            }


            int currentPid =
                Environment.ProcessId;


            string currentDirectory =
                AppContext.BaseDirectory;


            string? currentExePath =
                Environment.ProcessPath;


            if (string.IsNullOrWhiteSpace(currentExePath))
            {
                return
                    "RESTART FAILED: nije moguće pronaći trenutni executable.";
            }


            string exeName =
                Path.GetFileName(
                    currentExePath
                );


            ProcessStartInfo updaterInfo =
                new ProcessStartInfo
                {
                    FileName = "dotnet",

                    Arguments =
                        $"run " +
                        $"--project \"{updaterProjectPath}\" " +
                        $"-c Release " +
                        $"--no-build " +
                        $"-- " +
                        $"{currentPid} " +
                        $"\"{stagingRoot}\" " +
                        $"\"{currentDirectory}\" " +
                        $"\"{exeName}\"",

                    UseShellExecute = false,
                    CreateNoWindow = false
                };


            Process? updaterProcess =
                Process.Start(
                    updaterInfo
                );


            if (updaterProcess == null)
            {
                return
                    "RESTART FAILED: updater proces nije pokrenut.";
            }


            Thread.Sleep(
                1000
            );


            Environment.Exit(0);


            return
                "Restart pokrenut.";
        }


        // ============================================
        // BUILD UPDATER
        // ============================================

        private string BuildUpdater()
        {
            ProcessStartInfo startInfo =
                new ProcessStartInfo
                {
                    FileName = "dotnet",

                    Arguments =
                        $"build \"{updaterProjectPath}\" " +
                        $"-c Release " +
                        $"--nologo " +
                        $"-v:minimal",

                    RedirectStandardOutput = true,
                    RedirectStandardError = true,

                    UseShellExecute = false,
                    CreateNoWindow = true
                };


            using Process? process =
                Process.Start(
                    startInfo
                );


            if (process == null)
            {
                return
                    "UPDATER BUILD FAILED: dotnet proces nije pokrenut.";
            }


            string output =
                process.StandardOutput
                    .ReadToEnd();


            string errors =
                process.StandardError
                    .ReadToEnd();


            process.WaitForExit();


            if (process.ExitCode != 0)
            {
                return
                    $"UPDATER BUILD FAILED:\n{output}\n{errors}";
            }


            return
                $"UPDATER BUILD SUCCESS\n{output}";
        }


        // ============================================
        // COPY SOURCE DIRECTORY FOR BACKUP
        // ============================================

        private void CopySourceDirectory(
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
                string directoryName =
                    Path.GetFileName(
                        directory
                    );


                if (
                    directoryName.Equals(
                        "bin",
                        StringComparison.OrdinalIgnoreCase
                    )
                    ||
                    directoryName.Equals(
                        "obj",
                        StringComparison.OrdinalIgnoreCase
                    )
                    ||
                    directoryName.Equals(
                        ".vs",
                        StringComparison.OrdinalIgnoreCase
                    )
                    ||
                    directoryName.Equals(
                        ".git",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    continue;
                }


                string destinationDirectory =
                    Path.Combine(
                        destination,
                        directoryName
                    );


                CopySourceDirectory(
                    directory,
                    destinationDirectory
                );
            }
        }
    }
}