param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

function Invoke-Native([string]$FilePath, [string[]]$Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$terminalCommit = "9ae724aa5b080aafbeea2bbf88db630b182cc802"
$terminalTag = "v1.25.622.0"
$sourceDir = Join-Path $repoRoot "artifacts\windows-terminal-src"
$outputDir = Join-Path $repoRoot "artifacts\terminal\win-x64"
$applyPatch = Join-Path $PSScriptRoot "Apply-Patch.ps1"

$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe was not found. Visual Studio with the C++ Desktop workload is required."
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" |
    Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild.exe was not found."
}

$sdkRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\Lib"
$sdk = Get-ChildItem $sdkRoot -Directory |
    Where-Object {
        $_.Name -match "^\d+\.\d+\.\d+\.\d+$" -and
        (Test-Path (Join-Path $_.FullName "um\x64"))
    } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $sdk) {
    throw "No Windows 10/11 SDK was found."
}
$sdkVersion = $sdk.Name

if (-not (Test-Path (Join-Path $sourceDir ".git"))) {
    New-Item -ItemType Directory -Force -Path (Split-Path $sourceDir) | Out-Null
    Invoke-Native "git" @("clone", "--filter=blob:none", "--no-checkout", "https://github.com/microsoft/terminal.git", $sourceDir)
}

Push-Location $sourceDir
try {
    Invoke-Native "git" @("fetch", "--depth", "1", "origin", $terminalCommit)
    Invoke-Native "git" @("checkout", "--force", $terminalCommit)
    Invoke-Native "git" @("clean", "-xfd")

    & $applyPatch -SourceRoot $sourceDir

    $project = Join-Path $sourceDir "src\cascadia\TerminalControl\dll\TerminalControl.vcxproj"
    $arguments = @(
        $project,
        "/restore",
        "/m",
        "/p:Configuration=$Configuration",
        "/p:Platform=x64",
        "/p:WindowsTargetPlatformVersion=$sdkVersion",
        "/p:OpenConsoleDir=$sourceDir\",
        "/p:SolutionDir=$sourceDir\"
    )
    Invoke-Native $msbuild $arguments
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
Write-Host "Windows SDK: $sdkVersion"
Write-Host "DLL: $(Join-Path $outputDir 'Microsoft.Terminal.Control.dll')"
