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

$dumpRoot = Join-Path $env:RUNNER_TEMP "multikilo-dumps"
New-Item -ItemType Directory -Force -Path $dumpRoot | Out-Null
$dumpKey = "HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\MultiKilo.exe"
New-Item -Path $dumpKey -Force | Out-Null
New-ItemProperty -Path $dumpKey -Name DumpFolder -Value $dumpRoot -PropertyType ExpandString -Force | Out-Null
New-ItemProperty -Path $dumpKey -Name DumpType -Value 2 -PropertyType DWord -Force | Out-Null

$process = Start-Process -FilePath $exe -ArgumentList "--smoke-test=$Mode" -PassThru
if (-not $process.WaitForExit(30000)) {
    $process.Kill($true)
    throw "Smoke test '$Mode' timed out after 30 seconds"
}

if ($process.ExitCode -ne 0) {
    if (Test-Path $log) {
        Get-Content $log
    }

    Write-Host "Recent Application Error / WER events:"
    Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = (Get-Date).AddMinutes(-5) } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in @("Application Error", "Windows Error Reporting") } |
        Select-Object -First 12 TimeCreated, ProviderName, Id, Message |
        Format-List

    $dumps = @(Get-ChildItem $dumpRoot -Filter "MultiKilo*.dmp" -File -ErrorAction SilentlyContinue)
    foreach ($dump in $dumps) {
        Write-Host "Crash dump: $($dump.FullName) ($($dump.Length) bytes)"
    }

    throw "Smoke test '$Mode' failed with exit code $($process.ExitCode)"
}

Write-Host "Smoke test '$Mode' passed."
