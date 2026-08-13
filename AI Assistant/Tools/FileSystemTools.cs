using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AI_Assistant.Tools
{
    public class FileSystemTools
    {
        private readonly List<string> allowedRoots;


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
            string fullPath =
                Path.GetFullPath(path)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar
                    );


            bool isAllowed =
                allowedRoots.Any(root =>
                {
                    string cleanRoot =
                        root.TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar
                        );


                    bool isRoot =
                        fullPath.Equals(
                            cleanRoot,
                            StringComparison.OrdinalIgnoreCase
                        );


                    bool isInside =
                        fullPath.StartsWith(
                            cleanRoot +
                            Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase
                        );


                    return isRoot || isInside;
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
        // ALLOWED ROOTS
        // ============================================

        public string ListAllowedRoots()
        {
            if (allowedRoots.Count == 0)
            {
                return
                    "Nema dozvoljenih filesystem lokacija.";
            }


            return string.Join(
                "\n",
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
                $"Folder napravljen: {fullPath}";
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


            if (
                !string.IsNullOrWhiteSpace(
                    directory
                )
            )
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
                $"File napravljen: {fullPath}";
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
                    $"File ne postoji: {fullPath}";
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
                    $"File ne postoji: {fullPath}";
            }


            if (startLine < 1)
            {
                startLine = 1;
            }


            if (endLine < startLine)
            {
                return
                    "Neispravan line range.";
            }


            string[] lines =
                File.ReadAllLines(
                    fullPath
                );


            if (lines.Length == 0)
            {
                return
                    $"File je prazan: {fullPath}";
            }


            if (startLine > lines.Length)
            {
                return
                    $"Start line {startLine} je iza kraja filea. File ima {lines.Length} linija.";
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
                    "\n",
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
                    $"Folder ne postoji: {fullPath}";
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


            return string.Join(
                "\n",
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
                    $"Folder ne postoji: {fullPath}";
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


            return string.Join(
                "\n",
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
                    $"Folder ne postoji: {safeRoot}";
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
                        safeRoot,
                        fileName,
                        options
                    )
                    .Take(50)
                    .ToArray();


            if (files.Length == 0)
            {
                return
                    $"Nije pronađen '{fileName}' unutar {safeRoot}";
            }


            return string.Join(
                "\n",
                files
            );
        }


        // ============================================
        // SEARCH ALL ROOTS + READ
        // ============================================

        public string SearchAndReadFile(
            string fileName
        )
        {
            EnumerationOptions options =
                new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true
                };


            foreach (
                string root
                in allowedRoots
            )
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


                FileInfo fileInfo =
                    new FileInfo(match);


                // Velike source fileove nećemo slati cijele.
                if (fileInfo.Length > 20000)
                {
                    int lineCount =
                        File.ReadLines(match)
                            .Count();


                    return
                        $"FOUND FILE:\n{match}\n\n" +
                        $"File je velik ({fileInfo.Length} bytes, {lineCount} lines).\n" +
                        $"Koristi read_file_section za potrebne dijelove.";
                }


                string content =
                    File.ReadAllText(
                        match
                    );


                return
                    $"FOUND FILE:\n{match}\n\nCONTENT:\n{content}";
            }


            return
                $"Nije pronađen file: {fileName}";
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
                    $"Izvorni file ne postoji: {safeSource}";
            }


            string? directory =
                Path.GetDirectoryName(
                    safeDestination
                );


            if (
                !string.IsNullOrWhiteSpace(
                    directory
                )
            )
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
                $"File kopiran:\n{safeSource}\n→\n{safeDestination}";
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
                    $"Izvorni file ne postoji: {safeSource}";
            }


            string? directory =
                Path.GetDirectoryName(
                    safeDestination
                );


            if (
                !string.IsNullOrWhiteSpace(
                    directory
                )
            )
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
                $"File premješten:\n{safeSource}\n→\n{safeDestination}";
        }
    }
}