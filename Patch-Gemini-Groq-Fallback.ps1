param(
    [string]$Path = ".\AI Assistant\AI\AIIntegration.cs"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Host "AIIntegration.cs nije pronadjen: $Path" -ForegroundColor Red
    exit 1
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$backupPath = "$resolvedPath.provider-fallback-backup"
$content = [System.IO.File]::ReadAllText($resolvedPath)

if ($content.Contains("SendWithProviderFallback(")) {
    Write-Host "[OK] Provider fallback je vec instaliran." -ForegroundColor Green
    exit 0
}

if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $resolvedPath -Destination $backupPath
}

$oldCallPattern = '(?s)client\s*\.DefaultRequestHeaders\s*\.Authorization\s*=\s*new AuthenticationHeaderValue\s*\(\s*"Bearer",\s*apiKey\s*\);\s*using HttpResponseMessage response\s*=\s*await SendWithRateLimitRetry\s*\(\s*"https://generativelanguage\.googleapis\.com/v1beta/openai/chat/completions",\s*requestJson\s*\);'

$newCall = @'
using HttpResponseMessage response =
                    await SendWithProviderFallback(
                        apiKey,
                        requestJson
                    );
'@

$updated = [regex]::Replace($content, $oldCallPattern, $newCall, 1)

if ($updated -eq $content) {
    Write-Host "[FAIL] Nisam nasao Gemini send blok. Fajl nije mijenjan." -ForegroundColor Red
    exit 2
}

$insertMarker = "        // ============================================================`r`n        // TEMP CAPABILITY TOOL"
if (-not $updated.Contains($insertMarker)) {
    $insertMarker = "        // ============================================================`n        // TEMP CAPABILITY TOOL"
}

if (-not $updated.Contains($insertMarker)) {
    Write-Host "[FAIL] Nisam nasao siguran marker za helper metode. Fajl nije mijenjan." -ForegroundColor Red
    exit 3
}

$helpers = @'
        // ============================================================
        // PROVIDER FALLBACK
        // ============================================================

        private async Task<HttpResponseMessage> SendWithProviderFallback(
            string geminiApiKey,
            string requestJson
        )
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    geminiApiKey
                );

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
                Environment.GetEnvironmentVariable(
                    "GROQ_API_KEY"
                );

            if (string.IsNullOrWhiteSpace(groqApiKey))
            {
                ReportActivity(
                    "[FALLBACK] Gemini limit/unavailable, ali GROQ_API_KEY nije postavljen."
                );

                return geminiResponse;
            }

            ReportActivity(
                "[FALLBACK] Gemini je vratio " +
                (int)geminiResponse.StatusCode +
                ". Prebacujem ovaj AI ciklus na Groq GPT-OSS-120B."
            );

            geminiResponse.Dispose();

            string groqRequestJson =
                ReplaceRequestModel(
                    requestJson,
                    "openai/gpt-oss-120b"
                );

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    groqApiKey
                );

            return
                await SendWithRateLimitRetry(
                    "https://api.groq.com/openai/v1/chat/completions",
                    groqRequestJson
                );
        }


        private static string ReplaceRequestModel(
            string requestJson,
            string model
        )
        {
            using JsonDocument document =
                JsonDocument.Parse(
                    requestJson
                );

            Dictionary<string, object?> body =
                JsonSerializer.Deserialize<Dictionary<string, object?>>(
                    requestJson
                )
                ?? new Dictionary<string, object?>();

            body["model"] = model;

            return
                JsonSerializer.Serialize(
                    body
                );
        }


'@

$updated = $updated.Replace($insertMarker, $helpers + $insertMarker)

[System.IO.File]::WriteAllText(
    $resolvedPath,
    $updated,
    [System.Text.UTF8Encoding]::new($true)
)

$verify = [System.IO.File]::ReadAllText($resolvedPath)

$required = @(
    "SendWithProviderFallback(",
    '"GROQ_API_KEY"',
    '"openai/gpt-oss-120b"',
    'https://api.groq.com/openai/v1/chat/completions',
    "ReplaceRequestModel("
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
    exit 4
}

Write-Host "[OK] Gemini -> Groq fallback dodat u AIIntegration.cs" -ForegroundColor Green
Write-Host "[OK] Gemini ostaje glavni provider" -ForegroundColor Green
Write-Host "[OK] Groq se koristi samo nakon Gemini 429/503" -ForegroundColor Green
Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Bitno: GROQ_API_KEY mora i dalje biti postavljen u User environment variables." -ForegroundColor Cyan
Write-Host "Zatim Rebuild Solution i restartuj AI Assistant."
