using System.Reflection;
using System.Runtime.InteropServices;

namespace NemotronDiarizationSharp.Native;

internal static class NativeMethods
{
    private const string LibraryName = "nemo_speech_asr_c";
    private static readonly Lazy<nint> RuntimeHandle = new(LoadSelectedRuntime, LazyThreadSafetyMode.ExecutionAndPublication);

    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, ResolveLibrary);
    }

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus DiarCreate(in DiarModelConfig config, out nint model);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DiarDestroy(nint model);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_num_speakers", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int DiarNumSpeakers(ModelHandle model);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_seconds_per_frame", CallingConvention = CallingConvention.Cdecl)]
    internal static extern double DiarSecondsPerFrame(ModelHandle model);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_stream_open", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus DiarStreamOpen(ModelHandle model, out nint stream);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_offline_f32", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus DiarOfflineF32(ModelHandle model, [In] float[] samples, nuint sampleCount, int sampleRate, out nint result);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_stream_push_f32", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus DiarStreamPushF32(StreamHandle stream, [In] float[] samples, nuint sampleCount, int sampleRate);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_stream_finish", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus DiarStreamFinish(StreamHandle stream);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_stream_close", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void DiarStreamClose(nint stream);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_frame_count", CallingConvention = CallingConvention.Cdecl)]
    internal static extern long DiarFrameCount(StreamHandle stream);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_frame_probs_start", CallingConvention = CallingConvention.Cdecl)]
    internal static extern long DiarFrameProbsStart(StreamHandle stream);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_frame_probs", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus DiarFrameProbs(StreamHandle stream, [Out] float[] probabilities, nuint capacityFloats);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_segments", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus DiarSegments(StreamHandle stream, nint config, [Out] NativeSegment[]? segments, nuint capacity, out nuint count);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_diar_segments", CallingConvention = CallingConvention.Cdecl)]
    internal static extern NativeStatus DiarSegmentsWithConfig(StreamHandle stream, in NativeSegmentationConfig config, [Out] NativeSegment[]? segments, nuint capacity, out nuint count);

    [DllImport(LibraryName, EntryPoint = "nemo_speech_asr_last_error", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint AsrLastError();

    [DllImport(LibraryName, EntryPoint = "nemo_speech_asr_version", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint AsrVersion();

    internal static string GetLastError()
    {
        nint message = AsrLastError();
        return message == 0 ? "The native library did not provide an error message." : Marshal.PtrToStringUTF8(message) ?? "Unknown native error.";
    }

    internal static string GetVersion()
    {
        nint version = AsrVersion();
        return version == 0 ? "unknown" : Marshal.PtrToStringUTF8(version) ?? "unknown";
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
            return 0;

        return RuntimeHandle.Value;
    }

    private static nint LoadSelectedRuntime()
    {
        SelectedNativeRuntime selection = NativeRuntimeManager.Current;
        DllImportSearchPath searchPath = DllImportSearchPath.UseDllDirectoryForDependencies |
            DllImportSearchPath.System32;
        try
        {
            return NativeLibrary.Load(selection.LibraryPath, typeof(NativeMethods).Assembly, searchPath);
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            throw new InvalidOperationException(
                $"Could not load the selected {selection.Backend} native runtime from '{selection.Directory}'. " +
                "The process will not try another backend because native loading may have partially changed process state.", exception);
        }
    }
}

internal enum NativeStatus
{
    Ok = 0,
    InvalidArgument = 1,
    OutOfMemory = 2,
    Runtime = 3,
    Cancelled = 4
}

[StructLayout(LayoutKind.Sequential)]
internal struct DiarModelConfig
{
    internal nuint Size;
    internal nint ModelPath;
    internal int Gpu;
    internal nint Preset;
    internal int ChunkFrames;
    internal int RightContextFrames;
    internal int LeftContextFrames;
    internal int FifoFrames;
    internal int SpeakerCacheFrames;
    internal int UpdatePeriodFrames;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSegment
{
    internal double StartTime;
    internal double EndTime;
    internal int Speaker;
}


[StructLayout(LayoutKind.Sequential)]
internal struct NativeSegmentationConfig
{
    internal nuint Size;
    internal float Onset;
    internal float Offset;
    internal double PadOnsetSec;
    internal double PadOffsetSec;
    internal double MinGapSec;
    internal double MinDurationSec;
}
