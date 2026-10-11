using NemotronDiarizationSharp.Native;
using Xunit;

namespace NemotronDiarizationSharp.Tests;

public sealed class NativeRuntimeSelectionTests
{
    private const string PrimaryName = NativeRuntimeSelectionResolver.PrimaryLibraryFileName;

    [Fact]
    public void ExplicitDirectoryTakesPrecedenceOverApplicationAndLegacyDirectories()
    {
        NativeRuntimeSelection selection = Select(
            explicitDirectory: @"C:\runtime\cuda",
            legacyDirectory: @"C:\runtime\legacy",
            applicationDirectory: @"C:\app",
            existingDirectories: [@"C:\runtime\cuda", @"C:\runtime\legacy"],
            existingFiles: [Path.Combine(@"C:\runtime\cuda", PrimaryName), Path.Combine(@"C:\app", PrimaryName)]);

        Assert.Equal(NativeRuntimeSource.ExplicitDirectory, selection.Source);
        Assert.Equal(Path.Combine(@"C:\runtime\cuda", PrimaryName), selection.LibraryPath);
    }

    [Fact]
    public void MissingExplicitDirectoryFailsWithoutFallingBack()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Select(
            @"C:\missing\runtime", null, @"C:\app", [], [Path.Combine(@"C:\app", PrimaryName)]));

        Assert.Contains(NativeRuntimeSelectionResolver.PrimaryEnvironmentVariable, exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", exception.Message, StringComparison.Ordinal);
        Assert.Contains("No other native runtime", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPrimaryDllInExplicitDirectoryFailsWithoutFallingBack()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Select(
            @"C:\runtime\cuda", null, @"C:\app", [@"C:\runtime\cuda"], [Path.Combine(@"C:\app", PrimaryName)]));

        Assert.Contains(PrimaryName, exception.Message, StringComparison.Ordinal);
        Assert.Contains("No other native runtime", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative-runtime")]
    [InlineData("   ")]
    public void InvalidExplicitConfigurationFailsActionably(string configuredDirectory)
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Select(
            configuredDirectory, null, @"C:\app", [@"C:\app"], [Path.Combine(@"C:\app", PrimaryName)]));

        Assert.Contains(NativeRuntimeSelectionResolver.PrimaryEnvironmentVariable, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\app\\nemo_speech_asr_c.dll", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyExplicitValueUsesExistingDefaultApplicationResolution()
    {
        NativeRuntimeSelection selection = Select(
            "", null, @"C:\app", [@"C:\app"], [Path.Combine(@"C:\app", PrimaryName)]);

        Assert.Equal(NativeRuntimeSource.ApplicationDirectory, selection.Source);
        Assert.Equal(Path.Combine(@"C:\app", PrimaryName), selection.LibraryPath);
    }

    [Fact]
    public void LegacyDirectoryIsUsedOnlyAfterApplicationDirectory()
    {
        NativeRuntimeSelection appLocal = Select(
            null, @"C:\legacy", @"C:\app", [@"C:\legacy"],
            [Path.Combine(@"C:\app", PrimaryName), Path.Combine(@"C:\legacy", PrimaryName)]);
        NativeRuntimeSelection legacyFallback = Select(
            null, @"C:\legacy", @"C:\app", [@"C:\legacy"], [Path.Combine(@"C:\legacy", PrimaryName)]);

        Assert.Equal(NativeRuntimeSource.ApplicationDirectory, appLocal.Source);
        Assert.Equal(NativeRuntimeSource.LegacyDirectory, legacyFallback.Source);
        Assert.Equal(Path.Combine(@"C:\legacy", PrimaryName), legacyFallback.LibraryPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingLegacyRuntimePreservesOperatingSystemResolution(string? legacyDirectory)
    {
        NativeRuntimeSelection selection = Select(null, legacyDirectory, @"C:\app", [], []);

        Assert.Equal(NativeRuntimeSource.OperatingSystem, selection.Source);
        Assert.Null(selection.LibraryPath);
    }

    [Fact]
    public void SelectionRemainsStableAfterTheConfigurationChanges()
    {
        string? configuredDirectory = @"C:\runtime\cpu";
        var cache = new NativeRuntimeSelectionCache(() => Select(
            configuredDirectory, null, @"C:\app", [@"C:\runtime\cpu", @"C:\runtime\cuda"],
            [Path.Combine(configuredDirectory!, PrimaryName), Path.Combine(@"C:\runtime\cuda", PrimaryName)]));

        NativeRuntimeSelection initial = cache.Value;
        configuredDirectory = @"C:\runtime\cuda";

        Assert.Equal(NativeRuntimeSource.ExplicitDirectory, initial.Source);
        Assert.Equal(Path.Combine(@"C:\runtime\cpu", PrimaryName), cache.Value.LibraryPath);
    }

    [Fact]
    public void ConcurrentInitialSelectionRunsTheFactoryOnce()
    {
        int factoryCalls = 0;
        var cache = new NativeRuntimeSelectionCache(() =>
        {
            Interlocked.Increment(ref factoryCalls);
            Thread.Sleep(20);
            return new NativeRuntimeSelection(NativeRuntimeSource.ExplicitDirectory, @"C:\runtime\cuda\nemo_speech_asr_c.dll");
        });
        var selected = new NativeRuntimeSelection[32];

        Parallel.For(0, selected.Length, index => selected[index] = cache.Value);

        Assert.Equal(1, factoryCalls);
        Assert.All(selected, selection => Assert.Equal(selected[0], selection));
    }

    private static NativeRuntimeSelection Select(
        string? explicitDirectory,
        string? legacyDirectory,
        string applicationDirectory,
        string[] existingDirectories,
        string[] existingFiles) => NativeRuntimeSelectionResolver.Select(
            explicitDirectory,
            legacyDirectory,
            applicationDirectory,
            path => existingDirectories.Contains(path, StringComparer.OrdinalIgnoreCase),
            path => existingFiles.Contains(path, StringComparer.OrdinalIgnoreCase));
}
