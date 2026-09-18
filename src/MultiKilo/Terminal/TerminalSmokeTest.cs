using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

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
            var rawReaderCommand =
                "$e=[char]27;[Console]::Write($e+'[?2004h');" +
                "$s=[Console]::OpenStandardInput();" +
                "$b=New-Object byte[] 65536;" +
                $"$target={expectedPasteBytes};$total=0;" +
                "while($total -lt $target){" +
                "$want=[Math]::Min($b.Length,$target-$total);" +
                "$n=$s.Read($b,0,$want);if($n -le 0){break};$total+=$n};" +
                "[Console]::WriteLine('RAWPASTE_BYTES='+$total)";

            sessions[0].Connection.WriteRawInput(rawReaderCommand + "\r");
            await Task.Delay(400);

            System.Windows.Clipboard.SetText(largePaste);

            var pasteTimer = Stopwatch.StartNew();
            sessions[0].Connection.BeginNativePaste();
            try
            {
                NativeTerminalClipboard.InvokeNativeCopyOrPaste(terminalHwnd);
            }
            finally
            {
                sessions[0].Connection.EndNativePaste();
            }
            pasteTimer.Stop();

            if (pasteTimer.Elapsed > TimeSpan.FromSeconds(1))
            {
                return 24;
            }

            if (!await WaitForOutputAsync(
                    sessions[0].Connection,
                    $"RAWPASTE_BYTES={expectedPasteBytes}",
                    TimeSpan.FromSeconds(10)))
            {
                return 25;
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                var token = $"MULTIKILO_SMOKE_{i + 1}";
                sessions[i].Connection.WriteRawInput(
                    $"[Console]::Write(([char]27).ToString() + \"[38;2;12;34;56m{token}\" + ([char]27) + \"[0m Tiếng Việt: Trường Sa, tiếng Việt ✓\" + [Environment]::NewLine)\r");
            }

            await Task.Delay(500);

            await sessions[1].TerminateAsync();
            if (sessions[0].Connection.ProcessHasExited ||
                sessions[2].Connection.ProcessHasExited)
            {
                return 21;
            }

            const string expected = "Tiếng Việt: Trường Sa, tiếng Việt ✓";
            if (!sessions[0].Connection.GetCapturedOutput().Contains(expected, StringComparison.Ordinal) ||
                !sessions[2].Connection.GetCapturedOutput().Contains(expected, StringComparison.Ordinal))
            {
                return 22;
            }

            await Task.WhenAll(
                sessions[0].TerminateAsync(),
                sessions[2].TerminateAsync());

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

    private static string CreateLargePastePayload()
    {
        const string line = "Tiếng Việt — Trường Sa — Unicode ✓ — MultiKilo paste test 0123456789\n";
        var builder = new StringBuilder(1_100_000);

        while (Encoding.UTF8.GetByteCount(builder.ToString()) < 1_048_576)
        {
            builder.Append(line);
        }

        return builder.ToString();
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
