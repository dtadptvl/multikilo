param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = "Stop"
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Replace-Exact([string]$RelativePath, [string]$OldText, [string]$NewText) {
    $path = Join-Path $SourceRoot $RelativePath
    $text = [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    if (-not $text.Contains($OldText)) {
        throw "Pinned-source patch anchor not found in $RelativePath"
    }
    $text = $text.Replace($OldText, $NewText)
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

$hppDeclOld = @'
__declspec(dllexport) const wchar_t* _stdcall TerminalGetSelection(void* terminal);
__declspec(dllexport) bool _stdcall TerminalIsSelectionActive(void* terminal);
__declspec(dllexport) void _stdcall DestroyTerminal(void* terminal);
'@
$hppDeclNew = @'
__declspec(dllexport) const wchar_t* _stdcall TerminalGetSelection(void* terminal);
__declspec(dllexport) bool _stdcall TerminalIsSelectionActive(void* terminal);
__declspec(dllexport) bool _stdcall TerminalCopySelectionToClipboard(void* terminal);
__declspec(dllexport) void _stdcall TerminalPasteFromClipboard(void* terminal);
__declspec(dllexport) bool _stdcall TerminalClipboardContainsText();
__declspec(dllexport) bool _stdcall TerminalClipboardContainsImage();
__declspec(dllexport) void _stdcall DestroyTerminal(void* terminal);
'@
Replace-Exact "src\cascadia\TerminalControl\HwndTerminal.hpp" $hppDeclOld $hppDeclNew

$friendOld = @'
    friend const wchar_t* _stdcall TerminalGetSelection(void* terminal);
    friend bool _stdcall TerminalIsSelectionActive(void* terminal);
    friend void _stdcall TerminalSendKeyEvent(void* terminal, WORD vkey, WORD scanCode, WORD flags, bool keyDown);
'@
$friendNew = @'
    friend const wchar_t* _stdcall TerminalGetSelection(void* terminal);
    friend bool _stdcall TerminalIsSelectionActive(void* terminal);
    friend bool _stdcall TerminalCopySelectionToClipboard(void* terminal);
    friend void _stdcall TerminalPasteFromClipboard(void* terminal);
    friend void _stdcall TerminalSendKeyEvent(void* terminal, WORD vkey, WORD scanCode, WORD flags, bool keyDown);
'@
Replace-Exact "src\cascadia\TerminalControl\HwndTerminal.hpp" $friendOld $friendNew

Replace-Exact "src\cascadia\TerminalControl\HwndTerminal.cpp" "#include <windowsx.h>" "#include <windowsx.h>`n#include `"../../types/inc/utils.hpp`""

$pasteOld = @'
void HwndTerminal::_PasteTextFromClipboard() noexcept
{
    // Get paste data from clipboard
    if (!OpenClipboard(_hwnd.get()))
    {
        return;
    }

    auto ClipboardDataHandle = GetClipboardData(CF_UNICODETEXT);
    if (ClipboardDataHandle == nullptr)
    {
        CloseClipboard();
        return;
    }

    if (const auto pwstr = static_cast<PCWCH>(GlobalLock(ClipboardDataHandle)))
    {
        _WriteTextToConnection(pwstr);
    }

    GlobalUnlock(ClipboardDataHandle);

    CloseClipboard();
}
'@
$pasteNew = @'
void HwndTerminal::_PasteTextFromClipboard() noexcept
try
{
    if (!OpenClipboard(_hwnd.get()))
    {
        return;
    }

    std::wstring clipboardText;
    const auto clipboardDataHandle = GetClipboardData(CF_UNICODETEXT);
    if (clipboardDataHandle != nullptr)
    {
        if (const auto pwstr = static_cast<PCWCH>(GlobalLock(clipboardDataHandle)))
        {
            clipboardText.assign(pwstr);
            GlobalUnlock(clipboardDataHandle);
        }
    }
    CloseClipboard();

    if (clipboardText.empty())
    {
        return;
    }

    using namespace ::Microsoft::Console::Utils;
    auto filtered = FilterStringForPaste(clipboardText, CarriageReturnNewline | ControlCodes);

    bool bracketedPaste = false;
    if (_terminal)
    {
        const auto lock = _terminal->LockForReading();
        bracketedPaste = _terminal->IsXtermBracketedPasteModeEnabled();
    }

    if (bracketedPaste)
    {
        filtered.insert(0, L"\x1b[200~");
        filtered.append(L"\x1b[201~");
    }

    _WriteTextToConnection(filtered);

    if (_terminal)
    {
        const auto lock = _terminal->LockForWriting();
        _terminal->ClearSelection();
        _terminal->TrySnapOnInput();
    }
}
CATCH_LOG()
'@
Replace-Exact "src\cascadia\TerminalControl\HwndTerminal.cpp" $pasteOld $pasteNew

$exportAnchor = "// Returns the selected text in the terminal."
$exports = @'
bool _stdcall TerminalCopySelectionToClipboard(void* terminal)
try
{
    const auto publicTerminal = static_cast<HwndTerminal*>(terminal);
    if (!publicTerminal || !publicTerminal->_terminal)
    {
        return false;
    }

    const auto lock = publicTerminal->_terminal->LockForWriting();
    if (!publicTerminal->_terminal->IsSelectionActive())
    {
        return false;
    }

    const auto bufferData = publicTerminal->_terminal->RetrieveSelectedTextFromBuffer(false, false, true, true);
    const auto hr = publicTerminal->_CopyTextToSystemClipboard(bufferData.plainText, bufferData.html, bufferData.rtf);
    if (FAILED(hr))
    {
        LOG_HR(hr);
        return false;
    }

    publicTerminal->_ClearSelection();
    return true;
}
catch (...)
{
    LOG_CAUGHT_EXCEPTION();
    return false;
}

void _stdcall TerminalPasteFromClipboard(void* terminal)
try
{
    const auto publicTerminal = static_cast<HwndTerminal*>(terminal);
    if (publicTerminal)
    {
        publicTerminal->_PasteTextFromClipboard();
    }
}
CATCH_LOG()

bool _stdcall TerminalClipboardContainsText()
{
    return IsClipboardFormatAvailable(CF_UNICODETEXT) != FALSE;
}

bool _stdcall TerminalClipboardContainsImage()
{
    if (IsClipboardFormatAvailable(CF_BITMAP) ||
        IsClipboardFormatAvailable(CF_DIB) ||
        IsClipboardFormatAvailable(CF_DIBV5) ||
        IsClipboardFormatAvailable(CF_ENHMETAFILE))
    {
        return true;
    }

    const auto png = RegisterClipboardFormatW(L"PNG");
    return png != 0 && IsClipboardFormatAvailable(png) != FALSE;
}

// Returns the selected text in the terminal.
'@
Replace-Exact "src\cascadia\TerminalControl\HwndTerminal.cpp" $exportAnchor $exports

$defOld = @'
  TerminalGetSelection
  TerminalIsSelectionActive
  TerminalRegisterScrollCallback
'@
$defNew = @'
  TerminalGetSelection
  TerminalIsSelectionActive
  TerminalCopySelectionToClipboard
  TerminalPasteFromClipboard
  TerminalClipboardContainsText
  TerminalClipboardContainsImage
  TerminalRegisterScrollCallback
'@
Replace-Exact "src\cascadia\TerminalControl\dll\Microsoft.Terminal.Control.def" $defOld $defNew

Write-Host "Applied MultiKilo native terminal patch to pinned Windows Terminal source."
