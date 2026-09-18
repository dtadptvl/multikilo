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
    $OldText = $OldText.Replace([string][char]13 + [char]10, [string][char]10)
    $NewText = $NewText.Replace([string][char]13 + [char]10, [string][char]10)
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


# ---------------------------------------------------------------------------
# MultiKilo native session backend
# ---------------------------------------------------------------------------
# The WPF layer owns layout/lifecycle only. HwndTerminal owns keyboard,
# clipboard, TerminalCore state, asynchronous input, ConPTY and the process tree.

$hppExportsOld = @'
__declspec(dllexport) bool _stdcall TerminalClipboardContainsImage();
'@
$hppExportsNew = @'
__declspec(dllexport) bool _stdcall TerminalClipboardContainsImage();
__declspec(dllexport) HRESULT _stdcall TerminalStartSession(void* terminal, LPCWSTR commandLine, LPCWSTR workingDirectory, uint32_t columns, uint32_t rows);
__declspec(dllexport) void _stdcall TerminalTerminateSession(void* terminal);
__declspec(dllexport) bool _stdcall TerminalSessionIsRunning(void* terminal);
__declspec(dllexport) void _stdcall TerminalResizeSession(void* terminal, uint32_t columns, uint32_t rows);
__declspec(dllexport) void _stdcall TerminalRegisterSessionExitCallback(void* terminal, void (_stdcall callback)(DWORD));
__declspec(dllexport) void _stdcall TerminalRegisterSessionOutputCallback(void* terminal, void (_stdcall callback)(LPCWSTR));
__declspec(dllexport) void _stdcall TerminalSelectAllForTesting(void* terminal);
'@
Replace-Once $hpp $hppExportsOld $hppExportsNew

$hppFriendsOld = @'
    friend void _stdcall TerminalPasteFromClipboard(void* terminal);
'@
$hppFriendsNew = @'
    friend void _stdcall TerminalPasteFromClipboard(void* terminal);
    friend HRESULT _stdcall TerminalStartSession(void* terminal, LPCWSTR commandLine, LPCWSTR workingDirectory, uint32_t columns, uint32_t rows);
    friend void _stdcall TerminalTerminateSession(void* terminal);
    friend bool _stdcall TerminalSessionIsRunning(void* terminal);
    friend void _stdcall TerminalResizeSession(void* terminal, uint32_t columns, uint32_t rows);
    friend void _stdcall TerminalRegisterSessionExitCallback(void* terminal, void (_stdcall callback)(DWORD));
    friend void _stdcall TerminalRegisterSessionOutputCallback(void* terminal, void (_stdcall callback)(LPCWSTR));
    friend void _stdcall TerminalSelectAllForTesting(void* terminal);
'@
Replace-Once $hpp $hppFriendsOld $hppFriendsNew

$structOld = "struct HwndTerminal : ::Microsoft::Console::Types::IControlAccessibilityInfo"
$structNew = "class MultiKiloNativeSession;" + [Environment]::NewLine + [Environment]::NewLine + $structOld
Replace-Once $hpp $structOld $structNew

$hppStateOld = @'
    std::function<void(wchar_t*)> _pfnWriteCallback;
'@
$hppStateNew = @'
    std::function<void(wchar_t*)> _pfnWriteCallback;
    std::unique_ptr<MultiKiloNativeSession> _nativeSession;
    void (_stdcall* _sessionExitCallback)(DWORD){ nullptr };
    void (_stdcall* _sessionOutputCallback)(LPCWSTR){ nullptr };
    bool _suppressCopyChar{ false };
    bool _suppressPasteChar{ false };
'@
Replace-Once $hpp $hppStateOld $hppStateNew

$cppIncludesOld = '#include "../../types/inc/utils.hpp"'
$cppIncludesNew = @'
#include "../../types/inc/utils.hpp"
#include <atomic>
#include <condition_variable>
#include <deque>
#include <mutex>
#include <thread>
#include <til/env.h>
'@
Replace-Once $cpp $cppIncludesOld $cppIncludesNew

