using AI_Assistant.Tools;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;

using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;


namespace AI_Assistant.TempCapabilities
{
    // ============================================================
    // CONTRACT
    // ============================================================

    public interface ITempCapability
    {
        string Name
        {
            get;
        }


        Task<string> ExecuteAsync(
            TempCapabilityContext context,
            JsonElement arguments
        );
    }


    // ============================================================
    // CONTROLLED TEMP CAPABILITY CONTEXT
    //
    // Generated code receives only explicitly exposed surfaces.
    //
    // It does NOT receive:
    // - AIIntegration
    // - filesystem tools
    // - self-development tools
    // - API keys
    //
    // For complex Unity work:
    //
    // context.NewUnityBatch()
    //     .CreateGameObject(...)
    //     .AddComponent(...)
    //     ...
    //     .Execute();
    //
    // ============================================================

    public sealed class TempCapabilityContext
    {
        private readonly UnityBridgeTools unity;


        public UnityBridgeTools Unity
        {
            get
            {
                return
                    unity;
            }
        }


        // IMPORTANT:
        //
        // Always returns a fresh builder.
        //
        // We intentionally do NOT keep one persistent builder because
        // operations from a previous capability must never leak into
        // the next capability execution.
        public UnityBatchBuilder NewUnityBatch()
        {
            return
                new UnityBatchBuilder(
                    unity
                );
        }


        public TempCapabilityContext(
            UnityBridgeTools unity
        )
        {
            this.unity =
                unity;
        }
    }


    // ============================================================
    // COLLECTIBLE ASSEMBLY CONTEXT
    // ============================================================

    internal sealed class TempCapabilityLoadContext
        : AssemblyLoadContext
    {
        public TempCapabilityLoadContext()
            : base(
                isCollectible: true
            )
        {
        }


        protected override Assembly? Load(
            AssemblyName assemblyName
        )
        {
            Assembly hostAssembly =
                typeof(ITempCapability)
                    .Assembly;


            string? hostAssemblyName =
                hostAssembly
                    .GetName()
                    .Name;


            // Generated capability references the main
            // AI Assistant assembly for:
            //
            // ITempCapability
            // TempCapabilityContext
            // UnityBatchBuilder
            // UnityBridgeTools
            //
            // Reuse the already-loaded host assembly so type
            // identity remains identical.
            if (
                string.Equals(
                    assemblyName.Name,
                    hostAssemblyName,
                    StringComparison.Ordinal
                )
            )
            {
                return
                    hostAssembly;
            }


            // Framework assemblies can resolve normally.
            return null;
        }
    }


    // ============================================================
    // TEMP CAPABILITY MANAGER
    // ============================================================

    public sealed class TempCapabilityManager
    {
        // Prevent unnecessarily huge model-generated source.
        private const int MaxSourceChars =
            30000;


        // Return several errors together so the model can repair
        // everything in ONE rewrite rather than one error per cycle.
        private const int MaxCompilerErrors =
            8;


        // Prevent giant results from entering model context.
        private const int MaxRuntimeResultChars =
            6000;


        private readonly string tempRoot;

        private readonly TempCapabilityContext context;

        private readonly MetadataReference[]
            compilationReferences;

        // Persistent registry of capabilities that were promoted
        // after a successful compile + run. See CapabilityLibrary.cs.
        private readonly CapabilityLibrary library;

        public CapabilityLibrary Library => library;


        // ========================================================
        // BASIC SOURCE GUARD
        //
        // IMPORTANT:
        //
        // This is NOT a strong security sandbox.
        //
        // It is a first safety layer intended mainly to stop
        // accidental use of operating-system / network APIs by
        // generated code.
        //
        // Strong commercial isolation should eventually use
        // a separate restricted worker process.
        // ========================================================

        private static readonly string[]
            BlockedSourceFragments =
        {
            // Filesystem
            "System.IO",
            "File.",
            "Directory.",
            "FileInfo",
            "DirectoryInfo",

            // Processes
            "System.Diagnostics",
            "Process.",
            "ProcessStartInfo",

            // Networking
            "System.Net",
            "HttpClient",
            "WebClient",

            // Registry
            "Microsoft.Win32",

            // Native / interop
            "DllImport",
            "NativeLibrary",
            "Marshal.",

            // Environment / host discovery
            "Environment.",
            "AppContext.",

            // Runtime dynamic loading
            "Assembly.Load",
            "AssemblyLoadContext",

            // Unsafe code
            "unsafe",
            "stackalloc"
        };


