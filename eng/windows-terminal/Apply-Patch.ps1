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
# MultiKilo bridge to Windows Terminal's real ConptyConnection
# ---------------------------------------------------------------------------

$connectionHpp = "src\cascadia\TerminalConnection\ConptyConnection.h"
$connectionHppOld = @'
        uint64_t RootProcessHandle() noexcept;
'@
$connectionHppNew = @'
        uint64_t RootProcessHandle() noexcept;
        void MultiKiloSetJob(HANDLE job) noexcept;
        void MultiKiloSetExitCallback(void* context, void(__stdcall* callback)(void*, DWORD)) noexcept;
'@
Replace-Once $connectionHpp $connectionHppOld $connectionHppNew

$connectionStateOld = @'
        DWORD _flags{ 0 };
'@
$connectionStateNew = @'
        DWORD _flags{ 0 };
        HANDLE _multiKiloJob{ nullptr };
        void* _multiKiloExitContext{ nullptr };
        void(__stdcall* _multiKiloExitCallback)(void*, DWORD){ nullptr };
'@
Replace-Once $connectionHpp $connectionStateOld $connectionStateNew

$connectionCpp = "src\cascadia\TerminalConnection\ConptyConnection.cpp"
$connectionIncludeOld = "#include <winmeta.h>"
$connectionIncludeNew = "#include <winmeta.h>" + [Environment]::NewLine + "#include <atomic>"
Replace-Once $connectionCpp $connectionIncludeOld $connectionIncludeNew

$launchAnchor = @'
    void ConptyConnection::_LaunchAttachedClient()
'@
$launchReplacement = @'
    void ConptyConnection::MultiKiloSetJob(HANDLE job) noexcept
    {
        _multiKiloJob = job;
    }

    void ConptyConnection::MultiKiloSetExitCallback(
        void* context,
        void(__stdcall* callback)(void*, DWORD)) noexcept
    {
        _multiKiloExitContext = context;
        _multiKiloExitCallback = callback;
    }

    void ConptyConnection::_LaunchAttachedClient()
'@
Replace-Once $connectionCpp $launchAnchor $launchReplacement

$createFlagsOld = "EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, // dwCreationFlags"
$createFlagsNew = "EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED, // dwCreationFlags"
Replace-Once $connectionCpp $createFlagsOld $createFlagsNew

$processCreatedOld = @'
            &_piClient // lpProcessInformation
            ));

        DeleteProcThreadAttributeList(siEx.lpAttributeList);
'@
$processCreatedNew = @'
            &_piClient // lpProcessInformation
            ));

        if (_multiKiloJob)
        {
            THROW_IF_WIN32_BOOL_FALSE(AssignProcessToJobObject(_multiKiloJob, _piClient.hProcess));
        }

        THROW_LAST_ERROR_IF(ResumeThread(_piClient.hThread) == static_cast<DWORD>(-1));

        DeleteProcThreadAttributeList(siEx.lpAttributeList);
'@
Replace-Once $connectionCpp $processCreatedOld $processCreatedNew


$disconnectOld = @'
        _transitionToState(exitCode == 0 || exitCode == STILL_ACTIVE ? ConnectionState::Closed : ConnectionState::Failed);
        _indicateExitWithStatus(exitCode);
    }
    CATCH_LOG()

    void ConptyConnection::WriteInput(const winrt::array_view<const char16_t> buffer)
'@
$disconnectNew = @'
        _transitionToState(exitCode == 0 || exitCode == STILL_ACTIVE ? ConnectionState::Closed : ConnectionState::Failed);
        _indicateExitWithStatus(exitCode);

        if (const auto callback = _multiKiloExitCallback)
        {
            callback(_multiKiloExitContext, exitCode);
        }
    }
    CATCH_LOG()

    void ConptyConnection::WriteInput(const winrt::array_view<const char16_t> buffer)
'@
Replace-Once $connectionCpp $disconnectOld $disconnectNew

$connectionBridge = @'

