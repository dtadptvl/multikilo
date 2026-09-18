param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot
)

$ErrorActionPreference = "Stop"
$utf8 = [System.Text.UTF8Encoding]::new($false)

function Read-Normalized([string]$RelativePath) {
    $path = Join-Path $SourceRoot $RelativePath
    return [System.IO.File]::ReadAllText($path).Replace("`r`n", "`n")
}

function Write-Normalized([string]$RelativePath, [string]$Text) {
    $path = Join-Path $SourceRoot $RelativePath
    [System.IO.File]::WriteAllText($path, $Text, $utf8)
}

function Replace-Once([string]$RelativePath, [string]$OldText, [string]$NewText) {
    $text = Read-Normalized $RelativePath
    $index = $text.IndexOf($OldText, [System.StringComparison]::Ordinal)
    if ($index -lt 0) {
        throw "Pinned-source patch anchor not found in $RelativePath : $OldText"
    }
    if ($text.IndexOf($OldText, $index + $OldText.Length, [System.StringComparison]::Ordinal) -ge 0) {
        throw "Pinned-source patch anchor was not unique in $RelativePath : $OldText"
    }
    $text = $text.Substring(0, $index) + $NewText + $text.Substring($index + $OldText.Length)
    Write-Normalized $RelativePath $text
}

$hpp = "src\cascadia\TerminalControl\HwndTerminal.hpp"
Replace-Once $hpp `
    "__declspec(dllexport) bool _stdcall TerminalIsSelectionActive(void* terminal);" `
    ("__declspec(dllexport) bool _stdcall TerminalIsSelectionActive(void* terminal);`n" +
     "__declspec(dllexport) bool _stdcall TerminalCopySelectionToClipboard(void* terminal);`n" +
     "__declspec(dllexport) void _stdcall TerminalPasteFromClipboard(void* terminal);`n" +
     "__declspec(dllexport) bool _stdcall TerminalClipboardContainsText();`n" +
     "__declspec(dllexport) bool _stdcall TerminalClipboardContainsImage();")

Replace-Once $hpp `
    "    friend bool _stdcall TerminalIsSelectionActive(void* terminal);" `
    ("    friend bool _stdcall TerminalIsSelectionActive(void* terminal);`n" +
     "    friend bool _stdcall TerminalCopySelectionToClipboard(void* terminal);`n" +
     "    friend void _stdcall TerminalPasteFromClipboard(void* terminal);")

$cpp = "src\cascadia\TerminalControl\HwndTerminal.cpp"
Replace-Once $cpp `
    "#include <windowsx.h>" `
    "#include <windowsx.h>`n#include `"../../types/inc/utils.hpp`""

$text = Read-Normalized $cpp
$pastePattern = "(?s)void HwndTerminal::_PasteTextFromClipboard\(\) noexcept\n\{.*?\n\}\n\ntil::size HwndTerminal::GetFontSize"
$pasteReplacement = @'
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

til::size HwndTerminal::GetFontSize
'@
$regex = [System.Text.RegularExpressions.Regex]::new($pastePattern)
$matches = $regex.Matches($text)
if ($matches.Count -ne 1) {
    throw "Expected exactly one _PasteTextFromClipboard function, found $($matches.Count)."
}
$text = $regex.Replace($text, $pasteReplacement, 1)
Write-Normalized $cpp $text

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
Replace-Once $cpp "// Returns the selected text in the terminal." $exports

$def = "src\cascadia\TerminalControl\dll\Microsoft.Terminal.Control.def"
Replace-Once $def `
    "  TerminalIsSelectionActive" `
    ("  TerminalIsSelectionActive`n" +
     "  TerminalCopySelectionToClipboard`n" +
     "  TerminalPasteFromClipboard`n" +
     "  TerminalClipboardContainsText`n" +
     "  TerminalClipboardContainsImage")

Write-Host "Applied MultiKilo native terminal patch to pinned Windows Terminal source."