        // ========================================================
        // CONSTRUCTOR
        // ========================================================

        public TempCapabilityManager(
            string sourceRoot,
            UnityBridgeTools unityTools
        )
        {
            tempRoot =
                Path.Combine(
                    sourceRoot,
                    "TempTools"
                );


            Directory.CreateDirectory(
                tempRoot
            );


            context =
                new TempCapabilityContext(
                    unityTools
                );


            // References are discovered once when the manager starts.
            //
            // We intentionally do not reconstruct these on every
            // generated capability execution.
            compilationReferences =
                BuildCompilationReferences();


            library =
                new CapabilityLibrary(
                    Path.Combine(
                        sourceRoot,
                        "CapabilityLibrary"
                    )
                );
        }


        // ========================================================
        // LIBRARY ENTRY
        //
        // Executes an already-promoted capability ("run_<Name>")
        // directly from its persisted DLL. No compilation involved.
        // Returns false if the tool name doesn't match any promoted
        // capability, so the caller can fall through to other tools.
        // ========================================================

        public bool TryExecuteLibraryCapability(
            string toolName,
            string argumentsJson,
            out string result
        )
        {
            if (
                !library.TryGetEntry(
                    toolName,
                    out CapabilityManifestEntry? entry
                )
                ||
                entry == null
            )
            {
                result = string.Empty;
                return false;
            }

            result =
                library
                    .ExecuteAsync(
                        entry,
                        context,
                        argumentsJson
                    )
                    .GetAwaiter()
                    .GetResult();

            return true;
        }


        // ========================================================
        // MAIN ENTRY
        //
        // ONE MODEL TOOL CALL CAN PERFORM:
        //
        // generated source
        //       ↓
        // validate
        //       ↓
        // save temporary .cs
        //       ↓
        // Roslyn compile
        //       ↓
        // load in memory
        //       ↓
        // execute
        //       ↓
        // unload
        //       ↓
        // delete temporary files
        //
        // ========================================================

