using Xunit;

namespace NemotronDiarizationSharp.Integration.Tests;

public sealed class A3ReportDirectoryTests
{
    [Fact]
    public void Resolve_UsesIgnoredLocalResultsByDefault()
    {
        string repositoryRoot = A3ReportDirectory.FindRepositoryRoot(AppContext.BaseDirectory);

        string result = A3ReportDirectory.Resolve(repositoryRoot, configuredDirectory: null);

        Assert.Equal(Path.Combine(repositoryRoot, "local", "results", "A3.1", "reports"), result);
    }

    [Fact]
    public void Resolve_ResolvesRelativeOverrideFromRepositoryRoot()
    {
        string repositoryRoot = A3ReportDirectory.FindRepositoryRoot(AppContext.BaseDirectory);

        string result = A3ReportDirectory.Resolve(repositoryRoot, Path.Combine("artifacts", "reports"));

        Assert.Equal(Path.Combine(repositoryRoot, "artifacts", "reports"), result);
    }

    [Fact]
    public void Resolve_PreservesAbsoluteOverride()
    {
        string repositoryRoot = A3ReportDirectory.FindRepositoryRoot(AppContext.BaseDirectory);
        string configuredDirectory = Path.GetFullPath(Path.Combine(repositoryRoot, "custom-results"));

        string result = A3ReportDirectory.Resolve(repositoryRoot, configuredDirectory);

        Assert.Equal(configuredDirectory, result);
    }
}
