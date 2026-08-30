param(
    [string]$Path = ".\AI Assistant\AgentV2\UnityCommandExecutor.cs"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "[FAIL] UnityCommandExecutor.cs nije pronadjen: $Path" -ForegroundColor Red
    exit 1
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$backupPath = "$resolvedPath.preflight-backup"
$content = [System.IO.File]::ReadAllText($resolvedPath)

if ($content.Contains("AGENT V2 PREFLIGHT v1")) {
    Write-Host "[OK] Agent V2 preflight + ordering fix je vec instaliran." -ForegroundColor Green
    exit 0
}

if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $resolvedPath -Destination $backupPath
}

# ------------------------------------------------------------
# 1. PREFLIGHT BEFORE ANY MUTATION
# ------------------------------------------------------------

$reportAnchor = @'
            AgentExecutionReportV2 report =
                new AgentExecutionReportV2();
'@

if (-not $content.Contains($reportAnchor)) {
    throw "Nisam nasao AgentExecutionReportV2 initialization."
}

$reportReplacement = $reportAnchor + @'


            // AGENT V2 PREFLIGHT v1
            // Normalize obvious model omissions locally before any Unity
            // mutation. This avoids wasting another AI call on fields that
            // can be derived deterministically (for example create_gameobject
            // name from object_path or a single script attach target).
            if (!NormalizeImplementation(implementation, report))
            {
                return report;
            }
'@

$content = $content.Replace($reportAnchor, $reportReplacement)

# ------------------------------------------------------------
# 2. SCENE CREATION/CONFIGURATION MUST HAPPEN BEFORE SCRIPT ATTACH
# ------------------------------------------------------------

$oldOrder = @'
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
'@

$newOrder = @'
            // 2. Create/configure requested scene objects BEFORE attaching
            // generated scripts. This is required for tasks such as
            // "create a new Player and attach this controller" where the
            // target GameObject does not exist at the start of the task.
            if (
                !ExecuteSceneActions(
                    implementation.SceneActions,
                    report
                )
            )
            {
                return report;
            }

            // 3. Attach generated persistent scripts only after every script
            // compiled and every requested target GameObject now exists.
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
'@

if (-not $content.Contains($oldOrder)) {
    throw "Nisam nasao stari attach/scene ordering blok."
}

$content = $content.Replace($oldOrder, $newOrder)

# ------------------------------------------------------------
# 3. INSERT DETERMINISTIC NORMALIZER / VALIDATOR
# ------------------------------------------------------------

$methodMarker = "        private bool WriteAndCompileScript("
if (-not $content.Contains($methodMarker)) {
    throw "Nisam nasao WriteAndCompileScript marker."
}

