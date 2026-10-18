namespace NemotronDiarizationSharp;

/// <summary>Selects the process-wide native backend used by diarization models.</summary>
public enum DiarizationBackend
{
    /// <summary>Prefer a staged CUDA runtime when Windows x64 and nvidia-smi report a device; otherwise use CPU when available.</summary>
    Auto,
    /// <summary>Require the CPU runtime.</summary>
    Cpu,
    /// <summary>Require the CUDA runtime and an NVIDIA device reported by nvidia-smi.</summary>
    Cuda
}
