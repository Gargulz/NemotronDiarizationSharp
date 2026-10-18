using System.Reflection;
using Xunit;

namespace NemotronDiarizationSharp.Integration.Tests;

public sealed class PublicApiContractTests
{
    private const string SdkAssemblyName = "NemotronDiarizationSharp";
    private const string SdkNamespace = "NemotronDiarizationSharp";

    [Fact]
    public void PublicSdkTypesAndOwnershipContractsAreAvailable()
    {
        Assembly sdk = LoadSdkAssembly();
        Type model = PublicType(sdk, "DiarizationModel");
        Type stream = PublicType(sdk, "DiarizationStream");

        Assert.True(typeof(IDisposable).IsAssignableFrom(model));
        Assert.True(typeof(IDisposable).IsAssignableFrom(stream));
        Assert.True(PublicType(sdk, "DiarizationModelOptions").IsClass);
        Assert.True(PublicType(sdk, "DiarizationSegmentationOptions").IsClass);
        Assert.True(PublicType(sdk, "DiarizationSnapshot").IsClass);
        Assert.True(PublicType(sdk, "DiarizationFrameBatch").IsClass);
        Assert.True(PublicType(sdk, "DiarizationSegment").IsSealed);

        Type historyGap = PublicType(sdk, "DiarizationFrameHistoryGapException");
        Assert.True(typeof(InvalidOperationException).IsAssignableFrom(historyGap));
    }

    [Fact]
    public void ModelLoadingAndProcessingSignaturesRemainAvailable()
    {
        Assembly sdk = LoadSdkAssembly();
        Type model = PublicType(sdk, "DiarizationModel");
        Type options = PublicType(sdk, "DiarizationModelOptions");
        Type segmentation = PublicType(sdk, "DiarizationSegmentationOptions");
        Type snapshot = PublicType(sdk, "DiarizationSnapshot");

        AssertMethod(model, "Load", [typeof(string)], model, isStatic: true);
        AssertMethod(model, "Load", [typeof(string), options], model, isStatic: true);
        AssertMethod(model, "OpenStream", Type.EmptyTypes, PublicType(sdk, "DiarizationStream"));
        AssertMethod(model, "ProcessOffline", [typeof(ReadOnlySpan<float>), typeof(int)], snapshot);
        AssertMethod(model, "ProcessOffline",
            [typeof(ReadOnlySpan<float>), typeof(int), segmentation], snapshot);
    }

    [Fact]
    public void StreamPushFinishAndReadSignaturesRemainAvailable()
    {
        Assembly sdk = LoadSdkAssembly();
        Type stream = PublicType(sdk, "DiarizationStream");
        Type snapshot = PublicType(sdk, "DiarizationSnapshot");
        Type segmentation = PublicType(sdk, "DiarizationSegmentationOptions");
        Type frameBatch = PublicType(sdk, "DiarizationFrameBatch");
        Type chunkSequence = typeof(IAsyncEnumerable<ReadOnlyMemory<float>>);

        AssertMethod(stream, "Push", [typeof(ReadOnlySpan<float>), typeof(int)], typeof(void));
        AssertMethod(stream, "PushChunksAsync",
            [chunkSequence, typeof(int), typeof(CancellationToken)], typeof(Task));
        AssertMethod(stream, "Finish", Type.EmptyTypes, typeof(void));
        AssertMethod(stream, "GetSnapshot", Type.EmptyTypes, snapshot);
        AssertMethod(stream, "GetSnapshot", [segmentation], snapshot);
        AssertMethod(stream, "ReadNewFrames", Type.EmptyTypes, frameBatch);
    }

