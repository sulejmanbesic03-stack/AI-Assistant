param(
    [string]$ExecutorPath = ".\AI Assistant\AgentV2\UnityCommandExecutor.cs",
    [string]$OrchestratorPath = ".\AI Assistant\AgentV2\AgentOrchestratorV2.cs"
)

$ErrorActionPreference = "Stop"

foreach ($path in @($ExecutorPath, $OrchestratorPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Host "[FAIL] Fajl nije pronadjen: $path" -ForegroundColor Red
        exit 1
    }
}

$executorResolved = (Resolve-Path -LiteralPath $ExecutorPath).Path
$orchestratorResolved = (Resolve-Path -LiteralPath $OrchestratorPath).Path

$executorBackup = "$executorResolved.attach-normalization-backup"
$orchestratorBackup = "$orchestratorResolved.task-correction-backup"

$executor = [System.IO.File]::ReadAllText($executorResolved)
$orchestrator = [System.IO.File]::ReadAllText($orchestratorResolved)

if ($executor.Contains("AGENT V2 ATTACH NORMALIZATION v2") -and $orchestrator.Contains("AGENT V2 TASK CORRECTION v2")) {
    Write-Host "[OK] Agent V2 attach/correction fix je vec instaliran." -ForegroundColor Green
    exit 0
}

if (-not (Test-Path -LiteralPath $executorBackup)) {
    Copy-Item -LiteralPath $executorResolved -Destination $executorBackup
}

if (-not (Test-Path -LiteralPath $orchestratorBackup)) {
    Copy-Item -LiteralPath $orchestratorResolved -Destination $orchestratorBackup
}

# ============================================================
# EXECUTOR: redundant generated-script attach actions
# ============================================================

if (-not $executor.Contains("AGENT V2 PREFLIGHT v1")) {
    throw "Preflight v1 nije instaliran. Prvo pokreni Patch-AgentV2-Preflight-And-Ordering.ps1"
}

$oldAttachFilter = @'
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
'@

