# MultiKilo

MultiKilo is a minimal Windows-only project terminal manager for running one persistent Kilo terminal session per project.

The GUI is intentionally only a session/project wrapper. Terminal behavior belongs to the native Windows Terminal stack, not to WPF or managed code.

## Product behavior

- Each project owns one live terminal session.
- Switching projects only changes which already-running terminal control is visible.
- Add Project uses the native folder picker.
- New project session: `pwsh -> kilo --auto`.
- Resume/restart: `pwsh -> kilo --auto --continue`.
- Restart/Terminate only affect the selected project.
- Remove confirms before terminating a live project.
- Closing the window hides to tray while sessions are running; otherwise the app exits.
- Tray menu provides Open and Quit; Quit confirms before terminating live sessions.
- Only project identity is persisted in `projects.json`: id, name, and folder. Runtime terminal/process state is never persisted.

## Requirements

- Windows 10 1809+ or Windows 11, x64.
- `pwsh.exe` on `PATH`.
- `kilo` on `PATH`.

## Native terminal stack

MultiKilo vendors and patches a pinned Windows Terminal source revision:

- Windows Terminal Preview: `v1.25.622.0`
- Commit: `9ae724aa5b080aafbeea2bbf88db630b182cc802`

The runtime terminal stack is:

```text
MultiKilo (WPF)
   |
   +-- Microsoft.Terminal.Wpf.TerminalControl
         |
         +-- HwndTerminal / TerminalCore
         |     - renderer
         |     - keyboard and Vietnamese IME
         |     - mouse, selection and scroll
         |     - clipboard
         |     - VT parsing
         |     - bracketed-paste state
         |
         +-- TerminalConnection.dll
               |
               +-- Windows Terminal ConptyConnection
               |     - WT_SESSION / WT_PROFILE_ID
               |     - 128 KiB duplex overlapped pipe
               |     - ordered async WriteInput()
               |     - overlapped output drain
               |     - resize / close lifecycle
               |
               +-- matching OpenConsole.exe
                     |
                     +-- ConPTY
                           |
                           +-- pwsh
                                 |
                                 +-- kilo
```

The WPF application owns project/session orchestration only. It must not implement terminal input semantics.

## Runtime files are a matched set

The following native files must come from the **same pinned Windows Terminal build** and must remain together in the portable output:

- `Microsoft.Terminal.Control.dll`
- `TerminalConnection.dll`
- `OpenConsole.exe`
- `OpenConsoleProxy.dll`

This is a correctness requirement, not just packaging convenience.

Windows Terminal's private winconpty implementation looks for `OpenConsole.exe` next to its module. If it is missing, it can fall back to the inbox `conhost.exe`. Mixing a newer `TerminalConnection/winconpty` with an older system console host can produce protocol mismatch, conhost fail-fast, broken terminal modes, and misleading paste/input symptoms.

Do not remove `OpenConsole.exe` from the portable build and do not replace it with the system `conhost.exe`.

## Session and process ownership

Every project session owns a native Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.

The root `pwsh` process is assigned to that Job Object atomically at process creation with `PROC_THREAD_ATTRIBUTE_JOB_LIST`. This preserves upstream `ConptyConnection` launch behavior while ensuring Kilo and descendants stay inside the selected project's process tree.

`TerminateSession()` terminates only that project's Job Object and closes its native terminal connection. Do not replace this with global process-name killing.

## Terminal ownership rules

These are architectural invariants.

**Native Windows Terminal owns:**

- keyboard input and key translation;
- Vietnamese IME/composition;
- mouse input, selection and scrolling;
- copy/paste shortcuts;
- clipboard filtering;
- bracketed paste;
- VT parsing and terminal-mode state;
- renderer and alternate screen;
- ConPTY transport;
- resize;
- session I/O ordering.

**MultiKilo/WPF owns:**

- project list;
- Start / Resume / Restart / Terminate / Remove;
- session visibility/switching;
- tray behavior;
- persistence;
- theme selection and host layout.

Do not move terminal behavior back into C#.

## Paste behavior: the important invariant

Large paste was the hardest failure mode in this project and is the easiest one to accidentally regress.

The correct path is:

```text
Windows clipboard
   |
native TerminalControl paste
   |
Windows Terminal clipboard filtering
   |
ESC[200~
<payload as one paste transaction>
ESC[201~
   |
ConptyConnection::WriteInput()
   |
ConPTY in VT input mode
   |
OpenTUI PasteEvent
   |
Kilo
```

Kilo then decides how to display the paste. In current Kilo behavior, a paste of roughly 5 or more lines (or sufficiently large text) is collapsed to a UI marker such as `[Pasted ~N lines]`.

That `>= 5 lines` behavior belongs to **Kilo**, not MultiKilo. MultiKilo must never implement its own line-count threshold or fake the `[Pasted ...]` UI.

### What not to do

Do **not** reintroduce any of the following:

- managed fake typing;
- per-character or delayed paste queues;
- chunk-and-sleep paste workarounds;
- WPF interception that converts clipboard text into key events;
- a managed VT parser;
- a custom managed ConPTY transport;
- unconditional bracket markers based on text length;
- stripping trailing newlines to hide auto-submit;
- forcing Kilo's `[Pasted ~N lines]` presentation from MultiKilo;
- replacing `ConptyConnection` with anonymous synchronous pipes.

