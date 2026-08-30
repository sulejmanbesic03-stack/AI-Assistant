param(
    [string]$Path = ".\AI Assistant\AI\AIIntegration.cs"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "[FAIL] AIIntegration.cs nije pronadjen: $Path" -ForegroundColor Red
    exit 1
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$backupPath = "$resolvedPath.sticky-fast-backup"
$content = [System.IO.File]::ReadAllText($resolvedPath)

if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $resolvedPath -Destination $backupPath
}

function Replace-ConstValue {
    param([string]$Text, [string]$ConstName, [int]$NewValue)
    $pattern = "(?s)(private\s+const\s+int\s+" + [regex]::Escape($ConstName) + "\s*=\s*)\d+(\s*;)"
    return [regex]::Replace($Text, $pattern, ('${1}' + $NewValue + '${2}'), 1)
}

# ------------------------------------------------------------
# FAST MODE
# ------------------------------------------------------------
$content = Replace-ConstValue $content "MaxIterations" 8
$content = Replace-ConstValue $content "MaxToolResultChars" 3000
$content = Replace-ConstValue $content "MaxUnityScriptReadResultChars" 10000
$content = Replace-ConstValue $content "MaxChatHistoryMessages" 2
$content = Replace-ConstValue $content "MaxToolCyclesInContext" 3
$content = Replace-ConstValue $content "MaxRoutingContextChars" 2600
$content = Replace-ConstValue $content "MaxRoutingSegmentChars" 700

# Remove the large v1 rules block if installed.
$rulesPattern = '(?s)UNITY ENGINEERING RULES v1:.*?(?=TEMPORARY CAPABILITIES:)'
$content = [regex]::Replace($content, $rulesPattern, '', 1)

# Add compact rules once.
if (-not $content.Contains("UNITY FAST RULES v2:")) {
    $marker = "TEMPORARY CAPABILITIES:"
    if (-not $content.Contains($marker)) {
        throw "TEMPORARY CAPABILITIES marker nije pronadjen."
    }

    $fastRules = @'
UNITY FAST RULES v2:

For gameplay fixes, use: INSPECT ONCE -> FIX -> COMPILE -> VERIFY ONCE.
Do not keep inspecting the same unchanged state.
Prefer repairing the existing script/system instead of creating duplicate controllers or AI scripts.
Before adding a Collider, Rigidbody, CharacterController or controller script, check whether one already exists.
For Rigidbody players, keep camera look independent from collision rotation; freeze unwanted body rotation axes and never let collision torque drive camera pitch.
Do not allow multiple scripts to own the same player movement or camera rotation.
For Enemy AI, use explicit Patrol/Chase/Attack/Return transitions. If NavMeshAgent.isStopped becomes true, there must be an explicit condition that sets it false again.
Compilation is required, then perform one targeted scene/runtime verification. Do not perform repeated verification loops unless an error is found.
Use Unity documentation only when an API is uncertain or version-sensitive.
Do not narrate plans; execute the smallest correct fix.

'@
    $content = $content.Replace($marker, $fastRules + $marker)
}

# ------------------------------------------------------------
# STICKY PROVIDER FALLBACK
# Once Gemini falls back to Groq inside Ask(), remain on Groq until
# that Ask() finishes. This prevents Groq tool calls without Gemini
# thought_signature from being sent back to Gemini.
# ------------------------------------------------------------

if (-not $content.Contains("private bool groqFallbackActive;")) {
    $fieldMarker = "        private string pendingAmbiguousPrompt;"
    if (-not $content.Contains($fieldMarker)) {
        throw "pendingAmbiguousPrompt field marker nije pronadjen."
    }
    $content = $content.Replace(
        $fieldMarker,
        $fieldMarker + "`r`n`r`n        private bool groqFallbackActive;"
    )
}

# Reset provider state at the beginning of every Ask request.
$askMarkerPattern = '(public\s+async\s+Task<string>\s+Ask\s*\(\s*string\s+prompt\s*\)\s*\{\s*)(requestSequence\+\+;)'
if ([regex]::IsMatch($content, $askMarkerPattern)) {
    $content = [regex]::Replace(
        $content,
        $askMarkerPattern,
        '${1}groqFallbackActive = false;' + "`r`n`r`n            " + '${2}',
        1
    )
}