    [Fact]
    public void OptionObjectsKeepTheirDocumentedUnsetDefaults()
    {
        Assembly sdk = LoadSdkAssembly();
        Type modelOptionsType = PublicType(sdk, "DiarizationModelOptions");
        object modelOptions = Activator.CreateInstance(modelOptionsType)!;

        foreach (string propertyName in new[]
        {
            "GpuDeviceIndex", "Preset", "ChunkFrames", "RightContextFrames", "LeftContextFrames",
            "FifoFrames", "SpeakerCacheFrames", "UpdatePeriodFrames"
        })
        {
            PropertyInfo? property = modelOptionsType.GetProperty(propertyName);
            Assert.NotNull(property);
            Assert.Null(property.GetValue(modelOptions));
        }

        PropertyInfo backend = modelOptionsType.GetProperty("Backend")!;
        Assert.Equal(PublicType(sdk, "DiarizationBackend"), backend.PropertyType);
        Assert.Equal(Enum.Parse(backend.PropertyType, "Auto"), backend.GetValue(modelOptions));

        Type segmentationType = PublicType(sdk, "DiarizationSegmentationOptions");
        object segmentation = Activator.CreateInstance(segmentationType)!;
        foreach (PropertyInfo property in segmentationType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            Assert.Null(property.GetValue(segmentation));

        PropertyInfo gpuIndex = modelOptionsType.GetProperty("GpuDeviceIndex")!;
        Assert.Equal(typeof(int?), gpuIndex.PropertyType);
        gpuIndex.SetValue(modelOptions, 2);
        Assert.Equal(2, gpuIndex.GetValue(modelOptions));
    }

    [Fact]
    public void ResultTypesExposeFrameAndNativeSpeakerData()
    {
        Assembly sdk = LoadSdkAssembly();
        Type snapshot = PublicType(sdk, "DiarizationSnapshot");
        Type segment = PublicType(sdk, "DiarizationSegment");
        Type batch = PublicType(sdk, "DiarizationFrameBatch");
        Type gap = PublicType(sdk, "DiarizationFrameHistoryGapException");

        AssertProperty(snapshot, "FirstProbabilityFrame", typeof(long));
        AssertProperty(snapshot, "FrameCount", typeof(long));
        AssertProperty(snapshot, "IsFinal", typeof(bool));
        AssertProperty(snapshot, "SpeakerCount", typeof(int));
        AssertProperty(snapshot, "SecondsPerFrame", typeof(double));
        AssertProperty(snapshot, "Segments", typeof(IReadOnlyList<>).MakeGenericType(segment));
        AssertMethod(snapshot, "GetProbability", [typeof(long), typeof(int)], typeof(float));
        AssertMethod(snapshot, "GetFrameTimestamp", [typeof(long)], typeof(double));

        AssertProperty(segment, "StartTime", typeof(double));
        AssertProperty(segment, "EndTime", typeof(double));
        AssertProperty(segment, "Speaker", typeof(int));
        AssertProperty(batch, "FirstFrameIndex", typeof(long));
        AssertProperty(batch, "FrameCount", typeof(int));
        AssertProperty(batch, "SpeakerCount", typeof(int));
        AssertProperty(batch, "SecondsPerFrame", typeof(double));
        AssertProperty(batch, "Probabilities", typeof(IReadOnlyList<float>));
        AssertProperty(gap, "MissingStartFrame", typeof(long));
        AssertProperty(gap, "MissingEndFrameExclusive", typeof(long));
        AssertProperty(gap, "EarliestAvailableFrame", typeof(long));
    }

    // The integration test project intentionally has no compile-time SDK reference. Its
    // NativeTestHost project reference exists only to build the host. Load the produced
    // managed assembly by path; reflection does not invoke SDK methods or initialize native code.
    private static Assembly LoadSdkAssembly()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the test build configuration.");
        DirectoryInfo? root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NemotronDiarizationSharp.slnx")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Could not locate the repository solution from the test output directory.");

        string assemblyPath = Path.Combine(root.FullName, "src", SdkAssemblyName, "bin", configuration,
            "net10.0", SdkAssemblyName + ".dll");
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("The managed SDK assembly was not built at its expected project output path.", assemblyPath);
        return Assembly.LoadFrom(assemblyPath);
    }

    private static Type PublicType(Assembly sdk, string typeName)
    {
        Type? type = sdk.GetType($"{SdkNamespace}.{typeName}", throwOnError: false);
        Assert.NotNull(type);
        Assert.True(type.IsPublic, $"{type.FullName} must remain a public SDK type.");
        return type;
    }

    private static void AssertMethod(Type declaringType, string name, Type[] parameterTypes,
        Type returnType, bool isStatic = false)
    {
        MethodInfo? method = declaringType.GetMethod(name,
            BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance),
            binder: null, parameterTypes, modifiers: null);
        Assert.NotNull(method);
        Assert.Equal(returnType, method.ReturnType);
    }

    private static void AssertProperty(Type declaringType, string name, Type propertyType)
    {
        PropertyInfo? property = declaringType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        Assert.Equal(propertyType, property.PropertyType);
    }
}
