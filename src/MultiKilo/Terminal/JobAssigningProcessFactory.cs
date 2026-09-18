using EasyWindowsTerminalControl.Internals;

namespace MultiKilo.Terminal;

internal sealed class JobAssigningProcessFactory(JobObject job) : IProcessFactory
{
    public IProcess Start(
        string command,
        nuint attributes,
        PseudoConsole console,
        string? workingDirectory = null)
    {
        var process = ProcessFactory.Start(command, attributes, console, workingDirectory);

        try
        {
            if (process is not ProcessFactory.WrappedProcess wrapped)
            {
                throw new InvalidOperationException("Unexpected ConPTY process implementation.");
            }

            job.Assign(wrapped.Process);
            return process;
        }
        catch
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
            }

            process.Dispose();
            throw;
        }
    }
}