# Patch SendWithProviderFallback if present.
if ($content.Contains("SendWithProviderFallback(")) {
    $methodPattern = '(?s)private\s+async\s+Task<HttpResponseMessage>\s+SendWithProviderFallback\s*\(\s*string\s+geminiApiKey,\s*string\s+requestJson\s*\)\s*\{.*?\n\s*\}\s*\n\s*\n\s*private\s+static\s+string\s+ReplaceRequestModel'

    $replacement = @'
private async Task<HttpResponseMessage> SendWithProviderFallback(
            string geminiApiKey,
            string requestJson
        )
        {
            if (groqFallbackActive)
            {
                string? stickyGroqKey =
                    Environment.GetEnvironmentVariable("GROQ_API_KEY");

                if (string.IsNullOrWhiteSpace(stickyGroqKey))
                {
                    throw new InvalidOperationException(
                        "Groq fallback is active but GROQ_API_KEY is missing."
                    );
                }

                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", stickyGroqKey);

                string stickyGroqRequest =
                    ReplaceRequestModel(requestJson, "openai/gpt-oss-120b");

                ReportActivity("[PROVIDER] Groq sticky fallback active for this task.");

                return await SendWithRateLimitRetry(
                    "https://api.groq.com/openai/v1/chat/completions",
                    stickyGroqRequest
                );
            }

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", geminiApiKey);

            HttpResponseMessage geminiResponse =
                await SendWithRateLimitRetry(
                    "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions",
                    requestJson
                );

            bool shouldFallback =
                geminiResponse.StatusCode == HttpStatusCode.TooManyRequests ||
                geminiResponse.StatusCode == HttpStatusCode.ServiceUnavailable;

            if (!shouldFallback)
            {
                return geminiResponse;
            }

            string? groqApiKey =
                Environment.GetEnvironmentVariable("GROQ_API_KEY");

            if (string.IsNullOrWhiteSpace(groqApiKey))
            {
                ReportActivity(
                    "[FALLBACK] Gemini limit/unavailable, ali GROQ_API_KEY nije postavljen."
                );
                return geminiResponse;
            }

            groqFallbackActive = true;

            ReportActivity(
                "[FALLBACK] Gemini je vratio " +
                (int)geminiResponse.StatusCode +
                ". Groq ostaje aktivan do kraja ovog taska."
            );

            geminiResponse.Dispose();

            string groqRequestJson =
                ReplaceRequestModel(requestJson, "openai/gpt-oss-120b");

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", groqApiKey);

            return await SendWithRateLimitRetry(
                "https://api.groq.com/openai/v1/chat/completions",
                groqRequestJson
            );
        }


        private static string ReplaceRequestModel
'@

    $newContent = [regex]::Replace($content, $methodPattern, $replacement, 1)
    if ($newContent -eq $content) {
        Write-Host "[WARN] Sticky fallback metoda nije automatski zamijenjena; provjeri da li je provider fallback patch instaliran." -ForegroundColor Yellow
    }
    else {
        $content = $newContent
    }
}
else {
    Write-Host "[WARN] SendWithProviderFallback nije pronadjen. Fast mode ce biti instaliran, ali sticky fallback ne." -ForegroundColor Yellow
}

[System.IO.File]::WriteAllText(
    $resolvedPath,
    $content,
    [System.Text.UTF8Encoding]::new($true)
)

$verify = [System.IO.File]::ReadAllText($resolvedPath)

$failed = $false
$checks = @(
    "UNITY FAST RULES v2:",
    "private bool groqFallbackActive;",
    "groqFallbackActive = false;"
)

foreach ($check in $checks) {
    if ($verify.Contains($check)) {
        Write-Host "[OK] $check" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $check" -ForegroundColor Red
        $failed = $true
    }
}

if ($verify.Contains("SendWithProviderFallback(")) {
    if ($verify.Contains("Groq ostaje aktivan do kraja ovog taska.")) {
        Write-Host "[OK] Sticky Gemini -> Groq fallback" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] Sticky fallback nije potvrden" -ForegroundColor Red
        $failed = $true
    }
}

$numeric = @{
    MaxIterations = 8
    MaxToolResultChars = 3000
    MaxUnityScriptReadResultChars = 10000
    MaxChatHistoryMessages = 2
    MaxToolCyclesInContext = 3
}

foreach ($name in $numeric.Keys) {
    $expected = $numeric[$name]
    $pattern = "(?s)private\s+const\s+int\s+" + [regex]::Escape($name) + "\s*=\s*" + $expected + "\s*;"
    if ([regex]::IsMatch($verify, $pattern)) {
        Write-Host "[OK] $name = $expected" -ForegroundColor Green
    }
    else {
        Write-Host "[FAIL] $name nije $expected" -ForegroundColor Red
        $failed = $true
    }
}

Write-Host ""
Write-Host "Backup: $backupPath" -ForegroundColor DarkGray

if ($failed) {
    Write-Host "Patch nije prosao sve provjere. Posalji ovaj output prije rebuilda." -ForegroundColor Red
    exit 4
}

Write-Host "[OK] Sticky fallback + FAST Unity mode instalirani." -ForegroundColor Green
Write-Host "Zatim Rebuild Solution i restartuj AI Assistant." -ForegroundColor Cyan
