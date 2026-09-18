using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;

namespace MultiKilo.Terminal;

internal static class TerminalSmokeTest
{
    private const string PwshCommand = "pwsh.exe -NoLogo -NoProfile -NoExit";

    public static async Task<int> RunAsync()
    {
        var sessions = new List<SmokeSession>();
        Window? window = null;

        try
        {
            for (var i = 0; i < 3; i++)
            {
                sessions.Add(await SmokeSession.StartAsync());
            }

            var host = new Grid
            {
                Width = 720,
                Height = 420,
                Background = System.Windows.Media.Brushes.Black
            };

            foreach (var session in sessions)
            {
                session.View.Visibility = Visibility.Hidden;
                host.Children.Add(session.View);
            }

            window = new Window
            {
                Width = 720,
                Height = 420,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = host
            };
            window.Show();

            await Task.WhenAll(sessions.Select(static session =>
                session.Connection.IoStarted.WaitAsync(TimeSpan.FromSeconds(10))));

            foreach (var session in sessions)
            {
                foreach (UIElement child in host.Children)
                {
                    child.Visibility = Visibility.Hidden;
                }

                session.View.Visibility = Visibility.Visible;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                    () => { },
                    DispatcherPriority.ApplicationIdle);
            }

            window.Width = 960;
            window.Height = 600;
            await Task.Delay(150);
            window.Width = 680;
            window.Height = 380;
            await Task.Delay(150);

            foreach (UIElement child in host.Children)
            {
                child.Visibility = Visibility.Hidden;
            }

            sessions[0].View.Visibility = Visibility.Visible;
            sessions[0].View.Focus();
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle);

            var terminalHwnd = NativeTerminalClipboard.FindVisibleTerminalDescendant(
                new WindowInteropHelper(window).Handle);
            if (terminalHwnd == IntPtr.Zero)
            {
                return 23;
            }

            var largePaste = CreateLargePastePayload();
            var expectedPasteBytes = Encoding.UTF8.GetByteCount(largePaste) + 12;

            var encoded = NativeConPtyConnection.EncodeNativePaste(
                largePaste,
                bracketed: true);

            if (encoded.Length != expectedPasteBytes ||
                !encoded.AsSpan(0, 6).SequenceEqual("\x1b[200~"u8) ||
                !encoded.AsSpan(encoded.Length - 6, 6).SequenceEqual("\x1b[201~"u8))
            {
                return Fail(
                    27,
                    $"1 MB native paste encoding mismatch. ExpectedBytes={expectedPasteBytes}, " +
                    $"ActualBytes={encoded.Length}.");
            }

