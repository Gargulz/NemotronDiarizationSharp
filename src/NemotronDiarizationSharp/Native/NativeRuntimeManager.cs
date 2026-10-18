using System.Diagnostics;

namespace NemotronDiarizationSharp.Native;

internal sealed record SelectedNativeRuntime(DiarizationBackend Backend, string Directory, string LibraryPath);

internal static class NativeRuntimeManager
{
    private static readonly NativeRuntimeSelectionState Selection = new();

    internal static DiarizationBackend? SelectedBackend
        => Selection.SelectedBackend;

    internal static SelectedNativeRuntime EnsureSelected(DiarizationBackend requested, int? requestedDeviceIndex = null)
        => Selection.EnsureSelected(requested, requestedDeviceIndex, () => Select(requested));

    internal static SelectedNativeRuntime Current
        => EnsureSelected(DiarizationBackend.Auto);

    private static SelectedNativeRuntime Select(DiarizationBackend requested)
    {
        string app = AppContext.BaseDirectory;
        string? explicitDirectory = Environment.GetEnvironmentVariable(NativeRuntimeSettings.PrimaryEnvironmentVariable);
        string? legacyDirectory = Environment.GetEnvironmentVariable(NativeRuntimeSettings.LegacyEnvironmentVariable);

        string cpuDirectory = Path.Combine(app, "runtimes", "nemotron", "cpu", "win-x64", "native");
        string cudaDirectory = Path.Combine(app, "runtimes", "nemotron", "cuda", "win-x64", "native");
        string legacyCpuDirectory = Path.Combine(app, "runtimes", "win-x64", "native");
        string? cpuRuntimeDirectory = new[] { cpuDirectory, legacyCpuDirectory, app }
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, NativeRuntimeSettings.EntryLibraryFileName)));
        bool cudaFiles = File.Exists(Path.Combine(cudaDirectory, NativeRuntimeSettings.EntryLibraryFileName));
        bool cpuFiles = cpuRuntimeDirectory is not null;

        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            if (!Path.IsPathFullyQualified(explicitDirectory))
                throw new InvalidOperationException($"{NativeRuntimeSettings.PrimaryEnvironmentVariable} must be an absolute runtime directory.");
            string entry = Path.Combine(explicitDirectory, NativeRuntimeSettings.EntryLibraryFileName);
            if (!File.Exists(entry))
                throw new InvalidOperationException($"Configured native runtime directory does not contain {Path.GetFileName(entry)}: '{explicitDirectory}'.");
            bool appearsCuda = File.Exists(Path.Combine(explicitDirectory, "ggml-cuda.dll"));
            DiarizationBackend explicitBackend = appearsCuda ? DiarizationBackend.Cuda : DiarizationBackend.Cpu;
            if (requested != DiarizationBackend.Auto && requested != explicitBackend)
                throw new InvalidOperationException($"The explicitly configured runtime directory contains the {explicitBackend} runtime, not {requested}.");
            if (explicitBackend == DiarizationBackend.Cuda && !NvidiaDeviceProbe.TryGetDeviceIndexes(out _))
                throw new InvalidOperationException("The explicitly configured CUDA runtime is present, but nvidia-smi did not report a usable NVIDIA device.");
            return Create(explicitBackend, explicitDirectory);
        }

        if (!string.IsNullOrWhiteSpace(legacyDirectory))
        {
            if (!Path.IsPathFullyQualified(legacyDirectory))
                throw new InvalidOperationException($"{NativeRuntimeSettings.LegacyEnvironmentVariable} must be an absolute runtime directory.");
            string entry = Path.Combine(legacyDirectory, NativeRuntimeSettings.EntryLibraryFileName);
            if (!File.Exists(entry))
                throw new InvalidOperationException($"Legacy native runtime directory does not contain {Path.GetFileName(entry)}: '{legacyDirectory}'.");
            if (requested == DiarizationBackend.Cuda)
                throw new InvalidOperationException("The legacy native runtime directory is treated as CPU; set the primary runtime directory to select CUDA.");
            return Create(DiarizationBackend.Cpu, legacyDirectory);
        }

        bool isWindowsX64 = OperatingSystem.IsWindows() && Environment.Is64BitProcess;
        bool nvidiaDeviceReported = requested == DiarizationBackend.Auto && cudaFiles && isWindowsX64 &&
            NvidiaDeviceProbe.TryGetDeviceIndexes(out _);
        if (requested == DiarizationBackend.Cuda)
        {
            if (!cudaFiles)
                throw new InvalidOperationException($"CUDA runtime package is missing its entry library at '{Path.Combine(cudaDirectory, NativeRuntimeSettings.EntryLibraryFileName)}'.");
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new PlatformNotSupportedException("The staged CUDA runtime is supported only in a Windows x64 process.");
            if (!NvidiaDeviceProbe.TryGetDeviceIndexes(out _))
                throw new InvalidOperationException("CUDA was explicitly requested, but nvidia-smi did not report an available NVIDIA device.");
            return Create(DiarizationBackend.Cuda, cudaDirectory);
        }

        if (requested == DiarizationBackend.Cpu)
        {
            if (cpuRuntimeDirectory is not null) return Create(DiarizationBackend.Cpu, cpuRuntimeDirectory);
            throw MissingRuntime("CPU");
        }

        DiarizationBackend autoBackend = ChooseAutoBackend(cudaFiles, isWindowsX64, nvidiaDeviceReported, cpuFiles);
        if (autoBackend == DiarizationBackend.Cuda) return Create(DiarizationBackend.Cuda, cudaDirectory);
        if (cpuRuntimeDirectory is not null) return Create(DiarizationBackend.Cpu, cpuRuntimeDirectory);
        throw MissingRuntime("Auto (CUDA and CPU)");
    }

    private static SelectedNativeRuntime Create(DiarizationBackend backend, string directory)
        => new(backend, Path.GetFullPath(directory), Path.GetFullPath(Path.Combine(directory, NativeRuntimeSettings.EntryLibraryFileName)));

    private static InvalidOperationException MissingRuntime(string backend)
        => new($"No usable {backend} native runtime was found. Install the matching NemotronDiarizationSharp.Runtime package for this application.");

    internal static DiarizationBackend ChooseAutoBackend(bool cudaRuntimePresent, bool isWindowsX64,
        bool nvidiaDeviceReported, bool cpuRuntimePresent)
    {
        if (cudaRuntimePresent && isWindowsX64 && nvidiaDeviceReported)
            return DiarizationBackend.Cuda;
        if (cpuRuntimePresent)
            return DiarizationBackend.Cpu;
        if (cudaRuntimePresent)
            throw new InvalidOperationException(
                "A CUDA runtime is installed, but this process is not Windows x64 or nvidia-smi did not confirm an NVIDIA device. Install the CPU runtime or correct the NVIDIA driver/device setup.");
        throw MissingRuntime("Auto (CUDA and CPU)");
    }
}

