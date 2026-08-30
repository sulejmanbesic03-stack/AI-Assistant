using AI_Assistant.TempCapabilities;
using AI_Assistant.Tools;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AI_Assistant.AgentV2
{
    // Authoritative Agent V2 executor.
    // Legacy executor files are excluded from compilation in the csproj.
    internal sealed class UnityCommandExecutorV2
    {
        private readonly UnityBridgeTools unity;
        private readonly TempCapabilityManager tempCapabilities;
        private readonly Action<string> activity;

        public UnityCommandExecutorV2(
            UnityBridgeTools unity,
            TempCapabilityManager tempCapabilities,
            Action<string> activity
        )
        {
            this.unity = unity;
            this.tempCapabilities = tempCapabilities;
            this.activity = activity;
        }

        public Task<AgentExecutionReportV2> ExecuteAsync(
            AgentImplementationV2 implementation,
            string userGoal
        )
        {
            return Task.Run(
                () => Execute(
                    implementation,
                    userGoal
                )
            );
        }

        private AgentExecutionReportV2 Execute(
            AgentImplementationV2 implementation,
            string userGoal
        )
        {
            AgentExecutionReportV2 report =
                new AgentExecutionReportV2();

            if (
                !NormalizeAndValidateImplementation(
                    implementation,
                    userGoal,
                    report
                )
            )
            {
                return report;
            }

            // 1. Compile persistent generated/repaired gameplay scripts first.
            foreach (ScriptChangeV2 script in implementation.ScriptChanges)
            {
                if (!WriteAndCompileScript(script, report))
                {
                    return report;
                }
            }

            // 2. Create/configure scene objects. Existing-script attach actions
            // are direct actions and execute after the batch creates the target.
            if (!ExecuteSceneActions(implementation.SceneActions, report))
            {
                return report;
            }

            // 3. Attach generated scripts through canonical script_changes.attach_to.
            foreach (ScriptChangeV2 script in implementation.ScriptChanges)
            {
                if (
                    !string.IsNullOrWhiteSpace(script.AttachTo)
                    && !AttachGeneratedScript(script, report)
                )
                {
                    return report;
                }
            }

            // 4. One broad one-shot temp capability remains the escape hatch.
            if (
                implementation.TemporaryCapability != null
                && !string.IsNullOrWhiteSpace(
                    implementation.TemporaryCapability.Name
                )
            )
            {
                TempCapabilitySpecV2 capability =
                    implementation.TemporaryCapability;

                activity("[V2 TEMP] " + capability.Name);

                string capabilityResult =
                    tempCapabilities.ExecuteTemporaryCapability(
                        capability.Name,
                        capability.Source,
                        string.IsNullOrWhiteSpace(
                            capability.ArgumentsJson
                        )
                            ? "{}"
                            : capability.ArgumentsJson
                    );

                if (!AgentJsonV2.LooksSuccessful(capabilityResult))
                {
                    report.Fail(
                        "Temporary capability failed: "
                        + AgentJsonV2.Compact(
                            capabilityResult,
                            2200
                        )
                    );

                    return report;
                }

                report.Steps.Add(
                    "Temporary capability: "
                    + capability.Name
                );
            }

            // 5. Save once and verify once.
            activity("[V2 SAVE] scene");

            string saveResult = unity.SaveScene();

            if (!AgentJsonV2.LooksSuccessful(saveResult))
            {
                report.Fail(
                    "Scene save failed: "
                    + AgentJsonV2.Compact(
                        saveResult,
                        1400
                    )
                );

                return report;
            }

            report.Steps.Add("Saved scene");

            activity("[V2 VERIFY] console");

            report.ConsoleResult =
                AgentJsonV2.Compact(
                    unity.GetConsoleErrors(),
                    3000
                );

            if (
                ShouldRunRuntimeVerification(userGoal)
                && implementation.RuntimeObjectPaths.Count > 0
            )
            {
                VerifyRuntime(
                    implementation.RuntimeObjectPaths,
                    report
                );
            }

            return report;
        }

        // ============================================================
        // PREFLIGHT / NORMALIZATION
        // ============================================================

        private bool NormalizeAndValidateImplementation(
            AgentImplementationV2 implementation,
            string userGoal,
            AgentExecutionReportV2 report
        )
        {
            implementation.ScriptChanges ??=
                new List<ScriptChangeV2>();

            implementation.SceneActions ??=
                new List<SceneActionV2>();

            implementation.RuntimeObjectPaths ??=
                new List<string>();

            foreach (ScriptChangeV2 script in implementation.ScriptChanges)
            {
                script.AssetPath = (script.AssetPath ?? "").Trim();
                script.ClassName = (script.ClassName ?? "").Trim();
                script.Source = script.Source ?? "";
                script.AttachTo = (script.AttachTo ?? "").Trim();
            }

            foreach (SceneActionV2 action in implementation.SceneActions)
            {
                NormalizeStrings(action);
            }

            // Collect likely target paths before filling create_gameobject names.
            List<string> likelyTargets =
                implementation.ScriptChanges
                    .Select(script => script.AttachTo)
                    .Concat(
                        implementation.SceneActions
                            .Where(action =>
                                action.Type == "attach_script"
                                || action.Type == "add_component"
                                || action.Type == "configure_rigidbody"
                                || action.Type == "configure_collider"
                            )
                            .Select(action => action.ObjectPath)
                    )
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            string singleLikelyTarget =
                likelyTargets.Count == 1
                    ? likelyTargets[0]
                    : "";

            string inferredExistingScriptType =
                InferExistingScriptTypeFromGoal(userGoal);

            foreach (SceneActionV2 action in implementation.SceneActions)
            {
                if (
                    action.Type == "create_gameobject"
                    || action.Type == "create_primitive"
                )
                {
                    if (
                        string.IsNullOrWhiteSpace(action.ObjectPath)
                        && !string.IsNullOrWhiteSpace(singleLikelyTarget)
                    )
                    {
                        action.ObjectPath = singleLikelyTarget;
                    }

                    if (string.IsNullOrWhiteSpace(action.Name))
                    {
                        action.Name = LastPathSegment(action.ObjectPath);
                    }

                    if (
                        string.IsNullOrWhiteSpace(action.ParentPath)
                        && !string.IsNullOrWhiteSpace(action.ObjectPath)
                    )
                    {
                        action.ParentPath = ParentPathOf(action.ObjectPath);
                    }

                    if (
                        string.IsNullOrWhiteSpace(action.ObjectPath)
                        && !string.IsNullOrWhiteSpace(action.Name)
                    )
                    {
                        action.ObjectPath =
                            string.IsNullOrWhiteSpace(action.ParentPath)
                                ? action.Name
                                : action.ParentPath.TrimEnd('/')
                                    + "/"
                                    + action.Name;
                    }
                }

                if (action.Type == "attach_script")
                {
                    if (
                        string.IsNullOrWhiteSpace(action.ObjectPath)
                        && !string.IsNullOrWhiteSpace(singleLikelyTarget)
                    )
                    {
                        action.ObjectPath = singleLikelyTarget;
                    }

                    if (
                        string.IsNullOrWhiteSpace(action.ScriptType)
                        && !string.IsNullOrWhiteSpace(action.ComponentType)
                    )
                    {
                        action.ScriptType = action.ComponentType;
                    }

                    // Generated script for this exact target.
                    if (string.IsNullOrWhiteSpace(action.ScriptType))
                    {
                        List<ScriptChangeV2> generatedForTarget =
                            implementation.ScriptChanges
                                .Where(script =>
                                    !string.IsNullOrWhiteSpace(script.AttachTo)
                                    && string.Equals(
                                        script.AttachTo,
                                        action.ObjectPath,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                .ToList();

                        if (generatedForTarget.Count == 1)
                        {
                            action.ScriptType =
                                generatedForTarget[0].ClassName;
                        }
                    }

                    // Existing script attachment. Example user goal:
                    // "koristi postojecu FPSPlayerController skriptu".
                    // The host can infer that class deterministically without
                    // another model call.
                    if (
                        string.IsNullOrWhiteSpace(action.ScriptType)
                        && !string.IsNullOrWhiteSpace(
                            inferredExistingScriptType
                        )
                    )
                    {
                        action.ScriptType =
                            inferredExistingScriptType;

                        activity(
                            "[V2 PREFLIGHT] inferred existing script_type="
                            + action.ScriptType
                        );
                    }
                }
            }

            // Generated-script attachment lives in script_changes.attach_to.
            // Drop any duplicate scene attach emitted by the model.
            implementation.SceneActions =
                implementation.SceneActions
                    .Where(action =>
                    {
                        if (action.Type != "attach_script")
                        {
                            return true;
                        }

                        List<ScriptChangeV2> generatedForTarget =
                            implementation.ScriptChanges
                                .Where(script =>
                                    !string.IsNullOrWhiteSpace(script.AttachTo)
                                    && string.Equals(
                                        script.AttachTo,
                                        action.ObjectPath,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                .ToList();

                        if (generatedForTarget.Count == 0)
                        {
                            return true;
                        }

                        if (string.IsNullOrWhiteSpace(action.ScriptType))
                        {
                            return false;
                        }

                        return !generatedForTarget.Any(script =>
                            string.Equals(
                                script.ClassName,
                                action.ScriptType,
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                    })
                    .ToList();

            // Deduplicate exact repeated scene operations locally.
            implementation.SceneActions =
                implementation.SceneActions
                    .GroupBy(
                        BuildActionIdentity,
                        StringComparer.OrdinalIgnoreCase
                    )
                    .Select(group => group.First())
                    .ToList();

            for (
                int index = 0;
                index < implementation.SceneActions.Count;
                index++
            )
            {
                SceneActionV2 action =
                    implementation.SceneActions[index];

                string prefix =
                    "scene_actions["
                    + index
                    + "] '"
                    + action.Type
                    + "'";

                if (string.IsNullOrWhiteSpace(action.Type))
                {
                    return FailRequired(
                        report,
                        prefix,
                        "type"
                    );
                }

                switch (action.Type)
                {
                    case "create_gameobject":
                        if (!Has(action.Name))
                        {
                            return FailRequired(report, prefix, "name");
                        }
                        break;

                    case "create_primitive":
                        if (!Has(action.Name))
                        {
                            return FailRequired(report, prefix, "name");
                        }
                        if (!Has(action.PrimitiveType))
                        {
                            return FailRequired(
                                report,
                                prefix,
                                "primitive_type"
                            );
                        }
                        break;

                    case "add_component":
                        if (!Has(action.ObjectPath))
                        {
                            return FailRequired(
                                report,
                                prefix,
                                "object_path"
                            );
                        }
                        if (!Has(action.ComponentType))
                        {
                            return FailRequired(
                                report,
                                prefix,
                                "component_type"
                            );
                        }
                        break;

                    case "attach_script":
                        if (!Has(action.ObjectPath))
                        {
                            return FailRequired(
                                report,
                                prefix,
                                "object_path"
                            );
                        }
                        if (!Has(action.ScriptType))
                        {
                            return FailRequired(
                                report,
                                prefix,
                                "script_type"
                            );
                        }
                        break;

                    case "set_position":
                    case "set_rotation":
                    case "set_scale":
                    case "set_active":
                    case "configure_rigidbody":
                    case "configure_collider":
                        if (!Has(action.ObjectPath))
                        {
                            return FailRequired(
                                report,
                                prefix,
                                "object_path"
                            );
                        }
                        break;

                    case "rename_gameobject":
                        if (!Has(action.ObjectPath))
                        {
                            return FailRequired(report, prefix, "object_path");
                        }
                        if (!Has(action.NewName))
                        {
                            return FailRequired(report, prefix, "new_name");
                        }
                        break;

                    case "set_parent":
                        if (!Has(action.ObjectPath))
                        {
                            return FailRequired(report, prefix, "object_path");
                        }
                        break;

                    case "duplicate_gameobject":
                        if (!Has(action.ObjectPath))
                        {
                            return FailRequired(report, prefix, "object_path");
                        }
                        if (!Has(action.NewName))
                        {
                            return FailRequired(report, prefix, "new_name");
                        }
                        break;

                    case "create_material":
                        if (!Has(action.AssetPath))
                        {
                            return FailRequired(report, prefix, "asset_path");
                        }
                        if (!Has(action.ShaderName))
                        {
                            return FailRequired(report, prefix, "shader_name");
                        }
                        break;

                    case "set_material_color":
                        if (!Has(action.MaterialPath))
                        {
                            return FailRequired(
                                report,
                                prefix,
                                "material_path"
                            );
                        }
                        break;

                    case "assign_material":
                        if (!Has(action.ObjectPath))
                        {
                            return FailRequired(report, prefix, "object_path");
                        }
                        if (!Has(action.MaterialPath))
                        {
                            return FailRequired(report, prefix, "material_path");
                        }
                        break;

                    case "import_asset":
                        if (!Has(action.AssetPath))
                        {
                            return FailRequired(report, prefix, "asset_path");
                        }
                        break;

                    default:
                        report.Fail(
                            "Agent V2 preflight: unsupported action type '"
                            + action.Type
                            + "'. No Unity mutation was sent."
                        );
                        return false;
                }
            }

            return true;
        }

        private static void NormalizeStrings(SceneActionV2 action)
        {
            action.Type =
                (action.Type ?? "")
                    .Trim()
                    .ToLowerInvariant();

            action.ObjectPath = (action.ObjectPath ?? "").Trim();
            action.ParentPath = (action.ParentPath ?? "").Trim();
            action.Name = (action.Name ?? "").Trim();
            action.NewName = (action.NewName ?? "").Trim();
            action.ComponentType = (action.ComponentType ?? "").Trim();
            action.ScriptType = (action.ScriptType ?? "").Trim();
            action.PrimitiveType = (action.PrimitiveType ?? "").Trim();
            action.AssetPath = (action.AssetPath ?? "").Trim();
            action.MaterialPath = (action.MaterialPath ?? "").Trim();
            action.ShaderName = (action.ShaderName ?? "").Trim();
        }

        private static string InferExistingScriptTypeFromGoal(
            string userGoal
        )
        {
            if (string.IsNullOrWhiteSpace(userGoal))
            {
                return "";
            }

            // Bosnian/Croatian/Serbian form:
            // "koristi postojecu FPSPlayerController skriptu"
            Match beforeScript =
                Regex.Match(
                    userGoal,
                    @"\b([A-Za-z_][A-Za-z0-9_]*)\s+(?:skriptu|skripta|skripte|script)\b",
                    RegexOptions.IgnoreCase
                );

            if (beforeScript.Success)
            {
                string candidate = beforeScript.Groups[1].Value;

                if (LooksLikeScriptType(candidate))
                {
                    return candidate;
                }
            }

            // English/reversed form: "script FPSPlayerController".
            Match afterScript =
                Regex.Match(
                    userGoal,
                    @"\b(?:skriptu|skripta|skripte|script)\s+([A-Za-z_][A-Za-z0-9_]*)\b",
                    RegexOptions.IgnoreCase
                );

            if (afterScript.Success)
            {
                string candidate = afterScript.Groups[1].Value;

                if (LooksLikeScriptType(candidate))
                {
                    return candidate;
                }
            }

            // Last resort: one strongly class-like identifier in the goal.
            List<string> candidates =
                Regex.Matches(
                    userGoal,
                    @"\b[A-Za-z_][A-Za-z0-9_]*(?:Controller|Manager|Handler|Behaviour|Behavior|System|AI)\b",
                    RegexOptions.IgnoreCase
                )
                .Select(match => match.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(LooksLikeScriptType)
                .ToList();

            return candidates.Count == 1
                ? candidates[0]
                : "";
        }

        private static bool LooksLikeScriptType(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string[] excluded =
            {
                "script",
                "skripta",
                "skriptu",
                "player",
                "enemy",
                "camera",
                "kamera",
                "unity",
                "gameobject"
            };

            return !excluded.Contains(
                value,
                StringComparer.OrdinalIgnoreCase
            );
        }

        private static string BuildActionIdentity(SceneActionV2 action)
        {
            return string.Join(
                "|",
                action.Type,
                action.ObjectPath,
                action.ParentPath,
                action.Name,
                action.NewName,
                action.ComponentType,
                action.ScriptType,
                action.PrimitiveType,
                action.AssetPath,
                action.MaterialPath
            );
        }

        private static bool Has(string? value)
        {
            return !string.IsNullOrWhiteSpace(value);
        }

        private static bool FailRequired(
            AgentExecutionReportV2 report,
            string action,
            string field
        )
        {
            report.Fail(
                "Agent V2 preflight: "
                + action
                + " is missing required field '"
                + field
                + "'. No Unity mutation was sent."
            );

            return false;
        }

        private static string LastPathSegment(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            string value =
                path
                    .Replace('\\', '/')
                    .Trim('/');

            int slash = value.LastIndexOf('/');

            return slash >= 0
                ? value.Substring(slash + 1)
                : value;
        }

        private static string ParentPathOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            string value =
                path
                    .Replace('\\', '/')
                    .Trim('/');

            int slash = value.LastIndexOf('/');

            return slash > 0
                ? value.Substring(0, slash)
                : "";
        }

        // ============================================================
        // PERSISTENT SCRIPTS
        // ============================================================

        private bool WriteAndCompileScript(
            ScriptChangeV2 script,
            AgentExecutionReportV2 report
        )
        {
            if (!ValidateScriptChange(script, report))
            {
                return false;
            }

            activity("[V2 WRITE] " + script.AssetPath);

            string createResult =
                unity.CreatePersistentScript(
                    script.AssetPath,
                    script.ClassName,
                    script.Source,
                    script.Overwrite
                );

            if (!AgentJsonV2.LooksSuccessful(createResult))
            {
                report.CompileFailed = true;
                report.CompileFailureText = createResult;

                report.Fail(
                    "Persistent script creation failed for "
                    + script.AssetPath
                    + ": "
                    + AgentJsonV2.Compact(createResult, 1800)
                );

                return false;
            }

            string? jobId =
                AgentJsonV2.FindStringProperty(
                    createResult,
                    "jobId"
                );

            if (string.IsNullOrWhiteSpace(jobId))
            {
                report.CompileFailed = true;
                report.CompileFailureText = createResult;

                report.Fail(
                    "Unity accepted "
                    + script.AssetPath
                    + " but no compilation jobId was returned."
                );

                return false;
            }

            activity("[V2 COMPILE] " + script.ClassName);

            string compileResult =
                unity.WaitForPersistentScript(jobId);

            string state =
                AgentJsonV2.FindStringProperty(
                    compileResult,
                    "state"
                )
                ?? "";

            bool compiled =
                state.Equals(
                    "compiled",
                    StringComparison.OrdinalIgnoreCase
                )
                && AgentJsonV2.LooksSuccessful(compileResult);

            if (!compiled)
            {
                report.CompileFailed = true;
                report.CompileFailureText = compileResult;

                report.Fail(
                    "Compilation failed for "
                    + script.AssetPath
                    + ": "
                    + AgentJsonV2.Compact(compileResult, 2800)
                );

                return false;
            }

            report.FilesChanged.Add(script.AssetPath);
            report.Steps.Add("Compiled " + script.AssetPath);

            return true;
        }

        private bool AttachGeneratedScript(
            ScriptChangeV2 script,
            AgentExecutionReportV2 report
        )
        {
            activity(
                "[V2 ATTACH] "
                + script.ClassName
                + " -> "
                + script.AttachTo
            );

            string result =
                unity.AttachScript(
                    script.AttachTo,
                    script.ClassName
                );

            if (!AgentJsonV2.LooksSuccessful(result))
            {
                report.Fail(
                    "Could not attach "
                    + script.ClassName
                    + " to "
                    + script.AttachTo
                    + ": "
                    + AgentJsonV2.Compact(result, 1600)
                );

                return false;
            }

            report.Steps.Add(
                "Attached "
                + script.ClassName
                + " to "
                + script.AttachTo
            );

            return true;
        }

        private static bool ValidateScriptChange(
            ScriptChangeV2 script,
            AgentExecutionReportV2 report
        )
        {
            if (
                string.IsNullOrWhiteSpace(script.AssetPath)
                || !script.AssetPath.StartsWith(
                    "Assets/",
                    StringComparison.OrdinalIgnoreCase
                )
                || !script.AssetPath.EndsWith(
                    ".cs",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                report.Fail(
                    "Invalid persistent script asset path: '"
                    + script.AssetPath
                    + "'."
                );

                return false;
            }

            if (
                string.IsNullOrWhiteSpace(script.ClassName)
                || string.IsNullOrWhiteSpace(script.Source)
            )
            {
                report.Fail(
                    "Persistent script change is missing class_name or source for "
                    + script.AssetPath
                    + "."
                );

                return false;
            }

            return true;
        }

        // ============================================================
        // SCENE ACTIONS
        // ============================================================

        private bool ExecuteSceneActions(
            IReadOnlyList<SceneActionV2> actions,
            AgentExecutionReportV2 report
        )
        {
            if (actions.Count == 0)
            {
                return true;
            }

            List<SceneActionV2> batchable =
                actions
                    .Where(IsBatchable)
                    .ToList();

            List<SceneActionV2> direct =
                actions
                    .Where(action => !IsBatchable(action))
                    .ToList();

            if (batchable.Count > 0)
            {
                activity(
                    "[V2 BATCH] "
                    + batchable.Count
                    + " scene actions"
                );

                UnityBatchBuilder batch =
                    new UnityBatchBuilder(unity)
                        .StopOnFailure(true);

                foreach (SceneActionV2 action in batchable)
                {
                    AddToBatch(batch, action);
                }

                string batchResult = batch.Execute();

                if (!AgentJsonV2.LooksSuccessful(batchResult))
                {
                    report.Fail(
                        "Unity scene batch failed: "
                        + AgentJsonV2.Compact(batchResult, 2200)
                    );

                    return false;
                }

                report.Steps.Add(
                    "Unity batch: "
                    + batchable.Count
                    + " scene actions"
                );
            }

            // Direct actions are deliberately executed after the batch so an
            // attach_script can target an object just created in that batch.
            foreach (SceneActionV2 action in direct)
            {
                activity(
                    "[V2 ACTION] "
                    + action.Type
                    + (
                        action.Type == "attach_script"
                            ? " " + action.ScriptType + " -> " + action.ObjectPath
                            : ""
                    )
                );

                string actionResult =
                    ExecuteDirectSceneAction(action);

                if (!AgentJsonV2.LooksSuccessful(actionResult))
                {
                    report.Fail(
                        "Scene action '"
                        + action.Type
                        + "' failed: "
                        + AgentJsonV2.Compact(actionResult, 1800)
                    );

                    return false;
                }

                report.Steps.Add(
                    "Scene action: "
                    + action.Type
                );
            }

            return true;
        }

        private static bool IsBatchable(SceneActionV2 action)
        {
            return action.Type == "add_component"
                || action.Type == "create_gameobject"
                || action.Type == "create_primitive"
                || action.Type == "set_position"
                || action.Type == "set_rotation"
                || action.Type == "set_scale"
                || action.Type == "set_active"
                || action.Type == "rename_gameobject"
                || action.Type == "set_parent";
        }

        private static void AddToBatch(
            UnityBatchBuilder batch,
            SceneActionV2 action
        )
        {
            switch (action.Type)
            {
                case "add_component":
                    batch.AddComponent(
                        action.ObjectPath,
                        action.ComponentType
                    );
                    break;

                case "create_gameobject":
                    batch.CreateGameObject(
                        action.Name,
                        action.ParentPath
                    );
                    break;

                case "create_primitive":
                    batch.CreatePrimitive(
                        action.PrimitiveType,
                        action.Name,
                        action.ParentPath
                    );
                    break;

                case "set_position":
                    batch.SetPosition(
                        action.ObjectPath,
                        action.X ?? 0f,
                        action.Y ?? 0f,
                        action.Z ?? 0f
                    );
                    break;

                case "set_rotation":
                    batch.SetRotation(
                        action.ObjectPath,
                        action.X ?? 0f,
                        action.Y ?? 0f,
                        action.Z ?? 0f
                    );
                    break;

                case "set_scale":
                    batch.SetScale(
                        action.ObjectPath,
                        action.X ?? 1f,
                        action.Y ?? 1f,
                        action.Z ?? 1f
                    );
                    break;

                case "set_active":
                    batch.SetActive(
                        action.ObjectPath,
                        action.Active ?? true
                    );
                    break;

                case "rename_gameobject":
                    batch.RenameGameObject(
                        action.ObjectPath,
                        action.NewName
                    );
                    break;

                case "set_parent":
                    batch.SetParent(
                        action.ObjectPath,
                        action.ParentPath
                    );
                    break;
            }
        }

        private string ExecuteDirectSceneAction(SceneActionV2 action)
        {
            return action.Type switch
            {
                "attach_script" =>
                    unity.AttachScript(
                        action.ObjectPath,
                        action.ScriptType
                    ),

                "duplicate_gameobject" =>
                    unity.DuplicateGameObject(
                        action.ObjectPath,
                        action.NewName,
                        action.ParentPath
                    ),

                "configure_rigidbody" =>
                    unity.ConfigureRigidbody(
                        action.ObjectPath,
                        action.Mass ?? 1f,
                        action.UseGravity ?? true,
                        action.IsKinematic ?? false
                    ),

                "configure_collider" =>
                    unity.ConfigureCollider(
                        action.ObjectPath,
                        action.Enabled ?? true,
                        action.IsTrigger ?? false
                    ),

                "create_material" =>
                    unity.CreateMaterial(
                        action.AssetPath,
                        action.ShaderName
                    ),

                "set_material_color" =>
                    unity.SetMaterialColor(
                        action.MaterialPath,
                        action.Red ?? 1f,
                        action.Green ?? 1f,
                        action.Blue ?? 1f,
                        action.Alpha ?? 1f
                    ),

                "assign_material" =>
                    unity.AssignMaterial(
                        action.ObjectPath,
                        action.MaterialPath
                    ),

                "import_asset" =>
                    unity.ImportAsset(
                        action.AssetPath
                    ),

                _ =>
                    "AGENT V2 ERROR: unsupported direct scene action type '"
                    + action.Type
                    + "'."
            };
        }

        // ============================================================
        // RUNTIME VERIFICATION
        // ============================================================

        private void VerifyRuntime(
            IEnumerable<string> objectPaths,
            AgentExecutionReportV2 report
        )
        {
            activity("[V2 RUNTIME] enter Play Mode");

            string enterResult =
                unity.SetUnityPlayMode("enter");

            if (!AgentJsonV2.LooksSuccessful(enterResult))
            {
                report.Fail(
                    "Could not enter Play Mode: "
                    + AgentJsonV2.Compact(enterResult, 1600)
                );

                return;
            }

            try
            {
                foreach (
                    string objectPath
                    in objectPaths
                        .Where(path =>
                            !string.IsNullOrWhiteSpace(path)
                        )
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(4)
                )
                {
                    activity("[V2 RUNTIME] " + objectPath);

                    string runtime =
                        unity.GetUnityRuntimeState(objectPath);

                    report.RuntimeResults.Add(
                        objectPath
                        + ": "
                        + AgentJsonV2.Compact(runtime, 2200)
                    );
                }
            }
            finally
            {
                activity("[V2 RUNTIME] exit Play Mode");

                string exitResult =
                    unity.SetUnityPlayMode("exit");

                if (!AgentJsonV2.LooksSuccessful(exitResult))
                {
                    report.Fail(
                        "Could not cleanly exit Play Mode: "
                        + AgentJsonV2.Compact(exitResult, 1400)
                    );
                }
            }
        }

        private static bool ShouldRunRuntimeVerification(
            string goal
        )
        {
            string text = (goal ?? "").ToLowerInvariant();

            string[] triggers =
            {
                "play mode",
                "runtime",
                "testiraj",
                "test",
                "probaj",
                "pokreni",
                "run it",
                "verify runtime",
                "provjeri u igri"
            };

            return triggers.Any(trigger =>
                text.Contains(
                    trigger,
                    StringComparison.OrdinalIgnoreCase
                )
            );
        }
    }
}
