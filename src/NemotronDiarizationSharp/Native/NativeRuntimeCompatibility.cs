namespace NemotronDiarizationSharp.Native;

internal static class NativeRuntimeCompatibility
{
    internal const string ExpectedVersion = "nemo-speech-asr 0.2.0";

    internal static void EnsureCompatible()
    {
        string actualVersion;
        try
        {
            actualVersion = NativeMethods.GetVersion();
        }
        catch (DllNotFoundException exception)
        {
            throw CreateLibraryLoadFailure(exception);
        }
        catch (BadImageFormatException exception)
        {
            throw CreateInvalidImageFailure(exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            throw CreateMissingEntryPointFailure("nemo_speech_asr_version", exception);
        }

        ValidateVersion(actualVersion);
    }

    internal static void ValidateVersion(string? actualVersion)
    {
        if (!string.Equals(actualVersion, ExpectedVersion, StringComparison.Ordinal))
        {
            string displayedVersion = string.IsNullOrWhiteSpace(actualVersion) ? "<empty>" : actualVersion;
            throw new InvalidOperationException(
                $"Incompatible NeMo-Speech.cpp native runtime. Expected '{ExpectedVersion}', received '{displayedVersion}'. " +
                "Use the runtime built from the pinned v0.2.0 source before loading a model.");
        }
    }

    internal static DllNotFoundException CreateLibraryLoadFailure(DllNotFoundException innerException) =>
        new(
            "Could not load the NeMo-Speech.cpp native runtime. Confirm that nemo_speech_asr_c.dll is available " +
            "beside the application, in NEMOTONDIARIZATIONSHARP_NATIVE_DIR, or through the operating system loader. " +
            "A missing transitive native dependency can produce the same loader failure; this diagnostic does not identify which module is missing.",
            innerException);

    internal static BadImageFormatException CreateInvalidImageFailure(BadImageFormatException innerException) =>
        new(
            "The NeMo-Speech.cpp native runtime or one of its dependencies is not a valid image for this process. " +
            "Verify that the native files match the process architecture (Windows x64 for the currently verified runtime).",
            innerException);

    internal static EntryPointNotFoundException CreateMissingEntryPointFailure(
        string entryPoint,
        EntryPointNotFoundException innerException) =>
        new(
            $"The NeMo-Speech.cpp native runtime is missing required entry point '{entryPoint}'. " +
            $"Use a runtime built from the pinned v0.2.0 source ({ExpectedVersion}).",
            innerException);

    internal static T InvokeRequiredEntryPoint<T>(string entryPoint, Func<T> invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        try
        {
            return invoke();
        }
        catch (EntryPointNotFoundException exception)
        {
            throw CreateMissingEntryPointFailure(entryPoint, exception);
        }
    }
}