// MultiKilo uses this tiny C ABI to consume the exact Windows Terminal
// ConptyConnection implementation without requiring packaged WinRT activation.
namespace
{
    using MultiKiloOutputCallback = void(__stdcall*)(void*, LPCWSTR, uint32_t);
    using MultiKiloExitCallback = void(__stdcall*)(void*, DWORD);

    struct MultiKiloConptySession
    {
        winrt::com_ptr<winrt::Microsoft::Terminal::TerminalConnection::implementation::ConptyConnection> connection;
        wil::unique_handle job;
        std::atomic_bool running{ false };
        std::atomic<void*> context{ nullptr };
        std::atomic<MultiKiloOutputCallback> outputCallback{ nullptr };
        std::atomic<MultiKiloExitCallback> exitCallback{ nullptr };
    };
}

extern "C" __declspec(dllexport) HRESULT __stdcall MultiKiloConptyCreate(
    LPCWSTR commandLine,
    LPCWSTR workingDirectory,
    uint32_t columns,
    uint32_t rows,
    void* context,
    MultiKiloOutputCallback outputCallback,
    MultiKiloExitCallback exitCallback,
    void** session)
try
{
    RETURN_HR_IF_NULL(E_INVALIDARG, commandLine);
    RETURN_HR_IF_NULL(E_INVALIDARG, session);
    *session = nullptr;

    auto holder = std::make_unique<MultiKiloConptySession>();
    holder->context = context;
    holder->outputCallback = outputCallback;
    holder->exitCallback = exitCallback;

    holder->job.reset(CreateJobObjectW(nullptr, nullptr));
    RETURN_LAST_ERROR_IF_NULL(holder->job);

    JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobInfo{};
    jobInfo.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    RETURN_IF_WIN32_BOOL_FALSE(SetInformationJobObject(
        holder->job.get(),
        JobObjectExtendedLimitInformation,
        &jobInfo,
        sizeof(jobInfo)));

    holder->connection = winrt::make_self<winrt::Microsoft::Terminal::TerminalConnection::implementation::ConptyConnection>();
    holder->connection->MultiKiloSetJob(holder->job.get());

    const auto settings = winrt::Microsoft::Terminal::TerminalConnection::implementation::ConptyConnection::CreateSettings(
        winrt::hstring{ commandLine },
        winrt::hstring{ workingDirectory ? workingDirectory : L"" },
        winrt::hstring{},
        false,
        winrt::hstring{},
        nullptr,
        rows,
        columns,
        winrt::guid{},
        winrt::guid{});
    holder->connection->Initialize(settings);

    const auto raw = holder.get();
    holder->connection->MultiKiloSetExitCallback(
        raw,
        [](void* context, DWORD exitCode) noexcept {
            const auto session = static_cast<MultiKiloConptySession*>(context);
            if (!session)
            {
                return;
            }

            session->running = false;
            const auto callback = session->exitCallback.load();
            const auto callbackContext = session->context.load();
            if (callback && callbackContext)
            {
                callback(callbackContext, exitCode);
            }
        });

    holder->connection->TerminalOutput([raw](const winrt::array_view<const char16_t> output) {
        const auto callback = raw->outputCallback.load();
        const auto callbackContext = raw->context.load();
        if (callback && callbackContext && !output.empty())
        {
            callback(
                callbackContext,
                reinterpret_cast<LPCWSTR>(output.data()),
                gsl::narrow_cast<uint32_t>(output.size()));
        }
    });

    holder->connection->Start();

    holder->running = true;

    *session = holder.release();
    return S_OK;
}
CATCH_RETURN()

extern "C" __declspec(dllexport) void __stdcall MultiKiloConptyWrite(
    void* session,
    LPCWSTR data,
    uint32_t length)
try
{
    const auto holder = static_cast<MultiKiloConptySession*>(session);
    if (!holder || !holder->connection || !data || length == 0)
    {
        return;
    }

    const auto first = reinterpret_cast<const char16_t*>(data);
    holder->connection->WriteInput(winrt::array_view<const char16_t>{ first, first + length });
}
CATCH_LOG()

extern "C" __declspec(dllexport) void __stdcall MultiKiloConptyResize(
    void* session,
    uint32_t columns,
    uint32_t rows)
