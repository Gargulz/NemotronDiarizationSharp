namespace NemotronDiarizationSharp.Integration.Tests;

internal static class A3ReportDirectory
{
    private const string EnvironmentVariableName = "NEMOTONDIARIZATIONSHARP_A3_REPORT_DIRECTORY";

    public static string Get()
    {
        string? configuredDirectory = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (Path.IsPathRooted(configuredDirectory))
            return Path.GetFullPath(configuredDirectory);

        string repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        return Resolve(repositoryRoot, configuredDirectory);
    }

    public static string Resolve(string repositoryRoot, string? configuredDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        string fullRepositoryRoot = Path.GetFullPath(repositoryRoot);

        return string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(fullRepositoryRoot, "local", "results", "A3.1", "reports")
            : Path.GetFullPath(configuredDirectory, fullRepositoryRoot);
    }

    public static string FindRepositoryRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        for (DirectoryInfo? directory = new(Path.GetFullPath(startDirectory)); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NemotronDiarizationSharp.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not find NemotronDiarizationSharp.slnx above '{startDirectory}'. Set {EnvironmentVariableName} to an absolute report directory.");
    }
}
