using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;

namespace MultiKilo.Terminal;

internal static class TerminalSmokeTest
{
    private const string PwshCommand = "pwsh.exe -NoLogo -NoProfile -NoExit";
    private const int WmMouseWheel = 0x020A;

    public static async Task<int> RunAsync()
    {
        try
        {
            var clipboardResult = await VerifyNativeClipboardAndScrollAsync();
            if (clipboardResult != 0)
            {
                return clipboardResult;
            }

            return await VerifyConPtySessionsAsync();
        }
        catch (Exception ex)
        {
            WriteFailure(ex.ToString());
            return 20;
        }
    }

    private static async Task<int> VerifyNativeClipboardAndScrollAsync()
    {
        var connection = new ProbeConnection();
        var terminal = new TerminalControl
        {
            AutoResize = true,
            Width = 760,
            Height = 440
        };

        var window = new System.Windows.Window
        {
            Width = 760,
            Height = 440,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = terminal
        };

        try
        {
            window.Show();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle);

            terminal.SetTheme(CreateTheme(), "Cascadia Mono", 13);
            terminal.Connection = connection;

            NativeMethods.TerminalSendCharEvent(
                terminal.NativeTerminalForTesting,
                'k',
                0,
                0);
            var keyboardProbe = await connection.ReadInputAsync(TimeSpan.FromSeconds(2));
            if (keyboardProbe != "k")
            {
                return Fail(
                    41,
                    $"Native printable keyboard input mismatch. Received={keyboardProbe.Replace("\x1b", "<ESC>")}.");
            }

            connection.EmitOutput("\x1b[?2004h");
            await Task.Delay(100);

            var payload = CreateLargePastePayload();
            System.Windows.Clipboard.SetText(payload);

            if (terminal.ClipboardShortcutPassesThroughForTesting(0x56, ctrl: true, shift: false))
            {
                return Fail(29, "Ctrl+V text clipboard incorrectly passed through to Kilo.");
            }

            var pasteTask = Task.Run(
                () => NativeMethods.TerminalPasteFromClipboard(terminal.NativeTerminalForTesting));

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Input).Task.WaitAsync(TimeSpan.FromSeconds(1));

            await pasteTask.WaitAsync(TimeSpan.FromSeconds(5));
            var pasted = await connection.ReadInputAsync(TimeSpan.FromSeconds(5));

            if (!pasted.StartsWith("\x1b[200~", StringComparison.Ordinal) ||
                !pasted.EndsWith("\x1b[201~", StringComparison.Ordinal))
            {
                return Fail(30, "Native 1 MB text paste was not bracketed.");
            }

            if (pasted.Contains('\n'))
            {
                return Fail(31, "Native paste did not apply Windows Terminal newline filtering.");
            }

            if (!pasted.Contains("Tiếng Việt", StringComparison.Ordinal))
            {
                return Fail(32, "Native paste lost Unicode/Vietnamese content.");
            }

            var pixels = new byte[] { 0x10, 0x20, 0x30, 0xFF };
            var bitmap = BitmapSource.Create(
                1,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                4);
            System.Windows.Clipboard.SetImage(bitmap);

            if (!NativeMethods.TerminalClipboardContainsImage())
            {
                return Fail(33, "Native clipboard image detection failed.");
            }

            if (!terminal.ClipboardShortcutPassesThroughForTesting(0x56, ctrl: true, shift: false))
            {
                return Fail(40, "Ctrl+V image clipboard was swallowed by the terminal instead of passing through to Kilo.");
            }

            var scrollText = new StringBuilder();
            for (var i = 0; i < 500; i++)
            {
                scrollText.Append("SCROLL-").Append(i).Append("\r\n");
            }

            connection.EmitOutput(scrollText.ToString());

            if (!await WaitForConditionAsync(
                    () => terminal.ScrollMaximumForTesting > 20 &&
                          terminal.ScrollValueForTesting > 20,
                    TimeSpan.FromSeconds(5)))
            {
                return Fail(
                    34,
                    $"Terminal never built scrollback. Value={terminal.ScrollValueForTesting}, Max={terminal.ScrollMaximumForTesting}.");
            }

            var before = terminal.ScrollValueForTesting;
            var wheelUp = new IntPtr(120 << 16);
            SendMessageW(terminal.NativeHwndForTesting, WmMouseWheel, wheelUp, IntPtr.Zero);