If a large paste looks like thousands of keystrokes, the correct response is to audit native terminal mode / ConPTY / runtime dependency parity. Do not "optimize" the key-event path.

## Why earlier approaches failed

Several apparently reasonable approaches reproduced only part of Windows Terminal behavior.

### Wrapper-level Ctrl+V interception

Intercepting Ctrl+V above the native control broke ownership boundaries. It caused combinations of broken text copy/paste, lost image paste, typing regressions, and scrolling/input conflicts.

**Finding:** the terminal control must own keyboard and clipboard semantics.

### Custom ConPTY session

A custom native session using anonymous synchronous pipes could display terminal output but did not reproduce Windows Terminal's connection semantics. Large paste could fall into ordinary input handling and UI redraw/backpressure made the problem look like slow typing.

**Finding:** renderer parity is not enough; connection-layer parity matters.

### Adding `WT_SESSION` only

Injecting `WT_SESSION` / `WT_PROFILE_ID` fixed terminal detection symptoms such as short paste auto-submit, but it did not make the custom transport equivalent to Windows Terminal.

**Finding:** environment parity is necessary but not sufficient.

### Forcing bracketed-paste markers

Synthetic probes could pass when they manually enabled `?2004h`, while real Kilo still behaved differently.

**Finding:** a smoke test that creates the terminal mode it is supposed to verify can hide the real bug.

### Shipping `TerminalConnection.dll` without matching `OpenConsole.exe`

This was the final critical deployment issue. The connection library could fall back to the inbox console host, creating a version/protocol mismatch. CI showed conhost fail-fast until the matching `OpenConsole.exe` from the pinned Windows Terminal build was packaged beside it.

**Finding:** `TerminalConnection.dll + OpenConsole.exe` is one runtime unit.

## Clipboard shortcuts

Clipboard handling stays in the native terminal layer.

Expected behavior includes:

- `Ctrl+Insert` / `Ctrl+Shift+C`: copy terminal selection.
- `Ctrl+Shift+V` / `Shift+Insert`: paste text.
- Plain `Ctrl+V`: native text paste when clipboard text exists.
- Plain `Ctrl+V` with image-only clipboard: leave the input path available to Kilo rather than converting it to text behavior.

Do not add a second shortcut/input system in WPF.

## Build

From the repository root:

```powershell
.\build.ps1
```

Portable output:

```text
dist\MultiKilo-win-x64\
```

The publish is self-contained and untrimmed. The native terminal runtime is built from the pinned Windows Terminal commit and copied into the portable folder.

## Running from source

The native terminal artifacts must exist before the managed app can run.

```powershell
.\eng\windows-terminal\Build-PatchedTerminal.ps1
dotnet run --project .\src\MultiKilo\MultiKilo.csproj
```

## Verification

CI supports targeted smoke modes:

- `[smoke:clipboard]` — native copy/selection/scroll behavior.
- `[smoke:paste]` — Windows Terminal identity environment, bracketed 288-line paste, and Unicode/Vietnamese paste.
- `[smoke:sessions]` — multiple native sessions, resize, switching and isolation.
- `[smoke:all]` — all smoke modes.
- `[artifact]` — publish the portable artifact.
- `[release]` — all smoke modes plus portable publish/inspection.

Native Windows Terminal artifacts are cached by the native patch/build-script hash. Do not force a full native rebuild for managed-only changes.

### Physical acceptance for paste

CI is necessary but does not replace a real Kilo test on an interactive Windows desktop.

Before accepting a terminal/paste change, verify at minimum:

1. short text paste does not auto-submit;
2. paste of 5+ lines is recognized by Kilo as one paste event and presents like native Windows Terminal;
3. the 288-line repro pastes immediately without per-keystroke lag;
4. Vietnamese/Unicode text survives exactly;
5. image paste behavior still works;
6. typing, mouse selection and scroll remain native and responsive.

For deeper interactive checks, see `docs/TESTING.md`.

## Change checklist for terminal work

Before changing terminal/input code, answer these questions:

1. Is this behavior already owned by Windows Terminal or Kilo? If yes, do not duplicate it in MultiKilo.
2. Does the proposed change keep input as a native terminal transaction rather than converting it to simulated keys?
3. Are `TerminalConnection.dll` and `OpenConsole.exe` still from the same pinned source build?
4. Does terminating one project still kill only its own Job Object/process tree?
5. Does the smoke test observe real application/terminal behavior instead of enabling the expected mode itself?
6. Has the physical 5-line and 288-line Kilo paste test been repeated?

If a change cannot answer those six questions clearly, it is not ready to merge.

## Non-goals

MultiKilo is deliberately not:

- a Windows Terminal application fork;
- Electron/Tauri/WebView2/xterm.js;
- a browser terminal;
- a daemon/service/IPC system;
- a plugin framework;
- a managed terminal emulator.

The design goal is a small project/session manager wrapped around the native terminal stack with as little duplicate terminal behavior as possible.
