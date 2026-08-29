param(
    [string]$Path = ".\AI Assistant\AI\AIIntegration.cs"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    throw "AIIntegration.cs not found: $Path"
}

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$content = [System.IO.File]::ReadAllText($resolvedPath)
$backupPath = "$resolvedPath.unity-docs-backup"

if (-not (Test-Path -LiteralPath $backupPath)) {
    Copy-Item -LiteralPath $resolvedPath -Destination $backupPath
}

function Replace-ExactOnce {
    param(
        [string]$Source,
        [string]$Old,
        [string]$New,
        [string]$Label
    )

    $first = $Source.IndexOf($Old, [StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "Patch marker not found: $Label"
    }

    $second = $Source.IndexOf($Old, $first + $Old.Length, [StringComparison]::Ordinal)
    if ($second -ge 0) {
        throw "Patch marker is not unique: $Label"
    }

    return $Source.Substring(0, $first) + $New + $Source.Substring($first + $Old.Length)
}

if ($content.Contains("private readonly UnityDocumentationTools unityDocsTools;")) {
    Write-Host "Unity documentation tools already appear to be registered." -ForegroundColor Yellow
    exit 0
}

$content = Replace-ExactOnce $content @'
        private readonly UnityBridgeTools unityTools;

        private readonly TempCapabilityManager tempCapabilities;
'@ @'
        private readonly UnityBridgeTools unityTools;

        private readonly UnityDocumentationTools unityDocsTools;

        private readonly TempCapabilityManager tempCapabilities;
'@ "field registration"

$content = Replace-ExactOnce $content @'
            unityTools =
                new UnityBridgeTools();


            tempCapabilities =
'@ @'
            unityTools =
                new UnityBridgeTools();


            unityDocsTools =
                new UnityDocumentationTools();


            tempCapabilities =
'@ "constructor registration"

$content = Replace-ExactOnce $content @'
        private void AddUnityToolsForTask(
            List<object> tools,
            string text
        )
        {
            bool complexTask =
'@ @'
        private void AddUnityToolsForTask(
            List<object> tools,
            string text
        )
        {
            // Official Unity documentation is available for every Unity task.
            // The model should use it only when API/package/version behavior is uncertain.
            tools.Add(
                OneStringTool(
                    "search_unity_docs",
                    "Searches only official docs.unity3d.com documentation. Use concise Unity API, package or workflow keywords.",
                    "query"
                )
            );

            tools.Add(
                OneStringTool(
                    "read_unity_doc",
                    "Reads one official https://docs.unity3d.com/... documentation page and returns cleaned text.",
                    "url"
                )
            );


            bool complexTask =
'@ "Unity tool definitions"

$content = Replace-ExactOnce $content @'
                // ====================================================
                // UNITY READ
                // ====================================================

                if (
                    functionName ==
                    "get_active_scene"
'@ @'
                // ====================================================
                // UNITY DOCUMENTATION
                // ====================================================

                if (
                    functionName ==
                    "search_unity_docs"
                )
                {
                    return
                        unityDocsTools.SearchUnityDocs(
                            GetStringArg(
                                args,
                                "query"
                            )
                        );
                }


                if (
                    functionName ==
                    "read_unity_doc"
                )
                {
                    return
                        unityDocsTools.ReadUnityDoc(
                            GetStringArg(
                                args,
                                "url"
                            )
                        );
                }


                // ====================================================
                // UNITY READ
                // ====================================================

                if (
                    functionName ==
                    "get_active_scene"
'@ "tool dispatch"

$content = Replace-ExactOnce $content @'
UNITY WORKFLOW:

For a simple Unity action, use the simple registered action tool.
'@ @'
UNITY DOCUMENTATION:

- When Unity API, package, version-specific behavior or workflow is uncertain, use search_unity_docs before generating code.
- Read only the specific official page needed with read_unity_doc.
- Prefer documentation matching the project's Unity version returned by get_unity_project_settings.
- Do not browse documentation when the current task is already fully covered by known project state and stable APIs.
- For AI Navigation/NavMesh work, verify the current supported package/API workflow before generating persistent gameplay code or editor setup.


UNITY WORKFLOW:

For a simple Unity action, use the simple registered action tool.
'@ "system prompt guidance"

[System.IO.File]::WriteAllText(
    $resolvedPath,
    $content,
    [System.Text.UTF8Encoding]::new($true)
)

$verify = [System.IO.File]::ReadAllText($resolvedPath)
$required = @(
    "private readonly UnityDocumentationTools unityDocsTools;",
    '"search_unity_docs"',
    '"read_unity_doc"',
    "unityDocsTools.SearchUnityDocs",
    "unityDocsTools.ReadUnityDoc",
    "UNITY DOCUMENTATION:"
)

foreach ($marker in $required) {
    if (-not $verify.Contains($marker)) {
        throw "Verification failed, missing: $marker"
    }
}

Write-Host "[OK] Unity documentation tools registered in AIIntegration.cs" -ForegroundColor Green
Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
Write-Host "Next: build the solution and test with a Unity documentation query." -ForegroundColor Cyan
