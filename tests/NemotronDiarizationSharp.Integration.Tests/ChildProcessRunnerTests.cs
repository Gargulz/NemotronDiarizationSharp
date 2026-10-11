using System.Diagnostics;
using Xunit;

namespace NemotronDiarizationSharp.Integration.Tests;

public sealed class ChildProcessRunnerTests
{
    [Fact]
    public void StartInfoKeepsEachArgumentSeparateAndDoesNotUseShell()
    {
        ProcessStartInfo startInfo = ChildProcessRunner.CreateStartInfo(
            "dotnet", ["host app.dll", "--request", "C:\\input files\\request.json"], "C:\\work dir");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(["host app.dll", "--request", "C:\\input files\\request.json"], startInfo.ArgumentList);
        Assert.Equal("C:\\work dir", startInfo.WorkingDirectory);
    }

    [Fact]
    public async Task ReturnsExitCodeAndCapturesBothOutputStreams()
    {
        var process = new FakeChildProcess(23, "structured stdout", "diagnostic stderr");
        var runner = new ChildProcessRunner(new FakeLauncher(process));
        ChildProcessResult result = await runner.RunAsync(
            ChildProcessRunner.CreateStartInfo("fake", [], Environment.CurrentDirectory), TimeSpan.FromSeconds(3),
            TestContext.Current.CancellationToken);
        Assert.True(result.Started);
        Assert.Equal(23, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal("structured stdout", result.StandardOutput);
        Assert.Equal("diagnostic stderr", result.StandardError);
        Assert.False(process.KillRequested);
    }

    [Fact]
    public async Task TimeoutTerminatesOnlyTheLaunchedChildTree()
    {
        var process = new FakeChildProcess(null, "", "", blockUntilKilled: true);
        var runner = new ChildProcessRunner(new FakeLauncher(process));
        ChildProcessResult result = await runner.RunAsync(
            ChildProcessRunner.CreateStartInfo("fake", [], Environment.CurrentDirectory), TimeSpan.FromMilliseconds(40),
            TestContext.Current.CancellationToken);
        Assert.True(result.Started);
        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.True(process.KillRequested);
        Assert.True(process.KillEntireTreeRequested);
    }

    [Fact]
    public async Task CallerCancellationTerminatesLaunchedChildTree()
    {
        var process = new FakeChildProcess(null, "", "", blockUntilKilled: true);
        var runner = new ChildProcessRunner(new FakeLauncher(process));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        ChildProcessResult result = await runner.RunAsync(
            ChildProcessRunner.CreateStartInfo("fake", [], Environment.CurrentDirectory),
            TimeSpan.FromSeconds(3), cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.True(process.KillEntireTreeRequested);
    }

    [Fact]
    public async Task StartFailureIsReturnedWithoutAttemptingToKillOtherProcesses()
    {
        var runner = new ChildProcessRunner(new ThrowingLauncher());
        ChildProcessResult result = await runner.RunAsync(
            ChildProcessRunner.CreateStartInfo("missing-executable", [], Environment.CurrentDirectory),
            TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(result.Started);
        Assert.Null(result.ExitCode);
        Assert.NotEmpty(result.ErrorMessage);
    }

    private sealed class FakeLauncher(FakeChildProcess process) : IChildProcessLauncher
    {
        public IChildProcess Start(ProcessStartInfo startInfo) => process;
    }

    private sealed class ThrowingLauncher : IChildProcessLauncher
    {
        public IChildProcess Start(ProcessStartInfo startInfo) => throw new InvalidOperationException("synthetic start failure");
    }

    private sealed class FakeChildProcess : IChildProcess
    {
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int? _exitCode;

        public FakeChildProcess(int? exitCode, string stdout, string stderr, bool blockUntilKilled = false)
        {
            _exitCode = exitCode;
            StandardOutput = new StringReader(stdout);
            StandardError = new StringReader(stderr);
            if (!blockUntilKilled)
                _exited.TrySetResult();
        }

        public TextReader StandardOutput { get; }
        public TextReader StandardError { get; }
        public bool KillRequested { get; private set; }
        public bool KillEntireTreeRequested { get; private set; }
        public bool HasExited => _exited.Task.IsCompleted;
        public int ExitCode => _exitCode ?? 137;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);
        public void KillEntireProcessTree()
        {
            KillRequested = true;
            KillEntireTreeRequested = true;
            _exited.TrySetResult();
        }
        public void Dispose()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }
}
