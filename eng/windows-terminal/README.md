# Patched Windows Terminal WPF control

MultiKilo embeds the native Windows Terminal `HwndTerminal` renderer/input engine. It does not fork or build the Windows Terminal application.

Pinned upstream:

- Windows Terminal Preview: `v1.25.622.0`
- commit: `9ae724aa5b080aafbeea2bbf88db630b182cc802`
- binary/package line: `1.25.260303002-preview`

`Build-PatchedTerminal.ps1` clones that exact source revision into the ignored `artifacts/` directory, applies `native-terminal.patch`, and builds only `Microsoft.Terminal.Control.dll` plus the static libraries required by that control. It does not build `TerminalApp` or `WindowsTerminal.exe`.

The patch is intentionally narrow:

- native text paste uses the TerminalCore bracketed-paste state directly;
- paste filtering is the same `FilterStringForPaste` path used by full Windows Terminal;
- a few flat C exports expose copy/paste and clipboard-format queries to the pinned WPF host.

Keyboard, TSF/IME, selection, renderer state and terminal VT parsing remain owned by the native Windows Terminal engine.
