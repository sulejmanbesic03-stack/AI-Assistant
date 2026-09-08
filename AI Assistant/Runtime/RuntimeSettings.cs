using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace AI_Assistant.Runtime
{
    public sealed class RuntimeSettings
    {
        public string UnityProjectRoot { get; set; } = "";
        public string BlenderExecutable { get; set; } = "";
        public string BlenderWorkspace { get; set; } = @"C:\BlenderProjects";
        public bool RequireApprovalForDestructiveChanges { get; set; } = true;
        public bool PreferFreeProviders { get; set; } = true;
        public int MaxModelCallsPerTask { get; set; } = 8;

        public static string SettingsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AI Assistant"
        );

        public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

        public static RuntimeSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    RuntimeSettings? loaded = JsonSerializer.Deserialize<RuntimeSettings>(json);
                    if (loaded != null)
                    {
                        loaded.Normalize();
                        return loaded;
                    }
                }
            }
            catch
            {
            }

            RuntimeSettings settings = new RuntimeSettings();
            settings.UnityProjectRoot = Environment.GetEnvironmentVariable("AI_UNITY_PROJECT_ROOT") ?? "";
            settings.BlenderExecutable = Environment.GetEnvironmentVariable("BLENDER_EXE") ?? "";
            settings.Normalize();
            return settings;
        }

        public void Save()
        {
            Normalize();
            Directory.CreateDirectory(SettingsDirectory);
            string json = JsonSerializer.Serialize(
                this,
                new JsonSerializerOptions { WriteIndented = true }
            );
            File.WriteAllText(SettingsFile, json);
        }

        public void ApplyToProcessEnvironment()
        {
            if (!string.IsNullOrWhiteSpace(UnityProjectRoot))
            {
                Environment.SetEnvironmentVariable("AI_UNITY_PROJECT_ROOT", UnityProjectRoot);
            }

            if (!string.IsNullOrWhiteSpace(BlenderExecutable))
            {
                Environment.SetEnvironmentVariable("BLENDER_EXE", BlenderExecutable);
            }

            Environment.SetEnvironmentVariable(
                "AI_REQUIRE_DESTRUCTIVE_APPROVAL",
                RequireApprovalForDestructiveChanges ? "1" : "0"
            );
            Environment.SetEnvironmentVariable(
                "AI_PREFER_FREE_PROVIDERS",
                PreferFreeProviders ? "1" : "0"
            );
            Environment.SetEnvironmentVariable(
                "AI_MAX_MODEL_CALLS",
                Math.Clamp(MaxModelCallsPerTask, 1, 20).ToString()
            );
        }

        public IReadOnlyList<string> Validate()
        {
            List<string> issues = new List<string>();

            if (!string.IsNullOrWhiteSpace(UnityProjectRoot))
            {
                if (!Directory.Exists(UnityProjectRoot))
                {
                    issues.Add("Unity project root does not exist: " + UnityProjectRoot);
                }
                else if (!Directory.Exists(Path.Combine(UnityProjectRoot, "Assets")))
                {
                    issues.Add("Unity project root has no Assets folder: " + UnityProjectRoot);
                }
            }

            string blender = ResolveBlenderExecutable();
            if (string.IsNullOrWhiteSpace(blender) || !File.Exists(blender))
            {
                issues.Add("Blender executable was not found. Set it in Settings or BLENDER_EXE.");
            }

            return issues;
        }

        public string ResolveBlenderExecutable()
        {
            if (!string.IsNullOrWhiteSpace(BlenderExecutable) && File.Exists(BlenderExecutable))
            {
                return BlenderExecutable;
            }

            string? env = Environment.GetEnvironmentVariable("BLENDER_EXE");
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            {
                return env;
            }

            string[] common =
            {
                @"C:\Program Files\Blender Foundation\Blender 4.5\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 4.4\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 4.3\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 4.1\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 4.0\blender.exe",
                @"C:\Program Files\Blender Foundation\Blender 3.6\blender.exe"
            };

            foreach (string candidate in common)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return "";
        }

        private void Normalize()
        {
            UnityProjectRoot = (UnityProjectRoot ?? "").Trim().Trim('"');
            BlenderExecutable = (BlenderExecutable ?? "").Trim().Trim('"');
            BlenderWorkspace = string.IsNullOrWhiteSpace(BlenderWorkspace)
                ? @"C:\BlenderProjects"
                : BlenderWorkspace.Trim().Trim('"');
            MaxModelCallsPerTask = Math.Clamp(MaxModelCallsPerTask, 1, 20);
        }
    }
}
