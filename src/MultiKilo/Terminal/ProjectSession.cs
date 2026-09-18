using System.Windows;
using System.Windows.Media;
using EasyWindowsTerminalControl;
using Microsoft.Terminal.Wpf;
using MultiKilo.Models;

namespace MultiKilo.Terminal;

public enum ProjectSessionState
{
    Stopped,
    Starting,
    Running
}

public sealed class ProjectSession
{
    private const string PwshCommand = "pwsh.exe -NoLogo -NoProfile -NoExit";

    private JobObject? _job;
    private BufferedTermPTY? _term;
    private Task? _termLifetimeTask;
    private int _generation;

    public ProjectSession(ProjectDefinition project)
    {
        Project = project;
    }

    public ProjectDefinition Project { get; }
    public ProjectSessionState State { get; private set; } = ProjectSessionState.Stopped;
    public EasyTerminalControl? View { get; private set; }
    public bool IsLive => State is ProjectSessionState.Starting or ProjectSessionState.Running;

    public event EventHandler? StateChanged;

    public string GetSelectedText() => View?.Terminal.GetSelectedText() ?? string.Empty;

    public void Paste(string text) => _term?.WritePaste(text);

    public async Task StartAsync(bool continueSession)
    {
        if (IsLive)
        {
            return;
        }

        State = ProjectSessionState.Starting;
        RaiseStateChanged();

        var generation = ++_generation;
        var job = new JobObject();
        var term = new BufferedTermPTY();
        var view = CreateTerminalView(term, Project.Folder);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        term.TermReady += (_, _) => ready.TrySetResult();

        _job = job;
        _term = term;
        View = view;

        try
        {
            var factory = new JobAssigningProcessFactory(job);
            var lifetimeTask = Task.Run(() =>
                term.Start(
                    PwshCommand,
                    consoleWidth: 120,
                    consoleHeight: 32,
                    logOutput: false,
                    factory: factory,
                    workingDirectory: Project.Folder));

            _termLifetimeTask = lifetimeTask;

            var first = await Task.WhenAny(ready.Task, lifetimeTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (first == lifetimeTask)
            {
                await lifetimeTask;
                throw new InvalidOperationException("PowerShell exited before the terminal became ready.");
            }

            if (first != ready.Task)
            {
                throw new TimeoutException("Timed out while starting the ConPTY session.");
            }

            await ready.Task;

            var kilo = continueSession ? "kilo --auto --continue" : "kilo --auto";
            term.WriteToTerm(kilo + "; exit\r");

            State = ProjectSessionState.Running;
            RaiseStateChanged();

            _ = ObserveExitAsync(lifetimeTask, generation);
        }
        catch
        {
            await CleanupFailedStartAsync();
            throw;
        }
    }

    public async Task RestartAsync()
    {
        await TerminateAsync();
        await StartAsync(continueSession: true);
    }

    public async Task TerminateAsync()
    {
        if (!IsLive && _job is null)
        {
            State = ProjectSessionState.Stopped;
            RaiseStateChanged();
            return;
        }

        ++_generation;

        var job = _job;
        var term = _term;
        var lifetime = _termLifetimeTask;

        _job = null;
        _term = null;
        _termLifetimeTask = null;

        term?.CompleteInput();

        try
        {
            term?.CloseStdinToApp();
        }
        catch
        {
        }

        try
        {
            job?.Terminate();
        }
        finally
        {
            if (lifetime is not null)
            {
                try
                {
                    await lifetime.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }

            job?.Dispose();
            State = ProjectSessionState.Stopped;
            RaiseStateChanged();
        }
    }

    private async Task ObserveExitAsync(Task lifetimeTask, int generation)
    {
        try
        {
            await lifetimeTask;
        }
        catch
        {
        }

        if (generation != _generation)
        {
            return;
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _term?.CompleteInput();
            _job?.Dispose();
            _job = null;
            _term = null;
            _termLifetimeTask = null;
            State = ProjectSessionState.Stopped;
            RaiseStateChanged();
        });
    }

    private async Task CleanupFailedStartAsync()
    {
        ++_generation;
        _term?.CompleteInput();

        try
        {
            _job?.Terminate();
        }
        catch
        {
        }

        if (_termLifetimeTask is not null)
        {
            try
            {
                await _termLifetimeTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }

        _job?.Dispose();
        _job = null;
        _term = null;
        _termLifetimeTask = null;
        View = null;
        State = ProjectSessionState.Stopped;
        RaiseStateChanged();
    }

    private static EasyTerminalControl CreateTerminalView(TermPTY term, string workingDirectory)
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
            WorkingDirectory = workingDirectory,
            Win32InputMode = true,
            InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey |
                           EasyTerminalControl.INPUT_CAPTURE.DirectionKeys,
            FontFamilyWhenSettingTheme = new FontFamily("Cascadia Mono"),
            FontSizeWhenSettingTheme = 13,
            Theme = theme,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
