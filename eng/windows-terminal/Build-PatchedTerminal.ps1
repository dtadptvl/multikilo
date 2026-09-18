param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$terminalCommit = "9ae724aa5b080aafbeea2bbf88db630b182cc802"
$terminalTag = "v1.25.622.0"
$sourceDir = Join-Path $repoRoot "artifacts\windows-terminal-src"
$outputDir = Join-Path $repoRoot "artifacts\terminal\win-x64"
$patchPath = Join-Path $PSScriptRoot "native-terminal.patch"

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe was not found. Visual Studio 2022 Build Tools with C++ Desktop workload is required."
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
    Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild.exe was not found."
}

if (-not (Test-Path (Join-Path $sourceDir ".git"))) {
    New-Item -ItemType Directory -Force -Path (Split-Path $sourceDir) | Out-Null
    git clone --filter=blob:none --no-checkout https://github.com/microsoft/terminal.git $sourceDir
}

Push-Location $sourceDir
try {
    git fetch --depth 1 origin $terminalCommit
    git checkout --force $terminalCommit
    git clean -xfd

    git apply --check $patchPath
    git apply $patchPath

    $project = Join-Path $sourceDir "src\cascadia\TerminalControl\dll\TerminalControl.vcxproj"
    & $msbuild $project /restore /m /p:Configuration=$Configuration /p:Platform=x64 "/p:OpenConsoleDir=$sourceDir\" "/p:SolutionDir=$sourceDir\"
    if ($LASTEXITCODE -ne 0) {
        throw "Patched Windows Terminal native control build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$dll = Get-ChildItem (Join-Path $sourceDir "bin") -Recurse -Filter "Microsoft.Terminal.Control.dll" -File |
    Where-Object { $_.FullName -match "\\x64\\$Configuration\\" } |
    Select-Object -First 1

if (-not $dll) {
    throw "Built Microsoft.Terminal.Control.dll was not found."
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
Copy-Item $dll.FullName (Join-Path $outputDir "Microsoft.Terminal.Control.dll") -Force

Write-Host "Built patched Windows Terminal control."
Write-Host "Source: $terminalTag ($terminalCommit)"
Write-Host "DLL: $(Join-Path $outputDir 'Microsoft.Terminal.Control.dll')"