$nativeSessionCode = @'
class MultiKiloNativeSession
{
public:
    using ExitCallback = void(_stdcall*)(DWORD);
    using OutputCallback = void(_stdcall*)(LPCWSTR);

    explicit MultiKiloNativeSession(HwndTerminal* owner) noexcept :
        _owner(owner)
    {
    }

    ~MultiKiloNativeSession()
    {
        Close();
    }

    HRESULT Start(std::wstring_view commandLine, std::wstring_view workingDirectory, uint32_t columns, uint32_t rows)
    try
    {
        Close();

        wil::unique_handle inputRead;
        wil::unique_handle inputWrite;
        wil::unique_handle outputRead;
        wil::unique_handle outputWrite;

        RETURN_IF_WIN32_BOOL_FALSE(CreatePipe(inputRead.addressof(), inputWrite.addressof(), nullptr, 0));
        RETURN_IF_WIN32_BOOL_FALSE(CreatePipe(outputRead.addressof(), outputWrite.addressof(), nullptr, 0));

        HPCON hpc{};
        const COORD size{
            gsl::narrow_cast<SHORT>(std::clamp<uint32_t>(columns, 1, SHRT_MAX)),
            gsl::narrow_cast<SHORT>(std::clamp<uint32_t>(rows, 1, SHRT_MAX)),
        };
        RETURN_IF_FAILED(CreatePseudoConsole(size, inputRead.get(), outputWrite.get(), 0, &hpc));

        const auto hpcCleanup = wil::scope_exit([&]() noexcept {
            if (hpc)
            {
                ClosePseudoConsole(hpc);
            }
        });

        wil::unique_handle job{ CreateJobObjectW(nullptr, nullptr) };
        RETURN_LAST_ERROR_IF_NULL(job);

        JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobInfo{};
        jobInfo.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        RETURN_IF_WIN32_BOOL_FALSE(SetInformationJobObject(
            job.get(),
            JobObjectExtendedLimitInformation,
            &jobInfo,
            sizeof(jobInfo)));

        STARTUPINFOEXW si{};
        si.StartupInfo.cb = sizeof(si);

        SIZE_T attributeBytes{};
        InitializeProcThreadAttributeList(nullptr, 1, 0, &attributeBytes);
        std::vector<std::byte> attributeStorage(attributeBytes);
        si.lpAttributeList = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributeStorage.data());
        RETURN_IF_WIN32_BOOL_FALSE(InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, &attributeBytes));
        const auto attributeCleanup = wil::scope_exit([&]() noexcept {
            DeleteProcThreadAttributeList(si.lpAttributeList);
        });

        RETURN_IF_WIN32_BOOL_FALSE(UpdateProcThreadAttribute(
            si.lpAttributeList,
            0,
            PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            hpc,
            sizeof(hpc),
            nullptr,
            nullptr));

        std::wstring mutableCommand{ commandLine };
        mutableCommand.push_back(L'\0');
        const auto directory = workingDirectory.empty() ? nullptr : workingDirectory.data();

        // Match Windows Terminal's ConptyConnection process environment.
        // A number of Windows TUIs use WT_SESSION to select their raw VT
        // input path (which is where bracketed paste is enabled).
        auto environment = til::env::from_current_environment();
        const auto sessionId = ::Microsoft::Console::Utils::CreateGuid();
        const auto profileId = ::Microsoft::Console::Utils::CreateGuid();
        environment.as_map().insert_or_assign(
            L"WT_SESSION",
            ::Microsoft::Console::Utils::GuidToPlainString(sessionId));
        environment.as_map().insert_or_assign(
            L"WT_PROFILE_ID",
            ::Microsoft::Console::Utils::GuidToString(profileId));
        auto environmentBlock = environment.to_string();
        auto environmentData = environmentBlock.empty() ? nullptr : environmentBlock.data();

        PROCESS_INFORMATION pi{};
        RETURN_IF_WIN32_BOOL_FALSE(CreateProcessW(
            nullptr,
            mutableCommand.data(),
            nullptr,
            nullptr,
            FALSE,
            EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED,
            environmentData,
            directory,
            &si.StartupInfo,
            &pi));

        wil::unique_handle process{ pi.hProcess };
        wil::unique_handle processThread{ pi.hThread };

        RETURN_IF_WIN32_BOOL_FALSE(AssignProcessToJobObject(job.get(), process.get()));

        _hpc = hpc;
        hpc = nullptr;
        _inputWrite = inputWrite.release();
        _outputRead = outputRead.release();
        _process = process.release();
        _job = job.release();
        _stopping = false;
        _running = true;

        _inputThread = std::thread([this]() noexcept { _InputLoop(); });
        _outputThread = std::thread([this]() noexcept { _OutputLoop(); });
        _waitThread = std::thread([this]() noexcept { _WaitLoop(); });

        if (ResumeThread(processThread.get()) == static_cast<DWORD>(-1))
        {
            const auto hr = HRESULT_FROM_WIN32(GetLastError());
            Close();
            return hr;
        }

        return S_OK;
    }
    catch (...)
    {
        Close();
        return wil::ResultFromCaughtException();
    }

    void Write(std::wstring_view input) noexcept
    {
        if (input.empty() || !_running || _stopping)
        {
            return;
        }

        const int bytes = WideCharToMultiByte(
            CP_UTF8,
            0,
            input.data(),
            gsl::narrow_cast<int>(input.size()),
            nullptr,
            0,
            nullptr,
            nullptr);
        if (bytes <= 0)
        {
            return;
        }

        std::string utf8(gsl::narrow_cast<size_t>(bytes), '\0');
        if (WideCharToMultiByte(
                CP_UTF8,
                0,
                input.data(),
                gsl::narrow_cast<int>(input.size()),
                utf8.data(),
                bytes,
                nullptr,
                nullptr) != bytes)
        {
            return;
        }

        {
            std::lock_guard guard{ _inputMutex };
            _inputQueue.emplace_back(std::move(utf8));
        }
        _inputWake.notify_one();
    }

    void Resize(uint32_t columns, uint32_t rows) noexcept
    {
        const auto hpc = _hpc;
        if (!hpc || !_running)
        {
            return;
        }

        const COORD size{
            gsl::narrow_cast<SHORT>(std::clamp<uint32_t>(columns, 1, SHRT_MAX)),
            gsl::narrow_cast<SHORT>(std::clamp<uint32_t>(rows, 1, SHRT_MAX)),
        };
        LOG_IF_FAILED(ResizePseudoConsole(hpc, size));
    }

    void Terminate() noexcept
    {
        if (_job)
        {
            LOG_IF_WIN32_BOOL_FALSE(TerminateJobObject(_job, 1));
        }
    }

    bool IsRunning() const noexcept
    {
        return _running;
    }

    void SetExitCallback(ExitCallback callback) noexcept
    {
        _exitCallback = callback;
    }

    void SetOutputCallback(OutputCallback callback) noexcept
    {
        _outputCallback = callback;
    }

    void Close() noexcept
    {
        _stopping = true;

        if (_job && _running)
        {
            TerminateJobObject(_job, 1);
        }

        if (_hpc)
        {
            ClosePseudoConsole(_hpc);
            _hpc = nullptr;
        }

        _inputWake.notify_all();

        if (_inputThread.joinable())
        {
            _inputThread.join();
        }
        if (_outputThread.joinable())
        {
            _outputThread.join();
        }
        if (_waitThread.joinable())
        {
            _waitThread.join();
        }

        if (_inputWrite)
        {
            CloseHandle(_inputWrite);
            _inputWrite = nullptr;
        }
        if (_outputRead)
        {
            CloseHandle(_outputRead);
            _outputRead = nullptr;
        }
        if (_process)
        {
            CloseHandle(_process);
            _process = nullptr;
        }
        if (_job)
        {
            CloseHandle(_job);
            _job = nullptr;
        }

        {
            std::lock_guard guard{ _inputMutex };
            _inputQueue.clear();
        }

        _running = false;
        _stopping = false;
    }

