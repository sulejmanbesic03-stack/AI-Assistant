param(
    [string]$Path = ".\AI Assistant\AI\AIIntegration.cs"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "[FAIL] AIIntegration.cs nije pronadjen: $Path" -ForegroundColor Red
    exit 1
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$backupPath = "$resolvedPath.agent-v2-backup"
$content = [System.IO.File]::ReadAllText($resolvedPath)

if ($content.Contains("private readonly AgentOrchestratorV2 agentV2;")) {
    Write-Host "[OK] Agent V2 je vec spojen u AIIntegration.cs" -ForegroundColor Green
    Write-Host "Uradi Rebuild Solution i pokreni AI Assistant."
    exit 0
}

if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $resolvedPath -Destination $backupPath
}

# ------------------------------------------------------------
# 1. USING
# ------------------------------------------------------------

$usingAnchor = "using AI_Assistant.Tools;"
if (-not $content.Contains($usingAnchor)) {
    throw "Nisam nasao using AI_Assistant.Tools;"
}

if (-not $content.Contains("using AI_Assistant.AgentV2;")) {
    $content =
        $content.Replace(
            $usingAnchor,
            $usingAnchor + "`r`nusing AI_Assistant.AgentV2;"
        )
}

# ------------------------------------------------------------
# 2. FIELD
# ------------------------------------------------------------

$fieldPattern =
    'private\s+readonly\s+TempCapabilityManager\s+tempCapabilities\s*;'

$fieldMatch =
    [regex]::Match(
        $content,
        $fieldPattern
    )

if (-not $fieldMatch.Success) {
    throw "Nisam nasao tempCapabilities field."
}

$fieldReplacement =
    $fieldMatch.Value +
    "`r`n`r`n        private readonly AgentOrchestratorV2 agentV2;"

$content =
    $content.Substring(0, $fieldMatch.Index) +
    $fieldReplacement +
    $content.Substring($fieldMatch.Index + $fieldMatch.Length)

# ------------------------------------------------------------
# 3. CONSTRUCTOR INITIALIZATION
# ------------------------------------------------------------

$constructorPattern =
    '(?s)(tempCapabilities\s*=\s*new\s+TempCapabilityManager\s*\(\s*sourceRoot\s*,\s*unityTools\s*\)\s*;)'

$constructorMatch =
    [regex]::Match(
        $content,
        $constructorPattern
    )

if (-not $constructorMatch.Success) {
    throw "Nisam nasao TempCapabilityManager constructor block."
}

$constructorReplacement =
    $constructorMatch.Groups[1].Value +
    "`r`n`r`n`r`n            agentV2 =`r`n                new AgentOrchestratorV2(`r`n                    unityTools,`r`n                    tempCapabilities,`r`n                    ReportActivity`r`n                );"

$content =
    $content.Substring(0, $constructorMatch.Index) +
    $constructorReplacement +
    $content.Substring($constructorMatch.Index + $constructorMatch.Length)

# ------------------------------------------------------------
# 4. RESET V2 TASK STATE WITH CHAT CONTEXT
# ------------------------------------------------------------

$resetPattern =
    '(public\s+void\s+ResetConversationContext\s*\(\s*\)\s*\{)'

$resetMatch =
    [regex]::Match(
        $content,
        $resetPattern
    )

if (-not $resetMatch.Success) {
    throw "Nisam nasao ResetConversationContext()."
}

$resetReplacement =
    $resetMatch.Groups[1].Value +
    "`r`n            agentV2.Reset();"

$content =
    $content.Substring(0, $resetMatch.Index) +
    $resetReplacement +
    $content.Substring($resetMatch.Index + $resetMatch.Length)

# ------------------------------------------------------------
# 5. ROUTE UNITY ACTION TASKS TO V2 BEFORE OLD TOOL LOOP
# ------------------------------------------------------------

$routeMarker =
    "            object[] toolDefinitions ="

$routeIndex =
    $content.IndexOf(
        $routeMarker,
        [StringComparison]::Ordinal
    )

if ($routeIndex -lt 0) {
    # Formatting-tolerant fallback.
    $routeRegex =
        [regex]::Match(
            $content,
            'object\[\]\s+toolDefinitions\s*='
        )

    if (-not $routeRegex.Success) {
        throw "Nisam nasao toolDefinitions marker u Ask()."
    }

    $routeIndex = $routeRegex.Index
}

$routeBlock = @'
            // ============================================================
            // AGENT V2
            //
            // Unity action tasks use deterministic orchestration:
            // compact context -> one implementation call -> local execute
            // -> one verify -> optional single compile repair.
            //
            // The old tool-calling loop remains below as a fallback for
            // other domains and can be restored for Unity by setting
            // AI_AGENT_V2=0.
            // ============================================================

            if (
                agentV2.ShouldHandle(
                    effectivePrompt
                )
            )
            {
                string v2Reply =
                    await agentV2.HandleAsync(
                        effectivePrompt
                    );

                conversationHistory.Add(
                    new ChatMessage(
                        "assistant",
                        v2Reply
                    )
                );

                answer = v2Reply;

                return v2Reply;
            }


'@

$content =
    $content.Insert(
        $routeIndex,
        $routeBlock
    )

# ------------------------------------------------------------
# WRITE + VERIFY
# ------------------------------------------------------------

[System.IO.File]::WriteAllText(
    $resolvedPath,
    $content,
    [System.Text.UTF8Encoding]::new($true)
)

$verify = [System.IO.File]::ReadAllText($resolvedPath)

$required = @(
    "using AI_Assistant.AgentV2;",
    "private readonly AgentOrchestratorV2 agentV2;",
    "new AgentOrchestratorV2(",
    "agentV2.Reset();",
    "agentV2.ShouldHandle(",
    "await agentV2.HandleAsync("
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
    Write-Host "" 
    Write-Host "Installer verification failed." -ForegroundColor Red
    Write-Host "Backup: $backupPath"
    exit 4
}

Write-Host ""
Write-Host "[OK] Agent V2 je instaliran." -ForegroundColor Green
Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Agent V2 arhitektura:" -ForegroundColor Cyan
Write-Host "  compact Unity snapshot"
Write-Host "  -> 1 AI implementation call"
Write-Host "  -> deterministic local Unity execution"
Write-Host "  -> 1 final verification"
Write-Host "  -> max 1 compile repair call"
Write-Host ""
Write-Host "Gemini/Groq vise ne dobijaju Unity tool schemas ni tool-call history u V2."
Write-Host "Ako Gemini dobije 429/503, isti task sticky prelazi na Groq."
Write-Host ""
Write-Host "Za privremeni povratak na stari Unity loop postavi AI_AGENT_V2=0."
Write-Host ""
Write-Host "Sada uradi Rebuild Solution i restartuj AI Assistant."
