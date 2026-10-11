namespace NemotronDiarizationSharp.Native;

internal enum NativeRuntimeSource
{
    ApplicationDirectory,
    LegacyDirectory,
    ExplicitDirectory,
    OperatingSystem
}

internal readonly record struct NativeRuntimeSelection(NativeRuntimeSource Source, string? LibraryPath)
{
    internal bool IsExplicit => Source == NativeRuntimeSource.ExplicitDirectory;
}

internal static class NativeRuntimeSelectionResolver
{
    internal const string PrimaryEnvironmentVariable = "NEMOTONDIARIZATIONSHARP_NATIVE_DIR";
    internal const string LegacyEnvironmentVariable = "NEMOTONSHARP_NATIVE_DIR";
    internal const string PrimaryLibraryFileName = "nemo_speech_asr_c.dll";

    internal static NativeRuntimeSelection Select(
        string? explicitDirectory,
        string? legacyDirectory,
        string applicationDirectory,
        Func<string, bool> directoryExists,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(applicationDirectory);
        ArgumentNullException.ThrowIfNull(directoryExists);
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!string.IsNullOrEmpty(explicitDirectory))
        {
            if (string.IsNullOrWhiteSpace(explicitDirectory))
                throw new InvalidOperationException(
                    $"{PrimaryEnvironmentVariable} is set to whitespace. Set it to an absolute directory containing " +
                    $"{PrimaryLibraryFileName}, or clear it to use the default CPU runtime search.");
            if (!Path.IsPathFullyQualified(explicitDirectory))
                throw new InvalidOperationException(
                    $"{PrimaryEnvironmentVariable} must be an absolute path. The configured runtime directory was rejected.");
            if (!directoryExists(explicitDirectory))
                throw new InvalidOperationException(
                    $"The directory configured by {PrimaryEnvironmentVariable} does not exist: '{explicitDirectory}'. " +
                    "No other native runtime will be selected.");

            string explicitLibraryPath = Path.Combine(explicitDirectory, PrimaryLibraryFileName);
            if (!fileExists(explicitLibraryPath))
                throw new InvalidOperationException(
                    $"The directory configured by {PrimaryEnvironmentVariable} does not contain {PrimaryLibraryFileName}: " +
                    $"'{explicitLibraryPath}'. No other native runtime will be selected.");

            return new NativeRuntimeSelection(NativeRuntimeSource.ExplicitDirectory, explicitLibraryPath);
        }

        string applicationLibraryPath = Path.Combine(applicationDirectory, PrimaryLibraryFileName);
        if (fileExists(applicationLibraryPath))
            return new NativeRuntimeSelection(NativeRuntimeSource.ApplicationDirectory, applicationLibraryPath);

        // Preserve the legacy resolver's whitespace-as-unset and app-local-before-legacy behavior.
        if (!string.IsNullOrWhiteSpace(legacyDirectory))
        {
            string legacyLibraryPath = Path.Combine(legacyDirectory, PrimaryLibraryFileName);
            if (fileExists(legacyLibraryPath))
                return new NativeRuntimeSelection(NativeRuntimeSource.LegacyDirectory, legacyLibraryPath);
        }

        return new NativeRuntimeSelection(NativeRuntimeSource.OperatingSystem, null);
    }
}

internal sealed class NativeRuntimeSelectionCache
{
    private readonly Lazy<NativeRuntimeSelection> _selection;

    internal NativeRuntimeSelectionCache(Func<NativeRuntimeSelection> selectionFactory)
    {
        ArgumentNullException.ThrowIfNull(selectionFactory);
        _selection = new Lazy<NativeRuntimeSelection>(selectionFactory, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal NativeRuntimeSelection Value => _selection.Value;
}
