using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Terminal.Wpf;

namespace MultiKilo.Terminal;

internal static class TerminalSmokeTest
{
    private const string PwshCommand = "pwsh.exe -NoLogo -NoProfile -NoExit";
    private const int WmMouseWheel = 0x020A;

    public static async Task<int> RunAsync(string mode = "all")
    {
        try
        {
            return mode.ToLowerInvariant() switch
            {
                "clipboard" => await VerifyNativeClipboardAndScrollAsync(),
                "paste" => await VerifyNativeBracketedPasteAsync(),
                "sessions" => await VerifyNativeSessionsAsync(),
                "all" => await RunAllAsync(),
                _ => Fail(21, $"Unknown smoke-test mode: {mode}")
            };
        }
        catch (Exception ex)
        {
            WriteFailure(ex.ToString());
            return 20;
        }
    }

    private static async Task<int> RunAllAsync()
    {
        var clipboardAndScroll = await VerifyNativeClipboardAndScrollAsync();
        if (clipboardAndScroll != 0)
        {
            return clipboardAndScroll;
        }

        var paste = await VerifyNativeBracketedPasteAsync();
        if (paste != 0)
        {
            return paste;
        }

        return await VerifyNativeSessionsAsync();
    }

    private static async Task<int> VerifyNativeClipboardAndScrollAsync()
    {
        var terminal = CreateTerminalControl();
        using var host = new HiddenTerminalWindow(terminal);

        host.Show();
        await PumpAsync();
        terminal.SetTheme(CreateTheme(), "Cascadia Mono", 13);

        const string copyMarker = "MULTIKILO_NATIVE_COPY_OK Tiếng Việt";
        NativeMethods.TerminalSendOutput(
            terminal.NativeTerminalForTesting,
            copyMarker + "\r\n");

        NativeMethods.TerminalSelectAllForTesting(terminal.NativeTerminalForTesting);
        if (!NativeMethods.TerminalCopySelectionToClipboard(terminal.NativeTerminalForTesting))
        {
            return Fail(51, "Native terminal selection could not be copied to the clipboard.");
        }

        var copied = System.Windows.Clipboard.GetText();
        if (!copied.Contains(copyMarker, StringComparison.Ordinal))
        {
            return Fail(52, $"Native clipboard copy lost selected text. Clipboard={copied}");
        }

        var scrollText = new StringBuilder();
        for (var i = 0; i < 500; i++)
        {
            scrollText.Append("SCROLL-").Append(i).Append("\r\n");
        }

        NativeMethods.TerminalSendOutput(
            terminal.NativeTerminalForTesting,
            scrollText.ToString());

        if (!await WaitForConditionAsync(
                () => terminal.ScrollMaximumForTesting > 20 &&
                      terminal.ScrollValueForTesting > 20,
                TimeSpan.FromSeconds(5)))
        {
            return Fail(
                53,
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
                54,
                $"Mouse wheel did not scroll terminal. Before={before}, After={terminal.ScrollValueForTesting}.");
        }

        return 0;
    }

    private static async Task<int> VerifyNativeBracketedPasteAsync()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "MultiKilo-Smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var scriptPath = Path.Combine(tempDir, "bracket-smoke.ps1");
        File.WriteAllText(
            scriptPath,
            """
            Add-Type -TypeDefinition @"
            using System;
            using System.Runtime.InteropServices;
            public static class MultiKiloConsoleMode {
                [DllImport("kernel32.dll", SetLastError = true)]
                public static extern IntPtr GetStdHandle(int nStdHandle);
                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
                [DllImport("kernel32.dll", SetLastError = true)]
                [return: MarshalAs(UnmanagedType.Bool)]
                public static extern bool ReadConsoleW(IntPtr hConsoleInput, char[] buffer, uint charsToRead, out uint charsRead, IntPtr inputControl);
            }
            "@

            $stdin = [MultiKiloConsoleMode]::GetStdHandle(-10)
            [uint32]$mode = 0
            if (-not [MultiKiloConsoleMode]::GetConsoleMode($stdin, [ref]$mode)) {
                throw "GetConsoleMode failed"
            }

            $ENABLE_LINE_INPUT = 0x0002
            $ENABLE_ECHO_INPUT = 0x0004
            $ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200
            $rawMode = ($mode -band (-bnot ($ENABLE_LINE_INPUT -bor $ENABLE_ECHO_INPUT))) -bor $ENABLE_VIRTUAL_TERMINAL_INPUT
            if (-not [MultiKiloConsoleMode]::SetConsoleMode($stdin, $rawMode)) {
                throw "SetConsoleMode failed"
            }
            $esc = [char]27
            $crlf = ([char]13).ToString() + ([char]10)
            $start = "$esc[200~"
            $end = "$esc[201~"
            [Console]::Write("$esc[?2004h")
            [Console]::Write("MULTIKILO_BRACKET_READY" + $crlf)

            $builder = [System.Text.StringBuilder]::new()
            $buffer = New-Object char[] 4096
            while ($builder.Length -lt 200000) {
                [uint32]$read = 0
                if (-not [MultiKiloConsoleMode]::ReadConsoleW($stdin, $buffer, [uint32]$buffer.Length, [ref]$read, [IntPtr]::Zero)) {
                    throw "ReadConsoleW failed"
                }
                if ($read -eq 0) { break }
                [void]$builder.Append($buffer, 0, [int]$read)
                if ($builder.ToString().EndsWith($end)) { break }
            }

            $text = $builder.ToString()
            $isBracketed = $text.StartsWith($start) -and $text.EndsWith($end)
            $hasUnicode = $text.Contains("Tiếng Việt")
            $crCount = ($text.ToCharArray() | Where-Object { $_ -eq [char]13 }).Count
            [Console]::Write("MULTIKILO_BRACKET_RESULT:${isBracketed}:${hasUnicode}:${crCount}" + $crlf)
            """,
            new UTF8Encoding(false));

