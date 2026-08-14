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

        private bool backupCreated;
        private bool sourceModified;
        private bool lastBuildSucceeded;


        public SelfDevelopmentTools(
            string projectFilePath,
            string sourceRoot,
            string updaterProjectPath
        )
        {
            this.projectFilePath =
                Path.GetFullPath(projectFilePath);

            this.sourceRoot =
                Path.GetFullPath(sourceRoot)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    );

            this.updaterProjectPath =
                Path.GetFullPath(updaterProjectPath);


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
                    $"SOURCE INSPECTION FAILED:\nSource root ne postoji:\n{sourceRoot}";
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
                        !IsIgnoredBuildPath(path)
                    )
                    .OrderBy(path => path)
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

            result.AppendLine(
                $"BACKUP CREATED: {backupCreated}"
            );

            result.AppendLine(
                $"SOURCE MODIFIED: {sourceModified}"
            );

            result.AppendLine(
                $"LAST BUILD SUCCESS: {lastBuildSucceeded}"
            );

            result.AppendLine();


            foreach (string file in files)
            {
                string relativePath =
                    Path.GetRelativePath(
                        sourceRoot,
                        file
                    );


                int lineCount =
                    File.ReadLines(file)
                        .Count();


                FileInfo info =
                    new FileInfo(file);


                result.AppendLine(
                    $"{relativePath} | {lineCount} lines | {info.Length} bytes"
                );
            }


            return result.ToString();
        }
        public string FindSelfText(
    string relativePath,
    string searchText
)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return
                    "SELF FIND FAILED: searchText je prazan.";
            }

            string safePath;

            try
            {
                safePath =
                    GetSafeSelfPath(
                        relativePath
                    );
            }
            catch (Exception ex)
            {
                return
                    $"SELF FIND DENIED: {ex.Message}";
            }

            if (!File.Exists(safePath))
            {
                return
                    $"SELF FIND FAILED: File ne postoji:\n{relativePath}";
            }

            string[] lines =
                File.ReadAllLines(
                    safePath
                );

            var matches =
                lines
                    .Select(
                        (line, index) =>
                            new
                            {
                                Line = line,
                                Number = index + 1
                            }
                    )
                    .Where(item =>
                        item.Line.Contains(
                            searchText,
                            StringComparison.Ordinal
                        )
                    )
                    .Take(10)
                    .ToList();

            if (matches.Count == 0)
            {
                return
                    $"SELF FIND FAILED:\n" +
                    $"FILE: {relativePath}\n" +
                    $"TEXT: {searchText}";
            }

            StringBuilder result =
                new StringBuilder();

            result.AppendLine(
                $"FOUND IN: {relativePath}"
            );

            result.AppendLine(
                $"MATCH COUNT SHOWN: {matches.Count}"
            );

            result.AppendLine();

            foreach (var match in matches)
            {
                result.AppendLine(
                    $"LINE {match.Number}: {match.Line.Trim()}"
                );
            }

            int firstLine =
                matches[0].Number;

            int suggestedStart =
                Math.Max(
                    1,
                    firstLine - 20
                );

            int suggestedEnd =
                Math.Min(
                    lines.Length,
                    firstLine + 80
                );

            result.AppendLine();

            result.AppendLine(
                $"SUGGESTED READ RANGE: {suggestedStart}-{suggestedEnd}"
            );

            return
                result.ToString();
        }

        // ============================================
        // READ SELF FILE SECTION
        // ============================================

        public string ReadSelfFileSection(
            string relativePath,
            int startLine,
            int endLine
        )
        {
            string safePath;


            try
            {
                safePath =
                    GetSafeSelfPath(
                        relativePath
                    );
            }
            catch (Exception ex)
            {
                return
                    $"SELF READ DENIED: {ex.Message}";
            }


            if (!File.Exists(safePath))
            {
                return
                    $"SELF READ FAILED: File ne postoji:\n{relativePath}";
            }


            if (startLine < 1)
            {
                startLine = 1;
            }


            if (endLine < startLine)
            {
                return
                    "SELF READ FAILED: endLine mora biti >= startLine.";
            }


            string[] lines =
                File.ReadAllLines(
                    safePath
                );


            if (lines.Length == 0)
            {
                return
                    $"SELF FILE IS EMPTY:\n{relativePath}";
            }


            if (startLine > lines.Length)
            {
                return
                    $"SELF READ FAILED: File ima samo {lines.Length} linija.";
            }


            endLine =
                Math.Min(
                    endLine,
                    lines.Length
                );


            StringBuilder result =
                new StringBuilder();


            result.AppendLine(
                $"FILE: {relativePath}"
            );

            result.AppendLine(
                $"LINES: {startLine}-{endLine} / {lines.Length}"
            );

            result.AppendLine();


            for (
                int i = startLine - 1;
                i < endLine;
                i++
            )
            {
                result.AppendLine(
                    $"{i + 1}: {lines[i]}"
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
                    $"BACKUP FAILED:\nSource root ne postoji:\n{sourceRoot}";
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


            try
            {
                Directory.CreateDirectory(
                    backupPath
                );


                CopySourceDirectory(
                    sourceRoot,
                    backupPath
                );
            }
            catch (Exception ex)
            {
                backupCreated = false;

                return
                    $"BACKUP FAILED:\n{ex.Message}";
            }


            backupCreated = true;
            sourceModified = false;
            lastBuildSucceeded = false;


            return
                $"BACKUP SUCCESS:\n{backupPath}";
        }


        // ============================================
        // WRITE WHOLE SELF FILE
        // ============================================

        public string WriteSelfFile(
            string relativePath,
            string content
        )
        {
            if (!backupCreated)
            {
                return
                    "WRITE DENIED: backup_project mora prvo biti uspješno izvršen.";
            }


            string safePath;


            try
            {
                safePath =
                    GetSafeSelfPath(
                        relativePath
                    );
            }
            catch (Exception ex)
            {
                return
                    $"WRITE DENIED: {ex.Message}";
            }


            try
            {
                string? directory =
                    Path.GetDirectoryName(
                        safePath
                    );


                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(
                        directory
                    );
                }


                WriteFileAtomic(
                    safePath,
                    content
                );
            }
            catch (Exception ex)
            {
                return
                    $"WRITE FAILED:\n{ex.Message}";
            }


            MarkSourceModified();


            return
                $"WRITE SUCCESS:\n{relativePath}";
        }


        // ============================================
        // REPLACE SELF TEXT
        // ============================================

        public string ReplaceSelfText(
            string relativePath,
            string oldText,
            string newText
        )
        {
            if (!backupCreated)
            {
                return
                    "REPLACE DENIED: backup_project mora prvo biti uspješno izvršen.";
            }


            if (string.IsNullOrWhiteSpace(oldText))
            {
                return
                    "REPLACE FAILED: oldText ne smije biti prazan.";
            }


            string safePath;


            try
            {
                safePath =
                    GetSafeSelfPath(
                        relativePath
                    );
            }
            catch (Exception ex)
            {
                return
                    $"REPLACE DENIED: {ex.Message}";
            }


            if (!File.Exists(safePath))
            {
                return
                    $"REPLACE FAILED: File ne postoji:\n{relativePath}";
            }


            string originalContent;


            try
            {
                originalContent =
                    File.ReadAllText(
                        safePath
                    );
            }
            catch (Exception ex)
            {
                return
                    $"REPLACE FAILED: File nije moguće pročitati.\n{ex.Message}";
            }


            int occurrenceCount =
                CountOccurrences(
                    originalContent,
                    oldText
                );


            if (occurrenceCount == 0)
            {
                return
                    "REPLACE FAILED: oldText nije pronađen. " +
                    "Ponovo pročitaj relevantnu sekciju fajla i koristi tačan tekst.";
            }


            if (occurrenceCount > 1)
            {
                return
                    $"REPLACE DENIED: oldText se pojavljuje {occurrenceCount} puta. " +
                    "Koristi veći i jedinstveniji blok teksta da izmjena ne bude dvosmislena.";
            }


            string updatedContent =
                originalContent.Replace(
                    oldText,
                    newText,
                    StringComparison.Ordinal
                );


            try
            {
                WriteFileAtomic(
                    safePath,
                    updatedContent
                );
            }
            catch (Exception ex)
            {
                return
                    $"REPLACE FAILED: Nova verzija nije mogla biti upisana.\n{ex.Message}";
            }


            MarkSourceModified();


            return
                $"REPLACE SUCCESS:\n{relativePath}\n" +
                $"Old chars: {oldText.Length}\n" +
                $"New chars: {newText.Length}";
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
                    $"BUILD FAILED:\nProject nije pronađen:\n{projectFilePath}";
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
                    $"BUILD FAILED:\nStaging folder nije moguće pripremiti.\n{ex.Message}";
            }


            ProcessStartInfo startInfo =
                new ProcessStartInfo
                {
                    FileName = "dotnet",

                    Arguments =
                        $"build \"{projectFilePath}\" " +
                        "-c Release " +
                        $"-o \"{stagingRoot}\" " +
                        "--nologo " +
                        "-v:minimal",

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
            if (!backupCreated)
            {
                return
                    "RESTART DENIED: backup_project nije izvršen.";
            }


            if (!sourceModified)
            {
                return
                    "RESTART DENIED: source nije izmijenjen.";
            }


            if (!lastBuildSucceeded)
            {
                return
                    "RESTART DENIED: build_self mora prvo završiti sa BUILD SUCCESS.";
            }


            if (!File.Exists(updaterProjectPath))
            {
                return
                    $"RESTART FAILED:\nUpdater project nije pronađen:\n{updaterProjectPath}";
            }


            string updaterBuild =
                BuildUpdater();


            if (
                !updaterBuild.StartsWith(
                    "UPDATER BUILD SUCCESS",
                    StringComparison.Ordinal
                )
            )
            {
                return updaterBuild;
            }


            int currentPid =
                Environment.ProcessId;


            string currentDirectory =
                AppContext.BaseDirectory
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    );


            string? currentExePath =
                Environment.ProcessPath;


            if (string.IsNullOrWhiteSpace(currentExePath))
            {
                return
                    "RESTART FAILED: trenutni executable nije pronađen.";
            }


            string exeName =
                Path.GetFileName(
                    currentExePath
                );


            ProcessStartInfo updaterInfo =
                new ProcessStartInfo
                {
                    FileName = "dotnet",
                    UseShellExecute = false,
                    CreateNoWindow = false
                };


            updaterInfo.ArgumentList.Add(
                "run"
            );

            updaterInfo.ArgumentList.Add(
                "--project"
            );

            updaterInfo.ArgumentList.Add(
                updaterProjectPath
            );

            updaterInfo.ArgumentList.Add(
                "-c"
            );

            updaterInfo.ArgumentList.Add(
                "Release"
            );

            updaterInfo.ArgumentList.Add(
                "--no-build"
            );

            updaterInfo.ArgumentList.Add(
                "--"
            );

            updaterInfo.ArgumentList.Add(
                currentPid.ToString()
            );

            updaterInfo.ArgumentList.Add(
                stagingRoot
            );

            updaterInfo.ArgumentList.Add(
                currentDirectory
            );

            updaterInfo.ArgumentList.Add(
                exeName
            );


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
        // CHECK SELF PATH
        // ============================================

        public bool IsSelfPath(
            string path
        )
        {
            try
            {
                string fullPath =
                    Path.GetFullPath(path)
                        .TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar
                        );


                bool isRoot =
                    fullPath.Equals(
                        sourceRoot,
                        StringComparison.OrdinalIgnoreCase
                    );


                bool insideRoot =
                    fullPath.StartsWith(
                        sourceRoot +
                        Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    );


                return isRoot || insideRoot;
            }
            catch
            {
                return false;
            }
        }


        // ============================================
        // SAFE RELATIVE SELF PATH
        // ============================================

        private string GetSafeSelfPath(
            string relativePath
        )
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new UnauthorizedAccessException(
                    "Relative path je prazan."
                );
            }


            if (Path.IsPathRooted(relativePath))
            {
                throw new UnauthorizedAccessException(
                    "Self-development tools prihvataju samo relativne putanje."
                );
            }


            string fullPath =
                Path.GetFullPath(
                    Path.Combine(
                        sourceRoot,
                        relativePath
                    )
                )
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                );


            bool isRoot =
                fullPath.Equals(
                    sourceRoot,
                    StringComparison.OrdinalIgnoreCase
                );


            bool isInside =
                fullPath.StartsWith(
                    sourceRoot +
                    Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase
                );


            if (!isRoot && !isInside)
            {
                throw new UnauthorizedAccessException(
                    "Path pokušava izaći iz source projekta."
                );
            }


            if (IsIgnoredBuildPath(fullPath))
            {
                throw new UnauthorizedAccessException(
                    "Pisanje u bin, obj, .git ili .vs nije dozvoljeno."
                );
            }


            return fullPath;
        }


        // ============================================
        // ATOMIC FILE WRITE
        // ============================================

        private void WriteFileAtomic(
            string destinationPath,
            string content
        )
        {
            string? directory =
                Path.GetDirectoryName(
                    destinationPath
                );


            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new IOException(
                    "Destination directory nije pronađen."
                );
            }


            Directory.CreateDirectory(
                directory
            );


            string tempFile =
                Path.Combine(
                    directory,
                    $".ai_temp_{Guid.NewGuid():N}.tmp"
                );


            try
            {
                File.WriteAllText(
                    tempFile,
                    content
                );


                File.Move(
                    tempFile,
                    destinationPath,
                    true
                );
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(
                        tempFile
                    );
                }
            }
        }


        // ============================================
        // COUNT EXACT OCCURRENCES
        // ============================================

        private int CountOccurrences(
            string text,
            string value
        )
        {
            int count =
                0;


            int index =
                0;


            while (
                (
                    index =
                        text.IndexOf(
                            value,
                            index,
                            StringComparison.Ordinal
                        )
                )
                >= 0
            )
            {
                count++;

                index +=
                    value.Length;
            }


            return count;
        }


        private void MarkSourceModified()
        {
            sourceModified = true;

            lastBuildSucceeded = false;
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
                        "-c Release --nologo -v:minimal",

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
        // COPY BACKUP
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
                File.Copy(
                    file,
                    Path.Combine(
                        destination,
                        Path.GetFileName(file)
                    ),
                    true
                );
            }


            foreach (
                string directory
                in Directory.GetDirectories(source)
            )
            {
                string name =
                    Path.GetFileName(
                        directory
                    );


                if (
                    name.Equals(
                        "bin",
                        StringComparison.OrdinalIgnoreCase
                    )
                    ||
                    name.Equals(
                        "obj",
                        StringComparison.OrdinalIgnoreCase
                    )
                    ||
                    name.Equals(
                        ".git",
                        StringComparison.OrdinalIgnoreCase
                    )
                    ||
                    name.Equals(
                        ".vs",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    continue;
                }


                CopySourceDirectory(
                    directory,
                    Path.Combine(
                        destination,
                        name
                    )
                );
            }
        }


        private bool IsIgnoredBuildPath(
            string path
        )
        {
            string slash =
                Path.DirectorySeparatorChar.ToString();


            return
                path.Contains(
                    $"{slash}bin{slash}",
                    StringComparison.OrdinalIgnoreCase
                )
                ||
                path.Contains(
                    $"{slash}obj{slash}",
                    StringComparison.OrdinalIgnoreCase
                )
                ||
                path.Contains(
                    $"{slash}.git{slash}",
                    StringComparison.OrdinalIgnoreCase
                )
                ||
                path.Contains(
                    $"{slash}.vs{slash}",
                    StringComparison.OrdinalIgnoreCase
                );
        }
    }
}