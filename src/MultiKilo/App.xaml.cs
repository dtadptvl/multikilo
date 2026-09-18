using System.Windows;
using MultiKilo.Terminal;

namespace MultiKilo;

public partial class App : System.Windows.Application
{
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var smokeArgument = e.Args.FirstOrDefault(
            static x => x.StartsWith("--smoke-test", StringComparison.OrdinalIgnoreCase));
        if (smokeArgument is not null)
        {
            const string prefix = "--smoke-test=";
            var mode = smokeArgument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? smokeArgument[prefix.Length..]
                : "all";
            var exitCode = await TerminalSmokeTest.RunAsync(mode);
            Shutdown(exitCode);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
