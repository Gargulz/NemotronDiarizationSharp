using System.Diagnostics;

namespace NemotronDiarizationSharp.Integration.Tests;

internal sealed record ChildProcessResult(
    bool Started,
    int? ExitCode,
    bool TimedOut,
    bool Cancelled,
    string StandardOutput,
    string StandardError,
    string ErrorMessage);

internal interface IChildProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void KillEntireProcessTree();
}

internal interface IChildProcessLauncher
{
    IChildProcess Start(ProcessStartInfo startInfo);
}

internal sealed class SystemChildProcessLauncher : IChildProcessLauncher
{
    public IChildProcess Start(ProcessStartInfo startInfo) => new SystemChildProcess(
        Process.Start(startInfo) ?? throw new InvalidOperationException("The child process could not be started."));

    private sealed class SystemChildProcess(Process process) : IChildProcess
    {
        public TextReader StandardOutput => process.StandardOutput;
        public TextReader StandardError => process.StandardError;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void KillEntireProcessTree() => process.Kill(entireProcessTree: true);
        public void Dispose() => process.Dispose();
    }
}

internal sealed class ChildProcessRunner(IChildProcessLauncher launcher)
{
    public static ProcessStartInfo CreateStartInfo(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach ((string name, string? value) in environment)
                startInfo.Environment[name] = value;
        }
        return startInfo;
    }

    public async Task<ChildProcessResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        IChildProcess child;
        try
        {
            child = launcher.Start(startInfo);
        }
        catch (Exception exception)
        {
            return new(false, null, false, false, string.Empty, string.Empty, exception.Message);
        }

        using (child)
        using (var timeoutSource = new CancellationTokenSource(timeout))
        using (var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
                   cancellationToken, timeoutSource.Token))
        {
            Task<string> stdoutTask = child.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = child.StandardError.ReadToEndAsync(cancellationToken);
            bool timedOut = false;
            bool cancelled = false;
            try
            {
                await child.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = cancellationToken.IsCancellationRequested;
                timedOut = !cancelled && timeoutSource.IsCancellationRequested;
                if (!child.HasExited)
                {
                    try { child.KillEntireProcessTree(); }
                    catch (InvalidOperationException) when (child.HasExited) { }
                }

                try
                {
                    await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10))
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
                {
                    return new(true, null, timedOut, cancelled, string.Empty, string.Empty,
                        "Launched child process did not exit after process-tree termination.");
                }
            }

            string stdout = await ReadAfterExitAsync(stdoutTask).ConfigureAwait(false);
            string stderr = await ReadAfterExitAsync(stderrTask).ConfigureAwait(false);
            return new(true, child.HasExited ? child.ExitCode : null, timedOut, cancelled,
                stdout, stderr, string.Empty);
        }
    }

    private static async Task<string> ReadAfterExitAsync(Task<string> readTask)
    {
        try { return await readTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException) { return string.Empty; }
        catch (OperationCanceledException) { return string.Empty; }
    }
}