        var terminal = CreateTerminalControl();
        using var host = new HiddenTerminalWindow(terminal);
        var output = new StringBuilder();
        var outputGate = new object();
        uint? sessionExitCode = null;

        terminal.SessionOutputForTesting += OnOutput;
        terminal.SessionExited += OnExit;

        try
        {
            host.Show();
            await PumpAsync();
            terminal.SetTheme(CreateTheme(), "Cascadia Mono", 13);

            terminal.StartSession(
                $"pwsh.exe -NoLogo -NoProfile -File \"{scriptPath}\"",
                tempDir);

            if (!await WaitForConditionAsync(
                    () => ContainsOutput("MULTIKILO_BRACKET_READY"),
                    TimeSpan.FromSeconds(10)))
            {
                string snapshot;
                lock (outputGate)
                {
                    snapshot = output.ToString();
                }

                return Fail(
                    55,
                    "Native ConPTY test app never enabled bracketed paste mode. " +
                    $"Running={terminal.IsSessionRunning}; ExitCode={sessionExitCode?.ToString() ?? "<none>"}; " +
                    $"Output={snapshot.Replace("\x1b", "<ESC>")}");
            }

            var payload = Create288LinePastePayload();
            System.Windows.Clipboard.SetText(payload);

            var pasteTask = Task.Run(
                () => NativeMethods.TerminalPasteFromClipboard(
                    terminal.NativeTerminalForTesting));

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.Input).Task.WaitAsync(TimeSpan.FromSeconds(1));

            await pasteTask.WaitAsync(TimeSpan.FromSeconds(2));

            if (!await WaitForConditionAsync(
                    () => ContainsOutput("MULTIKILO_BRACKET_RESULT:True:True:288"),
                    TimeSpan.FromSeconds(10)))
            {
                string snapshot;
                lock (outputGate)
                {
                    snapshot = output.ToString();
                }

                return Fail(
                    56,
                    "288-line native paste did not arrive as one bracketed transaction. " +
                    $"Output={snapshot.Replace("\x1b", "<ESC>")}");
            }

