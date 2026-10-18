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
}
