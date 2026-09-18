using System.Windows;
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
    private int _generation;
    private TaskCompletionSource<uint>? _exitSignal;

    public ProjectSession(ProjectDefinition project)
    {
        Project = project;
    }

    public ProjectDefinition Project { get; }
    public ProjectSessionState State { get; private set; } = ProjectSessionState.Stopped;
    public EasyTerminalControl? View { get; private set; }
    public bool IsLive => State is ProjectSessionState.Starting or ProjectSessionState.Running;

    public event EventHandler? StateChanged;

    public async Task StartAsync(
        bool continueSession,
        Func<EasyTerminalControl, Task> prepareViewAsync)
    {
        ArgumentNullException.ThrowIfNull(prepareViewAsync);

        if (IsLive)
        {
            return;
        }

        State = ProjectSessionState.Starting;
        RaiseStateChanged();

        var generation = ++_generation;
        var view = CreateTerminalView();
        var exitSignal = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);

        View = view;
        _exitSignal = exitSignal;
        view.Terminal.NativeSessionExited += OnNativeSessionExited;

        try
        {
            await prepareViewAsync(view);

            var kilo = continueSession ? "kilo --auto --continue" : "kilo --auto";
            var commandLine = $"pwsh.exe -NoLogo -NoProfile -Command \"{kilo}; exit\"";
            view.Terminal.StartNativeSession(commandLine, Project.Folder);

            if (generation != _generation)
            {
                view.Terminal.TerminateNativeSession();
                return;
            }

            State = ProjectSessionState.Running;
            RaiseStateChanged();
        }
        catch
        {
            CleanupFailedStart(view);
            throw;
        }
    }

    public async Task TerminateAsync()
    {
        var view = View;
        if (!IsLive && (view is null || !view.Terminal.NativeSessionIsRunning))
        {
            State = ProjectSessionState.Stopped;
            RaiseStateChanged();
            return;
        }

        ++_generation;
        var exitSignal = _exitSignal;

        try
        {
            view?.Terminal.TerminateNativeSession();

            if (exitSignal is not null)
            {
                try
                {
                    await exitSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }
            }
        }
        finally
        {
            State = ProjectSessionState.Stopped;
            RaiseStateChanged();
        }
    }

    private void OnNativeSessionExited(uint exitCode)
    {
        _exitSignal?.TrySetResult(exitCode);

        if (State == ProjectSessionState.Stopped)
        {
            return;
        }

        State = ProjectSessionState.Stopped;
        RaiseStateChanged();
    }

    private void CleanupFailedStart(EasyTerminalControl view)
    {
        ++_generation;

        try
        {
            view.Terminal.TerminateNativeSession();
        }
        catch
        {
        }

        view.Terminal.NativeSessionExited -= OnNativeSessionExited;
        _exitSignal = null;
        View = null;
        State = ProjectSessionState.Stopped;
        RaiseStateChanged();
    }

    private static EasyTerminalControl CreateTerminalView()
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
            InputCapture = EasyTerminalControl.INPUT_CAPTURE.TabKey |
                           EasyTerminalControl.INPUT_CAPTURE.DirectionKeys,
            FontFamilyWhenSettingTheme = new System.Windows.Media.FontFamily("Cascadia Mono"),
            FontSizeWhenSettingTheme = 13,
            Theme = theme,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
