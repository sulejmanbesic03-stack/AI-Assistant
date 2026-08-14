using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AI_Assistant.Tools
{
    public class FileSystemTools
    {
        private readonly List<string> allowedRoots;


        private const long MaxWholeFileReadBytes =
            12000;


        private const int MaxReadSectionLines =
            120;


        public FileSystemTools(
            List<string> allowedRoots
        )
        {
            this.allowedRoots =
                allowedRoots
                    .Select(path =>
                        Path.GetFullPath(path)
                            .TrimEnd(
                                Path.DirectorySeparatorChar,
                                Path.AltDirectorySeparatorChar
                            )
                    )
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase
                    )
                    .ToList();
        }


        // ============================================
        // SAFE PATH
        // ============================================

        private string GetSafePath(
            string path
        )
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new UnauthorizedAccessException(
                    "Path je prazan."
                );
            }


            if (!Path.IsPathRooted(path))
            {
                throw new UnauthorizedAccessException(
                    "Generic filesystem tools zahtijevaju apsolutnu putanju."
                );
            }


            string fullPath =
                Path.GetFullPath(path)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    );


            bool isAllowed =
                allowedRoots.Any(root =>
                {
                    bool isRoot =
                        fullPath.Equals(
                            root,
                            StringComparison.OrdinalIgnoreCase
                        );


                    bool isInsideRoot =
                        fullPath.StartsWith(
                            root +
                            Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase
                        );


                    return
                        isRoot ||
                        isInsideRoot;
                });


            if (!isAllowed)
            {
                throw new UnauthorizedAccessException(
                    $"Path nije unutar dozvoljenih lokacija: {fullPath}"
                );
            }


            return fullPath;
        }


        // ============================================
        // LIST ALLOWED ROOTS
        // ============================================

        public string ListAllowedRoots()
        {
            if (allowedRoots.Count == 0)
            {
                return
                    "Nema dozvoljenih filesystem lokacija.";
            }


            return string.Join(
                Environment.NewLine,
                allowedRoots
            );
        }


        // ============================================
        // CREATE FOLDER
        // ============================================

        public string CreateFolder(
            string folderPath
        )
        {
            string fullPath =
                GetSafePath(folderPath);


            Directory.CreateDirectory(
                fullPath
            );


            return
                $"FOLDER CREATED:\n{fullPath}";
        }


        // ============================================
        // CREATE / OVERWRITE FILE
        // ============================================

        public string CreateFile(
            string filePath,
            string content
        )
        {
            string fullPath =
                GetSafePath(filePath);


            string? directory =
                Path.GetDirectoryName(
                    fullPath
                );


            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(
                    directory
                );
            }


            File.WriteAllText(
                fullPath,
                content
            );


            return
                $"FILE WRITTEN:\n{fullPath}";
        }


        // ============================================
        // READ WHOLE FILE
        // ============================================

        public string ReadFile(
            string filePath
        )
        {
            string fullPath =
                GetSafePath(filePath);


            if (!File.Exists(fullPath))
            {
                return
                    $"FILE NOT FOUND:\n{fullPath}";
            }


            FileInfo info =
                new FileInfo(fullPath);


            if (info.Length > MaxWholeFileReadBytes)
            {
                int lineCount =
                    File.ReadLines(fullPath)
                        .Count();


                return
                    $"FILE TOO LARGE FOR FULL READ\n" +
                    $"PATH: {fullPath}\n" +
                    $"SIZE: {info.Length} bytes\n" +
                    $"LINES: {lineCount}\n" +
                    $"Use read_file_section.";
            }


            return
                File.ReadAllText(
                    fullPath
                );
        }


        // ============================================
        // READ FILE SECTION
        // ============================================

        public string ReadFileSection(
            string filePath,
            int startLine,
            int endLine
        )
        {
            string fullPath =
                GetSafePath(filePath);


            if (!File.Exists(fullPath))
            {
                return
                    $"FILE NOT FOUND:\n{fullPath}";
            }


            if (startLine < 1)
            {
                startLine = 1;
            }


            if (endLine < startLine)
            {
                return
                    "READ FAILED: endLine mora biti >= startLine.";
            }


            if (
                endLine - startLine + 1 >
                MaxReadSectionLines
            )
            {
                endLine =
                    startLine +
                    MaxReadSectionLines -
                    1;
            }


            string[] lines =
                File.ReadAllLines(
                    fullPath
                );


            if (lines.Length == 0)
            {
                return
                    $"FILE IS EMPTY:\n{fullPath}";
            }


            if (startLine > lines.Length)
            {
                return
                    $"READ FAILED: File ima {lines.Length} linija.";
            }


            endLine =
                Math.Min(
                    endLine,
                    lines.Length
                );


            IEnumerable<string> selectedLines =
                lines
                    .Skip(startLine - 1)
                    .Take(
                        endLine -
                        startLine +
                        1
                    )
                    .Select(
                        (line, index) =>
                            $"{startLine + index}: {line}"
                    );


            return
                $"FILE: {fullPath}\n" +
                $"LINES: {startLine}-{endLine} / {lines.Length}\n\n" +
                string.Join(
                    Environment.NewLine,
                    selectedLines
                );
        }


        // ============================================
        // LIST FILES
        // ============================================

        public string ListFiles(
            string folderPath
        )
        {
            string fullPath =
                GetSafePath(folderPath);


            if (!Directory.Exists(fullPath))
            {
                return
                    $"FOLDER NOT FOUND:\n{fullPath}";
            }


            string[] files =
                Directory.GetFiles(
                    fullPath
                );


            if (files.Length == 0)
            {
                return
                    "Folder nema fileova.";
            }


            return
                string.Join(
                    Environment.NewLine,
                    files
                );
        }


        // ============================================
        // LIST DIRECTORIES
        // ============================================

        public string ListDirectories(
            string folderPath
        )
        {
            string fullPath =
                GetSafePath(folderPath);


            if (!Directory.Exists(fullPath))
            {
                return
                    $"FOLDER NOT FOUND:\n{fullPath}";
            }


            string[] directories =
                Directory.GetDirectories(
                    fullPath
                );


            if (directories.Length == 0)
            {
                return
                    "Folder nema podfoldera.";
            }


            return
                string.Join(
                    Environment.NewLine,
                    directories
                );
        }


        // ============================================
        // FIND FILE
        // ============================================

        public string FindFile(
            string rootPath,
            string fileName
        )
        {
            string safeRoot =
                GetSafePath(rootPath);


            if (!Directory.Exists(safeRoot))
            {
                return
                    $"FOLDER NOT FOUND:\n{safeRoot}";
            }


            EnumerationOptions options =
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                };


            string[] matches =
                Directory
                    .EnumerateFiles(
                        safeRoot,
                        fileName,
                        options
                    )
                    .Take(25)
                    .ToArray();


            if (matches.Length == 0)
            {
                return
                    $"FILE NOT FOUND: {fileName}";
            }


            return
                string.Join(
                    Environment.NewLine,
                    matches
                );
        }


        // ============================================
        // SEARCH ALL ROOTS + READ SMALL FILE
        // ============================================

        public string SearchAndReadFile(
            string fileName
        )
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return
                    "SEARCH FAILED: fileName je prazan.";
            }


            // Ovaj tool prima samo filename/pattern,
            // ne full path.
            fileName =
                Path.GetFileName(
                    fileName
                );


            EnumerationOptions options =
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                };


            foreach (string root in allowedRoots)
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }


                string? match =
                    Directory
                        .EnumerateFiles(
                            root,
                            fileName,
                            options
                        )
                        .FirstOrDefault();


                if (match == null)
                {
                    continue;
                }


                FileInfo info =
                    new FileInfo(match);


                int lineCount =
                    File.ReadLines(match)
                        .Count();


                if (info.Length > MaxWholeFileReadBytes)
                {
                    return
                        $"FOUND FILE:\n{match}\n\n" +
                        $"SIZE: {info.Length} bytes\n" +
                        $"LINES: {lineCount}\n" +
                        $"File je prevelik za full read. Use read_file_section.";
                }


                return
                    $"FOUND FILE:\n{match}\n\n" +
                    $"CONTENT:\n{File.ReadAllText(match)}";
            }


            return
                $"FILE NOT FOUND: {fileName}";
        }


        // ============================================
        // COPY FILE
        // ============================================

        public string CopyFile(
            string sourcePath,
            string destinationPath,
            bool overwrite
        )
        {
            string safeSource =
                GetSafePath(sourcePath);


            string safeDestination =
                GetSafePath(destinationPath);


            if (!File.Exists(safeSource))
            {
                return
                    $"SOURCE FILE NOT FOUND:\n{safeSource}";
            }


            string? directory =
                Path.GetDirectoryName(
                    safeDestination
                );


            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(
                    directory
                );
            }


            File.Copy(
                safeSource,
                safeDestination,
                overwrite
            );


            return
                $"FILE COPIED:\n" +
                $"{safeSource}\n→\n{safeDestination}";
        }


        // ============================================
        // MOVE FILE
        // ============================================

        public string MoveFile(
            string sourcePath,
            string destinationPath,
            bool overwrite
        )
        {
            string safeSource =
                GetSafePath(sourcePath);


            string safeDestination =
                GetSafePath(destinationPath);


            if (!File.Exists(safeSource))
            {
                return
                    $"SOURCE FILE NOT FOUND:\n{safeSource}";
            }


            string? directory =
                Path.GetDirectoryName(
                    safeDestination
                );


            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(
                    directory
                );
            }


            if (
                overwrite &&
                File.Exists(safeDestination)
            )
            {
                File.Delete(
                    safeDestination
                );
            }


            File.Move(
                safeSource,
                safeDestination
            );


            return
                $"FILE MOVED:\n" +
                $"{safeSource}\n→\n{safeDestination}";
        }
    }
}