        public string ExecuteTemporaryCapability(
            string capabilityName,
            string sourceCode,
            string argumentsJson
        )
        {
            string? inputError =
                ValidateInput(
                    capabilityName,
                    sourceCode
                );


            if (
                inputError != null
            )
            {
                return
                    inputError;
            }


            string preparedSource =
    AddRequiredUsings(
        sourceCode
    );

            string? sourceError =
                ValidateSource(
                    preparedSource
                );


            if (
                sourceError != null
            )
            {
                return
                    sourceError;
            }


            string taskId =
                CreateTaskId();


            string taskFolder =
                Path.Combine(
                    tempRoot,
                    taskId
                );


            string sourcePath =
                Path.Combine(
                    taskFolder,
                    capabilityName + ".cs"
                );


            TempCapabilityLoadContext?
                loadContext =
                    null;


            try
            {
                Directory.CreateDirectory(
                    taskFolder
                );


                // =================================================
                // SAVE TEMP SOURCE
                // =================================================

                File.WriteAllText(
                    sourcePath,
                    preparedSource,
                    new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false
                    )
                );


                // =================================================
                // PARSE
                // =================================================

                SyntaxTree syntaxTree =
                    CSharpSyntaxTree.ParseText(
                        preparedSource,

                        new CSharpParseOptions(
                            LanguageVersion.Latest
                        ),

                        sourcePath
                    );


                // =================================================
                // COMPILE
                // =================================================

                CSharpCompilation compilation =
                    CSharpCompilation.Create(
                        assemblyName:
                            "AIAssistant.Temp."
                            +
                            capabilityName
                            +
                            "."
                            +
                            Guid.NewGuid()
                                .ToString("N"),

                        syntaxTrees:
                            new[]
                            {
                                syntaxTree
                            },

                        references:
                            compilationReferences,

                        options:
                            new CSharpCompilationOptions(
                                OutputKind.DynamicallyLinkedLibrary,

                                optimizationLevel:
                                    OptimizationLevel.Release,

                                allowUnsafe:
                                    false,

                                nullableContextOptions:
                                    NullableContextOptions.Enable
                            )
                    );


                using MemoryStream assemblyStream =
                    new MemoryStream();


                EmitResult emitResult =
                    compilation.Emit(
                        assemblyStream
                    );


                // =================================================
                // COMPILE FAILED
                // =================================================

                if (
                    !emitResult.Success
                )
                {
                    return
                      FormatCompilerErrors(
                        emitResult.Diagnostics,
                          preparedSource
                        );
                }


                // =================================================
                // LOAD IN MEMORY
                // =================================================

                assemblyStream.Position =
                    0;


                // Capture the raw bytes BEFORE the collectible load
                // context is created/unloaded, so a successful run
                // can be promoted into the persistent CapabilityLibrary
                // without needing to recompile from source.
                byte[] assemblyBytes =
                    assemblyStream.ToArray();


                loadContext =
                    new TempCapabilityLoadContext();


                Assembly assembly =
                    loadContext.LoadFromStream(
                        assemblyStream
                    );


                // =================================================
                // EXECUTE
                // =================================================

                string result =
                    ExecuteAssembly(
                        assembly,
                        capabilityName,
                        argumentsJson
                    );


                // =================================================
                // PROMOTE ON SUCCESS
                //
                // A run counts as promotable if ExecuteAssembly did
                // not report a compile/runtime/name-mismatch error.
                // All of ExecuteAssembly's own error strings start
                // with "TEMP CAPABILITY ERROR:", so anything else
                // (including "TEMP CAPABILITY SUCCESS" and normal
                // tool output) is treated as a successful run.
                // =================================================

                if (
                    !result.StartsWith(
                        "TEMP CAPABILITY ERROR:",
                        StringComparison.Ordinal
                    )
                )
                {
                    try
                    {
                        string description =
                            ExtractDescription(
                                preparedSource,
                                capabilityName
                            );

                        library.Promote(
                            capabilityName,
                            preparedSource,
                            assemblyBytes,
                            description
                        );
                    }
                    catch
                    {
                        // Promotion is a bonus, not a requirement.
                        // A failure here must never make an otherwise
                        // successful Unity operation appear failed.
                    }
                }


                return
                    TrimRuntimeResult(
                        result
                    );
            }
            catch (
                JsonException ex
            )
            {
                return
                    "TEMP ARGUMENT ERROR: "
                    +
                    ex.Message;
            }
            catch (
                ReflectionTypeLoadException ex
            )
            {
                string loaderErrors =
                    string.Join(
                        " | ",

                        ex.LoaderExceptions
                            .Where(item =>
                                item != null
                            )
                            .Take(4)
                            .Select(item =>
                                item!.Message
                            )
                    );


                return
                    "TEMP LOAD ERROR: "
                    +
                    loaderErrors;
            }
            catch (
                Exception ex
            )
            {
                return
                    "TEMP RUNTIME ERROR: "
                    +
                    ex.GetType().Name
                    +
                    ": "
                    +
                    ex.Message;
            }
            finally
            {
                // =================================================
                // UNLOAD GENERATED ASSEMBLY
                // =================================================

                if (
                    loadContext != null
                )
                {
                    loadContext.Unload();
                }


                // Do NOT force GC here.
                //
                // Collectible ALC becomes eligible for collection,
                // but forcing full GC after every task would waste
                // resources.

                TryDeleteDirectory(
                    taskFolder
                );
            }
        }


        // ========================================================
        // EXECUTE COMPILED CAPABILITY
        // ========================================================