private:
    void _InputLoop() noexcept
    {
        for (;;)
        {
            std::string data;
            {
                std::unique_lock lock{ _inputMutex };
                _inputWake.wait(lock, [this]() noexcept {
                    return _stopping || !_inputQueue.empty();
                });

                if (_inputQueue.empty())
                {
                    if (_stopping)
                    {
                        return;
                    }
                    continue;
                }

                data = std::move(_inputQueue.front());
                _inputQueue.pop_front();
            }

            size_t offset = 0;
            while (offset < data.size() && !_stopping)
            {
                DWORD written{};
                if (!WriteFile(
                        _inputWrite,
                        data.data() + offset,
                        gsl::narrow_cast<DWORD>(data.size() - offset),
                        &written,
                        nullptr))
                {
                    return;
                }
                if (written == 0)
                {
                    return;
                }
                offset += written;
            }
        }
    }

    void _OutputLoop() noexcept
    {
        char buffer[128 * 1024];
        til::u8state state;
        std::wstring converted;

        while (!_stopping)
        {
            DWORD read{};
            if (!ReadFile(_outputRead, buffer, sizeof(buffer), &read, nullptr) || read == 0)
            {
                break;
            }

            converted.clear();
            if (FAILED_LOG(til::u8u16(
                    { &buffer[0], gsl::narrow_cast<size_t>(read) },
                    converted,
                    state)))
            {
                continue;
            }

            if (!converted.empty())
            {
                _owner->SendOutput(converted);
                if (const auto callback = _outputCallback)
                {
                    callback(converted.c_str());
                }
            }
        }
    }

    void _WaitLoop() noexcept
    {
        const auto process = _process;
        if (!process)
        {
            return;
        }

        WaitForSingleObject(process, INFINITE);

        DWORD exitCode{};
        GetExitCodeProcess(process, &exitCode);
        _running = false;

        if (!_stopping)
        {
            if (const auto callback = _exitCallback)
            {
                callback(exitCode);
            }
        }
    }

    HwndTerminal* _owner{};
    HPCON _hpc{};
    HANDLE _inputWrite{};
    HANDLE _outputRead{};
    HANDLE _process{};
    HANDLE _job{};

    std::thread _inputThread;
    std::thread _outputThread;
    std::thread _waitThread;

    std::mutex _inputMutex;
    std::condition_variable _inputWake;
    std::deque<std::string> _inputQueue;

    std::atomic_bool _running{ false };
    std::atomic_bool _stopping{ false };
    ExitCallback _exitCallback{};
    OutputCallback _outputCallback{};
};
'@

