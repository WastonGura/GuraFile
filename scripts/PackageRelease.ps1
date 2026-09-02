[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.3.1'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src\GuraFile\GuraFile.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath
$projectVersion = [string]$project.Project.PropertyGroup.Version
if ($projectVersion -ne $Version) {
    throw "Requested version $Version does not match project version $projectVersion."
}

$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$packageName = "GuraFile-v$Version-win-x64"
$packageDirectory = Join-Path $artifactRoot $packageName
$zipPath = Join-Path $artifactRoot "$packageName.zip"
$checksumPath = "$zipPath.sha256"
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

foreach ($target in @($packageDirectory, $zipPath, $checksumPath)) {
    $resolvedTarget = [System.IO.Path]::GetFullPath($target)
    $artifactPrefix = [System.IO.Path]::GetFullPath($artifactRoot) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTarget.StartsWith($artifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected artifact target: $resolvedTarget"
    }

    if (Test-Path -LiteralPath $resolvedTarget) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}

dotnet clean $projectPath --configuration Release --runtime win-x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build $projectPath `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    -p:Version=$Version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$buildDirectory = Join-Path $repositoryRoot 'src\GuraFile\bin\Release\net10.0-windows10.0.26100.0\win-x64'
$requiredResources = @('App.xbf', 'MainWindow.xbf', 'GuraFile.pri')
foreach ($resource in $requiredResources) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildDirectory $resource) -PathType Leaf)) {
        throw "Required WinUI resource is missing from the build: $resource"
    }
}

New-Item -ItemType Directory -Path $packageDirectory | Out-Null
Copy-Item -Path (Join-Path $buildDirectory '*') -Destination $packageDirectory -Recurse

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'CHANGELOG.md') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'THIRD_PARTY_NOTICES.md') -Destination $packageDirectory

$licenseDirectory = Join-Path $packageDirectory 'licenses'
New-Item -ItemType Directory -Path $licenseDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'licenses\MIT.txt') -Destination $licenseDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'licenses\Apache-2.0.txt') -Destination $licenseDirectory

$dotnetRoot = Split-Path (Get-Command dotnet).Source
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $licenseDirectory 'DotNet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $licenseDirectory 'DotNet-ThirdPartyNotices.txt')

$nugetRoot = if ($env:NUGET_PACKAGES) {
    [System.IO.Path]::GetFullPath($env:NUGET_PACKAGES)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $env:USERPROFILE '.nuget\packages'))
}
$packageLicenses = @{
    (Join-Path $nugetRoot 'microsoft.windowsappsdk\2.4.0\license.txt') = 'WindowsAppSDK-LICENSE.txt'
    (Join-Path $nugetRoot 'microsoft.windowsappsdk\2.4.0\NOTICE.txt') = 'WindowsAppSDK-NOTICE.txt'
    (Join-Path $nugetRoot 'microsoft.web.webview2\1.0.3719.77\LICENSE.txt') = 'WebView2-LICENSE.txt'
}
foreach ($source in $packageLicenses.Keys) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required package license is missing: $source"
    }

    Copy-Item -LiteralPath $source -Destination (Join-Path $licenseDirectory $packageLicenses[$source])
}

Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$hash  $([System.IO.Path]::GetFileName($zipPath))" -Encoding ascii

Write-Host "Package: $zipPath"
Write-Host "SHA-256: $hash"
