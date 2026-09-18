# Patched Windows Terminal WPF control

MultiKilo embeds the native Windows Terminal `HwndTerminal` renderer/input engine. It does not fork or build the Windows Terminal application.

Pinned upstream:

- Windows Terminal Preview: `v1.25.622.0`
- commit: `9ae724aa5b080aafbeea2bbf88db630b182cc802`
- binary/package line: `1.25.260303002-preview`

`Build-PatchedTerminal.ps1` clones that exact source revision into the ignored `artifacts/` directory. `Apply-Patch.ps1` performs exact source replacements against that pinned revision, then MSBuild builds only `Microsoft.Terminal.Control.dll` plus the static libraries required by the control. It does not build `TerminalApp` or `WindowsTerminal.exe`.

The native patch is intentionally narrow:

- native text paste uses TerminalCore bracketed-paste state directly;
- paste filtering uses the same `FilterStringForPaste` path as full Windows Terminal;
- small flat C exports expose copy/paste and clipboard-format queries to the pinned WPF host.

The WPF host is also pinned to the same upstream revision. Keyboard, TSF/IME, selection, mouse behavior, renderer state and VT parsing remain owned by Windows Terminal components. MultiKilo intentionally keeps DECSET 9001 (Win32 input mode) disabled so keyboard input uses the normal VT path expected by Kilo.