$termClassOld = 'static LPCWSTR term_window_class = L"HwndTerminalClass";'
$termClassNew = $termClassOld + [Environment]::NewLine + [Environment]::NewLine + $nativeSessionCode
Replace-Once $cpp $termClassOld $termClassNew

$teardownOld = @'
void HwndTerminal::Teardown() noexcept
try
{
'@
$teardownNew = @'
void HwndTerminal::Teardown() noexcept
try
{
    _nativeSession.reset();
'@
Replace-Once $cpp $teardownOld $teardownNew

$writeOld = @'
void HwndTerminal::_WriteTextToConnection(const std::wstring_view input) noexcept
{
    if (input.empty() || !_pfnWriteCallback)
    {
        return;
    }

    try
    {
        auto callingText{ wil::make_cotaskmem_string(input.data(), input.size()) };
        _pfnWriteCallback(callingText.release());
    }
    CATCH_LOG();
}
'@
$writeNew = @'
void HwndTerminal::_WriteTextToConnection(const std::wstring_view input) noexcept
{
    if (input.empty())
    {
        return;
    }

    if (_nativeSession && _nativeSession->IsRunning())
    {
        _nativeSession->Write(input);
        return;
    }

    if (!_pfnWriteCallback)
    {
        return;
    }

    try
    {
        auto callingText{ wil::make_cotaskmem_string(input.data(), input.size()) };
        _pfnWriteCallback(callingText.release());
    }
    CATCH_LOG();
}
'@
Replace-Once $cpp $writeOld $writeNew

$keyOld = @'
void HwndTerminal::_SendKeyEvent(WORD vkey, WORD scanCode, WORD flags, bool keyDown) noexcept
try
{
    if (!_terminal)
    {
        return;
    }

    auto modifiers = getControlKeyState();
'@
$keyNew = @'
void HwndTerminal::_SendKeyEvent(WORD vkey, WORD scanCode, WORD flags, bool keyDown) noexcept
try
{
    if (!_terminal)
    {
        return;
    }

    constexpr WORD VkC = 0x43;
    constexpr WORD VkV = 0x56;
    const auto ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
    const auto shift = (GetKeyState(VK_SHIFT) & 0x8000) != 0;

    if (!keyDown)
    {
        if (vkey == VkC && _suppressCopyChar)
        {
            _suppressCopyChar = false;
            return;
        }
        if ((vkey == VkV || vkey == VK_INSERT) && _suppressPasteChar)
        {
            _suppressPasteChar = false;
            return;
        }
    }
    else
    {
        bool selectionActive = false;
        {
            const auto lock = _terminal->LockForReading();
            selectionActive = _terminal->IsSelectionActive();
        }

        const auto copyShortcut =
            (ctrl && vkey == VK_INSERT) ||
            (ctrl && shift && vkey == VkC) ||
            (ctrl && !shift && vkey == VkC && selectionActive);

        if (copyShortcut)
        {
            if (selectionActive)
            {
                TerminalCopySelectionToClipboard(this);
            }
            _suppressCopyChar = true;
            return;
        }

        const auto explicitPaste =
            (ctrl && shift && vkey == VkV) ||
            (shift && !ctrl && vkey == VK_INSERT);
        const auto plainCtrlV = ctrl && !shift && vkey == VkV;

        if (explicitPaste || plainCtrlV)
        {
            if (plainCtrlV && TerminalClipboardContainsImage())
            {
            }
            else if (TerminalClipboardContainsText())
            {
                _PasteTextFromClipboard();
                _suppressPasteChar = true;
                return;
            }
            else if (explicitPaste)
            {
                _suppressPasteChar = true;
                return;
            }
        }
    }

    auto modifiers = getControlKeyState();
'@
Replace-Once $cpp $keyOld $keyNew

$charFunction = Read-Normalized $cpp
$charIndex = $charFunction.IndexOf('void HwndTerminal::_SendCharEvent')
if ($charIndex -lt 0) { throw "_SendCharEvent not found" }
$charTail = $charFunction.Substring($charIndex)
$charOld = @'
    TerminalInput::OutputType out;
    {
        const auto lock = _terminal->LockForWriting();
'@
$charNew = @'
    if ((ch == L'\x03' && _suppressCopyChar) ||
        (ch == L'\x16' && _suppressPasteChar))
    {
        return;
    }

    TerminalInput::OutputType out;
    {
        const auto lock = _terminal->LockForWriting();
'@
$charOld = $charOld.Replace([string][char]13 + [char]10, [string][char]10)
$charNew = $charNew.Replace([string][char]13 + [char]10, [string][char]10)
$localIndex = $charTail.IndexOf($charOld)
if ($localIndex -lt 0) { throw "_SendCharEvent output anchor not found" }
$absoluteIndex = $charIndex + $localIndex
$charFunction = $charFunction.Substring(0, $absoluteIndex) + $charNew + $charFunction.Substring($absoluteIndex + $charOld.Length)
Write-Normalized $cpp $charFunction

$sessionExports = @'
HRESULT _stdcall TerminalStartSession(void* terminal, LPCWSTR commandLine, LPCWSTR workingDirectory, uint32_t columns, uint32_t rows)
try
{
    const auto publicTerminal = static_cast<HwndTerminal*>(terminal);
    RETURN_HR_IF_NULL(E_INVALIDARG, publicTerminal);
    RETURN_HR_IF_NULL(E_INVALIDARG, commandLine);

    publicTerminal->_nativeSession = std::make_unique<MultiKiloNativeSession>(publicTerminal);
    publicTerminal->_nativeSession->SetExitCallback(publicTerminal->_sessionExitCallback);
    publicTerminal->_nativeSession->SetOutputCallback(publicTerminal->_sessionOutputCallback);

    const auto hr = publicTerminal->_nativeSession->Start(
        commandLine,
        workingDirectory ? std::wstring_view{ workingDirectory } : std::wstring_view{},
        columns,
        rows);
    if (FAILED(hr))
    {
        publicTerminal->_nativeSession.reset();
    }
    return hr;
}
CATCH_RETURN()

void _stdcall TerminalTerminateSession(void* terminal)
try
{
    if (const auto publicTerminal = static_cast<HwndTerminal*>(terminal); publicTerminal && publicTerminal->_nativeSession)
    {
        publicTerminal->_nativeSession->Terminate();
    }
}
CATCH_LOG()

bool _stdcall TerminalSessionIsRunning(void* terminal)
try
{
    const auto publicTerminal = static_cast<HwndTerminal*>(terminal);
    return publicTerminal && publicTerminal->_nativeSession && publicTerminal->_nativeSession->IsRunning();
}
catch (...)
{
    LOG_CAUGHT_EXCEPTION();
    return false;
}

void _stdcall TerminalResizeSession(void* terminal, uint32_t columns, uint32_t rows)
try
{
    if (const auto publicTerminal = static_cast<HwndTerminal*>(terminal); publicTerminal && publicTerminal->_nativeSession)
    {
        publicTerminal->_nativeSession->Resize(columns, rows);
    }
}
CATCH_LOG()

void _stdcall TerminalRegisterSessionExitCallback(void* terminal, void (_stdcall callback)(DWORD))
try
{
    if (const auto publicTerminal = static_cast<HwndTerminal*>(terminal))
    {
        publicTerminal->_sessionExitCallback = callback;
        if (publicTerminal->_nativeSession)
        {
            publicTerminal->_nativeSession->SetExitCallback(callback);
        }
    }
}
CATCH_LOG()

void _stdcall TerminalRegisterSessionOutputCallback(void* terminal, void (_stdcall callback)(LPCWSTR))
try
{
    if (const auto publicTerminal = static_cast<HwndTerminal*>(terminal))
    {
        publicTerminal->_sessionOutputCallback = callback;
        if (publicTerminal->_nativeSession)
        {
            publicTerminal->_nativeSession->SetOutputCallback(callback);
        }
    }
}
CATCH_LOG()

void _stdcall TerminalSelectAllForTesting(void* terminal)
try
{
    if (const auto publicTerminal = static_cast<HwndTerminal*>(terminal); publicTerminal && publicTerminal->_terminal)
    {
        const auto lock = publicTerminal->_terminal->LockForWriting();
        publicTerminal->_terminal->SelectAll();
        publicTerminal->_renderer->TriggerSelection();
    }
}
CATCH_LOG()

'@
Replace-Once $cpp "// Returns the selected text in the terminal." ($sessionExports + "// Returns the selected text in the terminal.")

$defOld = @'
  TerminalClipboardContainsImage
'@
$defNew = @'
  TerminalClipboardContainsImage
  TerminalStartSession
  TerminalTerminateSession
  TerminalSessionIsRunning
  TerminalResizeSession
  TerminalRegisterSessionExitCallback
  TerminalRegisterSessionOutputCallback
  TerminalSelectAllForTesting
'@
Replace-Once $def $defOld $defNew

Write-Host "Applied MultiKilo native ConPTY session backend."
