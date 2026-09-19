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
    $recentEvents = @(Get-WinEvent -FilterHashtable @{ LogName = "Application"; StartTime = (Get-Date).AddMinutes(-5) } -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -in @("Application Error", "Windows Error Reporting") } |
        Select-Object -First 12 TimeCreated, ProviderName, Id, Message)
    $recentEvents | Format-List

    $terminalCrash = $recentEvents |
        Where-Object { $_.Message -match "Faulting application name: MultiKilo\.exe" -and $_.Message -match "Faulting module name: TerminalConnection\.dll" } |
        Select-Object -First 1

    $mapPath = ".\artifacts\terminal\win-x64\TerminalConnection.map"
    if ($terminalCrash -and (Test-Path $mapPath) -and $terminalCrash.Message -match "Fault offset:\s+0x([0-9a-fA-F]+)") {
        $faultRva = [Convert]::ToUInt64($Matches[1], 16)
        $mapLines = Get-Content $mapPath
        $preferredLine = $mapLines | Where-Object { $_ -match "Preferred load address is\s+([0-9A-Fa-f]+)" } | Select-Object -First 1

        if ($preferredLine -match "Preferred load address is\s+([0-9A-Fa-f]+)") {
            $preferredBase = [Convert]::ToUInt64($Matches[1], 16)
            $faultVa = $preferredBase + $faultRva
            $nearest = $null
            $nearestVa = [UInt64]0

            foreach ($line in $mapLines) {
                if ($line -match "^\s*[0-9A-Fa-f]+:[0-9A-Fa-f]+\s+(.+?)\s+([0-9A-Fa-f]{16})\s") {
                    $symbol = $Matches[1].Trim()
                    $symbolVa = [Convert]::ToUInt64($Matches[2], 16)
                    if ($symbolVa -le $faultVa -and $symbolVa -ge $nearestVa) {
                        $nearestVa = $symbolVa
                        $nearest = $symbol
                    }
                }
            }

            if ($nearest) {
                $delta = $faultVa - $nearestVa
                Write-Host ("TerminalConnection fault symbol: {0}+0x{1:x} (RVA 0x{2:x})" -f $nearest, $delta, $faultRva)
            }
        }
    }

    $dumps = @(Get-ChildItem $dumpRoot -Filter "MultiKilo*.dmp" -File -ErrorAction SilentlyContinue)
    foreach ($dump in $dumps) {
        Write-Host "Crash dump: $($dump.FullName) ($($dump.Length) bytes)"

        $cdb = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\Debuggers\x64" -Filter "cdb.exe" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($cdb) {
            Write-Host "Crash analysis for $($dump.Name):"
            & $cdb.FullName -z $dump.FullName -c "!analyze -v; kv; q" 2>&1 | Select-Object -First 500
        }
    }

    throw "Smoke test '$Mode' failed with exit code $($process.ExitCode)"
}

Write-Host "Smoke test '$Mode' passed."
