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

$vs2022 = & $vswhere -version "[17.0,18.0)" -products * -property installationPath |
    Select-Object -First 1

if ($vs2022) {
    $vsInstall = $vs2022
    $uwpComponent = "Microsoft.VisualStudio.ComponentGroup.UWP.VC"
    Write-Host "Using Visual Studio 2022 with v143 UWP tools."
}
else {
    $vsInstall = & $vswhere -latest -products * -property installationPath |
        Select-Object -First 1
    if (-not $vsInstall) {
        throw "No Visual Studio installation was found."
    }

    # VS 18 no longer installs the older UWP toolset by default. The pinned
    # Windows Terminal source still targets v143, so install only that
    # compatibility component rather than retargeting the source.
    $uwpComponent = "Microsoft.VisualStudio.ComponentGroup.UWP.VC.v143"
    Write-Host "Visual Studio 2022 is unavailable; using the latest Visual Studio with v143 UWP compatibility tools."
}

$vsWithUwp = & $vswhere -products * -requires $uwpComponent -property installationPath |
    Where-Object { $_ -eq $vsInstall } |
    Select-Object -First 1

if (-not $vsWithUwp) {
    $vsInstaller = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vs_installer.exe"
    if (-not (Test-Path $vsInstaller)) {
        throw "Visual Studio Installer was not found; cannot add the required v143 UWP C++ component."
    }

    Write-Host "Installing $uwpComponent..."
    $installerArgs = "modify --installPath `"$vsInstall`" --add $uwpComponent --quiet --norestart"
    $process = Start-Process -FilePath $vsInstaller -ArgumentList $installerArgs -PassThru -Wait
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "Visual Studio Installer failed with exit code $($process.ExitCode)."
    }
}

$msbuild = Join-Path $vsInstall "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    throw "MSBuild.exe was not found under $vsInstall."
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

    $nuget = (Get-Command "nuget.exe" -ErrorAction SilentlyContinue).Source
    if (-not $nuget) {
        $nuget = (Get-Command "nuget" -ErrorAction SilentlyContinue).Source
    }
    if (-not $nuget) {
        throw "nuget.exe was not found on PATH."
    }

    $nugetConfig = Join-Path $sourceDir "NuGet.config"
    $packagesDir = Join-Path $sourceDir "packages"

    # Match the upstream Windows Terminal restore pipeline. Native projects
    # still consume packages.config dependencies from the repository-local
    # packages directory, so MSBuild /restore alone is insufficient.
    Invoke-Native $nuget @(
        "restore",
        (Join-Path $sourceDir "build\packages.config"),
        "-PackagesDirectory", $packagesDir,
        "-ConfigFile", $nugetConfig,
        "-NonInteractive"
    )

    $solution = Join-Path $sourceDir "OpenConsole.slnx"
    Invoke-Native $msbuild @(
        $solution,
        "/t:Restore",
        "/m",
        "/p:Configuration=$Configuration",
        "/p:Platform=x64",
        "/p:WindowsTargetPlatformVersion=$sdkVersion"
    )

    Invoke-Native $nuget @(
        "restore",
        (Join-Path $sourceDir "dep\nuget\packages.config"),
        "-PackagesDirectory", $packagesDir,
        "-ConfigFile", $nugetConfig,
        "-NonInteractive"
    )

    Invoke-Native $msbuild @(
        $solution,
        "/t:Terminal\Control\TerminalControl",
        "/m",
        "/p:Configuration=$Configuration",
        "/p:Platform=x64",
        "/p:GenerateAppxPackageOnBuild=false",
        "/p:WindowsTargetPlatformVersion=$sdkVersion"
    )
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

$connectionDll = Get-ChildItem (Join-Path $sourceDir "bin") -Recurse -Filter "TerminalConnection.dll" -File |
    Where-Object { $_.FullName -match "\\x64\\$Configuration\\" } |
    Select-Object -First 1
if (-not $connectionDll) {
    throw "Built TerminalConnection.dll was not found."
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
Copy-Item $dll.FullName (Join-Path $outputDir "Microsoft.Terminal.Control.dll") -Force
Copy-Item $connectionDll.FullName (Join-Path $outputDir "TerminalConnection.dll") -Force

Write-Host "Built patched Windows Terminal control and upstream TerminalConnection."
Write-Host "Source: $terminalTag ($terminalCommit)"
Write-Host "Windows SDK: $sdkVersion"
Write-Host "Control DLL: $(Join-Path $outputDir 'Microsoft.Terminal.Control.dll')"
Write-Host "Connection DLL: $(Join-Path $outputDir 'TerminalConnection.dll')"