        private string ExecuteAssembly(
            Assembly assembly,
            string requestedName,
            string argumentsJson
        )
        {
            Type[] capabilityTypes =
                assembly
                    .GetTypes()
                    .Where(type =>
                        !type.IsAbstract
                        &&
                        !type.IsInterface
                        &&
                        typeof(ITempCapability)
                            .IsAssignableFrom(
                                type
                            )
                    )
                    .ToArray();


            if (
                capabilityTypes.Length ==
                0
            )
            {
                return
                    "TEMP CAPABILITY ERROR: " +
                    "compile succeeded but no concrete class implements ITempCapability.";
            }


            if (
                capabilityTypes.Length >
                1
            )
            {
                return
                    "TEMP CAPABILITY ERROR: " +
                    "source must contain exactly one concrete ITempCapability implementation.";
            }


            Type capabilityType =
                capabilityTypes[0];


            object? instance =
                Activator.CreateInstance(
                    capabilityType
                );


            if (
                instance is not ITempCapability capability
            )
            {
                return
                    "TEMP CAPABILITY ERROR: " +
                    "could not instantiate generated capability.";
            }


            if (
                !string.Equals(
                    capability.Name,
                    requestedName,
                    StringComparison.Ordinal
                )
            )
            {
                return
                    "TEMP CAPABILITY ERROR: " +
                    $"requested name '{requestedName}' " +
                    $"does not match generated Name '{capability.Name}'.";
            }


            if (
                string.IsNullOrWhiteSpace(
                    argumentsJson
                )
            )
            {
                argumentsJson =
                    "{}";
            }


            using JsonDocument arguments =
                JsonDocument.Parse(
                    argumentsJson
                );


            Task<string> executionTask =
                capability.ExecuteAsync(
                    context,
                    arguments.RootElement
                );


            string result =
                executionTask
                    .GetAwaiter()
                    .GetResult();


            if (
                string.IsNullOrWhiteSpace(
                    result
                )
            )
            {
                return
                    "TEMP CAPABILITY SUCCESS";
            }


            return
                result;
        }


        // ========================================================
        // DESCRIPTION EXTRACTION (for CapabilityLibrary promotion)
        //
        // Looks for a /// <summary> doc comment above the class.
        // Falls back to the capability name so promotion never
        // fails just because the model didn't add a doc comment.
        // ========================================================

        private static string ExtractDescription(
            string sourceCode,
            string fallbackName
        )
        {
            Match match =
                Regex.Match(
                    sourceCode,
                    @"///\s*<summary>\s*([^\r\n]+)"
                );

            return
                match.Success
                    ? match.Groups[1].Value.Trim()
                    : fallbackName;
        }


        // ========================================================
        // INPUT VALIDATION
        // ========================================================

        private static string? ValidateInput(
            string capabilityName,
            string sourceCode
        )
        {
            if (
                string.IsNullOrWhiteSpace(
                    capabilityName
                )
            )
            {
                return
                    "TEMP CAPABILITY ERROR: name is empty.";
            }


            if (
                !Regex.IsMatch(
                    capabilityName,
                    "^[A-Za-z][A-Za-z0-9_]{0,63}$"
                )
            )
            {
                return
                    "TEMP CAPABILITY ERROR: " +
                    "name must start with a letter and contain only letters, numbers or underscore.";
            }


            if (
                string.IsNullOrWhiteSpace(
                    sourceCode
                )
            )
            {
                return
                    "TEMP CAPABILITY ERROR: source is empty.";
            }


            if (
                sourceCode.Length >
                MaxSourceChars
            )
            {
                return
                    $"TEMP CAPABILITY ERROR: source too large " +
                    $"({sourceCode.Length}/{MaxSourceChars} chars). " +
                    "Use one compact high-level capability.";
            }


            return null;
        }


        // ========================================================
        // GENERATED SOURCE VALIDATION
        // ========================================================
        private static string AddRequiredUsings(
            string sourceCode
        )
        {
            CompilationUnitSyntax root =
                CSharpSyntaxTree.ParseText(
                    sourceCode,
                    new CSharpParseOptions(
                        LanguageVersion.Latest
                    )
                )
                .GetCompilationUnitRoot();

            HashSet<string> existingUsings =
                root
                    .DescendantNodes()
                    .OfType<UsingDirectiveSyntax>()
                    .Select(usingDirective =>
                        usingDirective.Name?.ToString()
                        ??
                        ""
                    )
                    .ToHashSet(
                        StringComparer.Ordinal
                    );

            StringBuilder header =
                new StringBuilder();

            AddUsingIfMissing(
                header,
                existingUsings,
                "AI_Assistant.TempCapabilities"
            );

            AddUsingIfMissing(
                header,
                existingUsings,
                "System.Text.Json"
            );

            AddUsingIfMissing(
                header,
                existingUsings,
                "System.Threading.Tasks"
            );

            if (header.Length == 0)
            {
                return sourceCode;
            }

            header.AppendLine();
            header.Append(sourceCode);

            return header.ToString();
        }


