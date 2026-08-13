using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AI_Assistant.Tools
{
    public class FileSystemTools
    {
        private readonly List<string> allowedRoots;


        public FileSystemTools(List<string> allowedRoots)
        {
            this.allowedRoots = allowedRoots
                .Select(path =>
                    Path.GetFullPath(path)
                        .TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar
                        )
                    + Path.DirectorySeparatorChar
                )
                .ToList();
        }

        public string ListAllowedRoots()
        {
            return string.Join(
                "\n",
                allowedRoots
            );
        }
        private string GetSafePath(string path)
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

                    bool isRootItself =
                        fullPath.Equals(
                            cleanRoot,
                            StringComparison.OrdinalIgnoreCase
                        );

                    bool isInsideRoot =
                        fullPath.StartsWith(
                            cleanRoot + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase
                        );

                    return isRootItself || isInsideRoot;
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
        // CREATE FOLDER
        // ============================================

        public string CreateFolder(string folderPath)
        {
            string fullPath =
                GetSafePath(folderPath);


            Directory.CreateDirectory(fullPath);


            return $"Folder napravljen: {fullPath}";
        }


        // ============================================
        // CREATE FILE
        // ============================================

        public string CreateFile(
            string filePath,
            string content
        )
        {
            string fullPath =
                GetSafePath(filePath);


            string? directory =
                Path.GetDirectoryName(fullPath);


            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }


            File.WriteAllText(
                fullPath,
                content
            );


            return $"File napravljen: {fullPath}";
        }


        // ============================================
        // READ FILE
        // ============================================

        public string ReadFile(string filePath)
        {
            string fullPath =
                GetSafePath(filePath);


            if (!File.Exists(fullPath))
            {
                return $"File ne postoji: {fullPath}";
            }


            return File.ReadAllText(fullPath);
        }


        // ============================================
        // LIST FILES
        // ============================================

        public string ListFiles(string folderPath)
        {
            string fullPath =
                GetSafePath(folderPath);


            if (!Directory.Exists(fullPath))
            {
                return $"Folder ne postoji: {fullPath}";
            }


            string[] files =
                Directory.GetFiles(fullPath);


            if (files.Length == 0)
            {
                return "Folder nema fileova.";
            }


            return string.Join(
                "\n",
                files
            );
        }


        // ============================================
        // LIST DIRECTORIES
        // ============================================

        public string ListDirectories(string folderPath)
        {
            string fullPath =
                GetSafePath(folderPath);


            if (!Directory.Exists(fullPath))
            {
                return $"Folder ne postoji: {fullPath}";
            }


            string[] directories =
                Directory.GetDirectories(fullPath);


            if (directories.Length == 0)
            {
                return "Folder nema podfoldera.";
            }


            return string.Join(
                "\n",
                directories
            );
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
                return $"Izvorni file ne postoji: {safeSource}";
            }


            string? destinationDirectory =
                Path.GetDirectoryName(
                    safeDestination
                );


            if (!string.IsNullOrWhiteSpace(
                destinationDirectory
            ))
            {
                Directory.CreateDirectory(
                    destinationDirectory
                );
            }


            File.Copy(
                safeSource,
                safeDestination,
                overwrite
            );


            return
                $"File kopiran iz:\n{safeSource}\nU:\n{safeDestination}";
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
                return $"Izvorni file ne postoji: {safeSource}";
            }


            string? destinationDirectory =
                Path.GetDirectoryName(
                    safeDestination
                );


            if (!string.IsNullOrWhiteSpace(
                destinationDirectory
            ))
            {
                Directory.CreateDirectory(
                    destinationDirectory
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
                $"File premješten iz:\n{safeSource}\nU:\n{safeDestination}";
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
                return $"Folder ne postoji: {safeRoot}";
            }


            string[] files =
                Directory.GetFiles(
                    safeRoot,
                    fileName,
                    SearchOption.AllDirectories
                );


            if (files.Length == 0)
            {
                return
                    $"Nije pronađen file '{fileName}' unutar {safeRoot}";
            }


            return string.Join(
                "\n",
                files
            );
        }
    }
}