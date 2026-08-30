using AI_Assistant.TempCapabilities;
using AI_Assistant.Tools;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace AI_Assistant.AgentV2
{
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

            // Persistent scripts are completed first. Scene mutations are
            // intentionally delayed until compilation succeeds so a bad
            // generated script cannot leave a half-configured scene.
            foreach (
                ScriptChangeV2 script
                in implementation.ScriptChanges
            )
            {
                if (!ValidateScriptChange(script, report))
                {
                    return report;
                }

                activity(
                    "[V2 WRITE] "
                    + script.AssetPath
                );

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
                        + AgentJsonV2.Compact(
                            createResult,
                            1800
                        )
                    );

                    return report;
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

                    return report;
                }

                activity(
                    "[V2 COMPILE] "
                    + script.ClassName
                );

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
                    && AgentJsonV2.LooksSuccessful(
                        compileResult
                    );

                if (!compiled)
                {
                    report.CompileFailed = true;
                    report.CompileFailureText = compileResult;
                    report.Fail(
                        "Compilation failed for "
                        + script.AssetPath
                        + ": "
                        + AgentJsonV2.Compact(
                            compileResult,
                            2800
                        )
                    );

                    return report;
                }

                report.FilesChanged.Add(script.AssetPath);
                report.Steps.Add(
                    "Compiled "
                    + script.AssetPath
                );
            }

            // Attach scripts only after all generated scripts compiled.
            foreach (
                ScriptChangeV2 script
                in implementation.ScriptChanges
            )
            {
                if (string.IsNullOrWhiteSpace(script.AttachTo))
                {
                    continue;
                }

                activity(
                    "[V2 ATTACH] "
                    + script.ClassName
                    + " -> "
                    + script.AttachTo
                );

                string attachResult =
                    unity.AttachScript(
                        script.AttachTo,
                        script.ClassName
                    );

                if (!AgentJsonV2.LooksSuccessful(attachResult))
                {
                    report.Fail(
                        "Could not attach "
                        + script.ClassName
                        + " to "
                        + script.AttachTo
                        + ": "
                        + AgentJsonV2.Compact(
                            attachResult,
                            1600
                        )
                    );

                    return report;
                }

                report.Steps.Add(
                    "Attached "
                    + script.ClassName
                    + " to "
                    + script.AttachTo
                );
            }

            foreach (
                SceneActionV2 action
                in implementation.SceneActions
            )
            {
                activity(
                    "[V2 ACTION] "
                    + action.Type
                );

                string actionResult =
                    ExecuteSceneAction(action);

                if (!AgentJsonV2.LooksSuccessful(actionResult))
                {
                    report.Fail(
                        "Scene action '"
                        + action.Type
                        + "' failed: "
                        + AgentJsonV2.Compact(
                            actionResult,
                            1800
                        )
                    );

                    return report;
                }

                report.Steps.Add(
                    "Scene action: "
                    + action.Type
                );
            }

            if (
                implementation.TemporaryCapability != null
                && !string.IsNullOrWhiteSpace(
                    implementation.TemporaryCapability.Name
                )
            )
            {
                TempCapabilitySpecV2 capability =
                    implementation.TemporaryCapability;

                activity(
                    "[V2 TEMP] "
                    + capability.Name
                );

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

            activity("[V2 SAVE] scene");

            string saveResult =
                unity.SaveScene();

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

            // One final verification read. No repeated polling loop.
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

        private string ExecuteSceneAction(SceneActionV2 action)
        {
            string type =
                action.Type
                    .Trim()
                    .ToLowerInvariant();

            return type switch
            {
                "add_component" =>
                    unity.AddComponent(
                        action.ObjectPath,
                        action.ComponentType
                    ),

                "attach_script" =>
                    unity.AttachScript(
                        action.ObjectPath,
                        action.ScriptType
                    ),

                "create_gameobject" =>
                    unity.CreateGameObject(
                        action.Name,
                        action.ParentPath
                    ),

                "create_primitive" =>
                    unity.CreatePrimitive(
                        action.PrimitiveType,
                        action.Name,
                        action.ParentPath
                    ),

                "set_position" =>
                    unity.SetPosition(
                        action.ObjectPath,
                        action.X ?? 0f,
                        action.Y ?? 0f,
                        action.Z ?? 0f
                    ),

                "set_rotation" =>
                    unity.SetRotation(
                        action.ObjectPath,
                        action.X ?? 0f,
                        action.Y ?? 0f,
                        action.Z ?? 0f
                    ),

                "set_scale" =>
                    unity.SetScale(
                        action.ObjectPath,
                        action.X ?? 1f,
                        action.Y ?? 1f,
                        action.Z ?? 1f
                    ),

                "set_active" =>
                    unity.SetActive(
                        action.ObjectPath,
                        action.Active ?? true
                    ),

                "rename_gameobject" =>
                    unity.RenameGameObject(
                        action.ObjectPath,
                        action.NewName
                    ),

                "set_parent" =>
                    unity.SetParent(
                        action.ObjectPath,
                        action.ParentPath
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
                    "AGENT V2 ERROR: unsupported scene action type '"
                    + action.Type
                    + "'. Use a temporary capability for unsupported one-shot Unity setup."
            };
        }

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
                    + AgentJsonV2.Compact(
                        enterResult,
                        1600
                    )
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
                        .Distinct(
                            StringComparer.OrdinalIgnoreCase
                        )
                        .Take(4)
                )
                {
                    activity(
                        "[V2 RUNTIME] "
                        + objectPath
                    );

                    string runtime =
                        unity.GetUnityRuntimeState(
                            objectPath
                        );

                    report.RuntimeResults.Add(
                        objectPath
                        + ": "
                        + AgentJsonV2.Compact(
                            runtime,
                            2200
                        )
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
                        + AgentJsonV2.Compact(
                            exitResult,
                            1400
                        )
                    );
                }
            }
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

        private static bool ShouldRunRuntimeVerification(
            string goal
        )
        {
            string text = goal.ToLowerInvariant();

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

            foreach (string trigger in triggers)
            {
                if (text.Contains(trigger))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
