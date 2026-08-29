param(
    [string]$Path = ".\AI Assistant\AI\AIIntegration.cs"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host ""
    Write-Host "AIIntegration.cs nije pronadjen na:" -ForegroundColor Red
    Write-Host "  $Path"
    Write-Host ""
    Write-Host "Pokreni ovako ako si u drugom folderu:"
    Write-Host '  .\Convert-AIIntegration-To-Gemini.ps1 -Path "C:\PUTANJA\AIIntegration.cs"'
    exit 1
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$backupPath = "$resolvedPath.groq-backup"

$content = [System.IO.File]::ReadAllText($resolvedPath)

$requiredMarkers = @(
    '"openai/gpt-oss-120b"',
    '"GROQ_API_KEY"',
    'https://api.groq.com/openai/v1/chat/completions'
)

$missing = @()

foreach ($marker in $requiredMarkers) {
    if (-not $content.Contains($marker)) {
        $missing += $marker
    }
}

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "Nisam izmijenio fajl jer trenutna verzija ne sadrzi ocekivane Groq markere:" -ForegroundColor Yellow
    foreach ($marker in $missing) {
        Write-Host "  $marker"
    }
    Write-Host ""
    Write-Host "Ovo je zastita da patch ne izmijeni pogresnu verziju fajla."
    exit 2
}

if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $resolvedPath -Destination $backupPath
    Write-Host "Backup napravljen:" -ForegroundColor DarkGray
    Write-Host "  $backupPath"
}
else {
    Write-Host "Backup vec postoji, ostavljam ga netaknutog:" -ForegroundColor DarkGray
    Write-Host "  $backupPath"
}

# ------------------------------------------------------------
# Provider/model migration: Groq -> Gemini 3.7 Flash
# ------------------------------------------------------------

$content = $content.Replace(
    '"openai/gpt-oss-120b"',
    '"gemini-3.7-flash"'
)

$content = $content.Replace(
    '"GROQ_API_KEY"',
    '"GEMINI_API_KEY"'
)

$content = $content.Replace(
    '"GROQ_API_KEY nije pronađen."',
    '"GEMINI_API_KEY nije pronađen."'
)

$content = $content.Replace(
    'https://api.groq.com/openai/v1/chat/completions',
    'https://generativelanguage.googleapis.com/v1beta/openai/chat/completions'
)

$content = $content.Replace(
    '$"Groq API greška:\n{responseText}"',
    '$"Gemini API greška:\n{responseText}"'
)

$content = $content.Replace(
    '$"Groq API greska:\n{responseText}"',
    '$"Gemini API greska:\n{responseText}"'
)

# The existing OpenAI-style messages/tools/tool_calls schema remains valid
# because Gemini exposes an OpenAI-compatible Chat Completions endpoint.
#
# Existing reasoning_effort is intentionally preserved:
#   low    -> lower Gemini thinking level
#   medium -> medium Gemini thinking level
#
# This keeps the agent's current task routing intact.

[System.IO.File]::WriteAllText(
    $resolvedPath,
    $content,
    [System.Text.UTF8Encoding]::new($true)
)

$verify = [System.IO.File]::ReadAllText($resolvedPath)

$checks = @(
    @{ Name = "Gemini model"; Value = '"gemini-3.7-flash"' },
    @{ Name = "Gemini API key"; Value = '"GEMINI_API_KEY"' },
    @{ Name = "Gemini endpoint"; Value = 'https://generativelanguage.googleapis.com/v1beta/openai/chat/completions' }
)

$failed = $false

Write-Host ""
Write-Host "Provjera:" -ForegroundColor Cyan

foreach ($check in $checks) {
    if ($verify.Contains($check.Value)) {
        Write-Host "  [OK] $($check.Name)" -ForegroundColor Green
    }
    else {
        Write-Host "  [FAIL] $($check.Name)" -ForegroundColor Red
        $failed = $true
    }
}

if ($verify.Contains('"openai/gpt-oss-120b"') -or
    $verify.Contains('"GROQ_API_KEY"') -or
    $verify.Contains('https://api.groq.com/openai/v1/chat/completions')) {
    Write-Host "  [FAIL] Ostao je aktivni Groq provider marker." -ForegroundColor Red
    $failed = $true
}
else {
    Write-Host "  [OK] Groq provider markeri uklonjeni" -ForegroundColor Green
}

Write-Host ""

if ($failed) {
    Write-Host "Patch je zavrsen, ali provjera nije prosla. Vrati backup ako treba:" -ForegroundColor Yellow
    Write-Host "  $backupPath"
    exit 3
}

Write-Host "AIIntegration.cs je prebacen na Gemini 3.7 Flash." -ForegroundColor Green
Write-Host ""
Write-Host "Sada postavi API key u PowerShellu:" -ForegroundColor Cyan
Write-Host '  [Environment]::SetEnvironmentVariable("GEMINI_API_KEY", "TVOJ_KEY", "User")'
Write-Host ""
Write-Host "Zatim restartuj aplikaciju / Visual Studio da ucita novi environment variable."
Write-Host ""
Write-Host "Model:    gemini-3.7-flash"
Write-Host "Endpoint: Gemini OpenAI-compatible Chat Completions"
Write-Host "Thinking: postojeci reasoning_effort ostaje aktivan"