        private static void AddUsingIfMissing(
            StringBuilder header,
            HashSet<string> existingUsings,
            string namespaceName
        )
        {
            if (
                !existingUsings.Contains(
                    namespaceName
                )
            )
            {
                header.AppendLine(
                    "using "
                    +
                    namespaceName
                    +
                    ";"
                );
            }
        }


        private static string? ValidateSource(
            string sourceCode
        )
        {
            SyntaxTree syntaxTree =
                CSharpSyntaxTree.ParseText(
                    sourceCode,
                    new CSharpParseOptions(
                        LanguageVersion.Latest
                    )
                );

            CompilationUnitSyntax root =
                syntaxTree.GetCompilationUnitRoot();

            foreach (
                UsingDirectiveSyntax usingDirective
                in root
                    .DescendantNodes()
                    .OfType<UsingDirectiveSyntax>()
            )
            {
                string namespaceName =
                    usingDirective.Name?.ToString()
                    ??
                    "";

                bool directUnityReference =
                    namespaceName.Equals(
                        "UnityEngine",
                        StringComparison.Ordinal
                    )
                    ||
                    namespaceName.StartsWith(
                        "UnityEngine.",
                        StringComparison.Ordinal
                    )
                    ||
                    namespaceName.Equals(
                        "UnityEditor",
                        StringComparison.Ordinal
                    )
                    ||
                    namespaceName.StartsWith(
                        "UnityEditor.",
                        StringComparison.Ordinal
                    );

                if (directUnityReference)
                {
                    return
                        "TEMP CAPABILITY DENIED: " +
                        "host capability must not reference UnityEngine or UnityEditor directly. " +
                        "Use context.NewUnityBatch(). " +
                        "Unity C# script text is allowed only inside CreateScript(...).";
                }
            }

            foreach (
                string blockedFragment
                in BlockedSourceFragments
            )
            {
                if (
                    sourceCode.Contains(
                        blockedFragment,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return
                        "TEMP CAPABILITY DENIED: " +
                        $"blocked API/token '{blockedFragment}'. " +
                        "Use TempCapabilityContext and NewUnityBatch() instead.";
                }
            }

            if (
                !sourceCode.Contains(
                    "ITempCapability",
                    StringComparison.Ordinal
                )
            )
            {
                return
                    "TEMP CAPABILITY ERROR: " +
                    "generated source must implement ITempCapability.";
            }

            return null;
        }

        // ========================================================
        // ROSLYN REFERENCES
        //
        // Cached once in constructor.
        // ========================================================

        private static MetadataReference[]
            BuildCompilationReferences()
        {
            HashSet<string> referencePaths =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase
                );


            string?
                trustedPlatformAssemblies =
                    AppContext.GetData(
                        "TRUSTED_PLATFORM_ASSEMBLIES"
                    )
                    as string;


            if (
                !string.IsNullOrWhiteSpace(
                    trustedPlatformAssemblies
                )
            )
            {
                foreach (
                    string assemblyPath
                    in trustedPlatformAssemblies.Split(
                        Path.PathSeparator,
                        StringSplitOptions.RemoveEmptyEntries
                    )
                )
                {
                    if (
                        File.Exists(
                            assemblyPath
                        )
                    )
                    {
                        referencePaths.Add(
                            assemblyPath
                        );
                    }
                }
            }


            // Main assembly contains:
            //
            // ITempCapability
            // TempCapabilityContext
            // UnityBatchBuilder
            // UnityBridgeTools
            // UnityBatchExtensions

            Assembly hostAssembly =
                typeof(ITempCapability)
                    .Assembly;


            if (
                !string.IsNullOrWhiteSpace(
                    hostAssembly.Location
                )
                &&
                File.Exists(
                    hostAssembly.Location
                )
            )
            {
                referencePaths.Add(
                    hostAssembly.Location
                );
            }


            return
                referencePaths
                    .Select(path =>
                        MetadataReference
                            .CreateFromFile(
                                path
                            )
                    )
                    .ToArray();
        }


        // ========================================================
        // COMPILER DIAGNOSTICS
        // ========================================================

