using System.Threading.Channels;
using EasyWindowsTerminalControl;

namespace MultiKilo.Terminal;

internal sealed class BufferedTermPTY : TermPTY
{
    private const string BracketedPastePrefix = "\x1b[200~";
    private const string BracketedPasteSuffix = "\x1b[201~";
    private const string BracketedModePrefix = "\x1b[?2004";

    private readonly Channel<string> _input = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly Task _writerTask;
    private int _modeProbeIndex;
    private volatile bool _bracketedPasteEnabled;

    public BufferedTermPTY(int readBufferSize = 1024 * 64)
        : base(READ_BUFFER_SIZE: readBufferSize)
    {
        InterceptInputToTermApp = QueueInput;
        InterceptOutputToUITerminal = TrackBracketedPasteMode;
        _writerTask = Task.Run(WriteQueuedInputAsync);
    }

    public void CompleteInput() => _input.Writer.TryComplete();

    public void WritePaste(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        QueueChunk(_bracketedPasteEnabled
            ? BracketedPastePrefix + text + BracketedPasteSuffix
            : text);
    }

    private void QueueInput(ref Span<char> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var copy = input.ToString();
        input = Span<char>.Empty;
        QueueChunk(copy);
    }

    private void QueueChunk(string chunk)
    {
        if (!_input.Writer.TryWrite(chunk))
        {
            throw new InvalidOperationException("Terminal input queue is closed.");
        }
    }

    private void TrackBracketedPasteMode(ref Span<char> output)
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

    private async Task WriteQueuedInputAsync()
    {
        try
        {
            await foreach (var chunk in _input.Reader.ReadAllAsync())
            {
                WriteToTerm(chunk);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException) when (Process?.HasExited != false)
        {
        }
    }
}
