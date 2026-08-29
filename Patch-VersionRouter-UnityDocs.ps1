param(
    [string]$Path = ".\AI Assistant\AI\AIIntegration.cs"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "AIIntegration.cs nije pronadjen: $Path" -ForegroundColor Red
    exit 1
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$backupPath = "$resolvedPath.version-router-backup"
$content = [System.IO.File]::ReadAllText($resolvedPath)

$old = @'
            if (
                ContainsAny(
                    currentText,
                    "version",
                    "verzija",
                    "verziju"
                )
            )
            {
                tools.Add(
                    ToolNoArgs(
                        "get_agent_version",
                        "Returns current AI Assistant version."
                    )
                );


                return
                    tools.ToArray();
            }
'@

$new = @'
            bool asksAgentVersion =
                ContainsAny(
                    currentText,
                    "agent version",
                    "assistant version",
                    "ai assistant version",
                    "verzija agenta",
                    "verziju agenta",
                    "verzija asistenta",
                    "verziju asistenta"
                );


            bool mentionsUnityVersion =
                ContainsAny(
                    currentText,
                    "unity version",
                    "unity verzija",
                    "unity verziju",
                    "verzija unity",
                    "verziju unity"
                );


            if (
                asksAgentVersion
                &&
                !mentionsUnityVersion
            )
            {
                tools.Add(
                    ToolNoArgs(
                        "get_agent_version",
                        "Returns current AI Assistant version."
                    )
                );


                return
                    tools.ToArray();
            }
'@

if (-not $content.Contains($old)) {
    Write-Host "[FAIL] Nisam nasao ocekivani VERSION router blok. Nista nije izmijenjeno." -ForegroundColor Red
    exit 2
}

if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $resolvedPath -Destination $backupPath
}

$content = $content.Replace($old, $new)

[System.IO.File]::WriteAllText(
    $resolvedPath,
    $content,
    [System.Text.UTF8Encoding]::new($true)
)

$verify = [System.IO.File]::ReadAllText($resolvedPath)

if ($verify.Contains('bool asksAgentVersion =') -and $verify.Contains('bool mentionsUnityVersion =')) {
    Write-Host "[OK] Version router fixed." -ForegroundColor Green
    Write-Host "Unity version queries will no longer short-circuit to get_agent_version." -ForegroundColor Green
    Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
}
else {
    Write-Host "[FAIL] Verification failed." -ForegroundColor Red
    exit 3
}
