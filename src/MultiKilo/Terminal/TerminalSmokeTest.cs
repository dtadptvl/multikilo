using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using EasyWindowsTerminalControl;
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
                sessions.Add(await SmokeSession.StartAsync(i + 1));
            }

            var host = new Grid { Width = 720, Height = 420, Background = Brushes.Black };
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

            foreach (var session in sessions)
            {
                foreach (UIElement child in host.Children)
                {
                    child.Visibility = Visibility.Hidden;
                }

                session.View.Visibility = Visibility.Visible;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }

            window.Width = 960;
            window.Height = 600;
            await Task.Delay(150);
            window.Width = 680;
            window.Height = 380;
            await Task.Delay(150);

            for (var i = 0; i < sessions.Count; i++)
            {
                var token = $"MULTIKILO_SMOKE_{i + 1}";
                sessions[i].Term.WriteToTerm(
                    $"[Console]::Write(([char]27).ToString() + \"[38;2;12;34;56m{token}\" + ([char]27) + \"[0m Tiếng Việt: Trường Sa, tiếng Việt ✓\" + [Environment]::NewLine)\r");
            }

            await Task.Delay(400);

            await sessions[1].TerminateAsync();
            if (sessions[0].Term.Process?.HasExited != false ||
                sessions[2].Term.Process?.HasExited != false)
            {
                return 21;
            }

            sessions[0].Term.WriteToTerm("exit\r");
            sessions[2].Term.WriteToTerm("exit\r");

            await Task.WhenAll(
                sessions[0].Lifetime.WaitAsync(TimeSpan.FromSeconds(10)),
                sessions[2].Lifetime.WaitAsync(TimeSpan.FromSeconds(10)));

            const string expected = "Tiếng Việt: Trường Sa, tiếng Việt ✓";
            if (!sessions[0].Term.GetConsoleText(stripVTCodes: false).Contains(expected, StringComparison.Ordinal) ||
                !sessions[2].Term.GetConsoleText(stripVTCodes: false).Contains(expected, StringComparison.Ordinal))
            {
                return 22;
            }

            return 0;
        }
        catch
        {
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

    private sealed class SmokeSession
    {
        private SmokeSession(JobObject job, BufferedTermPTY term, EasyTerminalControl view, Task lifetime)
        {
            Job = job;
            Term = term;
            View = view;
            Lifetime = lifetime;
        }

        public JobObject Job { get; }
        public BufferedTermPTY Term { get; }
        public EasyTerminalControl View { get; }
        public Task Lifetime { get; }

        public static async Task<SmokeSession> StartAsync(int index)
        {
            var job = new JobObject();
            var term = new BufferedTermPTY();
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            term.TermReady += (_, _) => ready.TrySetResult();

            var view = CreateView(term);
            var factory = new JobAssigningProcessFactory(job);
            var lifetime = Task.Run(() =>
                term.Start(
                    PwshCommand,
                    consoleWidth: 100,
                    consoleHeight: 28,
                    logOutput: true,
                    factory: factory,
                    workingDirectory: Environment.CurrentDirectory));

            var first = await Task.WhenAny(ready.Task, lifetime, Task.Delay(TimeSpan.FromSeconds(15)));
            if (first != ready.Task)
            {
                job.Terminate();
                try { await lifetime; } catch { }
                throw new InvalidOperationException($"Smoke session {index} did not start.");
            }

            await ready.Task;
            return new SmokeSession(job, term, view, lifetime);
        }

        public async Task TerminateAsync()
        {
            try { Job.Terminate(); } catch { }
            try { await Lifetime.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        }

        public async ValueTask DisposeAsync()
        {
            if (!Lifetime.IsCompleted)
            {
                await TerminateAsync();
            }

            Job.Dispose();
        }

        private static EasyTerminalControl CreateView(BufferedTermPTY term)
        {
            var theme = new TerminalTheme
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

            return new EasyTerminalControl
            {
                ConPTYTerm = term,
                StartupCommandLine = PwshCommand,
                Win32InputMode = true,
                InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey |
                               EasyTerminalControl.INPUT_CAPTURE.DirectionKeys,
                Theme = theme,
                FontFamilyWhenSettingTheme = new FontFamily("Cascadia Mono"),
                FontSizeWhenSettingTheme = 13
            };
        }
    }
}
