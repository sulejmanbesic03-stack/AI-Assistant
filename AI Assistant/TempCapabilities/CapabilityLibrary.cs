using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;


namespace AI_Assistant.TempCapabilities
{
    // ============================================================
    // MANIFEST ENTRY
    // ============================================================

    public sealed class CapabilityManifestEntry
    {
        public string Name { get; set; } = "";

        public string Description { get; set; } = "";

        public string SourcePath { get; set; } = "";

        public string DllPath { get; set; } = "";

        public string SourceHash { get; set; } = "";

        public DateTime CreatedUtc { get; set; }

        public int TimesUsed { get; set; }
    }


    // ============================================================
    // NON-COLLECTIBLE LOAD CONTEXT FOR PERSISTED CAPABILITIES
    //
    // Unlike TempCapabilityLoadContext (collectible, unloaded after
    // every temp execution), persisted capabilities stay loaded for
    // the lifetime of the process so repeated calls don't pay the
    // reflection/load cost again.
    // ============================================================

    internal sealed class CapabilityLibraryLoadContext : AssemblyLoadContext
    {
        public CapabilityLibraryLoadContext(string name)
            : base(name, isCollectible: false)
        {
        }


        protected override Assembly? Load(AssemblyName assemblyName)
        {
            Assembly hostAssembly =
                typeof(ITempCapability).Assembly;

            string? hostAssemblyName =
                hostAssembly.GetName().Name;

            if (
                string.Equals(
                    assemblyName.Name,
                    hostAssemblyName,
                    StringComparison.Ordinal
                )
            )
            {
                return hostAssembly;
            }

            return null;
        }
    }


    // ============================================================
    // CAPABILITY LIBRARY
    //
    // Persistent registry of capabilities that were originally
    // written and compiled through TempCapabilityManager, then
    // promoted after a successful run.
    //
    // A promoted capability:
    //   - keeps its compiled DLL on disk (no recompiling next time)
    //   - keeps its source on disk (for audit/debugging)
    //   - is exposed to the model as its own first-class tool
    //     ("run_<Name>"), independent of any keyword heuristic.
    // ============================================================

    public sealed class CapabilityLibrary
    {
        private readonly string binRoot;
        private readonly string sourceRoot;
        private readonly string manifestPath;

        private readonly object manifestLock = new object();

        private List<CapabilityManifestEntry> entries =
            new List<CapabilityManifestEntry>();

        private readonly Dictionary<string, Assembly> loadedAssemblies =
            new Dictionary<string, Assembly>(StringComparer.Ordinal);


        public CapabilityLibrary(string libraryRoot)
        {
            binRoot = Path.Combine(libraryRoot, "bin");
            sourceRoot = Path.Combine(libraryRoot, "source");
            manifestPath = Path.Combine(libraryRoot, "manifest.json");

            Directory.CreateDirectory(binRoot);
            Directory.CreateDirectory(sourceRoot);

            Load();
        }


        // ============================================================
        // MANIFEST I/O
        // ============================================================

        private void Load()
        {
            if (!File.Exists(manifestPath))
            {
                entries = new List<CapabilityManifestEntry>();
                return;
            }

            try
            {
                string json = File.ReadAllText(manifestPath);

                entries =
                    JsonSerializer.Deserialize<List<CapabilityManifestEntry>>(json)
                    ?? new List<CapabilityManifestEntry>();
            }
            catch
            {
                // Corrupt manifest should not crash the agent on startup.
                // Start with an empty library rather than throwing.
                entries = new List<CapabilityManifestEntry>();
            }
        }


        private void Save()
        {
            lock (manifestLock)
            {
                string json = JsonSerializer.Serialize(
                    entries,
                    new JsonSerializerOptions { WriteIndented = true }
                );

                File.WriteAllText(manifestPath, json);
            }
        }


        // ============================================================
        // PROMOTE
        //
        // Called by TempCapabilityManager after a temp capability
        // compiles AND executes without a runtime error.
        // ============================================================

        public CapabilityManifestEntry Promote(
            string capabilityName,
            string sourceCode,
            byte[] assemblyBytes,
            string description
        )
        {
            string safeName =
                Regex.Replace(capabilityName, "[^A-Za-z0-9_]", "_");

            string hash =
                Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        Encoding.UTF8.GetBytes(sourceCode)
                    )
                ).Substring(0, 16);

            string dllPath =
                Path.Combine(binRoot, $"{safeName}.{hash}.dll");

            string srcPath =
                Path.Combine(sourceRoot, $"{safeName}.{hash}.cs");