try
{
    const auto holder = static_cast<MultiKiloConptySession*>(session);
    if (holder && holder->connection)
    {
        holder->connection->Resize(rows, columns);
    }
}
CATCH_LOG()

extern "C" __declspec(dllexport) void __stdcall MultiKiloConptyTerminate(void* session)
try
{
    const auto holder = static_cast<MultiKiloConptySession*>(session);
    if (holder && holder->job)
    {
        LOG_IF_WIN32_BOOL_FALSE(TerminateJobObject(holder->job.get(), 1));
    }
}
CATCH_LOG()

extern "C" __declspec(dllexport) BOOL __stdcall MultiKiloConptyIsRunning(void* session)
{
    const auto holder = static_cast<MultiKiloConptySession*>(session);
    return holder && holder->running ? TRUE : FALSE;
}

extern "C" __declspec(dllexport) void __stdcall MultiKiloConptyDestroy(void* session)
try
{
    std::unique_ptr<MultiKiloConptySession> holder{ static_cast<MultiKiloConptySession*>(session) };
    if (!holder)
    {
        return;
    }

    holder->outputCallback = nullptr;
    holder->exitCallback = nullptr;
    holder->context = nullptr;

    if (holder->job && holder->running)
    {
        TerminateJobObject(holder->job.get(), 1);
    }

    if (holder->connection)
    {
        holder->connection->MultiKiloSetExitCallback(nullptr, nullptr);
        holder->connection->Close();
    }

    holder->running = false;
    holder->connection = nullptr;
}
CATCH_LOG()
'@

$connectionText = Read-Normalized $connectionCpp
$connectionText = $connectionText.TrimEnd() + [Environment]::NewLine + $connectionBridge + [Environment]::NewLine
Write-Normalized $connectionCpp $connectionText

Write-Host "Patched upstream TerminalConnection for MultiKilo."


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
$structNew = "class MultiKiloTerminalConnectionSession;" + [Environment]::NewLine + [Environment]::NewLine + $structOld
Replace-Once $hpp $structOld $structNew

$hppStateOld = @'
    std::function<void(wchar_t*)> _pfnWriteCallback;
'@
$hppStateNew = @'
    std::function<void(wchar_t*)> _pfnWriteCallback;
    std::unique_ptr<MultiKiloTerminalConnectionSession> _nativeSession;
    void (_stdcall* _sessionExitCallback)(DWORD){ nullptr };
    void (_stdcall* _sessionOutputCallback)(LPCWSTR){ nullptr };
    bool _suppressCopyChar{ false };
    bool _suppressPasteChar{ false };
'@
Replace-Once $hpp $hppStateOld $hppStateNew

$cppIncludesOld = '#include "../../types/inc/utils.hpp"'
$cppIncludesNew = @'
#include "../../types/inc/utils.hpp"
'@
Replace-Once $cpp $cppIncludesOld $cppIncludesNew

$nativeSessionCode = @'
class MultiKiloTerminalConnectionSession
{
public:
    using ExitCallback = void(_stdcall*)(DWORD);
    using OutputCallback = void(_stdcall*)(LPCWSTR);
    using BridgeOutputCallback = void(_stdcall*)(void*, LPCWSTR, uint32_t);
    using BridgeExitCallback = void(_stdcall*)(void*, DWORD);

    explicit MultiKiloTerminalConnectionSession(HwndTerminal* owner) noexcept :
        _owner(owner)
    {
    }

    ~MultiKiloTerminalConnectionSession()
    {
        Close();
    }