            var probe = new ClipboardProbeConnection();
            var probeTerminal = new TerminalControl
            {
                AutoResize = true,
                Visibility = Visibility.Visible
            };
            host.Children.Add(probeTerminal);
            probeTerminal.Connection = probe;
            probeTerminal.Focus();

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle);

            var probeHwnd = NativeTerminalClipboard.FindVisibleTerminalDescendant(
                new WindowInteropHelper(window).Handle);
            if (probeHwnd == IntPtr.Zero)
            {
                return Fail(29, "Could not locate native HwndTerminal for clipboard probe.");
            }

            System.Windows.Clipboard.SetText(largePaste);
            var pasteTimer = Stopwatch.StartNew();
            NativeTerminalClipboard.InvokeNativeCopyOrPaste(probeHwnd);
            pasteTimer.Stop();

            if (pasteTimer.Elapsed > TimeSpan.FromSeconds(1) ||
                probe.ReceivedTextLength != largePaste.Length ||
                probe.ReceivedTextFirst != largePaste[0] ||
                probe.ReceivedTextLast != largePaste[^1])
            {
                return Fail(
                    24,
                    $"Native 1 MB clipboard dispatch mismatch. Ms={pasteTimer.Elapsed.TotalMilliseconds:F0}, " +
                    $"ExpectedChars={largePaste.Length}, ReceivedChars={probe.ReceivedTextLength}.");
            }

            probeTerminal.Connection = null!;
            host.Children.Remove(probeTerminal);

            sessions[0].Connection.TrackBracketedPasteMode("\x1b[?1;2004;1004h");
            if (!sessions[0].Connection.BracketedPasteEnabled)
            {
                return Fail(26, "Bracketed-paste mode tracker did not recognise DECSET 2004.");
            }

            const string smallPaste = "Tiếng Việt paste integration\nline 2\nline 3";
            var smallExpectedBytes =
                NativeConPtyConnection.EncodeNativePaste(smallPaste, bracketed: true).Length;

            System.Windows.Clipboard.SetText(smallPaste);
            sessions[0].Connection.BeginNativePaste();
            try
            {
                NativeTerminalClipboard.InvokeNativeCopyOrPaste(terminalHwnd);
            }
            finally
            {
                sessions[0].Connection.EndNativePaste();
            }

            if (!await WaitForConditionAsync(
                    () => sessions[0].Connection.LastNativePasteWrittenBytes == smallExpectedBytes,
                    TimeSpan.FromSeconds(5)))
            {
                return Fail(
                    25,
                    $"Small bracketed paste did not reach ConPTY. ExpectedBytes={smallExpectedBytes}, " +
                    $"EncodedBytes={sessions[0].Connection.LastNativePasteBytes}, " +
                    $"WrittenBytes={sessions[0].Connection.LastNativePasteWrittenBytes}, " +
                    $"Bracketed={sessions[0].Connection.LastNativePasteWasBracketed}.");
            }

            await sessions[0].TerminateAsync();
            if (sessions[1].Connection.ProcessHasExited ||
                sessions[2].Connection.ProcessHasExited)
            {
                return 21;
            }

            for (var i = 1; i < sessions.Count; i++)
            {
                var token = $"MULTIKILO_SMOKE_{i + 1}";
                sessions[i].Connection.WriteRawInput(
                    $"[Console]::Write(([char]27).ToString() + \"[38;2;12;34;56m{token}\" + ([char]27) + \"[0m Tiếng Việt: Trường Sa, tiếng Việt ✓\" + [Environment]::NewLine)\r");
            }

            await Task.Delay(500);

            const string expected = "Tiếng Việt: Trường Sa, tiếng Việt ✓";
            if (!sessions[1].Connection.GetCapturedOutput().Contains(expected, StringComparison.Ordinal) ||
                !sessions[2].Connection.GetCapturedOutput().Contains(expected, StringComparison.Ordinal))
            {
                return 22;
            }

            await sessions[1].TerminateAsync();
            if (sessions[2].Connection.ProcessHasExited)
            {
                return 28;
            }

            await sessions[2].TerminateAsync();
            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(AppContext.BaseDirectory, "smoke-error.txt"),
                    ex.ToString());
            }
            catch
            {
            }

            return 20;
        }
        finally
        {
            window?.Close();
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
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

            await Task.Delay(25);
        }

        return false;
    }

    private static async Task<bool> WaitForOutputAsync(
        NativeConPtyConnection connection,
        string expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (connection.GetCapturedOutput().Contains(expected, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    private static int Fail(int code, string details)
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

        return code;
    }

    private static string Tail(string text)
    {
        const int maxChars = 4000;
        return text.Length <= maxChars ? text : text[^maxChars..];
    }

    private static string CreateLargePastePayload()
    {
        const string line = "Tiếng Việt — Trường Sa — Unicode ✓ — MultiKilo paste test 0123456789\n";
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

    private sealed class ClipboardProbeConnection : ITerminalConnection
    {
        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

        public int ReceivedTextLength { get; private set; }
        public char ReceivedTextFirst { get; private set; }
        public char ReceivedTextLast { get; private set; }

        public void Start()
        {
        }

        public void WriteInput(string data)
        {
            ReceivedTextLength = data.Length;
            if (data.Length > 0)
            {
                ReceivedTextFirst = data[0];
                ReceivedTextLast = data[^1];
            }
        }

        public void Resize(uint rows, uint columns)
        {
        }

        public void Close()
        {
        }
    }

    private sealed class SmokeSession
    {
        private SmokeSession(
            JobObject job,
            NativeConPtyConnection connection,
            TerminalSessionView view)
        {
            Job = job;
            Connection = connection;
            View = view;
        }

        public JobObject Job { get; }
        public NativeConPtyConnection Connection { get; }
        public TerminalSessionView View { get; }

        public static async Task<SmokeSession> StartAsync()
        {
            var job = new JobObject();
            var connection = new NativeConPtyConnection(
                PwshCommand,
                Environment.CurrentDirectory,
                job,
                captureOutput: true);
            var view = new TerminalSessionView(connection);

            await connection.StartProcessAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return new SmokeSession(job, connection, view);
        }

        public async Task TerminateAsync()
        {
            Connection.CompleteInput();
            try { Job.Terminate(); } catch { }
            try { await Connection.Completion.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            if (!Connection.ProcessHasExited)
            {
                await TerminateAsync();
            }

            View.Disconnect();
            Connection.Dispose();
            Job.Dispose();
        }
    }
}
