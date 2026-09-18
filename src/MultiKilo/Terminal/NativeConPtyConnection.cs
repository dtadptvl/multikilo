using System.IO;
using System.Text;
using System.Threading.Channels;
using EasyWindowsTerminalControl.Internals;
using Microsoft.Terminal.Wpf;
using Windows.Win32;

namespace MultiKilo.Terminal;

internal sealed class NativeConPtyConnection : ITerminalConnection, IDisposable
{
    private const string BracketedPastePrefix = "\x1b[200~";
    private const string BracketedPasteSuffix = "\x1b[201~";
    private const string BracketedModePrefix = "\x1b[?2004";
    private const string Win32InputMode = "\x1b[?9001h";

    private readonly string _command;
    private readonly string _workingDirectory;
    private readonly JobObject _job;
    private readonly bool _captureOutput;
    private readonly Channel<byte[]> _inputQueue = Channel.CreateUnbounded<byte[]>(
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
    private int _modeProbeIndex;
    private bool _disposed;
    private volatile bool _bracketedPasteEnabled;
    private int _pendingColumns = 120;
    private int _pendingRows = 32;

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

        if (Volatile.Read(ref _nativePasteDepth) > 0)
        {
            data = FilterStringForPaste(data);
            if (_bracketedPasteEnabled)
            {
                data = string.Concat(BracketedPastePrefix, data, BracketedPasteSuffix);
            }
        }

        QueueUtf8(data);
    }

    public void WriteRawInput(string data)
    {
        if (!string.IsNullOrEmpty(data) && !_disposed)
        {
            QueueUtf8(data);
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
                PInvoke.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
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

            await foreach (var bytes in _inputQueue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
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

    private void QueueUtf8(string data)
    {
        var bytes = Encoding.UTF8.GetBytes(data);
        if (!_inputQueue.Writer.TryWrite(bytes))
        {
            throw new InvalidOperationException("Terminal input queue is closed.");
        }
    }

    private void TrackBracketedPasteMode(string output)
    {
        foreach (var ch in output)
        {
            if (_modeProbeIndex < BracketedModePrefix.Length)
            {
                if (ch == BracketedModePrefix[_modeProbeIndex])
                {
                    _modeProbeIndex++;
                }
                else
                {
                    _modeProbeIndex = ch == BracketedModePrefix[0] ? 1 : 0;
                }

                continue;
            }

            if (ch == 'h')
            {
                _bracketedPasteEnabled = true;
            }
            else if (ch == 'l')
            {
                _bracketedPasteEnabled = false;
            }

            _modeProbeIndex = ch == BracketedModePrefix[0] ? 1 : 0;
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
