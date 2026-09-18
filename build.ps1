$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root "src\MultiKilo\MultiKilo.csproj"
$out = Join-Path $root "dist\MultiKilo-win-x64"

if (Test-Path $out) {
    Remove-Item $out -Recurse -Force
}

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false -o $out

$bytes = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum
$megabytes = [Math]::Round($bytes / 1MB, 2)
Write-Host "Portable build: $out"
Write-Host "Size: $megabytes MB"
