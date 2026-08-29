param(
    [string]$Path = ".\AI Assistant\AI\AIIntegration.cs"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "AIIntegration.cs nije pronadjen: $Path" -ForegroundColor Red
    exit 1
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$backupPath = "$resolvedPath.navmesh-router-backup"
$content = [System.IO.File]::ReadAllText($resolvedPath)

if ($content.Contains('"navmesh",') -and
    $content.Contains('"navigation",') -and
    $content.Contains('"patrol",') -and
    $content.Contains('"chase",')) {
    Write-Host "[OK] NavMesh persistent-tool routing je vec instaliran." -ForegroundColor Green
    exit 0
}

if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $resolvedPath -Destination $backupPath
}

$old = @'
                    "enemy ai",
                    "weapon system",
'@

$new = @'
                    "enemy ai",
                    "enemy",
                    "navmesh",
                    "nav mesh",
                    "navigation",
                    "navmeshagent",
                    "nav mesh agent",
                    "patrol",
                    "chase",
                    "attack distance",
                    "detection radius",
                    "weapon system",
'@

if (-not $content.Contains($old)) {
    Write-Host "[FAIL] Nisam nasao persistentRuntimeScript keyword blok. Fajl nije mijenjan." -ForegroundColor Red
    Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
    exit 2
}

$updated = $content.Replace($old, $new)

# Ensure NavMesh/enemy tasks are also treated as complex Unity tasks.
$complexOld = @'
                    "enemy ai",
                    "vehicle",
'@

$complexNew = @'
                    "enemy ai",
                    "enemy",
                    "navmesh",
                    "nav mesh",
                    "navigation",
                    "patrol",
                    "chase",
                    "vehicle",
'@

if ($updated.Contains($complexOld)) {
    $updated = $updated.Replace($complexOld, $complexNew)
}

[System.IO.File]::WriteAllText(
    $resolvedPath,
    $updated,
    [System.Text.UTF8Encoding]::new($true)
)

$verify = [System.IO.File]::ReadAllText($resolvedPath)

$required = @(
    '"enemy",',
    '"navmesh",',
    '"navigation",',
    '"patrol",',
    '"chase",',
    '"create_unity_script"',
    '"wait_for_unity_script_compile"',
    '"attach_script"'
)

$failed = $false
foreach ($item in $required) {
    if (-not $verify.Contains($item)) {
        Write-Host "[FAIL] Missing: $item" -ForegroundColor Red
        $failed = $true
    }
}

if ($failed) {
    Write-Host "Patch verification failed. Backup: $backupPath" -ForegroundColor Red
    exit 3
}

Write-Host "[OK] Enemy/NavMesh/navigation routing prosiren." -ForegroundColor Green
Write-Host "[OK] Takvi Unity taskovi sada dobijaju persistent script toolove." -ForegroundColor Green
Write-Host "[OK] create_unity_script / compile / attach ostaju dostupni kroz fallback." -ForegroundColor Green
Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Zatim Rebuild Solution i restartuj AI Assistant." -ForegroundColor Cyan
