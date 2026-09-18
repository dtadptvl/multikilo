# MultiKilo

Minimal Windows-only project terminal manager.

Each project owns one live Windows Terminal WPF control + ConPTY + PowerShell + Kilo session. Switching projects only changes which already-running terminal control is visible.

## Requirements

- Windows 10 1809+ / Windows 11 x64
- pwsh.exe on PATH
- kilo on PATH

## Run from source

dotnet run --project .\src\MultiKilo\MultiKilo.csproj

## Portable build

.\build.ps1

Output: dist\MultiKilo-win-x64\MultiKilo.exe

The publish is self-contained and untrimmed. That is intentionally larger than a framework-dependent build because WPF plus the native Windows Terminal control are reliability-sensitive and the requested folder must be copy-and-run without installing a .NET desktop runtime.

## Architecture

- C# / .NET 8 / WPF
- Windows Terminal WPF control through EasyWindowsTerminalControl, backed by Microsoft.Terminal.Wpf
- Windows ConPTY
- one Win32 Job Object per live project session
- pwsh launches kilo directly; the PowerShell process lifetime follows Kilo
- native Windows tray through NotifyIcon
- native WPF OpenFolderDialog
- portable projects.json next to the executable

Runtime state is never persisted.

## Verification

CI builds, runs a three-session ConPTY/terminal smoke test, and publishes the portable x64 folder. IME composition and physical clipboard paste require an interactive Windows desktop; the exact checks are in docs/TESTING.md.
