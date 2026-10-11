using System.Security.Cryptography;

namespace NemotronDiarizationSharp.NativeTestHost;

internal static class NativeStageValidator
{
    private const string PinnedRuntimeIdentity = "nemo-speech-asr 0.2.0";

    private static readonly string[] CommonFiles =
    [
        "nemo_speech_asr_c.dll", "nemo_speech_asr.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll"
    ];

    private static readonly string[] CudaFiles =
    [
        "ggml-cuda.dll", "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll"
    ];

    private static readonly HashSet<string> KnownNativeFiles = new(
        CommonFiles.Concat(CudaFiles), StringComparer.OrdinalIgnoreCase);

    public static string? Validate(NativeTestRequest request)
    {
        if (!string.Equals(request.NativeRuntimeIdentity, PinnedRuntimeIdentity, StringComparison.Ordinal))
            return "Native runtime identity must be exactly 'nemo-speech-asr 0.2.0'.";

        if (string.IsNullOrWhiteSpace(request.NativeStageDirectory)
            || !Path.IsPathFullyQualified(request.NativeStageDirectory)
            || !Directory.Exists(request.NativeStageDirectory))
            return "Native stage directory does not exist or is not an absolute path.";

        string baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        foreach (string name in KnownNativeFiles)
        {
            if (File.Exists(Path.Combine(baseDirectory, name)))
                return $"Host application directory contains '{name}'; native loading would bypass the requested stage.";
        }

        string stage = Path.GetFullPath(request.NativeStageDirectory);
        string[] occurrences = Directory.EnumerateFiles(stage, "*.dll", SearchOption.AllDirectories).ToArray();
        foreach (string path in occurrences)
        {
            if (!string.Equals(Path.GetDirectoryName(path), stage, StringComparison.OrdinalIgnoreCase))
                return $"Stage DLL '{Path.GetFileName(path)}' must be located directly in the stage root; nested runtime layouts are rejected.";
        }

        string[] names = occurrences.Select(static path => Path.GetFileName(path)!).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            return "Native stage contains duplicate DLL names in recursive directories.";

        if (request.Backend == NativeBackend.Cpu && CudaFiles.Any(name => names.Contains(name, StringComparer.OrdinalIgnoreCase)))
            return "CPU stage contains CUDA runtime DLLs; mixed CPU/CUDA stages are rejected.";

        string[] required = request.Backend == NativeBackend.Cuda
            ? CommonFiles.Concat(CudaFiles).ToArray()
            : CommonFiles;
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (NativeFileHash item in request.NativeFiles)
        {
            if (string.IsNullOrWhiteSpace(item.Name) || Path.GetFileName(item.Name) != item.Name
                || !string.Equals(Path.GetExtension(item.Name), ".dll", StringComparison.OrdinalIgnoreCase))
                return "Native file manifest entries must use DLL basenames.";
            if (item.Sha256.Length != 64 || !item.Sha256.All(Uri.IsHexDigit))
                return $"Native file manifest contains an invalid SHA-256 for '{item.Name}'.";
            if (!expected.TryAdd(item.Name, item.Sha256))
                return $"Native file manifest contains duplicate entry '{item.Name}'.";
        }

        string[] unmanifested = names.Where(name => !expected.ContainsKey(name)).ToArray();
        if (unmanifested.Length != 0)
            return $"Native stage contains unmanifested DLL '{unmanifested[0]}'.";

        string[] absent = expected.Keys.Where(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (absent.Length != 0)
            return $"Manifest DLL '{absent[0]}' is missing from the stage root.";

        foreach (string name in required)
        {
            if (!KnownNativeFiles.Contains(name) || !expected.ContainsKey(name))
                return $"Native file manifest is missing required DLL '{name}'.";
            string file = Path.Combine(stage, name);
            if (!File.Exists(file))
                return $"Required native DLL '{name}' is missing from the stage root.";
            string actualHash;
            using (FileStream stream = File.OpenRead(file))
                actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actualHash, expected[name], StringComparison.OrdinalIgnoreCase))
                return $"SHA-256 mismatch for native DLL '{name}'.";
        }

        foreach ((string name, string expectedHash) in expected)
        {
            string file = Path.Combine(stage, name);
            if (!File.Exists(file))
                return $"Manifest DLL '{name}' is missing from the stage root.";
            using FileStream stream = File.OpenRead(file);
            string actualHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                return $"SHA-256 mismatch for native DLL '{name}'.";
        }

        return null;
    }
}