            if (!await WaitForConditionAsync(
                    () => terminal.ScrollValueForTesting < before,
                    TimeSpan.FromSeconds(3)))
            {
                return Fail(
                    35,
                    $"Mouse wheel did not scroll terminal. Before={before}, After={terminal.ScrollValueForTesting}.");
            }

            return 0;
        }
        finally
        {
            terminal.Connection = null!;
            window.Close();
        }
    }

    private static async Task<int> VerifyConPtySessionsAsync()
    {
        var host = new Grid
        {
            Width = 760,
            Height = 440,
            Background = System.Windows.Media.Brushes.Black
        };
        var window = new System.Windows.Window
        {
            Width = 760,
            Height = 440,
            Left = -10000,
            Top = -10000,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Content = host
        };

        var sessions = new List<SmokeSession>();

        try
        {
            for (var i = 0; i < 3; i++)
            {
                var session = new SmokeSession(CreateTerminalControl());
                sessions.Add(session);
                session.Terminal.Visibility = i == 0 ? Visibility.Visible : Visibility.Hidden;
                host.Children.Add(session.Terminal);
            }

            window.Show();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle);

            foreach (var session in sessions)
            {
                await session.StartAsync();
                session.Terminal.Connection = session.Term;
                session.Term.Win32DirectInputMode(false);

                if (session.Terminal.Columns > 0 && session.Terminal.Rows > 0)
                {
                    session.Term.Resize(session.Terminal.Columns, session.Terminal.Rows);
                }
            }

            await Task.Delay(250);

            var keyboardCommand = "Write-Output 'MULTIKILO_KEYBOARD_OK'";
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                foreach (var ch in keyboardCommand)
                {
                    NativeMethods.TerminalSendCharEvent(
                        sessions[0].Terminal.NativeTerminalForTesting,
                        ch,
                        0,
                        0);
                }

                const ushort VkReturn = 0x0D;
                const ushort ReturnScanCode = 0x1C;
                NativeMethods.TerminalSendKeyEvent(
                    sessions[0].Terminal.NativeTerminalForTesting,
                    VkReturn,
                    ReturnScanCode,
                    0,
                    true);
                NativeMethods.TerminalSendKeyEvent(
                    sessions[0].Terminal.NativeTerminalForTesting,
                    VkReturn,
                    ReturnScanCode,
                    0,
                    false);
            });

            if (!await WaitForConditionAsync(
                    () => sessions[0].ContainsOutput("MULTIKILO_KEYBOARD_OK"),
                    TimeSpan.FromSeconds(8)))
            {
                return Fail(36, "TerminalCore -> standard VT input -> ConPTY keyboard path failed.");
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                var token = $"MULTIKILO_UNICODE_{i + 1}";
                sessions[i].Term.WriteToTerm(
                    $"[Console]::Write(([char]27).ToString() + \"[38;2;12;34;56m{token}\" + ([char]27) + \"[0m Tiếng Việt: Trường Sa, tiếng Việt ✓\" + [Environment]::NewLine)\r");
            }

            if (!await WaitForConditionAsync(
                    () => sessions.All(static session => session.ContainsOutput("Tiếng Việt: Trường Sa, tiếng Việt ✓")),
                    TimeSpan.FromSeconds(8)))
            {
                return Fail(37, "ANSI/Unicode output did not survive ConPTY/terminal path.");
            }

            var originalPids = sessions.Select(static session => session.Pid).ToArray();

            for (var selected = 0; selected < sessions.Count; selected++)
            {
                for (var i = 0; i < sessions.Count; i++)
                {
                    sessions[i].Terminal.Visibility =
                        i == selected ? Visibility.Visible : Visibility.Hidden;
                }

                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => { },
                    DispatcherPriority.ApplicationIdle);
            }

            window.Width = 980;
            window.Height = 620;
            await Task.Delay(150);
            window.Width = 700;
            window.Height = 390;
            await Task.Delay(150);

            if (!sessions.Select(static session => session.Pid).SequenceEqual(originalPids))
            {
                return Fail(38, "Project switching/re-layout restarted a live session.");
            }

            await sessions[1].TerminateAsync();
            if (sessions[0].HasExited || sessions[2].HasExited)
            {
                return Fail(39, "Terminating one Job Object affected another session.");
            }

            await Task.WhenAll(
                sessions[0].TerminateAsync(),
                sessions[2].TerminateAsync());

            return 0;
        }
        finally
        {
            foreach (var session in sessions)
            {
                session.Terminal.Connection = null!;
                await session.DisposeAsync();
            }

            window.Close();
        }
    }

    private static TerminalControl CreateTerminalControl() =>
        new()
        {
            AutoResize = true,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };

    private static TerminalTheme CreateTheme() =>
        new()
        {
            DefaultBackground = 0x0C0C0C,
            DefaultForeground = 0xCCCCCC,
            DefaultSelectionBackground = 0x777777,
            CursorStyle = CursorStyle.BlinkingBar,
            ColorTable =
            [
                0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1,
                0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
                0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9,
                0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2
            ]
        };

    private static string CreateLargePastePayload()
    {
        const string line =
            "Tiếng Việt — Trường Sa — Unicode ✓ — MultiKilo native terminal paste 0123456789\n";
        const int targetBytes = 1_048_576;

        var lineBytes = Encoding.UTF8.GetByteCount(line);
        var repeats = (targetBytes + lineBytes - 1) / lineBytes;
        var builder = new StringBuilder(line.Length * repeats);

        for (var i = 0; i < repeats; i++)
        {
            builder.Append(line);
        }

        return builder.ToString();
    }

    private static async Task<bool> WaitForConditionAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(40);
        }

        return false;
    }

    private static int Fail(int code, string details)
    {
        WriteFailure(details);
        return code;
    }

    private static void WriteFailure(string details)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"),
                details);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    private sealed class ProbeConnection : ITerminalConnection
    {
        private readonly Channel<string> _input =
            Channel.CreateUnbounded<string>();

        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

        public void Start()
        {
        }

        public void WriteInput(string data)
        {
            _input.Writer.TryWrite(data);
        }

        public void Resize(uint rows, uint columns)
        {
        }

        public void Close()
        {
            _input.Writer.TryComplete();
        }

        public void EmitOutput(string data) =>
            TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(data));

        public async Task<string> ReadInputAsync(TimeSpan timeout) =>
            await _input.Reader.ReadAsync().AsTask().WaitAsync(timeout);
    }

    private sealed class SmokeSession
    {
        private readonly object _outputGate = new();
        private readonly StringBuilder _output = new();
        private Task? _lifetime;

        public SmokeSession(TerminalControl terminal)
        {
            Terminal = terminal;
            Job = new JobObject();
            Term = new TermPTY(READ_BUFFER_SIZE: 1024 * 64);
            Term.TerminalOutput += (_, e) =>
            {
                lock (_outputGate)
                {
                    _output.Append(e.Data);
                }
            };
        }

        public JobObject Job { get; }
        public TermPTY Term { get; }
        public TerminalControl Terminal { get; }
        public bool HasExited => Term.Process?.HasExited != false;

        public int Pid =>
            Term.Process is EasyWindowsTerminalControl.Internals.ProcessFactory.WrappedProcess wrapped
                ? wrapped.Pid
                : -1;

        public async Task StartAsync()
        {
            var ready = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Term.TermReady += OnReady;

            try
            {
                var factory = new JobAssigningProcessFactory(Job);
                _lifetime = Task.Run(() =>
                    Term.Start(
                        PwshCommand,
                        consoleWidth: 100,
                        consoleHeight: 28,
                        logOutput: false,
                        factory: factory,
                        workingDirectory: Environment.CurrentDirectory));

                var first = await Task.WhenAny(
                    ready.Task,
                    _lifetime,
                    Task.Delay(TimeSpan.FromSeconds(15)));

                if (first != ready.Task)
                {
                    throw new InvalidOperationException(
                        "ConPTY smoke session failed to become ready.");
                }

                await ready.Task;
            }
            finally
            {
                Term.TermReady -= OnReady;
            }

            void OnReady(object? sender, EventArgs e) => ready.TrySetResult();
        }

        public bool ContainsOutput(string value)
        {
            lock (_outputGate)
            {
                return _output.ToString().Contains(value, StringComparison.Ordinal);
            }
        }

        public async Task TerminateAsync()
        {
            Term.CompleteInput();
            try
            {
                Job.Terminate();
            }
            catch
            {
            }

            if (_lifetime is not null)
            {
                try
                {
                    await _lifetime.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!HasExited)
            {
                await TerminateAsync();
            }

            try
            {
                Term.CloseStdinToApp();
            }
            catch
            {
            }

            Job.Dispose();
        }
    }
}