            File.WriteAllBytes(dllPath, assemblyBytes);
            File.WriteAllText(srcPath, sourceCode);

            lock (manifestLock)
            {
                entries.RemoveAll(e =>
                    string.Equals(e.Name, capabilityName, StringComparison.Ordinal)
                );

                var entry = new CapabilityManifestEntry
                {
                    Name = capabilityName,
                    Description = string.IsNullOrWhiteSpace(description)
                        ? capabilityName
                        : description,
                    SourcePath = srcPath,
                    DllPath = dllPath,
                    SourceHash = hash,
                    CreatedUtc = DateTime.UtcNow,
                    TimesUsed = 0
                };

                entries.Add(entry);

                // Drop any previously loaded (now stale) assembly for
                // this name so ExecuteAsync reloads the new version.
                loadedAssemblies.Remove(capabilityName);

                Save();

                return entry;
            }
        }


        public IReadOnlyList<CapabilityManifestEntry> Entries => entries;


        // ============================================================
        // TOOL DEFINITIONS
        //
        // Appended to the `tools` array sent to the model, alongside
        // the native tools and the execute_temp_capability escape
        // hatch. Each promoted capability becomes directly callable
        // by name, without the model re-writing/re-compiling it.
        // ============================================================

        public List<object> GetToolDefinitions()
        {
            List<object> tools = new List<object>();

            foreach (CapabilityManifestEntry entry in entries)
            {
                tools.Add(new
                {
                    type = "function",
                    function = new
                    {
                        name = "run_" + entry.Name,
                        description = entry.Description,
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                arguments = new
                                {
                                    type = "object",
                                    description = "Arguments for this capability, as documented by its description."
                                }
                            },
                            required = Array.Empty<string>()
                        }
                    }
                });
            }

            return tools;
        }


        public bool TryGetEntry(string toolName, out CapabilityManifestEntry? entry)
        {
            string capabilityName =
                toolName.StartsWith("run_", StringComparison.Ordinal)
                    ? toolName.Substring("run_".Length)
                    : toolName;

            entry = entries.FirstOrDefault(e =>
                string.Equals(e.Name, capabilityName, StringComparison.Ordinal)
            );

            return entry != null;
        }


        // ============================================================
        // EXECUTE — no recompilation, loads persisted DLL once
        // ============================================================

        public async Task<string> ExecuteAsync(
            CapabilityManifestEntry entry,
            TempCapabilityContext context,
            string argumentsJson
        )
        {
            try
            {
                if (!loadedAssemblies.TryGetValue(entry.Name, out Assembly? assembly))
                {
                    if (!File.Exists(entry.DllPath))
                    {
                        return
                            "LIBRARY CAPABILITY ERROR: " +
                            $"DLL missing on disk for '{entry.Name}' ({entry.DllPath}).";
                    }

                    byte[] bytes = await File.ReadAllBytesAsync(entry.DllPath);
                    using MemoryStream stream = new MemoryStream(bytes);

                    CapabilityLibraryLoadContext loadContext =
                        new CapabilityLibraryLoadContext(entry.Name);

                    assembly = loadContext.LoadFromStream(stream);

                    loadedAssemblies[entry.Name] = assembly;
                }

                Type? capabilityType =
                    assembly.GetTypes().FirstOrDefault(t =>
                        !t.IsAbstract
                        && !t.IsInterface
                        && typeof(ITempCapability).IsAssignableFrom(t)
                    );

                if (capabilityType == null)
                {
                    return
                        "LIBRARY CAPABILITY ERROR: " +
                        $"no ITempCapability implementation found in persisted assembly for '{entry.Name}'.";
                }

                if (Activator.CreateInstance(capabilityType) is not ITempCapability instance)
                {
                    return
                        "LIBRARY CAPABILITY ERROR: " +
                        $"could not instantiate persisted capability '{entry.Name}'.";
                }

                if (string.IsNullOrWhiteSpace(argumentsJson))
                {
                    argumentsJson = "{}";
                }

                using JsonDocument arguments = JsonDocument.Parse(argumentsJson);

                string result =
                    await instance.ExecuteAsync(context, arguments.RootElement);

                entry.TimesUsed++;
                Save();

                return string.IsNullOrWhiteSpace(result)
                    ? "LIBRARY CAPABILITY SUCCESS"
                    : result;
            }
            catch (JsonException ex)
            {
                return "LIBRARY CAPABILITY ARGUMENT ERROR: " + ex.Message;
            }
            catch (Exception ex)
            {
                return
                    "LIBRARY CAPABILITY RUNTIME ERROR: "
                    + ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}