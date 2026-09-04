[CmdletBinding()]
param(
    [ValidateRange(3, 20)]
    [int]$Runs = 3,

    [ValidateRange(10, 120)]
    [int]$TimeoutSeconds = 70
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceAssets = Join-Path $repositoryRoot 'src\GuraFile\Assets\graph'
$buildAssets = Join-Path $repositoryRoot 'src\GuraFile\bin\Release\net10.0-windows10.0.26100.0\win-x64\Assets\graph'
$harness = Join-Path $PSScriptRoot 'GraphRenderHarness\bin\Release\net10.0-windows10.0.26100.0\win-x64\GraphRenderHarness.exe'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$resultDirectory = Join-Path $repositoryRoot "TestResults\GraphFirstFrame\$stamp"
$results = Join-Path $resultDirectory 'results.jsonl'
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$profileRoot = [System.IO.Path]::GetFullPath((Join-Path $tempRoot "GuraFile-GraphFirstFrame-$stamp"))
$requiredAssets = 'index.html', 'cytoscape.min.js', 'graph.css', 'graph.js'

if (-not (Test-Path -LiteralPath $harness -PathType Leaf)) {
    throw "GraphRenderHarness is not built. Build GuraFile.slnx in Release first."
}

foreach ($name in $requiredAssets) {
    $sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $sourceAssets $name)).Hash
    $buildHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $buildAssets $name)).Hash
    if ($sourceHash -ne $buildHash) { throw "$name differs between source and release build output." }
    Write-Host "ASSET $name SHA256 $sourceHash MATCH"
}

New-Item -ItemType Directory -Path $resultDirectory, $profileRoot | Out-Null
try {
    for ($run = 1; $run -le $Runs; $run++) {
        $profile = Join-Path $profileRoot "run-$run"
        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $harness
        $startInfo.UseShellExecute = $false
        foreach ($argument in '--run', "$run", '--assets', $buildAssets, '--output', $results, '--profile', $profile) {
            $startInfo.ArgumentList.Add($argument)
        }

        $process = [System.Diagnostics.Process]::Start($startInfo)
        try {
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw "Graph first-frame run $run timed out after $TimeoutSeconds seconds."
            }
            Write-Host "RUN $run EXIT $($process.ExitCode) RESULT $((Get-Content -LiteralPath $results | Select-Object -Last 1))"
        }
        finally {
            if (-not $process.HasExited) {
                $process.Kill($true)
                $process.WaitForExit()
            }
            $process.Dispose()
        }
    }
}
finally {
    if (-not $profileRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
        [System.IO.Path]::GetFileName($profileRoot) -notlike 'GuraFile-GraphFirstFrame-*') {
        throw "Refusing to clean unexpected profile path: $profileRoot"
    }
    for ($attempt = 0; $attempt -lt 20 -and (Test-Path -LiteralPath $profileRoot); $attempt++) {
        try { Remove-Item -LiteralPath $profileRoot -Recurse -Force -ErrorAction Stop }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (Test-Path -LiteralPath $profileRoot) { throw "Failed to clean isolated WebView2 profiles: $profileRoot" }
}

$parsed = @(Get-Content -LiteralPath $results | ForEach-Object { $_ | ConvertFrom-Json })
if ($parsed.Count -ne $Runs) { throw "Expected $Runs results, got $($parsed.Count)." }
$failed = @($parsed | Where-Object { -not $_.success }).Count
Write-Host "RESULTS $results"
if ($failed -ne 0) { throw "$failed/$Runs graph first-frame runs failed." }
Write-Host "PASS $Runs/$Runs runs; JS and host elapsed were all under 1000 ms."