        private static string FormatCompilerErrors(
      IEnumerable<Diagnostic> diagnostics,
      string sourceCode
  )
        {
            Diagnostic[] compilerErrors =
                diagnostics
                    .Where(diagnostic =>
                        diagnostic.Severity
                        ==
                        DiagnosticSeverity.Error
                    )
                    .Take(
                        MaxCompilerErrors
                    )
                    .ToArray();

            if (compilerErrors.Length == 0)
            {
                return
                    "TEMP COMPILE FAILED: compiler returned no useful diagnostics.";
            }

            StringBuilder result =
                new StringBuilder();

            result.AppendLine(
                "TEMP COMPILE FAILED"
            );

            result.AppendLine(
                "Fix ALL errors below in ONE complete rewrite:"
            );

            foreach (
                Diagnostic compilerError
                in compilerErrors
            )
            {
                FileLinePositionSpan location =
                    compilerError
                        .Location
                        .GetLineSpan();

                int line =
                    location
                        .StartLinePosition
                        .Line
                    +
                    1;

                int column =
                    location
                        .StartLinePosition
                        .Character
                    +
                    1;

                result.Append(
                    compilerError.Id
                );

                result.Append(
                    " L"
                );

                result.Append(
                    line
                );

                result.Append(
                    ":"
                );

                result.Append(
                    column
                );

                result.Append(
                    " "
                );

                result.AppendLine(
                    compilerError.GetMessage()
                );
            }

            AppendRelevantSourceLines(
                result,
                sourceCode,
                compilerErrors
            );

            return result
                .ToString()
                .TrimEnd();
        }


        private static void AppendRelevantSourceLines(
            StringBuilder result,
            string sourceCode,
            IEnumerable<Diagnostic> diagnostics
        )
        {
            string[] lines =
                sourceCode
                    .Replace(
                        "\r\n",
                        "\n"
                    )
                    .Split('\n');

            int[] relevantLines =
                diagnostics
                    .Where(diagnostic =>
                        diagnostic.Location.IsInSource
                    )
                    .Select(diagnostic =>
                        diagnostic.Location
                            .GetLineSpan()
                            .StartLinePosition
                            .Line
                    )
                    .SelectMany(line =>
                        new[]
                        {
                    line - 1,
                    line,
                    line + 1
                        }
                    )
                    .Where(line =>
                        line >= 0
                        &&
                        line < lines.Length
                    )
                    .Distinct()
                    .OrderBy(line =>
                        line
                    )
                    .Take(24)
                    .ToArray();

            if (relevantLines.Length == 0)
            {
                return;
            }

            result.AppendLine();
            result.AppendLine(
                "RELEVANT SOURCE LINES:"
            );

            foreach (
                int line
                in relevantLines
            )
            {
                result.Append(
                    line + 1
                );

                result.Append(
                    ": "
                );

                result.AppendLine(
                    lines[line]
                );
            }
        }

        // ========================================================
        // RESULT LIMIT
        // ========================================================

        private static string TrimRuntimeResult(
            string result
        )
        {
            if (
                string.IsNullOrEmpty(
                    result
                )
            )
            {
                return
                    "TEMP CAPABILITY SUCCESS";
            }


            if (
                result.Length <=
                MaxRuntimeResultChars
            )
            {
                return
                    result;
            }


            return
                result.Substring(
                    0,
                    MaxRuntimeResultChars
                )
                +
                "\n[TEMP RESULT TRUNCATED]";
        }


        // ========================================================
        // UNIQUE TEMP TASK ID
        // ========================================================

        private static string CreateTaskId()
        {
            return
                DateTime.UtcNow.ToString(
                    "yyyyMMdd_HHmmss_fff"
                )
                +
                "_"
                +
                Guid.NewGuid()
                    .ToString("N")
                    .Substring(
                        0,
                        8
                    );
        }


        // ========================================================
        // CLEANUP
        // ========================================================

        private static void TryDeleteDirectory(
            string directory
        )
        {
            try
            {
                if (
                    Directory.Exists(
                        directory
                    )
                )
                {
                    Directory.Delete(
                        directory,
                        recursive: true
                    );
                }
            }
            catch
            {
                // Cleanup failure should not make a successful
                // Unity operation appear failed.
            }
        }
    }
}