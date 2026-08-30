param(
    [string]$Path = ".\AI Assistant\AI\AIIntegration.cs"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "[FAIL] AIIntegration.cs nije pronadjen: $Path" -ForegroundColor Red
    exit 1
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$backupPath = "$resolvedPath.unity-rules-token-backup"
$content = [System.IO.File]::ReadAllText($resolvedPath)

if ($content.Contains("UNITY ENGINEERING RULES v1")) {
    Write-Host "[OK] Unity Rules + token optimization su vec instalirani." -ForegroundColor Green
    exit 0
}

if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $resolvedPath -Destination $backupPath
}

function Replace-ConstValue {
    param(
        [string]$Text,
        [string]$ConstName,
        [int]$NewValue
    )

    $pattern = "(?s)(private\s+const\s+int\s+" + [regex]::Escape($ConstName) + "\s*=\s*)\d+(\s*;)"
    $replacement = '${1}' + $NewValue + '${2}'
    $updated = [regex]::Replace($Text, $pattern, $replacement, 1)

    if ($updated -eq $Text) {
        throw "Nisam nasao konstantu: $ConstName"
    }

    return $updated
}

# ------------------------------------------------------------
# TOKEN BUDGET
# ------------------------------------------------------------

$content = Replace-ConstValue $content "MaxIterations" 10
$content = Replace-ConstValue $content "MaxToolResultChars" 3500
$content = Replace-ConstValue $content "MaxUnityScriptReadResultChars" 12000
$content = Replace-ConstValue $content "MaxChatHistoryMessages" 3
$content = Replace-ConstValue $content "MaxToolCyclesInContext" 2
$content = Replace-ConstValue $content "MaxRoutingContextChars" 3500
$content = Replace-ConstValue $content "MaxRoutingSegmentChars" 900

# ------------------------------------------------------------
# UNITY ENGINEERING RULES
# Insert immediately before TEMPORARY CAPABILITIES in system prompt.
# ------------------------------------------------------------

$marker = "TEMPORARY CAPABILITIES:"
if (-not $content.Contains($marker)) {
    throw "Nisam nasao TEMPORARY CAPABILITIES marker u system promptu."
}

$rules = @'
UNITY ENGINEERING RULES v1:

Use a strict INSPECT -> DIAGNOSE -> CHANGE -> COMPILE -> VERIFY workflow for gameplay systems.
Do not write gameplay code before you understand what currently controls the same behavior.

BEFORE MODIFYING PLAYER, CAMERA, PHYSICS OR ENEMY AI:
- inspect the relevant hierarchy and attached components,
- inspect existing related scripts,
- inspect project/input settings when relevant,
- identify conflicting scripts/components before adding anything,
- prefer repairing the existing implementation instead of stacking another controller or AI script.

TOKEN / TOOL DISCIPLINE:
- Use the smallest sufficient toolset and the minimum useful reads.
- Do not call Unity documentation unless the API/workflow is uncertain or version-sensitive.
- Do not re-read unchanged hierarchy, settings, docs or scripts.
- Do not repeat a successful mutation for verification.
- After a successful tool result, reason from that result instead of calling another tool that returns equivalent information.
- For script repair, read/review the existing script once, then rewrite the complete corrected source in one pass.
- Prefer one final verification pass over verification after every mutation.

SINGLE AUTHORITY RULE:
- Only one runtime system should own player movement.
- Only one runtime system should own mouse-look/camera pitch.
- Only one runtime system should own an enemy's high-level navigation state.
- Before creating a new controller/AI script, find existing scripts that may control the same Transform, Rigidbody, CharacterController, NavMeshAgent or Camera.
- Never create PlayerController2, NewPlayerController, EnemyAI2 or equivalent duplicate systems as a repair strategy unless explicitly requested.

COLLIDER RULES:
- Inspect all existing Collider and CharacterController components before adding another collider.
- Do not add duplicate body colliders.
- A normal capsule-style player should usually have one primary physical body collider/CharacterController unless a concrete requirement justifies more.
- Extra detection/hitbox colliders should normally be triggers with an explicit purpose and must not unintentionally participate in body physics.
- If multiple colliders already exist, determine why each exists before changing movement code.

RIGIDBODY PLAYER RULES:
- Read input in Update and apply physics movement in FixedUpdate.
- Do not move or rotate a dynamic Rigidbody-controlled player through Transform.
- Use Rigidbody physics APIs for physical movement/rotation.
- Freeze unwanted physical rotation axes where appropriate so collision torque cannot tip/spin the player.
- Camera pitch must never be driven by collision-induced Rigidbody rotation.
- Separate body yaw from camera pitch.
- Avoid two scripts simultaneously writing player rotation.

CAMERA RULES:
- Mouse-look must be isolated from collision physics.
- Inspect the Camera parent hierarchy before implementing look code.
- Never accumulate camera Euler angles from a physically rotating parent without a deliberate stable reference.
- Clamp pitch and keep yaw/pitch ownership explicit.
- A collision impulse must not be able to cause uncontrolled camera spin.

