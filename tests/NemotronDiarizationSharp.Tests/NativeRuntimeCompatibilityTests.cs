using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class NativeRuntimeCompatibilityTests
{
    [Fact]
    public void AcceptsOnlyThePinnedNativeRuntimeIdentity()
    {
        NativeRuntimeCompatibility.ValidateVersion("nemo-speech-asr 0.2.0");
    }

    [Theory]
    [InlineData("nemo-speech-asr 0.1.0")]
    [InlineData("nemo-speech-asr 0.2.0-custom")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsAnUnknownOrDifferentNativeRuntimeIdentity(string? version)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => NativeRuntimeCompatibility.ValidateVersion(version));

        Assert.Contains("nemo-speech-asr 0.2.0", exception.Message, StringComparison.Ordinal);
        Assert.Contains(string.IsNullOrWhiteSpace(version) ? "<empty>" : version, exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LoaderDiagnosticPreservesDllNotFoundTypeAndInnerExceptionWithoutEchoingEnvironmentValues()
    {
        var original = new DllNotFoundException("loader detail");

        DllNotFoundException diagnostic = NativeRuntimeCompatibility.CreateLibraryLoadFailure(original);

        Assert.Same(original, diagnostic.InnerException);
        Assert.Contains("nemo_speech_asr_c.dll", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("NEMOTONDIARIZATIONSHARP_NATIVE_DIR", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("loader detail", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidImageDiagnosticPreservesBadImageFormatTypeAndInnerException()
    {
        var original = new BadImageFormatException("image detail");

        BadImageFormatException diagnostic = NativeRuntimeCompatibility.CreateInvalidImageFailure(original);

        Assert.Same(original, diagnostic.InnerException);
        Assert.Contains("process architecture", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("image detail", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingEntryPointDiagnosticNamesTheExportAndPreservesTheOriginalFailure()
    {
        var original = new EntryPointNotFoundException("export detail");

        EntryPointNotFoundException diagnostic = NativeRuntimeCompatibility.CreateMissingEntryPointFailure(
            "nemo_speech_diar_create", original);

        Assert.Same(original, diagnostic.InnerException);
        Assert.Contains("nemo_speech_diar_create", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("nemo-speech-asr 0.2.0", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nemo_speech_diar_create")]
    [InlineData("nemo_speech_diar_num_speakers")]
    [InlineData("nemo_speech_diar_seconds_per_frame")]
    public void RequiredNativeCallsReportTheMissingExport(string entryPoint)
    {
        EntryPointNotFoundException original = Assert.Throws<EntryPointNotFoundException>(
            () => NativeRuntimeCompatibility.InvokeRequiredEntryPoint<int>(entryPoint,
                () => throw new EntryPointNotFoundException("native export")));

        Assert.Contains(entryPoint, original.Message, StringComparison.Ordinal);
        Assert.IsType<EntryPointNotFoundException>(original.InnerException);
    }
}
