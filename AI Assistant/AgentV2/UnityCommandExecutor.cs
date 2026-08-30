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

            // 1. Persistent scripts first. Scene mutations wait until every
            // generated script has compiled so a bad script cannot leave a
            // half-configured scene behind.
            foreach (
                ScriptChangeV2 script
                in implementation.ScriptChanges
            )
            {
                if (!WriteAndCompileScript(script, report))
                {
                    return report;
                }
            }

            // 2. Attach only after ALL scripts compiled.
            foreach (
                ScriptChangeV2 script
                in implementation.ScriptChanges
            )
            {
                if (
                    !string.IsNullOrWhiteSpace(script.AttachTo)
                    && !AttachScript(script, report)
                )
                {
                    return report;
                }
            }

            // 3. Most scene operations go through ONE existing Unity batch
            // request. This removes the old one-model-call/one-HTTP-call style.
            if (
                !ExecuteSceneActions(
                    implementation.SceneActions,
                    report
                )
            )
            {
                return report;
            }

            // 4. One broad self-generated temp capability is still available
            // for one-shot Unity API work that the deterministic command layer
            // cannot express efficiently.
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

            // 5. Save once, verify once.
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

        private bool WriteAndCompileScript(
            ScriptChangeV2 script,
            AgentExecutionReportV2 report
        )
        {
            if (!ValidateScriptChange(script, report))
            {
                return false;
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

                return false;
            }

            report.FilesChanged.Add(script.AssetPath);
            report.Steps.Add(
                "Compiled "
                + script.AssetPath
            );

            return true;
        }

        private bool AttachScript(
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
                        + AgentJsonV2.Compact(
                            batchResult,
                            2200
                        )
                    );

                    return false;
                }

                report.Steps.Add(
                    "Unity batch: "
                    + batchable.Count
                    + " scene actions"
                );
            }

            // A few endpoints are not represented by UnityBatchBuilder yet.
            // Execute only those directly, locally, without another AI call.
            foreach (SceneActionV2 action in direct)
            {
                activity(
                    "[V2 ACTION] "
                    + action.Type
                );

                string actionResult =
                    ExecuteDirectSceneAction(action);

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
            string type =
                action.Type.Trim().ToLowerInvariant();

            return type == "add_component"
                || type == "create_gameobject"
                || type == "create_primitive"
                || type == "set_position"
                || type == "set_rotation"
                || type == "set_scale"
                || type == "set_active"
                || type == "rename_gameobject"
                || type == "set_parent";
        }

        private static void AddToBatch(
            UnityBatchBuilder batch,
            SceneActionV2 action
        )
        {
            string type =
                action.Type.Trim().ToLowerInvariant();

            switch (type)
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
            string type =
                action.Type
                    .Trim()
                    .ToLowerInvariant();

            return type switch
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
