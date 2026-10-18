using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class NativeRuntimeSelectionTests
{
    [Theory]
    [InlineData(false, true, false, true, DiarizationBackend.Cpu)]
    [InlineData(true, true, true, false, DiarizationBackend.Cuda)]
    [InlineData(true, true, true, true, DiarizationBackend.Cuda)]
    [InlineData(true, true, false, true, DiarizationBackend.Cpu)]
    [InlineData(true, false, true, true, DiarizationBackend.Cpu)]
    public void AutoPrefersCudaAndUsesCpuWhenCudaPreflightIsUnavailable(bool cudaRuntime,
        bool windowsX64, bool deviceReported, bool cpuRuntime, DiarizationBackend expected)
    {
        Assert.Equal(expected, NativeRuntimeManager.ChooseAutoBackend(
            cudaRuntime, windowsX64, deviceReported, cpuRuntime));
    }

    [Fact]
    public void AutoWithNoRuntimeFailsClearly()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            NativeRuntimeManager.ChooseAutoBackend(false, true, false, false));

        Assert.Contains("No usable Auto", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CudaOnlyWithoutWindowsX64DeviceFailsClearly()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            NativeRuntimeManager.ChooseAutoBackend(true, true, false, false));

        Assert.Contains("nvidia-smi", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "0\r\n1\r\n", true, 2)]
    [InlineData(1, "0\r\n", false, 0)]
    [InlineData(0, "", false, 0)]
    [InlineData(0, "GPU 0", false, 0)]
    [InlineData(0, "0\r\ninvalid", false, 0)]
    [InlineData(0, "-1", false, 0)]
    public void NvidiaSmiOutputMustBeSuccessfulAndFullyValid(int exitCode, string output,
        bool expected, int expectedCount)
    {
        bool succeeded = NvidiaDeviceProbe.TryParseOutput(exitCode, output, out IReadOnlySet<int> indexes);

        Assert.Equal(expected, succeeded);
        Assert.Equal(expectedCount, indexes.Count);
    }

    [Fact]
    public void DeviceIndexRepresentsCudaOrdinalAndNullDefaultsToZero()
    {
        Assert.Equal(0, new DiarizationModelOptions { Backend = DiarizationBackend.Cuda }
            .GetNativeGpuDeviceIndex(DiarizationBackend.Cuda));
        Assert.Equal(3, new DiarizationModelOptions { Backend = DiarizationBackend.Cuda, GpuDeviceIndex = 3 }
            .GetNativeGpuDeviceIndex(DiarizationBackend.Cuda));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void MissingOrUnusableNvidiaSmiCannotReportADevice(bool executableFound,
        bool started, bool timedOut)
    {
        bool succeeded = NvidiaDeviceProbe.TryParseProcessResult(executableFound, started,
            timedOut, exitCode: 0, output: "0", out IReadOnlySet<int> indexes);

        Assert.False(succeeded);
        Assert.Empty(indexes);
    }

    [Fact]
    public async Task ConcurrentFirstSelectionCommitsOneProcessBackend()
    {
        var state = new NativeRuntimeSelectionState();
        using var start = new ManualResetEventSlim(false);
        int selectionCalls = 0;

        Task<SelectedNativeRuntime?> SelectAsync(DiarizationBackend requested) => Task.Run(() =>
        {
            start.Wait();
            try
            {
                return state.EnsureSelected(requested, null, () =>
                {
                    Interlocked.Increment(ref selectionCalls);
                    Thread.Sleep(10);
                    return new SelectedNativeRuntime(requested, requested.ToString(), requested.ToString());
                });
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        });

        Task<SelectedNativeRuntime?> cpu = SelectAsync(DiarizationBackend.Cpu);
        Task<SelectedNativeRuntime?> cuda = SelectAsync(DiarizationBackend.Cuda);
        start.Set();
        SelectedNativeRuntime?[] results = await Task.WhenAll(cpu, cuda);

        Assert.Single(results, result => result is not null);
        Assert.Equal(1, selectionCalls);
        Assert.Contains(state.SelectedBackend.GetValueOrDefault(), new[] { DiarizationBackend.Cpu, DiarizationBackend.Cuda });
    }
}