    HRESULT Start(std::wstring_view commandLine, std::wstring_view workingDirectory, uint32_t columns, uint32_t rows) noexcept
    {
        Close();

        // Keep the component loaded for the process lifetime. Upstream
        // ConptyConnection uses get_strong()/final_release from its output
        // thread, so C++/WinRT owns its lifetime and deferred release code must
        // never run from an unloaded module.
        static const HMODULE terminalConnectionModule = LoadLibraryW(L"TerminalConnection.dll");
        _module = terminalConnectionModule;
        if (!_module)
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        _create = reinterpret_cast<CreateFn>(GetProcAddress(_module, "MultiKiloConptyCreate"));
        _write = reinterpret_cast<WriteFn>(GetProcAddress(_module, "MultiKiloConptyWrite"));
        _resize = reinterpret_cast<ResizeFn>(GetProcAddress(_module, "MultiKiloConptyResize"));
        _terminate = reinterpret_cast<TerminateFn>(GetProcAddress(_module, "MultiKiloConptyTerminate"));
        _isRunning = reinterpret_cast<IsRunningFn>(GetProcAddress(_module, "MultiKiloConptyIsRunning"));
        _destroy = reinterpret_cast<DestroyFn>(GetProcAddress(_module, "MultiKiloConptyDestroy"));

        if (!_create || !_write || !_resize || !_terminate || !_isRunning || !_destroy)
        {
            const auto hr = HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
            Close();
            return hr;
        }

        const std::wstring command{ commandLine };
        const std::wstring directory{ workingDirectory };
        const auto hr = _create(
            command.c_str(),
            directory.c_str(),
            columns,
            rows,
            this,
            &_OutputThunk,
            &_ExitThunk,
            &_session);
        if (FAILED(hr))
        {
            Close();
        }
        return hr;
    }

    void Write(std::wstring_view input) noexcept
    {
        if (_session && _write && !input.empty())
        {
            _write(_session, input.data(), gsl::narrow_cast<uint32_t>(input.size()));
        }
    }

    void Resize(uint32_t columns, uint32_t rows) noexcept
    {
        if (_session && _resize)
        {
            _resize(_session, columns, rows);
        }
    }

    void Terminate() noexcept
    {
        if (_session && _terminate)
        {
            _terminate(_session);
        }
    }

    bool IsRunning() const noexcept
    {
        return _session && _isRunning && _isRunning(_session) != FALSE;
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
        if (_session && _destroy)
        {
            _destroy(_session);
        }
        _session = nullptr;

        _create = nullptr;
        _write = nullptr;
        _resize = nullptr;
        _terminate = nullptr;
        _isRunning = nullptr;
        _destroy = nullptr;

        // TerminalConnection.dll is intentionally process-lifetime.
        _module = nullptr;
    }

private:
    using CreateFn = HRESULT(_stdcall*)(
        LPCWSTR, LPCWSTR, uint32_t, uint32_t, void*, BridgeOutputCallback, BridgeExitCallback, void**);
    using WriteFn = void(_stdcall*)(void*, LPCWSTR, uint32_t);
    using ResizeFn = void(_stdcall*)(void*, uint32_t, uint32_t);
    using TerminateFn = void(_stdcall*)(void*);
    using IsRunningFn = BOOL(_stdcall*)(void*);
    using DestroyFn = void(_stdcall*)(void*);

    static void _stdcall _OutputThunk(void* context, LPCWSTR data, uint32_t length) noexcept
    {
        const auto self = static_cast<MultiKiloTerminalConnectionSession*>(context);
        if (!self || !self->_owner || !data || length == 0)
        {
            return;
        }

        self->_owner->SendOutput(std::wstring_view{ data, length });
        if (const auto callback = self->_outputCallback)
        {
            const std::wstring copy{ data, length };
            callback(copy.c_str());
        }
    }

    static void _stdcall _ExitThunk(void* context, DWORD exitCode) noexcept
    {
        const auto self = static_cast<MultiKiloTerminalConnectionSession*>(context);
        if (self)
        {
            if (const auto callback = self->_exitCallback)
            {
                callback(exitCode);
            }
        }
    }

    HwndTerminal* _owner{};
    HMODULE _module{};
    void* _session{};
    CreateFn _create{};
    WriteFn _write{};
    ResizeFn _resize{};
    TerminateFn _terminate{};
    IsRunningFn _isRunning{};
    DestroyFn _destroy{};
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

    publicTerminal->_nativeSession = std::make_unique<MultiKiloTerminalConnectionSession>(publicTerminal);
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
    if (const auto publicTerminal = static_cast<HwndTerminal*>(terminal); publicTerminal)
    {
        if (publicTerminal->_nativeSession)
        {
            publicTerminal->_nativeSession->Terminate();
        }
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
