using System.IO;
using System.Text;
using System.Threading.Channels;
using EasyWindowsTerminalControl.Internals;
using Microsoft.Terminal.Wpf;

namespace MultiKilo.Terminal;

internal sealed class NativeConPtyConnection : ITerminalConnection, IDisposable
{
    private const string BracketedPastePrefix = "\x1b[200~";
    private const string BracketedPasteSuffix = "\x1b[201~";
    private const string BracketedModePrefix = "\x1b[?2004";
    private const string Win32InputMode = "\x1b[?9001h";
    private const nuint ProcThreadAttributePseudoConsole = 0x00020016;

    private readonly string _command;
    private readonly string _workingDirectory;
    private readonly JobObject _job;
    private readonly bool _captureOutput;
    private readonly Channel<InputWorkItem> _inputQueue = Channel.CreateUnbounded<InputWorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly TaskCompletionSource _processStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ioStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private readonly object _outputGate = new();
    private readonly StringBuilder _capturedOutput = new();

    private Task? _startProcessTask;
    private PseudoConsolePipe? _inputPipe;
    private PseudoConsolePipe? _outputPipe;
    private PseudoConsole? _pseudoConsole;
    private IProcess? _process;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private Task? _readerTask;
    private Task? _writerTask;
    private int _ioStartRequested;
    private int _nativePasteDepth;
    private int _vtParseState;
    private int _privateModeParam;
    private bool _privateModeHas2004;
    private bool _disposed;
    private volatile bool _bracketedPasteEnabled;
    private long _lastNativePasteBytes;
    private int _lastNativePasteBracketed;
    private int _pendingColumns = 120;
    private int _pendingRows = 32;
    private long _lastNativePasteWriteMilliseconds;
    private long _lastNativePasteWrittenBytes;

    private readonly record struct InputWorkItem(
        string Text,
        bool IsNativePaste,
        bool Bracketed);

    public NativeConPtyConnection(
        string command,
        string workingDirectory,
        JobObject job,
        bool captureOutput = false)
    {
        _command = command;
        _workingDirectory = workingDirectory;
        _job = job;
        _captureOutput = captureOutput;
    }

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    public Task ProcessStarted => _processStarted.Task;
    public Task IoStarted => _ioStarted.Task;
    public Task Completion => _completion.Task;
    public bool ProcessHasExited => _process?.HasExited ?? true;
    internal bool BracketedPasteEnabled => _bracketedPasteEnabled;
    internal long LastNativePasteBytes => Interlocked.Read(ref _lastNativePasteBytes);
    internal bool LastNativePasteWasBracketed => Volatile.Read(ref _lastNativePasteBracketed) != 0;
    internal long LastNativePasteWriteMilliseconds => Interlocked.Read(ref _lastNativePasteWriteMilliseconds);
    internal long LastNativePasteWrittenBytes => Interlocked.Read(ref _lastNativePasteWrittenBytes);

