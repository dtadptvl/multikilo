using System.Windows;
using MultiKilo.Terminal;

namespace MultiKilo;

public partial class App : System.Windows.Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        if (e.Args.Any(static x => string.Equals(x, "--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            var exitCode = await TerminalSmokeTest.RunAsync();
            Shutdown(exitCode);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
