# Final interactive verification

CI runs the automated --smoke-test first. These checks require a real interactive Windows x64 desktop with pwsh and kilo installed.

## Terminal and project lifecycle

1. Start three projects in three different folders.
2. Confirm each reaches its own Kilo session.
3. Switch Alpha -> Beta -> Gamma -> Alpha repeatedly.
4. Confirm no session restarts and background sessions keep progressing/outputting.
5. Run ANSI color output and a full-screen TUI inside one session.
6. Maximize, restore and drag-resize repeatedly while a TUI is active.
7. Terminate Beta and confirm Alpha and Gamma remain alive.
8. Restart Beta and confirm it starts with kilo --auto --continue.

## Vietnamese input

With the normal Vietnamese IME you actually use:

1. Type Vietnamese directly into Kilo.
2. Include tone marks and multi-keystroke composition.
3. Edit in the middle of a composed line.
4. Use arrows, Home/End, Backspace/Delete and Enter around composed text.
5. Confirm no duplicate characters, stale composition text or broken caret movement.

## Clipboard / paste

Run scripts\New-PasteFixtures.ps1. Copy each whole file from a normal Windows editor and paste into Kilo using the terminal's normal paste shortcut:

- 20 lines
- 200 lines
- 1000 lines
- 1 MB mixed Unicode/Vietnamese text

Confirm the UI stays responsive, paste is bulk/fast rather than simulated typing, Unicode survives intact, bracketed paste works, and nothing is truncated.

## Tray lifecycle

1. With at least one session running, close the main window. It must hide to tray and sessions must remain alive.
2. Tray -> Open: the same live sessions must still be there.
3. Tray -> Quit, choose No: nothing terminates.
4. Tray -> Quit, choose Yes: all owned sessions terminate and the app exits.
5. With no live sessions, close the main window: the app exits.

## Portable build

Run dist\MultiKilo-win-x64\MultiKilo.exe outside the source tree and repeat one project start, Vietnamese typing, 1 MB paste, resize, terminate and tray-close check.