$newAttachFilter = @'
            // AGENT V2 ATTACH NORMALIZATION v2
            // script_changes.attach_to is canonical for generated scripts.
            // Models sometimes also emit a redundant scene_actions attach_script
            // and may omit script_type there. If the target is already covered
            // by script_changes, remove the redundant action completely. The
            // generated script will be attached later by the deterministic host.
            implementation.SceneActions =
                implementation.SceneActions
                    .Where(action =>
                    {
                        if (action.Type != "attach_script")
                        {
                            return true;
                        }

                        string target =
                            (action.ObjectPath ?? "").Trim();

                        if (string.IsNullOrWhiteSpace(target))
                        {
                            return true;
                        }

                        List<ScriptChangeV2> generatedForTarget =
                            implementation.ScriptChanges
                                .Where(script =>
                                    !string.IsNullOrWhiteSpace(script.AttachTo)
                                    && string.Equals(
                                        script.AttachTo.Trim(),
                                        target,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                                .ToList();

                        if (generatedForTarget.Count == 0)
                        {
                            return true;
                        }

                        // Blank script_type is safe to discard here because all
                        // generated script attachments for this target are already
                        // represented by script_changes.attach_to.
                        if (string.IsNullOrWhiteSpace(action.ScriptType))
                        {
                            return false;
                        }

                        // Also discard an explicitly duplicated generated class.
                        return !generatedForTarget.Any(script =>
                            string.Equals(
                                script.ClassName.Trim(),
                                action.ScriptType.Trim(),
                                StringComparison.OrdinalIgnoreCase
                            )
                        );
                    })
                    .ToList();
'@

if (-not $executor.Contains("AGENT V2 ATTACH NORMALIZATION v2")) {
    if (-not $executor.Contains($oldAttachFilter)) {
        throw "Nisam nasao preflight v1 generatedAttachments blok u UnityCommandExecutor.cs"
    }

    $executor = $executor.Replace($oldAttachFilter, $newAttachFilter)
}

# ============================================================
# ORCHESTRATOR: prompt contract + task corrections
# ============================================================

# Give one extra bounded model slot so a correction after provider fallback can
# still leave room for one compile-repair pass. Normal tasks still use 1 call.
$orchestrator = [regex]::Replace(
    $orchestrator,
    'private\s+const\s+int\s+MaxModelCallsPerTask\s*=\s*3\s*;',
    'private const int MaxModelCallsPerTask = 4;',
    1
)

$promptAnchor =
    '+ "- Persistent gameplay logic belongs in normal MonoBehaviour scripts under Assets, never in a temporary capability.\\n"'

# PowerShell continuation rule: keep -and at the end of the previous line.
if (
    (-not $orchestrator.Contains("When script_changes uses attach_to")) -and
    $orchestrator.Contains($promptAnchor)
) {
    $promptReplacement =
        $promptAnchor + "`r`n                " +
        '+ "- When script_changes uses attach_to for a generated script, do NOT also emit an attach_script scene_action for that same script.\\n"'

    $orchestrator = $orchestrator.Replace($promptAnchor, $promptReplacement)
}

# ShouldHandle: accept short corrections while an unfinished V2 task exists.
$oldShouldHandle = @'
            if (
                activeTask != null
                && !activeTask.Completed
                && IsContinuation(text)
            )
            {
                return true;
            }
'@

$newShouldHandle = @'
            // AGENT V2 TASK CORRECTION v2
            // While a V2 task is unfinished, accept both explicit "nastavi"
            // and short task corrections such as "Player neka se zove Player".
            if (
                activeTask != null
                && !activeTask.Completed
                && (
                    IsContinuation(text)
                    || IsTaskCorrection(text)
                )
            )
            {
                return true;
            }
'@

if (-not $orchestrator.Contains("AGENT V2 TASK CORRECTION v2")) {
    if (-not $orchestrator.Contains($oldShouldHandle)) {
        throw "Nisam nasao ShouldHandle continuation blok."
    }

    $orchestrator = $orchestrator.Replace($oldShouldHandle, $newShouldHandle)
}

# HandleAsync: distinguish plain continuation from a correction. A correction
# updates the stored goal and invalidates only the model implementation, while
# reusing the already-captured compact Unity snapshot.
$oldContinuation = @'
            bool continuation =
                activeTask != null
                && !activeTask.Completed
                && IsContinuation(
                    cleanedPrompt
                        .Trim()
                        .ToLowerInvariant()
                );

            if (!continuation)
            {
                activeTask =
                    new AgentTaskStateV2
                    {
                        Goal = cleanedPrompt
                    };

                activeSnapshot = null;
                activeImplementation = null;
            }
'@

$newContinuation = @'
            string normalizedPrompt =
                cleanedPrompt
                    .Trim()
                    .ToLowerInvariant();

            bool explicitContinuation =
                activeTask != null
                && !activeTask.Completed
                && IsContinuation(normalizedPrompt);

            bool taskCorrection =
                activeTask != null
                && !activeTask.Completed
                && !explicitContinuation
                && IsTaskCorrection(normalizedPrompt);

            bool continuation =
                explicitContinuation
                || taskCorrection;

            if (!continuation)
            {
                activeTask =
                    new AgentTaskStateV2
                    {
                        Goal = cleanedPrompt
                    };

                activeSnapshot = null;
                activeImplementation = null;
            }
            else if (taskCorrection && activeTask != null)
            {
                activeTask.Goal =
                    activeTask.Goal
                    + "\nUSER CORRECTION: "
                    + cleanedPrompt;

                // Keep the compact project snapshot; only regenerate the
                // implementation against the corrected task goal.
                activeImplementation = null;

                activity(
                    "[V2 CORRECTION] task goal updated"
                );
            }
'@

if (-not $orchestrator.Contains("[V2 CORRECTION] task goal updated")) {
    if (-not $orchestrator.Contains($oldContinuation)) {
        throw "Nisam nasao HandleAsync continuation blok."
    }

    $orchestrator = $orchestrator.Replace($oldContinuation, $newContinuation)
}

# Add correction classifier immediately before ContainsAny helper.
$containsAnyMarker = @'
        private static bool ContainsAny(
            string text,
            params string[] values
        )
'@

$correctionMethod = @'
        private static bool IsTaskCorrection(string text)
        {
            string normalized =
                (text ?? "")
                    .Trim()
                    .ToLowerInvariant();

            if (
                string.IsNullOrWhiteSpace(normalized)
                || normalized.Length > 260
            )
            {
                return false;
            }

            return ContainsAny(
                normalized,
                "neka ",
                "zove",
                "nazovi",
                "ime ",
                "name ",
                "koristi",
                "use ",
                "stavi",
                "postavi",
                "umjesto",
                "instead",
                "target",
                "objekat",
                "object",
                "player",
                "enemy",
                "kamera",
                "camera",
                "skript",
                "script"
            );
        }


'@

if (-not $orchestrator.Contains("private static bool IsTaskCorrection")) {
    if (-not $orchestrator.Contains($containsAnyMarker)) {
        throw "Nisam nasao ContainsAny marker."
    }

    $orchestrator = $orchestrator.Replace(
        $containsAnyMarker,
        $correctionMethod + $containsAnyMarker
    )
}

# ============================================================
# WRITE + VERIFY
# ============================================================

[System.IO.File]::WriteAllText(
    $executorResolved,
    $executor,
    [System.Text.UTF8Encoding]::new($true)
)

[System.IO.File]::WriteAllText(
    $orchestratorResolved,
    $orchestrator,
    [System.Text.UTF8Encoding]::new($true)
)

$executorVerify = [System.IO.File]::ReadAllText($executorResolved)
$orchestratorVerify = [System.IO.File]::ReadAllText($orchestratorResolved)

$checks = @(
    @{ Ok = $executorVerify.Contains("AGENT V2 ATTACH NORMALIZATION v2"); Text = "redundant attach_script normalization" },
    @{ Ok = $orchestratorVerify.Contains("AGENT V2 TASK CORRECTION v2"); Text = "unfinished-task correction routing" },
    @{ Ok = $orchestratorVerify.Contains("private static bool IsTaskCorrection"); Text = "task correction classifier" },
    @{ Ok = $orchestratorVerify.Contains("[V2 CORRECTION] task goal updated"); Text = "correction keeps task state and refreshes implementation" },
    @{ Ok = [regex]::IsMatch($orchestratorVerify, 'MaxModelCallsPerTask\s*=\s*4\s*;'); Text = "bounded model budget = 4" }
)

$failed = $false
foreach ($check in $checks) {
    if ($check.Ok) {
        Write-Host "[OK] $($check.Text)" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $($check.Text)" -ForegroundColor Red
        $failed = $true
    }
}

if ($failed) {
    Write-Host ""
    Write-Host "Patch verification failed." -ForegroundColor Red
    Write-Host "Executor backup: $executorBackup"
    Write-Host "Orchestrator backup: $orchestratorBackup"
    exit 4
}

Write-Host ""
Write-Host "[OK] Agent V2 attach/correction fix instaliran." -ForegroundColor Green
Write-Host "[OK] Redundant attach_script bez script_type vise ne blokira task." -ForegroundColor Green
Write-Host "[OK] script_changes.attach_to ostaje jedini autoritet za generated-script attach." -ForegroundColor Green
Write-Host "[OK] Kratke dopune tipa 'Player neka se zove Player' nastavljaju isti V2 task." -ForegroundColor Green
Write-Host ""
Write-Host "Executor backup: $executorBackup" -ForegroundColor DarkGray
Write-Host "Orchestrator backup: $orchestratorBackup" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Uradi Rebuild Solution i restartuj AI Assistant."
