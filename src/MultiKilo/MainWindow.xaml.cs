using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
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
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
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
            MessageBox.Show(this, $"Could not read projects.json.\n\n{ex.Message}", "MultiKilo",
                MessageBoxButton.OK, MessageBoxImage.Error);
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
            DetachView(oldView);
            await session.StartAsync(continueSession: true);
            AttachView(session.View);
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
            var result = MessageBox.Show(this,
                "This project is running. Terminate its Kilo session and remove the project?",
                "Remove project", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
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
            DetachView(oldView);
            await session.StartAsync(continueSession);
            AttachView(session.View);
            ProjectsList.SelectedItem = project;
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
        StartButton.IsEnabled = !starting && session?.State != ProjectSessionState.Running;
        RestartButton.IsEnabled = !starting;
        TerminateButton.IsEnabled = session?.IsLive == true;
        RemoveButton.IsEnabled = !starting;
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
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
            MessageBox.Show(this, $"Could not save projects.json.\n\n{ex.Message}", "MultiKilo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string NormalizeFolder(string folder) =>
        Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void ShowSessionError(string message, Exception ex)
    {
        MessageBox.Show(this,
            $"{message}\n\n{ex.Message}\n\nMake sure pwsh.exe and kilo are available on PATH.",
            "MultiKilo", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        const int WmKeyDown = 0x0100;
        const int WmSysKeyDown = 0x0104;
        const int VkControl = 0x11;
        const int VkShift = 0x10;
        const int VkC = 0x43;
        const int VkV = 0x56;

        if (handled ||
            msg.message is not (WmKeyDown or WmSysKeyDown) ||
            !IsVisible ||
            !IsActive ||
            SelectedSession is not { IsLive: true } session)
        {
            return;
        }

        var ctrl = (GetKeyState(VkControl) & 0x8000) != 0;
        var shift = (GetKeyState(VkShift) & 0x8000) != 0;
        if (!ctrl || !shift)
        {
            return;
        }

        var key = msg.wParam.ToInt32();
        if (key == VkC)
        {
            var selectedText = session.GetSelectedText();
            if (!string.IsNullOrEmpty(selectedText))
            {
                Clipboard.SetText(selectedText);
            }

            handled = true;
        }
        else if (key == VkV)
        {
            if (Clipboard.ContainsText())
            {
                session.Paste(Clipboard.GetText());
            }

            handled = true;
        }
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
            var result = MessageBox.Show(this,
                "Terminate all Kilo sessions and exit?",
                "Quit MultiKilo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
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
        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayDrawingIcon?.Dispose();
        _trayDrawingIcon = null;
        Application.Current.Shutdown();
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);
}
