using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiKilo.Models;
using MultiKilo.Services;
using MultiKilo.Terminal;
using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace MultiKilo;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProjectDefinition> _projects = [];
    private readonly Dictionary<Guid, ProjectSession> _sessions = [];
    private readonly ProjectStore _store = new();
    private Forms.NotifyIcon? _trayIcon;
    private DrawingIcon? _trayDrawingIcon;
    private bool _isShuttingDown;

    public MainWindow()
    {
        InitializeComponent();
        ProjectsList.ItemsSource = _projects;
        LoadProjects();
        InitializeTray();

        if (_projects.Count > 0)
        {
            ProjectsList.SelectedIndex = 0;
        }

        RefreshSelectedProjectUi();
    }

    private ProjectDefinition? SelectedProject => ProjectsList.SelectedItem as ProjectDefinition;

    private ProjectSession? SelectedSession =>
        SelectedProject is { } project && _sessions.TryGetValue(project.Id, out var session)
            ? session
            : null;

    private bool HasLiveSessions => _sessions.Values.Any(static session => session.IsLive);

    private void LoadProjects()
    {
        try
        {
            foreach (var project in _store.Load())
            {
                if (project.Id == Guid.Empty ||
                    string.IsNullOrWhiteSpace(project.Name) ||
                    string.IsNullOrWhiteSpace(project.Folder))
                {
                    continue;
                }

                _projects.Add(project);
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Could not read projects.json.\n\n{ex.Message}", "MultiKilo",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void InitializeTray()
    {
        _trayDrawingIcon = DrawingIcon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowFromTray());
        menu.Items.Add("Quit", null, async (_, _) => await QuitFromTrayAsync());

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "MultiKilo",
            Icon = _trayDrawingIcon,
            Visible = true,
            ContextMenuStrip = menu
        };

        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private async void OnNewProjectClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = "Choose project folder",
            Multiselect = false
        };

        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var folder = NormalizeFolder(picker.FolderName);
        var existing = _projects.FirstOrDefault(project =>
            string.Equals(NormalizeFolder(project.Folder), folder, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            ProjectsList.SelectedItem = existing;
            await StartProjectAsync(existing, continueSession: true);
            return;
        }

        var name = new DirectoryInfo(folder).Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = folder;
        }

        var project = new ProjectDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            Folder = folder
        };

        _projects.Add(project);
        PersistProjects();
        ProjectsList.SelectedItem = project;
        await StartProjectAsync(project, continueSession: false);
    }

    private void OnOpenProjectFolderClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is not { } project)
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(project.Folder);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                this,
                $"Could not open the project folder.\n\n{ex.Message}",
                "MultiKilo",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async void OnStartResumeClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is { } project)
        {
            await StartProjectAsync(project, continueSession: true);
        }
    }

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is not { } project)
        {
            return;
        }

        var session = GetSession(project);
        var oldView = session.View;

        try
        {
            SetActionButtonsEnabled(false);
            await session.TerminateAsync();
            oldView?.DisconnectConPTYTerm();
            DetachView(oldView);
            await session.StartAsync(
                continueSession: true,
                prepareViewAsync: PrepareTerminalViewAsync);
            ShowSelectedTerminal();
        }
        catch (Exception ex)
        {
            ShowSessionError("Could not restart this project.", ex);
        }
        finally
        {
            RefreshSelectedProjectUi();
        }
    }

    private async void OnTerminateClick(object sender, RoutedEventArgs e)
    {
        if (SelectedSession is not { } session)
        {
            return;
        }

        try
        {
            SetActionButtonsEnabled(false);
            await session.TerminateAsync();
        }
        catch (Exception ex)
        {
            ShowSessionError("Could not terminate this project.", ex);
        }
        finally
        {
            RefreshSelectedProjectUi();
        }
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs e)
    {
        if (SelectedProject is not { } project)
        {
            return;
        }

        var session = _sessions.GetValueOrDefault(project.Id);
        if (session?.IsLive == true)
        {
            var result = System.Windows.MessageBox.Show(this,
                "This project is running. Terminate its Kilo session and remove the project?",
                "Remove project", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);

            if (result != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                await session.TerminateAsync();
            }
            catch (Exception ex)
            {
                ShowSessionError("Could not terminate this project.", ex);
                return;
            }
        }

        if (session is not null)
        {
            session.View?.DisconnectConPTYTerm();
            DetachView(session.View);
            _sessions.Remove(project.Id);
        }

        var index = ProjectsList.SelectedIndex;
        _projects.Remove(project);
        PersistProjects();

        if (_projects.Count > 0)
        {
            ProjectsList.SelectedIndex = Math.Clamp(index, 0, _projects.Count - 1);
        }

        RefreshSelectedProjectUi();
    }

    private void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshSelectedProjectUi();
        ShowSelectedTerminal();
    }

    private async Task StartProjectAsync(ProjectDefinition project, bool continueSession)
    {
        var session = GetSession(project);
        if (session.IsLive)
        {
            ProjectsList.SelectedItem = project;
            ShowSelectedTerminal();
            session.View?.Focus();
            return;
        }

        var oldView = session.View;

        try
        {
            SetActionButtonsEnabled(false);
            ProjectsList.SelectedItem = project;
            oldView?.DisconnectConPTYTerm();
            DetachView(oldView);
            await session.StartAsync(
                continueSession,
                prepareViewAsync: PrepareTerminalViewAsync);
            ShowSelectedTerminal();
            await Dispatcher.InvokeAsync(() => session.View?.Focus(), DispatcherPriority.Input);
        }
        catch (Exception ex)
        {
            ShowSessionError("Could not start this project.", ex);
        }
        finally
        {
            RefreshSelectedProjectUi();
        }
    }

    private async Task PrepareTerminalViewAsync(
        EasyWindowsTerminalControl.EasyTerminalControl view)
    {
        AttachView(view);
        ShowSelectedTerminal();

        if (view.IsLoaded)
        {
            return;
        }

        var loaded = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler? handler = null;
        handler = (_, _) => loaded.TrySetResult();

        view.Loaded += handler;
        try
        {
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            view.Loaded -= handler;
        }

        // Let HwndHost finish BuildWindowCore/registration before the child
        // process is allowed to emit terminal state.
        await Dispatcher.InvokeAsync(
            () => { },
            DispatcherPriority.Loaded);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (view.Terminal.NativeTerminalForTesting == IntPtr.Zero &&
               DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        if (view.Terminal.NativeTerminalForTesting == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Native Windows Terminal control did not initialize.");
        }
    }

    private ProjectSession GetSession(ProjectDefinition project)
    {
        if (_sessions.TryGetValue(project.Id, out var existing))
        {
            return existing;
        }

        var session = new ProjectSession(project);
        session.StateChanged += (_, _) => Dispatcher.Invoke(RefreshSelectedProjectUi);
        _sessions.Add(project.Id, session);
        return session;
    }

    private void AttachView(UIElement? view)
    {
        if (view is null || TerminalHostGrid.Children.Contains(view))
        {
            return;
        }

        TerminalHostGrid.Children.Add(view);
    }

    private void DetachView(UIElement? view)
    {
        if (view is not null)
        {
            TerminalHostGrid.Children.Remove(view);
        }
    }

    private void ShowSelectedTerminal()
    {
        foreach (UIElement child in TerminalHostGrid.Children)
        {
            child.Visibility = Visibility.Hidden;
        }

        var view = SelectedSession?.View;
        if (view is not null && TerminalHostGrid.Children.Contains(view))
        {
            view.Visibility = Visibility.Visible;
            TerminalPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            TerminalPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void RefreshSelectedProjectUi()
    {
        var project = SelectedProject;
        var session = SelectedSession;

        if (project is null)
        {
            ProjectNameText.Text = "No project selected";
            ProjectFolderText.Text = string.Empty;
            ProjectStatusText.Text = "Stopped";
            SetActionButtonsEnabled(false);
            TerminalPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        ProjectNameText.Text = project.Name;
        ProjectFolderText.Text = project.Folder;
        ProjectFolderText.ToolTip = project.Folder;
        ProjectStatusText.Text = session?.State switch
        {
            ProjectSessionState.Starting => "Starting",
            ProjectSessionState.Running => "Running",
            _ => "Stopped"
        };

        var starting = session?.State == ProjectSessionState.Starting;
        OpenFolderButton.IsEnabled = true;
        StartButton.IsEnabled = !starting && session?.State != ProjectSessionState.Running;
        RestartButton.IsEnabled = !starting;
        TerminateButton.IsEnabled = session?.IsLive == true;
        RemoveButton.IsEnabled = !starting;
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        OpenFolderButton.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
        RestartButton.IsEnabled = enabled;
        TerminateButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled;
    }

    private void PersistProjects()
    {
        try
        {
            _store.Save(_projects);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Could not save projects.json.\n\n{ex.Message}", "MultiKilo",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private static string NormalizeFolder(string folder)
    {
        var fullPath = Path.GetFullPath(folder);
        var root = Path.GetPathRoot(fullPath);

        if (!string.IsNullOrEmpty(root) &&
            string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void ShowSessionError(string message, Exception ex)
    {
        System.Windows.MessageBox.Show(this,
            $"{message}\n\n{ex.Message}\n\nMake sure pwsh.exe and kilo are available on PATH.",
            "MultiKilo", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        e.Cancel = true;

        if (HasLiveSessions)
        {
            Hide();
            return;
        }

        BeginShutdown();
    }

    private void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private async Task QuitFromTrayAsync()
    {
        if (HasLiveSessions)
        {
            var result = System.Windows.MessageBox.Show(this,
                "Terminate all Kilo sessions and exit?",
                "Quit MultiKilo", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);

            if (result != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            foreach (var session in _sessions.Values.Where(static item => item.IsLive).ToArray())
            {
                try
                {
                    await session.TerminateAsync();
                }
                catch
                {
                }
            }
        }

        BeginShutdown();
    }

    private void BeginShutdown()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayDrawingIcon?.Dispose();
        _trayDrawingIcon = null;
        System.Windows.Application.Current.Shutdown();
    }

}
