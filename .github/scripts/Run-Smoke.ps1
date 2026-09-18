param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("clipboard", "paste", "sessions", "all")]
    [string]$Mode
)

$ErrorActionPreference = "Stop"

$exe = ".\src\MultiKilo\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\MultiKilo.exe"
if (-not (Test-Path $exe)) {
    throw "Built MultiKilo.exe not found at $exe"
}

$log = Join-Path (Split-Path $exe) "smoke-error.txt"
Remove-Item $log -ErrorAction SilentlyContinue

$process = Start-Process -FilePath $exe -ArgumentList "--smoke-test=$Mode" -PassThru
if (-not $process.WaitForExit(30000)) {
    $process.Kill($true)
    throw "Smoke test '$Mode' timed out after 30 seconds"
}

if ($process.ExitCode -ne 0) {
    if (Test-Path $log) {
        Get-Content $log
    }
    throw "Smoke test '$Mode' failed with exit code $($process.ExitCode)"
}

Write-Host "Smoke test '$Mode' passed."
