using MultiKilo.Models;

namespace MultiKilo.Terminal;

internal enum ProjectSessionState
{
    Stopped,
    Starting,
    Running
}

internal sealed class ProjectSession
{
    private const string PwshCommand = "pwsh.exe -NoLogo -NoProfile -NoExit";

    private JobObject? _job;
    private NativeConPtyConnection? _connection;
    private int _generation;

    public ProjectSession(ProjectDefinition project)
    {
        Project = project;
    }

    public ProjectDefinition Project { get; }
    public ProjectSessionState State { get; private set; } = ProjectSessionState.Stopped;
    public TerminalSessionView? View { get; private set; }
    public bool IsLive => State is ProjectSessionState.Starting or ProjectSessionState.Running;

    public event EventHandler? StateChanged;

    public void BeginNativePaste() => _connection?.BeginNativePaste();

    public void EndNativePaste() => _connection?.EndNativePaste();

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
        var connection = new NativeConPtyConnection(PwshCommand, Project.Folder, job);
        var view = new TerminalSessionView(connection);

        _job = job;
        _connection = connection;
        View = view;

        try
        {
            var startTask = connection.StartProcessAsync();
            var first = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(15)));
            if (first != startTask)
            {
                throw new TimeoutException("Timed out while starting the ConPTY session.");
            }

            await startTask;

            var kilo = continueSession ? "kilo --auto --continue" : "kilo --auto";
            connection.WriteRawInput(kilo + "; exit\r");

            State = ProjectSessionState.Running;
            RaiseStateChanged();

            _ = ObserveExitAsync(connection.Completion, generation);
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
        var connection = _connection;

        _job = null;
        _connection = null;

        connection?.CompleteInput();

        try
        {
            job?.Terminate();
        }
        finally
        {
            if (connection is not null)
            {
                try
                {
                    await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch
                {
                }

                connection.Dispose();
            }

            job?.Dispose();
            State = ProjectSessionState.Stopped;
            RaiseStateChanged();
        }
    }

    private async Task ObserveExitAsync(Task completion, int generation)
    {
        try
        {
            await completion;
        }
        catch
        {
        }

        if (generation != _generation)
        {
            return;
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
        {
            _connection?.CompleteInput();
            _connection?.Dispose();
            _job?.Dispose();
            _connection = null;
            _job = null;
            State = ProjectSessionState.Stopped;
            RaiseStateChanged();
        });
    }

    private async Task CleanupFailedStartAsync()
    {
        ++_generation;
        _connection?.CompleteInput();

        try
        {
            _job?.Terminate();
        }
        catch
        {
        }

        if (_connection is not null)
        {
            try
            {
                await _connection.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _connection.Dispose();
        }

        _job?.Dispose();
        _job = null;
        _connection = null;
        View = null;
        State = ProjectSessionState.Stopped;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
