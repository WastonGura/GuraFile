[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [ValidateRange(1, 60)]
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
$targetFramework = 'net10.0-windows10.0.26100.0'
$executable = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot "..\src\GuraFile\bin\$Configuration\$targetFramework\$RuntimeIdentifier\GuraFile.exe"))

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "GuraFile executable not found: $executable. Build configuration '$Configuration' for '$RuntimeIdentifier' first."
}

$process = $null
try {
    $process = Start-Process -FilePath $executable -PassThru -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()

        if ($process.HasExited) {
            throw "GuraFile exited before its window appeared. Exit code: $($process.ExitCode)."
        }

        if ($process.MainWindowTitle -eq 'GuraFile' -and $process.Responding) {
            Write-Host "Launch smoke passed: GuraFile window is responding."
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out after $TimeoutSeconds seconds waiting for a responding window titled 'GuraFile'. Last title: '$($process.MainWindowTitle)'."
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            if ($process.CloseMainWindow()) {
                $null = $process.WaitForExit(5000)
                $process.Refresh()
            }
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force
                $process.WaitForExit()
            }
        }
        $process.Dispose()
    }
}