    public Task StartProcessAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _startProcessTask ??= Task.Run(StartProcessCore);
        }
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _ioStartRequested, 1) != 0)
        {
            return;
        }

        TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(Win32InputMode));

        _readerTask = Task.Run(ReadOutputLoop);
        _writerTask = Task.Run(WriteInputLoopAsync);
        _ioStarted.TrySetResult();
    }

    public void WriteInput(string data)
    {
        if (string.IsNullOrEmpty(data) || _disposed)
        {
            return;
        }

        var isNativePaste = Volatile.Read(ref _nativePasteDepth) > 0;
        QueueInput(new InputWorkItem(
            data,
            IsNativePaste: isNativePaste,
            Bracketed: isNativePaste && _bracketedPasteEnabled));
    }

    public void WriteRawInput(string data)
    {
        if (!string.IsNullOrEmpty(data) && !_disposed)
        {
            QueueInput(new InputWorkItem(data, IsNativePaste: false, Bracketed: false));
        }
    }

    public void BeginNativePaste() => Interlocked.Increment(ref _nativePasteDepth);

    public void EndNativePaste() => Interlocked.Decrement(ref _nativePasteDepth);

    public void Resize(uint rows, uint columns)
    {
        if (rows == 0 || columns == 0 || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            _pendingRows = checked((int)rows);
            _pendingColumns = checked((int)columns);

            try
            {
                _pseudoConsole?.Resize(_pendingColumns, _pendingRows);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Close()
    {
        CompleteInput();
    }

    public void CompleteInput()
    {
        _inputQueue.Writer.TryComplete();
    }

    public string GetCapturedOutput()
    {
        lock (_outputGate)
        {
            return _capturedOutput.ToString();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inputQueue.Writer.TryComplete();

            try { _inputStream?.Dispose(); } catch { }
            try { _outputStream?.Dispose(); } catch { }
            try { _pseudoConsole?.Dispose(); } catch { }
            try { _process?.Dispose(); } catch { }
            try { _inputPipe?.Dispose(); } catch { }
            try { _outputPipe?.Dispose(); } catch { }

            _inputStream = null;
            _outputStream = null;
            _pseudoConsole = null;
            _process = null;
            _inputPipe = null;
            _outputPipe = null;
        }
    }

    private void StartProcessCore()
    {
        PseudoConsolePipe? inputPipe = null;
        PseudoConsolePipe? outputPipe = null;
        PseudoConsole? pseudoConsole = null;
        IProcess? process = null;
        FileStream? inputStream = null;
        FileStream? outputStream = null;

        try
        {
            inputPipe = new PseudoConsolePipe();
            outputPipe = new PseudoConsolePipe();
            pseudoConsole = PseudoConsole.Create(
                inputPipe.ReadSide,
                outputPipe.WriteSide,
                _pendingColumns,
                _pendingRows);

            var factory = new JobAssigningProcessFactory(_job);
            process = factory.Start(
                _command,
                ProcThreadAttributePseudoConsole,
                pseudoConsole,
                _workingDirectory);

            inputStream = new FileStream(inputPipe.WriteSide, FileAccess.Write, 64 * 1024, isAsync: false);
            outputStream = new FileStream(outputPipe.ReadSide, FileAccess.Read, 64 * 1024, isAsync: false);

            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _inputPipe = inputPipe;
                _outputPipe = outputPipe;
                _pseudoConsole = pseudoConsole;
                _process = process;
                _inputStream = inputStream;
                _outputStream = outputStream;
            }

            inputPipe = null;
            outputPipe = null;
            pseudoConsole = null;
            process = null;
            inputStream = null;
            outputStream = null;

            _processStarted.TrySetResult();
            _ = Task.Run(WaitForProcessExit);
        }
        catch (Exception ex)
        {
            try { inputStream?.Dispose(); } catch { }
            try { outputStream?.Dispose(); } catch { }
            try { process?.Dispose(); } catch { }
            try { pseudoConsole?.Dispose(); } catch { }
            try { inputPipe?.Dispose(); } catch { }
            try { outputPipe?.Dispose(); } catch { }

            _processStarted.TrySetException(ex);
            _completion.TrySetException(ex);
            throw;
        }
    }

    private void WaitForProcessExit()
    {
        try
        {
            var process = _process;
            if (process is null)
            {
                return;
            }

            process.WaitForExit();
            _completion.TrySetResult();
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
        finally
        {
            _inputQueue.Writer.TryComplete();
        }
    }

    private void ReadOutputLoop()
    {
        try
        {
            _processStarted.Task.GetAwaiter().GetResult();
            var stream = _outputStream;
            if (stream is null)
            {
                return;
            }

            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
                detectEncodingFromByteOrderMarks: false,
                bufferSize: 64 * 1024,
                leaveOpen: true);

            var buffer = new char[64 * 1024];
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                var output = new string(buffer, 0, count);
                TrackBracketedPasteMode(output);

                if (_captureOutput)
                {
                    lock (_outputGate)
                    {
                        _capturedOutput.Append(output);
                    }
                }

                TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(output));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _completion.TrySetException(ex);
            }
        }
    }

    private async Task WriteInputLoopAsync()
    {
        try
        {
            await _processStarted.Task.ConfigureAwait(false);
            var stream = _inputStream;
            if (stream is null)
            {
                return;
            }

            await foreach (var item in _inputQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var bytes = EncodeInput(item);
                var started = item.IsNativePaste ? Environment.TickCount64 : 0;

                if (item.IsNativePaste)
                {
                    Interlocked.Exchange(ref _lastNativePasteBytes, bytes.LongLength);
                    Volatile.Write(ref _lastNativePasteBracketed, item.Bracketed ? 1 : 0);
                }

                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();

                if (item.IsNativePaste)
                {
                    Interlocked.Exchange(ref _lastNativePasteWrittenBytes, bytes.LongLength);
                    Interlocked.Exchange(
                        ref _lastNativePasteWriteMilliseconds,
                        Environment.TickCount64 - started);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                _completion.TrySetException(ex);
            }
        }
    }

    private static byte[] EncodeInput(InputWorkItem item)
    {
        if (!item.IsNativePaste)
        {
            return Encoding.UTF8.GetBytes(item.Text);
        }

        return EncodeNativePaste(item.Text, item.Bracketed);
    }

    internal static byte[] EncodeNativePaste(string text, bool bracketed)
    {
        var filtered = FilterStringForPaste(text);

        if (!bracketed)
        {
            return Encoding.UTF8.GetBytes(filtered);
        }

        var prefix = Encoding.ASCII.GetBytes(BracketedPastePrefix);
        var suffix = Encoding.ASCII.GetBytes(BracketedPasteSuffix);
        var contentByteCount = Encoding.UTF8.GetByteCount(filtered);
        var bytes = GC.AllocateUninitializedArray<byte>(
            prefix.Length + contentByteCount + suffix.Length);

        prefix.CopyTo(bytes, 0);
        Encoding.UTF8.GetBytes(
            filtered.AsSpan(),
            bytes.AsSpan(prefix.Length, contentByteCount));
        suffix.CopyTo(bytes, prefix.Length + contentByteCount);

        return bytes;
    }

    private void QueueInput(InputWorkItem item)
    {
        if (!_inputQueue.Writer.TryWrite(item))
        {
            throw new InvalidOperationException("Terminal input queue is closed.");
        }
    }

    internal void TrackBracketedPasteMode(string output)
    {
        foreach (var ch in output)
        {
            switch (_vtParseState)
            {
                case 0:
                    if (ch == '\x1b')
                    {
                        _vtParseState = 1;
                    }
                    break;

                case 1:
                    if (ch == '[')
                    {
                        _vtParseState = 2;
                    }
                    else
                    {
                        if (ch == 'c')
                        {
                            _bracketedPasteEnabled = false;
                        }

                        _vtParseState = ch == '\x1b' ? 1 : 0;
                    }
                    break;

                case 2:
                    if (ch == '?')
                    {
                        _privateModeParam = 0;
                        _privateModeHas2004 = false;
                        _vtParseState = 3;
                    }
                    else
                    {
                        _vtParseState = ch == '\x1b' ? 1 : 0;
                    }
                    break;

                case 3:
                    if (ch is >= '0' and <= '9')
                    {
                        _privateModeParam = Math.Min(
                            100_000,
                            (_privateModeParam * 10) + (ch - '0'));
                    }
                    else if (ch is ';' or ':')
                    {
                        _privateModeHas2004 |= _privateModeParam == 2004;
                        _privateModeParam = 0;
                    }
                    else if (ch is 'h' or 'l')
                    {
                        _privateModeHas2004 |= _privateModeParam == 2004;
                        if (_privateModeHas2004)
                        {
                            _bracketedPasteEnabled = ch == 'h';
                        }

                        _vtParseState = 0;
                        _privateModeParam = 0;
                        _privateModeHas2004 = false;
                    }
                    else
                    {
                        _vtParseState = ch == '\x1b' ? 1 : 0;
                        _privateModeParam = 0;
                        _privateModeHas2004 = false;
                    }
                    break;
            }
        }
    }

    private static string FilterStringForPaste(string input)
    {
        StringBuilder? filtered = null;
        var segmentStart = 0;

        for (var i = 0; i < input.Length; i++)
        {
            var ch = input[i];

            if (ch == '\n')
            {
                filtered ??= new StringBuilder(input.Length);
                filtered.Append(input, segmentStart, i - segmentStart);

                if (i == 0 || input[i - 1] != '\r')
                {
                    filtered.Append('\r');
                }

                segmentStart = i + 1;
                continue;
            }

            if (IsRemovedPasteControlCode(ch))
            {
                filtered ??= new StringBuilder(input.Length);
                filtered.Append(input, segmentStart, i - segmentStart);
                segmentStart = i + 1;
            }
        }

        if (filtered is null)
        {
            return input;
        }

        if (segmentStart < input.Length)
        {
            filtered.Append(input, segmentStart, input.Length - segmentStart);
        }

        return filtered.ToString();
    }

    private static bool IsRemovedPasteControlCode(char ch)
    {
        if (ch >= '\x20' && ch < '\x7f')
        {
            return false;
        }

        if (ch > '\x9f')
        {
            return false;
        }

        return ch is not ('\t' or '\n' or '\r');
    }
}