internal static class NvidiaDeviceProbe
{
    internal static bool TryGetDeviceIndexes(out IReadOnlySet<int> indexes)
    {
        indexes = new HashSet<int>();
        if (!OperatingSystem.IsWindows()) return false;
        string? path = FindNvidiaSmi();
        if (path is null) return TryParseProcessResult(executableFound: false, started: false,
            timedOut: false, exitCode: -1, output: string.Empty, out indexes);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(path, "--query-gpu=index --format=csv,noheader")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    CreateNoWindow = true
                }
            };
            if (!process.Start()) return TryParseProcessResult(executableFound: true, started: false,
                timedOut: false, exitCode: -1, output: string.Empty, out indexes);
            Task<string> outputRead = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                process.Kill(entireProcessTree: true);
                return TryParseProcessResult(executableFound: true, started: true,
                    timedOut: true, exitCode: -1, output: string.Empty, out indexes);
            }
            string output = outputRead.GetAwaiter().GetResult();
            return TryParseProcessResult(executableFound: true, started: true,
                timedOut: false, process.ExitCode, output, out indexes);
        }
        catch
        {
            return TryParseProcessResult(executableFound: true, started: false,
                timedOut: false, exitCode: -1, output: string.Empty, out indexes);
        }
    }

    internal static bool TryParseProcessResult(bool executableFound, bool started, bool timedOut,
        int exitCode, string output, out IReadOnlySet<int> indexes)
    {
        if (!executableFound || !started || timedOut)
        {
            indexes = new HashSet<int>();
            return false;
        }
        return TryParseOutput(exitCode, output, out indexes);
    }

    internal static bool TryParseOutput(int exitCode, string output, out IReadOnlySet<int> indexes)
    {
        indexes = new HashSet<int>();
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output)) return false;

        var parsed = new HashSet<int>();
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(line, out int index) || index < 0) return false;
            parsed.Add(index);
        }

        if (parsed.Count == 0) return false;
        indexes = parsed;
        return true;
    }

    private static string? FindNvidiaSmi()
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string systemPath = Path.Combine(system, "nvidia-smi.exe");
        if (File.Exists(systemPath)) return systemPath;
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(directory, "nvidia-smi.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