$normalizer = @'
        private static bool NormalizeImplementation(
            AgentImplementationV2 implementation,
            AgentExecutionReportV2 report
        )
        {
            List<string> attachTargets =
                implementation.ScriptChanges
                    .Select(script => script.AttachTo)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

            string singleAttachTarget =
                attachTargets.Count == 1
                    ? attachTargets[0]
                    : "";

            // Normalize action names/paths before validating required fields.
            foreach (SceneActionV2 action in implementation.SceneActions)
            {
                action.Type =
                    (action.Type ?? "")
                        .Trim()
                        .ToLowerInvariant();

                if (
                    action.Type == "create_gameobject"
                    || action.Type == "create_primitive"
                )
                {
                    // Some models naturally return object_path="Player"
                    // instead of name="Player". That is unambiguous, so fix
                    // it locally rather than spending another model call.
                    if (
                        string.IsNullOrWhiteSpace(action.ObjectPath)
                        && !string.IsNullOrWhiteSpace(singleAttachTarget)
                    )
                    {
                        action.ObjectPath = singleAttachTarget;
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
                                : action.ParentPath.TrimEnd('/') + "/" + action.Name;
                    }
                }

                if (
                    action.Type == "attach_script"
                    && string.IsNullOrWhiteSpace(action.ScriptType)
                    && !string.IsNullOrWhiteSpace(action.ComponentType)
                )
                {
                    action.ScriptType = action.ComponentType;
                }

                if (
                    action.Type == "attach_script"
                    && string.IsNullOrWhiteSpace(action.ObjectPath)
                    && !string.IsNullOrWhiteSpace(singleAttachTarget)
                )
                {
                    action.ObjectPath = singleAttachTarget;
                }
            }

            // script_changes.attach_to is the canonical way to attach a
            // generated script. Remove an equivalent scene attach action so
            // the same MonoBehaviour is not accidentally attached twice.
            HashSet<string> generatedAttachments =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ScriptChangeV2 script in implementation.ScriptChanges)
            {
                if (!string.IsNullOrWhiteSpace(script.AttachTo))
                {
                    generatedAttachments.Add(
                        script.AttachTo.Trim()
                        + "|"
                        + script.ClassName.Trim()
                    );
                }
            }

            implementation.SceneActions =
                implementation.SceneActions
                    .Where(action =>
                    {
                        if (action.Type != "attach_script")
                        {
                            return true;
                        }

                        string key =
                            (action.ObjectPath ?? "").Trim()
                            + "|"
                            + (action.ScriptType ?? "").Trim();

                        return !generatedAttachments.Contains(key);
                    })
                    .ToList();

            for (int i = 0; i < implementation.SceneActions.Count; i++)
            {
                SceneActionV2 action = implementation.SceneActions[i];
                string prefix =
                    "scene_actions["
                    + i
                    + "] '"
                    + action.Type
                    + "'";

                if (string.IsNullOrWhiteSpace(action.Type))
                {
                    report.Fail(
                        "Agent V2 preflight: scene_actions["
                        + i
                        + "] is missing type."
                    );
                    return false;
                }

                switch (action.Type)
                {
                    case "create_gameobject":
                        if (!Require(action.Name, "name", prefix, report))
                        {
                            return false;
                        }
                        break;

                    case "create_primitive":
                        if (
                            !Require(action.Name, "name", prefix, report)
                            || !Require(action.PrimitiveType, "primitive_type", prefix, report)
                        )
                        {
                            return false;
                        }
                        break;

                    case "add_component":
                        if (
                            !Require(action.ObjectPath, "object_path", prefix, report)
                            || !Require(action.ComponentType, "component_type", prefix, report)
                        )
                        {
                            return false;
                        }
                        break;

                    case "attach_script":
                        if (
                            !Require(action.ObjectPath, "object_path", prefix, report)
                            || !Require(action.ScriptType, "script_type", prefix, report)
                        )
                        {
                            return false;
                        }
                        break;

                    case "set_position":
                    case "set_rotation":
                    case "set_scale":
                    case "set_active":
                    case "configure_rigidbody":
                    case "configure_collider":
                        if (!Require(action.ObjectPath, "object_path", prefix, report))
                        {
                            return false;
                        }
                        break;

                    case "rename_gameobject":
                        if (
                            !Require(action.ObjectPath, "object_path", prefix, report)
                            || !Require(action.NewName, "new_name", prefix, report)
                        )
                        {
                            return false;
                        }
                        break;

                    case "set_parent":
                        if (!Require(action.ObjectPath, "object_path", prefix, report))
                        {
                            return false;
                        }
                        break;

                    case "duplicate_gameobject":
                        if (
                            !Require(action.ObjectPath, "object_path", prefix, report)
                            || !Require(action.NewName, "new_name", prefix, report)
                        )
                        {
                            return false;
                        }
                        break;

                    case "create_material":
                        if (
                            !Require(action.AssetPath, "asset_path", prefix, report)
                            || !Require(action.ShaderName, "shader_name", prefix, report)
                        )
                        {
                            return false;
                        }
                        break;

                    case "set_material_color":
                        if (!Require(action.MaterialPath, "material_path", prefix, report))
                        {
                            return false;
                        }
                        break;

                    case "assign_material":
                        if (
                            !Require(action.ObjectPath, "object_path", prefix, report)
                            || !Require(action.MaterialPath, "material_path", prefix, report)
                        )
                        {
                            return false;
                        }
                        break;

                    case "import_asset":
                        if (!Require(action.AssetPath, "asset_path", prefix, report))
                        {
                            return false;
                        }
                        break;

                    default:
                        report.Fail(
                            "Agent V2 preflight: unsupported action type '"
                            + action.Type
                            + "'."
                        );
                        return false;
                }
            }

            return true;
        }

        private static bool Require(
            string? value,
            string field,
            string actionDescription,
            AgentExecutionReportV2 report
        )
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            report.Fail(
                "Agent V2 preflight: "
                + actionDescription
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

            string normalized =
                path.Replace('\\', '/').Trim('/');

            int slash = normalized.LastIndexOf('/');

            return slash >= 0
                ? normalized.Substring(slash + 1)
                : normalized;
        }

        private static string ParentPathOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "";
            }

            string normalized =
                path.Replace('\\', '/').Trim('/');

            int slash = normalized.LastIndexOf('/');

            return slash > 0
                ? normalized.Substring(0, slash)
                : "";
        }


'@

$content = $content.Replace($methodMarker, $normalizer + $methodMarker)

[System.IO.File]::WriteAllText(
    $resolvedPath,
    $content,
    [System.Text.UTF8Encoding]::new($true)
)

$verify = [System.IO.File]::ReadAllText($resolvedPath)

$required = @(
    "AGENT V2 PREFLIGHT v1",
    "NormalizeImplementation(implementation, report)",
    "Create/configure requested scene objects BEFORE attaching",
    "private static string LastPathSegment",
    "private static bool Require("
)

$failed = $false
foreach ($item in $required) {
    if ($verify.Contains($item)) {
        Write-Host "[OK] $item" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $item" -ForegroundColor Red
        $failed = $true
    }
}

if ($failed) {
    Write-Host "Patch verification failed. Backup: $backupPath" -ForegroundColor Red
    exit 4
}

Write-Host ""
Write-Host "[OK] Agent V2 preflight normalization instaliran." -ForegroundColor Green
Write-Host "[OK] create_gameobject name se lokalno izvodi iz object_path/attach_to kada je jednoznacno." -ForegroundColor Green
Write-Host "[OK] Scene object creation sada ide prije generated-script attach koraka." -ForegroundColor Green
Write-Host "[OK] Ekvivalentni dupli attach_script action se uklanja prije izvrsenja." -ForegroundColor Green
Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Uradi Rebuild Solution i restartuj AI Assistant."
