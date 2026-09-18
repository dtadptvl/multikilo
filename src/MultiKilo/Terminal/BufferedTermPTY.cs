using System.Threading.Channels;
using EasyWindowsTerminalControl;

namespace MultiKilo.Terminal;

internal sealed class BufferedTermPTY : TermPTY
{
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly Task _writerTask;

    public BufferedTermPTY(int readBufferSize = 1024 * 64)
        : base(READ_BUFFER_SIZE: readBufferSize)
    {
        InterceptInputToTermApp = QueueInput;
        _writerTask = Task.Run(WriteQueuedInputAsync);
    }

    public void CompleteInput() => _input.Writer.TryComplete();

    private void QueueInput(ref Span<char> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var copy = input.ToString();
        input = Span<char>.Empty;

        if (!_input.Writer.TryWrite(copy))
        {
            throw new InvalidOperationException("Terminal input queue is closed.");
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