            return 0;
        }
        finally
        {
            terminal.SessionOutputForTesting -= OnOutput;
            terminal.SessionExited -= OnExit;
            terminal.TerminateSession();

            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
            }
        }

        void OnOutput(string data)
        {
            lock (outputGate)
            {
                output.Append(data);
            }
        }

        void OnExit(uint exitCode)
        {
            sessionExitCode = exitCode;
        }

        bool ContainsOutput(string value)
        {
            lock (outputGate)
            {
                return output.ToString().Contains(value, StringComparison.Ordinal);
            }
        }
    }

    private static async Task<int> VerifyNativeSessionsAsync()
    {
        var hostGrid = new Grid
        {
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
            Content = hostGrid
        };

        var sessions = Enumerable.Range(0, 3)
            .Select(_ => new NativeSmokeSession(CreateTerminalControl()))
            .ToList();

        try
        {
            for (var i = 0; i < sessions.Count; i++)
            {
                sessions[i].Terminal.Visibility =
                    i == 0 ? Visibility.Visible : Visibility.Hidden;
                hostGrid.Children.Add(sessions[i].Terminal);
            }

            window.Show();
            await PumpAsync();

            foreach (var session in sessions)
            {
                session.Terminal.SetTheme(CreateTheme(), "Cascadia Mono", 13);
                session.Start(PwshCommand, Environment.CurrentDirectory);
            }

            for (var i = 0; i < sessions.Count; i++)
            {
                SendCommand(
                    sessions[i].Terminal,
                    $"Write-Output 'MULTIKILO_NATIVE_SESSION_{i + 1}'");
            }

            if (!await WaitForConditionAsync(
                    () => sessions.Select(
                            static (session, index) =>
                                session.ContainsOutput($"MULTIKILO_NATIVE_SESSION_{index + 1}"))
                        .All(static ready => ready),
                    TimeSpan.FromSeconds(12)))
            {
                return Fail(57, "Native keyboard -> native ConPTY path failed for one or more sessions.");
            }

            for (var selected = 0; selected < sessions.Count; selected++)
            {
                for (var i = 0; i < sessions.Count; i++)
                {
                    sessions[i].Terminal.Visibility =
                        i == selected ? Visibility.Visible : Visibility.Hidden;
                }

                await PumpAsync();
            }

            window.Width = 980;
            window.Height = 620;
            await Task.Delay(150);
            window.Width = 700;
            window.Height = 390;
            await Task.Delay(150);

            if (sessions.Any(static session => !session.Terminal.IsSessionRunning))
            {
                return Fail(58, "Project switching or resize stopped a live native session.");
            }

            sessions[1].Terminal.TerminateSession();

            await sessions[1].Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

            if (!sessions[0].Terminal.IsSessionRunning ||
                !sessions[2].Terminal.IsSessionRunning)
            {
                return Fail(60, "Terminating one native Job Object affected another project session.");
            }

            sessions[0].Terminal.TerminateSession();
            sessions[2].Terminal.TerminateSession();

            return 0;
        }
        finally
        {
            foreach (var session in sessions)
            {
                session.Terminal.TerminateSession();
            }

            window.Close();
        }
    }

    private static void SendCommand(TerminalControl terminal, string command)
    {
        foreach (var ch in command)
        {
            NativeMethods.TerminalSendCharEvent(
                terminal.NativeTerminalForTesting,
                ch,
                0,
                0);
        }

        const ushort returnScanCode = 0x1C;
        NativeMethods.TerminalSendKeyEvent(
            terminal.NativeTerminalForTesting,
            0x0D,
            returnScanCode,
            0,
            true);
        NativeMethods.TerminalSendCharEvent(
            terminal.NativeTerminalForTesting,
            '\r',
            returnScanCode,
            0);
        NativeMethods.TerminalSendKeyEvent(
            terminal.NativeTerminalForTesting,
            0x0D,
            returnScanCode,
            0,
            false);
    }

    private static TerminalControl CreateTerminalControl() =>
        new()
        {
            AutoResize = true,
            Width = 760,
            Height = 440,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
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

    private static string Create288LinePastePayload()
    {
        var builder = new StringBuilder();

        for (var i = 0; i < 288; i++)
        {
            builder
                .Append("LINE-")
                .Append(i.ToString("D3"))
                .Append(" Tiếng Việt — Trường Sa — Unicode ✓")
                .Append('\n');
        }

        return builder.ToString();
    }

    private static async Task PumpAsync() =>
        await System.Windows.Application.Current.Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.ApplicationIdle);

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

        return condition();
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

    private sealed class HiddenTerminalWindow : IDisposable
    {
        private readonly System.Windows.Window _window;

        public HiddenTerminalWindow(TerminalControl terminal)
        {
            _window = new System.Windows.Window
            {
                Width = 760,
                Height = 440,
                Left = -10000,
                Top = -10000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = terminal
            };
        }

        public void Show() => _window.Show();

        public void Dispose() => _window.Close();
    }

    private sealed class NativeSmokeSession
    {
        private readonly object _outputGate = new();
        private readonly StringBuilder _output = new();

        public NativeSmokeSession(TerminalControl terminal)
        {
            Terminal = terminal;
            Terminal.SessionOutputForTesting += data =>
            {
                lock (_outputGate)
                {
                    _output.Append(data);
                }
            };
            Terminal.SessionExited += _ => Exited.TrySetResult(true);
        }

        public TerminalControl Terminal { get; }

        public TaskCompletionSource<bool> Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start(string commandLine, string workingDirectory) =>
            Terminal.StartSession(commandLine, workingDirectory);

        public bool ContainsOutput(string value)
        {
            lock (_outputGate)
            {
                return _output.ToString().Contains(value, StringComparison.Ordinal);
            }
        }
    }
}
