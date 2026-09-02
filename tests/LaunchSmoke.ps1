[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [ValidateRange(1, 60)]
    [int]$TimeoutSeconds = 15,

    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
if (-not ('GuraFile.Tests.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

namespace GuraFile.Tests
{
    public static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr windowHandle);

        public static IntPtr FindWindowByTitle(int processId, string expectedTitle)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((hWnd, lParam) =>
            {
                uint pid = 0;
                GetWindowThreadProcessId(hWnd, out pid);
                if (pid == (uint)processId)
                {
                    StringBuilder sb = new StringBuilder(256);
                    GetWindowTextW(hWnd, sb, 256);
                    if (string.Equals(sb.ToString(), expectedTitle, StringComparison.Ordinal))
                    {
                        found = hWnd;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }
}
'@
}

$targetFramework = 'net10.0-windows10.0.26100.0'
$executable = if ($ExecutablePath) {
    [System.IO.Path]::GetFullPath($ExecutablePath)
} else {
    [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "..\src\GuraFile\bin\$Configuration\$targetFramework\$RuntimeIdentifier\GuraFile.exe"))
}

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "GuraFile executable not found: $executable. Build configuration '$Configuration' for '$RuntimeIdentifier' first."
}

$process = $null
try {
    $process = Start-Process -FilePath $executable -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)

    do {
        Start-Sleep -Milliseconds 100
        $process.Refresh()

        if ($process.HasExited) {
            throw "GuraFile exited before its window appeared. Exit code: $($process.ExitCode)."
        }

        $handle = $process.MainWindowHandle
        $title = $process.MainWindowTitle
        $visible = $handle -ne [IntPtr]::Zero -and [GuraFile.Tests.NativeMethods]::IsWindowVisible($handle)

        if (-not ($handle -ne [IntPtr]::Zero -and $title -eq 'GuraFile' -and $visible)) {
            $titledHandle = [GuraFile.Tests.NativeMethods]::FindWindowByTitle($process.Id, 'GuraFile')
            if ($titledHandle -ne [IntPtr]::Zero) {
                $handle = $titledHandle
                $title = 'GuraFile'
                $visible = [GuraFile.Tests.NativeMethods]::IsWindowVisible($handle)
            }
        }

        if ($handle -ne [IntPtr]::Zero -and $title -eq 'GuraFile' -and $process.Responding -and $visible) {
            Write-Host "Launch smoke passed: GuraFile window is visible and responding."
            return
        }
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out after $TimeoutSeconds seconds waiting for a visible, responding window titled 'GuraFile'. Handle: $handle. Last title: '$title'. Responding: $($process.Responding). Visible: $visible."
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