ENEMY AI / NAVMESH RULES:
- Use explicit states such as Patrol, Chase, Attack and Return/Investigate where appropriate.
- Every state must have an entry condition, active behavior and exit condition.
- Never set NavMeshAgent.isStopped = true without defining exactly when it becomes false again.
- When entering attack range, continue updating facing/attack behavior as required; do not simply stop all AI logic.
- If the target leaves attack range, resume Chase explicitly.
- If the target leaves detection/leash range, transition explicitly back to Patrol/Return.
- Before SetDestination, ensure the agent is enabled, is on a NavMesh and has a valid target.
- Do not spam SetDestination with identical destinations when no meaningful target movement occurred.
- Compilation alone does not prove an AI state machine or NavMesh behavior works.

FIXING EXISTING BUGS:
Before editing, form an internal diagnosis containing:
1. what currently owns the behavior,
2. what components/scripts can conflict,
3. the most likely root cause,
4. the smallest safe change,
5. how runtime correctness will be verified.
Do not output this internal plan unless the user asks for it.

FINAL SANITY CHECK BEFORE CLAIMING SUCCESS:
- no unintended duplicate colliders/controllers,
- no two scripts fighting over the same transform/rotation,
- Rigidbody collision torque cannot drive camera pitch,
- AI states have valid exit transitions,
- stopped NavMeshAgents have an explicit resume path,
- required components are attached exactly once,
- compilation succeeded,
- scene/runtime verification matches what can actually be observed.

'@

$content = $content.Replace($marker, $rules + $marker)

# ------------------------------------------------------------
# Strengthen existing code-review rules without adding a new model call.
# ------------------------------------------------------------

$oldReviewAnchor = "- Compilation proves syntax and type correctness, not gameplay correctness."
$newReviewAnchor = @'
- Compilation proves syntax and type correctness, not gameplay correctness.
- Treat duplicate movement/camera/AI ownership and duplicate physical colliders as correctness bugs, not harmless setup details.
- When debugging runtime behavior, prefer identifying and removing the root conflict over adding compensating code.
'@

if ($content.Contains($oldReviewAnchor)) {
    $content = $content.Replace($oldReviewAnchor, $newReviewAnchor.TrimEnd())
}

[System.IO.File]::WriteAllText(
    $resolvedPath,
    $content,
    [System.Text.UTF8Encoding]::new($true)
)

$verify = [System.IO.File]::ReadAllText($resolvedPath)

$checks = @(
    @{ Name = "Unity Engineering Rules"; Value = "UNITY ENGINEERING RULES v1" },
    @{ Name = "MaxIterations 10"; Value = "private const int MaxIterations" },
    @{ Name = "Single authority rule"; Value = "SINGLE AUTHORITY RULE:" },
    @{ Name = "Collider rules"; Value = "COLLIDER RULES:" },
    @{ Name = "Enemy AI rules"; Value = "ENEMY AI / NAVMESH RULES:" },
    @{ Name = "Final sanity check"; Value = "FINAL SANITY CHECK BEFORE CLAIMING SUCCESS:" }
)

$failed = $false
foreach ($check in $checks) {
    if ($verify.Contains($check.Value)) {
        Write-Host "[OK] $($check.Name)" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $($check.Name)" -ForegroundColor Red
        $failed = $true
    }
}

# Exact numeric verification through regex.
$numericChecks = @{
    MaxIterations = 10
    MaxToolResultChars = 3500
    MaxUnityScriptReadResultChars = 12000
    MaxChatHistoryMessages = 3
    MaxToolCyclesInContext = 2
    MaxRoutingContextChars = 3500
    MaxRoutingSegmentChars = 900
}

foreach ($name in $numericChecks.Keys) {
    $expected = $numericChecks[$name]
    $pattern = "(?s)private\s+const\s+int\s+" + [regex]::Escape($name) + "\s*=\s*" + $expected + "\s*;"

    if ([regex]::IsMatch($verify, $pattern)) {
        Write-Host "[OK] $name = $expected" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $name nije $expected" -ForegroundColor Red
        $failed = $true
    }
}

if ($failed) {
    Write-Host "" 
    Write-Host "Patch provjera nije prosla. Backup je ovdje:" -ForegroundColor Red
    Write-Host "  $backupPath"
    exit 4
}

Write-Host ""
Write-Host "[OK] Unity Rules + token optimization instalirani." -ForegroundColor Green
Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Nove vrijednosti:" -ForegroundColor Cyan
Write-Host "  MaxIterations = 10"
Write-Host "  MaxChatHistoryMessages = 3"
Write-Host "  MaxToolCyclesInContext = 2"
Write-Host "  MaxToolResultChars = 3500"
Write-Host "  MaxUnityScriptReadResultChars = 12000"
Write-Host ""
Write-Host "Zatim uradi Rebuild Solution i restartuj AI Assistant."
